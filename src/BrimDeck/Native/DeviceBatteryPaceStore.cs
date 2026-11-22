using System.IO;
using BrimDeck.Core;

namespace BrimDeck.Native;

// The kept charge readings for time estimates (DeviceBatteryPace) on disk, in the app's data folder
// (device-battery-pace.json), one file for every profile. Written only while the setting is on; turning it off or switching
// profiles keeps it, and only Clear readings in the settings erases it.
internal static class DeviceBatteryPaceStore
{
    private const string FileName = "device-battery-pace.json";
    private static readonly object Gate = new();
    private static string? _folder;

    public static void Start(string folder) { lock (Gate) _folder = folder; }

    public static string? Read()
    {
        lock (Gate)
        {
            if (_folder is null) return null;
            try
            {
                var path = Path.Combine(_folder, FileName);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    // Writes when the pace changed. Called after a new sample and on exit.
    public static void Save(DeviceBatteryPace pace)
    {
        lock (Gate)
        {
            if (_folder is null || !pace.Dirty) return;
            string text = pace.Serialize();
            try
            {
                Directory.CreateDirectory(_folder);
                var path = Path.Combine(_folder, FileName);
                File.WriteAllText(path + ".tmp", text);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // Clear readings in the settings: the file, and the temporary file a failed save left behind (Save does not try again).
    public static void Erase()
    {
        lock (Gate)
        {
            if (_folder is null) return;
            var path = Path.Combine(_folder, FileName);
            foreach (var file in new[] { path, path + ".tmp" })
            {
                try { if (File.Exists(file)) File.Delete(file); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public static void Finish(DeviceBatteryPace pace) { lock (Gate) Save(pace); }
}
