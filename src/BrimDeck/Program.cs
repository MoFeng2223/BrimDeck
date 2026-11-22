namespace BrimDeck;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // A newer portable version started to replace this folder's files; it does that and exits without opening the app.
        if (Core.PortableUpdate.TryRun(args, out var exitCode)) { Environment.ExitCode = exitCode; return; }
        // The elevated instance a player's install starts writes its libcef.dll and exits without opening the app.
        if (args is [Native.KugouInstall.ElevatedSwitch, var mode, var library]) { Environment.ExitCode = Native.KugouInstall.RunElevated(mode, library); return; }
        if (args is [Native.NeteaseInstall.ElevatedSwitch, var neteaseMode, var neteaseLibrary]) { Environment.ExitCode = Native.NeteaseInstall.RunElevated(neteaseMode, neteaseLibrary); return; }
        // The elevated instance FramePermission starts adds the user to Performance Log Users and exits.
        if (args is [Native.FramePermission.ElevatedSwitch, var user]) { Environment.ExitCode = Native.FramePermission.RunElevated(user); return; }
        // The elevated instance the enhanced sensors page starts sets up or removes the sensor service and exits.
        if (args is [Native.SensorSetup.ElevatedSwitch, .. var sensors]) { Environment.ExitCode = Native.SensorSetup.RunElevated(sensors); return; }
        // The helper started as administrator to paste into windows run as administrator (PasteHelper), and the helper started with
        // the shell's rights to take files dragged in while BrimDeck runs as administrator (DropHelper); each runs until BrimDeck ends.
        if (args is [Native.PasteHelper.Switch, var pastePipe, var pasteParent]) { Environment.ExitCode = Native.PasteHelper.Run(pastePipe, pasteParent); return; }
        if (args is [Native.DropHelper.Switch, var dropPipe, var dropParent]) { Environment.ExitCode = Native.DropHelper.Run(dropPipe, dropParent); return; }
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
