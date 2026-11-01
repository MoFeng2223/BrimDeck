using System.Runtime.InteropServices;

namespace BrimDeck.Calendar;

// The smallest possible SQLite surface: open read-only, run one statement, read columns. It binds to
// winsqlite3.dll, which ships with Windows 10 2004 and later — no native file is added to the module.
internal static class Sqlite
{
    private const string Dll = "winsqlite3.dll";
    public const int Ok = 0, HasRow = 100, Done = 101;
    private const int FlagReadOnly = 0x0000_0001;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr statement);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern long sqlite3_column_int64(IntPtr statement, int index);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int index);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_type(IntPtr statement, int index);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr db);

    // The library ships with Windows 10 2004 and later; checking the file avoids loading it just to ask.
    public static bool Available() =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "winsqlite3.dll"));

    public sealed class Database : IDisposable
    {
        private IntPtr _db;

        private Database(IntPtr db) => _db = db;

        public static Database OpenReadOnly(string path)
        {
            int code = sqlite3_open_v2(path, out var handle, FlagReadOnly, IntPtr.Zero);
            if (code != Ok || handle == IntPtr.Zero) throw new IOException($"vivo-db-open-{code}");
            sqlite3_busy_timeout(handle, 250);
            return new Database(handle);
        }

        // Rows are handed out one at a time; the callback reads only the columns it asked for.
        public void Query(string sql, Action<Row> read)
        {
            int code = sqlite3_prepare_v2(_db, sql, -1, out var statement, IntPtr.Zero);
            if (code != Ok) throw new IOException($"vivo-db-prepare-{code}: {Error()}");
            try
            {
                while (true)
                {
                    code = sqlite3_step(statement);
                    if (code == Done) return;
                    if (code != HasRow) throw new IOException($"vivo-db-step-{code}: {Error()}");
                    read(new Row(statement));
                }
            }
            finally { sqlite3_finalize(statement); }
        }

        private string Error() => Marshal.PtrToStringUTF8(sqlite3_errmsg(_db)) ?? "";

        public void Dispose()
        {
            if (_db == IntPtr.Zero) return;
            sqlite3_close_v2(_db);
            _db = IntPtr.Zero;
        }

        public readonly struct Row(IntPtr statement)
        {
            public long Int64(int index) => sqlite3_column_int64(statement, index);
            public string Text(int index) => sqlite3_column_type(statement, index) == 5 ? "" : Marshal.PtrToStringUTF8(sqlite3_column_text(statement, index)) ?? "";
        }
    }
}
