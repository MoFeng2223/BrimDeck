using System.IO;

internal sealed record TestArtifacts(string Notes, string InspectionFixture, string ModuleCheck, string Timers)
{
    internal static TestArtifacts Load(string file, string? notes = null)
    {
        file = Path.GetFullPath(file);
        if (!File.Exists(file)) throw new FileNotFoundException("Missing module test output manifest. Build BrimDeck.ModuleTests or pass --artifacts <manifest>.", file);
        var entries = File.ReadAllLines(file).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts.Length == 2 ? parts[1] : "", StringComparer.Ordinal);
        string Require(string key, bool directory = false)
        {
            string value = key == "Notes" && notes is not null ? notes : entries.GetValueOrDefault(key) ?? "";
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"Test output manifest {file} is missing {key}.");
            string path = Path.GetFullPath(value, Path.GetDirectoryName(file)!);
            if (directory ? !Directory.Exists(path) : !File.Exists(path)) throw new FileNotFoundException($"Missing {key} test artifact from {file}: {path}", path);
            return path;
        }
        var result = new TestArtifacts(Require("Notes", true), Require("InspectionFixture"), Require("ModuleCheck"), Require("Timers", true));
        foreach (string name in new[] { "module.json", "BrimDeck.Sample.dll", "BrimDeck.Sample.Helper.dll", "BrimDeck.Sample.deps.json" })
            if (!File.Exists(Path.Combine(result.Notes, name))) throw new FileNotFoundException("Missing Notes test artifact: " + name, Path.Combine(result.Notes, name));
        if (!File.Exists(Path.Combine(result.Timers, "module.json"))) throw new FileNotFoundException("Missing Timers package manifest.", Path.Combine(result.Timers, "module.json"));
        return result;
    }
}
