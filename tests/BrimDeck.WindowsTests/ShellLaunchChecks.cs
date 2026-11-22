using System.IO;
using BrimDeck.Native;

// The drop helper is started through the shell's desktop view (ShellLaunch), so that it runs with the shell's rights when BrimDeck
// runs as administrator. Here a hidden cmd.exe started that way writes a file; the shell, not this process, starts it.
internal static class ShellLaunchChecks
{
    public static void Run(Action<string, bool> check, string folder)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "shell-launch-" + Guid.NewGuid().ToString("N") + ".txt");
        bool asked = ShellLaunch.Start(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c echo shell> \"" + file + "\"", folder);
        check("shell launch: the desktop's shell takes the request", asked);
        var until = Environment.TickCount64 + 10000;
        while (!File.Exists(file) && Environment.TickCount64 < until) Thread.Sleep(100);
        check("shell launch: the program it starts runs", File.Exists(file));
        try { File.Delete(file); } catch (IOException) { }
    }
}
