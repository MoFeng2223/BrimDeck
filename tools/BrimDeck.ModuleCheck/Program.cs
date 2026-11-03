using System.Text.Json;
using System.Text.Json.Serialization;
using BrimDeck.Modules;

string? directory = null, modulesRoot = null;
bool json = false, strict = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--help" or "-h": Usage(); return 0;
        case "--json": json = true; break;
        case "--warnings-as-errors": strict = true; break;
        case "--modules-root" when i + 1 < args.Length && !args[i + 1].StartsWith('-'): modulesRoot = args[++i]; break;
        case var argument when !argument.StartsWith('-') && directory is null: directory = argument; break;
        default: Console.Error.WriteLine("Unknown or incomplete argument: " + args[i]); Usage(); return 2;
    }
}
if (directory is null) { Usage(); return 2; }
try
{
    var report = ModulePackageInspector.Inspect(directory, modulesRoot);
    bool accepted = report.Passed && !(strict && report.Issues.Any(i => i.Severity == ModuleCheckSeverity.Warning));
    if (json) Console.WriteLine(JsonSerializer.Serialize(new { report.Directory, report.Manifest, report.Digest, report.Mode, report.ExecutesModuleCode, report.Passed, WarningsAsErrors = strict, Accepted = accepted, report.Assemblies, report.Issues }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
    else
    {
        Console.WriteLine("BrimDeck module check | static metadata only | module code is never executed");
        Console.WriteLine(report.Manifest is { } manifest ? $"{manifest.Id} {manifest.Version} | contract {manifest.ApiVersion}.{manifest.ApiMinor}" : "Manifest unavailable");
        if (report.Digest is not null) Console.WriteLine("Content SHA-256: " + report.Digest);
        foreach (var assembly in report.Assemblies) Console.WriteLine($"Assembly: {assembly.File} | {assembly.Name} {assembly.Version}");
        foreach (var issue in report.Issues) Console.WriteLine($"{issue.Severity} {issue.Code} [{issue.Location}]: {issue.Message}");
        Console.WriteLine(accepted ? "Static checks passed. Runtime behavior has not been tested." : report.Passed ? "Static checks failed because warnings are treated as errors." : "Static checks failed.");
    }
    return accepted ? 0 : 1;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
{ Console.Error.WriteLine("Inspection failed: " + error.Message); return 1; }

static void Usage() => Console.WriteLine("Usage: BrimDeck.ModuleCheck <package-directory> [--modules-root <directory>] [--json] [--warnings-as-errors]\nReads installed/built package files only. Does not load modules, call getters, inspect user storage, or provide a safety certificate.");
