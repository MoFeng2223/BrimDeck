using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrimDeck.Modules;

internal static partial class Program
{
    private static void PackageInspectorChecks(string sample)
    {
        string root = Path.Combine(_root, "inspector"); Directory.CreateDirectory(root);
        string Copy(string id)
        {
            string folder = Path.Combine(root, id); Directory.CreateDirectory(folder);
            foreach (string name in new[] { "module.json", "BrimDeck.Sample.dll", "BrimDeck.Sample.Helper.dll", "BrimDeck.Sample.deps.json" }) File.Copy(Path.Combine(sample, name), Path.Combine(folder, name));
            return folder;
        }
        void Edit(string folder, Action<JsonObject> edit)
        { string file = Path.Combine(folder, "module.json"); var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject(); edit(json); File.WriteAllText(file, json.ToJsonString()); }
        string valid = Copy("valid"); var report = ModulePackageInspector.Inspect(valid);
        Check("static inspector accepts complete SDK-only notes package", report.Passed && report.Assemblies.Count == 2);
        Check("inspection reports static scope and never inspects runtime registrations", report.Mode == "static" && !report.ExecutesModuleCode && report.Issues.Any(i => i.Code == "static-limits"));
        Check("static inspection does not create host data or cache directories", Directory.GetDirectories(valid).Length == 0);
        string missing = Copy("missing"); File.Delete(Path.Combine(missing, "BrimDeck.Sample.Helper.dll"));
        Check("missing private dependency fails metadata inspection", ModulePackageInspector.Inspect(missing).Issues.Any(i => i.Code == "private-dependency-missing"));
        string wrong = Copy("wrong-entry"); Edit(wrong, json => json["entryType"] = "Not.Found");
        Check("entry type is checked without constructing it", ModulePackageInspector.Inspect(wrong).Issues.Any(i => i.Code == "entry-type-missing"));
        string minor = Copy("old-minor"); Edit(minor, json => json["apiMinor"] = 0);
        Check("known SDK additions require their declared minor", ModulePackageInspector.Inspect(minor).Issues.Any(i => i.Code == "sdk-minor-too-low"));
        string malformed = Copy("malformed"); File.WriteAllText(Path.Combine(malformed, "module.json"), "{broken");
        Check("malformed manifest gives a typed failure", !ModulePackageInspector.Inspect(malformed).Passed);
        string duplicate = Copy("duplicate-fields"); File.WriteAllText(Path.Combine(duplicate, "module.json"), File.ReadAllText(Path.Combine(duplicate, "module.json")).Replace("{", "{\"ID\":\"different\",", StringComparison.Ordinal));
        Check("case-insensitive duplicate manifest fields are rejected", ModulePackageInspector.Inspect(duplicate).Issues.Any(i => i.Code == "duplicate-manifest-field"));
        string warnings = Copy("warnings"); Edit(warnings, json => json["unrecognizedField"] = true);
        Check("unknown manifest fields warn without inventing a runtime rejection", ModulePackageInspector.Inspect(warnings).Passed && ModulePackageInspector.Inspect(warnings).Issues.Any(i => i.Code == "unknown-manifest-field"));
        string fixture = Path.GetFullPath(Path.Combine(sample, "..", "..", "BrimDeck.InspectionFixture", "release", "BrimDeck.InspectionFixture.dll"));
        string probe = Copy("no-execution"); File.Copy(fixture, Path.Combine(probe, Path.GetFileName(fixture)));
        string sentinel = Path.Combine(root, "must-not-execute.txt"), oldSentinel = Environment.GetEnvironmentVariable("BRIMDECK_CHECK_SENTINEL") ?? "";
        Environment.SetEnvironmentVariable("BRIMDECK_CHECK_SENTINEL", sentinel);
        try
        {
            var inspected = ModulePackageInspector.Inspect(probe);
            Check("private assemblies cannot hide desktop references", inspected.Issues.Any(i => i.Code == "desktop-reference-denied"));
            Check("module initializer and native imports are reported without running them", inspected.Issues.Any(i => i.Code == "module-initializer-present") && inspected.Assemblies.Any(a => a.NativeImports.Contains("kernel32.dll")));
            Check("static inspection never executes module initializer or constructors", !File.Exists(sentinel) && !AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "BrimDeck.InspectionFixture"));
            Edit(probe, json => { json["assembly"] = "BrimDeck.InspectionFixture.dll"; json["entryType"] = "BrimDeck.InspectionFixture.NoDefault"; });
            Check("entry without public parameterless constructor is rejected", ModulePackageInspector.Inspect(probe).Issues.Any(i => i.Code == "invalid-entry-type"));
            Edit(probe, json => json["entryType"] = "BrimDeck.InspectionFixture.GenericEntry");
            var generic = ModulePackageInspector.Inspect(probe);
            Check("unresolved generic inheritance is reported as unverified rather than falsely invalid", generic.Issues.Any(i => i.Code == "entry-contract-unverified") && !generic.Issues.Any(i => i.Code == "entry-contract-missing"));
            Edit(probe, json => json["entryType"] = "BrimDeck.InspectionFixture.Hidden+Nested");
            Check("public nested entry inside hidden parent is not treated as publicly visible", ModulePackageInspector.Inspect(probe).Issues.Any(i => i.Code == "invalid-entry-type"));
            var fixtureBytes = File.ReadAllBytes(Path.Combine(probe, Path.GetFileName(fixture)));
            byte[] desktopName = System.Text.Encoding.ASCII.GetBytes("BrimDeck\0"), lowerName = System.Text.Encoding.ASCII.GetBytes("brimdeck\0");
            for (int offset = 0; offset <= fixtureBytes.Length - desktopName.Length; offset++)
                if (fixtureBytes.AsSpan(offset, desktopName.Length).SequenceEqual(desktopName)) lowerName.CopyTo(fixtureBytes, offset);
            File.WriteAllBytes(Path.Combine(probe, Path.GetFileName(fixture)), fixtureBytes);
            Check("desktop assembly reference ban is case insensitive", ModulePackageInspector.Inspect(probe).Issues.Any(i => i.Code == "desktop-reference-denied"));
            string checker = Path.GetFullPath(Path.Combine(sample, "..", "..", "BrimDeck.ModuleCheck", "release", "BrimDeck.ModuleCheck.exe"));
            (int Code, string Output) Run(params string[] arguments)
            {
                var start = new ProcessStartInfo(checker) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root };
                foreach (string argument in arguments) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(10000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("Static checker did not finish."); }
                return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
            }
            var ok = Run(valid, "--json"); using var jsonReport = JsonDocument.Parse(ok.Output);
            Check("CLI emits a machine-readable static report and exit zero", ok.Code == 0 && jsonReport.RootElement.GetProperty("Mode").GetString() == "static");
            Check("CLI strict warnings have deterministic exit status", Run(warnings).Code == 0 && Run(warnings, "--warnings-as-errors").Code == 1);
            var strict = Run(warnings, "--json", "--warnings-as-errors"); using var strictJson = JsonDocument.Parse(strict.Output);
            Check("strict JSON distinguishes scan validity from policy acceptance", strict.Code == 1 && strictJson.RootElement.GetProperty("Passed").GetBoolean() && !strictJson.RootElement.GetProperty("Accepted").GetBoolean());
            Check("CLI rejects invalid packages without executing their code", Run(probe, "--json").Code == 1 && !File.Exists(sentinel));
            Check("CLI rejects unsupported execution flags", Run(valid, "--probe").Code == 2);
            Check("CLI missing option value cannot consume another flag as a path", Run(valid, "--modules-root", "--json").Code == 2);
        }
        finally { Environment.SetEnvironmentVariable("BRIMDECK_CHECK_SENTINEL", oldSentinel.Length == 0 ? null : oldSentinel); }
        string dependencies = Copy("dependencies"); Edit(dependencies, json => json["dependencies"] = JsonNode.Parse("[{\"id\":\"absent\",\"minimumVersion\":\"1.0.0\"}]")!);
        Check("dependency checks without a root are explicitly incomplete", ModulePackageInspector.Inspect(dependencies).Issues.Any(i => i.Code == "dependency-context-required"));
        Check("missing module dependency fails only when context is supplied", ModulePackageInspector.Inspect(dependencies, root).Issues.Any(i => i.Code == "module-dependency-missing"));
        string cycleRoot = Path.Combine(root, "cycle"); Directory.CreateDirectory(cycleRoot);
        foreach (string id in new[] { "a", "b" })
        {
            string target = Path.Combine(cycleRoot, id); Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(valid)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            Edit(target, json => { json["id"] = id; json["dependencies"] = JsonNode.Parse("[{\"id\":\"" + (id == "a" ? "b" : "a") + "\",\"minimumVersion\":\"1.0.0\"}]")!; });
        }
        Check("module dependency cycles are reported offline", ModulePackageInspector.Inspect(Path.Combine(cycleRoot, "a"), cycleRoot).Issues.Any(i => i.Code == "dependency-cycle"));
        Check("unrelated bad packages do not change primary package inspection", ModulePackageInspector.Inspect(valid).Passed);
        string corruptDll = Copy("corrupt-dll"); File.WriteAllBytes(Path.Combine(corruptDll, "broken.dll"), [1, 2, 3]);
        Check("invalid DLL is a structured metadata error", ModulePackageInspector.Inspect(corruptDll).Issues.Any(i => i.Code == "invalid-assembly"));
    }
}
