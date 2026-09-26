using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BrimDeck.Core;

namespace BrimDeck.Native;

public sealed class DesktopSources : IDesktopSources
{
    public ClaudeDesktopState ReadClaudeDesktop(DataLocations locations) => ClaudeDesktopReader.Read(locations);

    public string? ReadCursorToken(string database)
    {
        if (!File.Exists(database)) return null;
        IntPtr db = IntPtr.Zero, statement = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(database, out db, 1, IntPtr.Zero) != 0) return null; // SQLITE_OPEN_READONLY
            sqlite3_busy_timeout(db, 1000);
            if (sqlite3_prepare_v2(db, "SELECT value FROM ItemTable WHERE key='cursorAuth/accessToken' LIMIT 1", -1, out statement, IntPtr.Zero) != 0) return null;
            return sqlite3_step(statement) == 100 ? Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 0)) : null;
        }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
    }

    public IReadOnlyList<string?[]>? QueryDatabase(string database, string sql)
    {
        if (!File.Exists(database)) return null;
        IntPtr db = IntPtr.Zero, statement = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(database, out db, 1, IntPtr.Zero) != 0) return null; // SQLITE_OPEN_READONLY
            sqlite3_busy_timeout(db, 1000);
            if (sqlite3_prepare_v2(db, sql, -1, out statement, IntPtr.Zero) != 0) return null;
            var rows = new List<string?[]>();
            int columns = sqlite3_column_count(statement), step;
            while ((step = sqlite3_step(statement)) == 100) // SQLITE_ROW
            {
                var row = new string?[columns];
                for (int i = 0; i < columns; i++) row[i] = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, i));
                rows.Add(row);
            }
            return step == 101 ? rows : null; // SQLITE_DONE
        }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
    }

    public async Task<IReadOnlyList<LocalEndpoint>> FindAntigravityAsync(CancellationToken cancellation)
    {
        // WMI reads only the matching local process metadata; credentials never leave this method's result in memory.
        var processes = new List<(uint Id, string Command)>();
        var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
        if (locatorType is null) return [];
        dynamic locator = Activator.CreateInstance(locatorType)!;
        dynamic? service = null, rows = null;
        try
        {
            service = locator.ConnectServer(".", "root\\cimv2");
            rows = service.ExecQuery("SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE Name LIKE '%language_server%'");
            foreach (dynamic row in rows)
            {
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    string path = WmiProperty(row, "ExecutablePath") as string ?? "";
                    string command = WmiProperty(row, "CommandLine") as string ?? "";
                    if (path.Contains("antigravity", StringComparison.OrdinalIgnoreCase)) processes.Add((Convert.ToUInt32(WmiProperty(row, "ProcessId")), command));
                }
                finally { Marshal.FinalReleaseComObject(row); }
            }
        }
        finally
        {
            if (rows is not null) Marshal.FinalReleaseComObject(rows);
            if (service is not null) Marshal.FinalReleaseComObject(service);
            Marshal.FinalReleaseComObject(locator);
        }
        if (processes.Count == 0) return [];
        using var netstat = Process.Start(new ProcessStartInfo("netstat.exe", "-ano -p tcp")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
        var outputTask = netstat.StandardOutput.ReadToEndAsync(cancellation);
        await netstat.WaitForExitAsync(cancellation);
        var output = await outputTask;
        var endpoints = new List<LocalEndpoint>();
        foreach (var (pid, command) in processes)
        {
            var match = Regex.Match(command, "--csrf_token(?:=|\\s+)\"?([^\\s\"]+)");
            if (!match.Success) continue;
            foreach (var line in output.Split('\n'))
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 5 || fields[3] != "LISTENING" || fields[4] != pid.ToString()) continue;
                if (!int.TryParse(fields[1][(fields[1].LastIndexOf(':') + 1)..], out var port)) continue;
                endpoints.Add(new(port, "https", match.Groups[1].Value));
                endpoints.Add(new(port, "http", match.Groups[1].Value));
            }
        }
        return endpoints.Distinct().ToList();
    }

    private static object? WmiProperty(object row, string name)
    {
        // Use named SWbemPropertySet access. Dynamic property access on SWbemObject
        // failed after the first query in repeated-refresh testing (0x80004005).
        dynamic? properties = null, property = null;
        try
        {
            properties = ((dynamic)row).Properties_;
            property = properties.Item(name);
            return property.Value;
        }
        finally
        {
            if (property is not null) Marshal.FinalReleaseComObject(property);
            if (properties is not null) Marshal.FinalReleaseComObject(properties);
        }
    }

    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr statement);
}
