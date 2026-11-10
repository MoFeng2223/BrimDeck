using System.Net.Http;
using System.Text.Json;

namespace BrimDeck.Thrm;

// One bridge sample, with what the page and the readings read out of it. The bridge (thrm-bridge.py, the cooler
// software's local HTTP service on 127.0.0.1:27391) is the only source: the host's system snapshot carries usage
// and memory, but no temperatures or fan speeds.
internal sealed record ThrmSample(double CpuTemp, double GpuTemp, double CpuFan, double GpuFan, double TargetRpm, string Mode);

internal static class ThermBridge
{
    public const string Url = "http://127.0.0.1:27391/sample";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    // A poll that reaches the bridge but finds it unready ("bridgeOk" false) or unreachable returns null — the
    // readings show dashes; cancellation (the module stopping) propagates.
    public static async Task<ThrmSample?> ReadAsync(CancellationToken cancellation)
    {
        try
        {
            using var document = JsonDocument.Parse(await Http.GetStringAsync(Url, cancellation));
            var root = document.RootElement;
            if (!root.TryGetProperty("bridgeOk", out var ok) || ok.ValueKind != JsonValueKind.True) return null;
            return new ThrmSample(
                Number(root, "cpuTemp"), Number(root, "gpuTemp"),
                Number(root, "cpuFanRpm"), Number(root, "gpuFanRpm"), Number(root, "targetRpm"),
                root.TryGetProperty("workMode", out var mode) && mode.ValueKind == JsonValueKind.String ? mode.GetString() ?? "" : "");
        }
        // Only our own cancellation propagates; HttpClient's timeout also arrives as a cancellation, and swallowing the
        // sample here would leave stale temperatures on the page instead of the dashes the bridge-down state deserves.
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    private static double Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0;
}

// The levels the cooler software colors its readings by: temperature healthy at 70 and below, warm up to 90, hot above;
// fan speed in four bands at 1900, 2700 and 3500. The module owns these colors — they mean the same at any panel
// theme — and only the arcs and dials take them; the figures stay in the palette's own colors, as the built-in
// readings' figures do.
internal static class ThermLevels
{
    public const string Green = "#34C759", Blue = "#0A84FF", Purple = "#AF52DE", Orange = "#FF9F0A", Red = "#FF3B30";

    public static string Temperature(double celsius) => celsius > 90 ? Red : celsius > 70 ? Orange : Green;

    public static string Fan(double rpm) => rpm > 3500 ? Orange : rpm > 2700 ? Purple : rpm > 1900 ? Blue : Green;
}
