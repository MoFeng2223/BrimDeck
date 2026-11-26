using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BrimDeck.Core.Notices;

namespace BrimDeck.Native;

// Reads DingTalk's encrypted store (DingTalkStore) through a SQLite file layer of BrimDeck's own: SQLite asks for the parts
// of the file it needs, and each 4096-byte page is decrypted as it is handed over, so only the pages of the few small tables
// that are queried are ever decrypted and what it costs does not grow with the chat history. The write-ahead log is small and
// is decrypted as a whole into memory beforehand (its checksums have to be written again, DingTalkStore.DecryptLog), and the
// file layer serves the log from there. Nothing is written: the database is opened read-only, no file of DingTalk's is
// changed, and no shared memory is touched -- refusing it (SQLITE_READONLY_CANTINIT) is what makes SQLite build the log's
// index in heap memory, the way a read-only reader of a database another program owns is meant to.
internal static unsafe class DingTalkVfs
{
    private const string Name = "brimdeck-dingtalk";
    private const int KindOther = 0, KindDatabase = 1, KindLog = 2;
    private const int OpenMainDb = 0x00000100, OpenWal = 0x00080000, OpenReadOnly = 0x00000001;
    private const int Ok = 0, Row = 100, Done = 101, NotFound = 12, CantOpen = 14, ShortRead = 522, ReadOnlyCantInit = 8 | (5 << 8);
    // The layout of SQLite's own structures on 64-bit Windows: a vfs is 168 bytes, its name is at 24, what it carries at 32
    // and its open at 40; the methods of a file are a table of pointers, close at 8, read at 16, size at 48.
    private const int VfsSize = 168, VfsNameAt = 24, VfsDataAt = 32, VfsOpenAt = 40, VfsFileSizeAt = 4;
    private const int MethodsSize = 160, CloseAt = 8, ReadAt = 16, SizeAt = 48, LockAt = 56, UnlockAt = 64;

    [StructLayout(LayoutKind.Sequential)]
    private struct File { public nint Methods, Real, Kind, Log, LogLength; }

    private static readonly Lock Gate = new();
    private static nint s_realVfs, s_realMethods, s_methods;
    private static bool s_registered;
    // The account being read now: set under Gate for the one query that is running.
    private static byte[] s_key = [], s_log = [];

    // Runs one query against DingTalk's store. key is the account's key (DingTalkStore.Key) and log the account's
    // write-ahead log, decrypted. Null when the store could not be opened or read.
    public static IReadOnlyList<string?[]>? Query(string database, byte[] key, byte[] log, string sql)
    {
        lock (Gate)
        {
            if (!Register()) return null;
            s_key = key; s_log = log;
            nint handle = 0;
            try
            {
                if (sqlite3_open_v2(database, out handle, OpenReadOnly, Name) != Ok) return null;
                sqlite3_busy_timeout(handle, 500);
                return Rows(handle, sql);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
            finally
            {
                if (handle != 0) sqlite3_close(handle);
                s_key = []; s_log = [];
            }
        }
    }

    private static bool Register()
    {
        if (s_registered) return true;
        try
        {
            s_realVfs = sqlite3_vfs_find(null);
            if (s_realVfs == 0) return false;
            s_methods = sqlite3_malloc64(MethodsSize);
            if (s_methods == 0) return false;
            new Span<byte>((void*)s_methods, MethodsSize).Clear();
            *(int*)s_methods = 2; // the shared-memory methods are there and refuse, so SQLite keeps the log's index in memory
            *(nint*)(s_methods + CloseAt) = (nint)(delegate* unmanaged[Cdecl]<nint, int>)&Close;
            *(nint*)(s_methods + ReadAt) = (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, long, int>)&Read;
            *(nint*)(s_methods + SizeAt) = (nint)(delegate* unmanaged[Cdecl]<nint, long*, int>)&Size;
            *(nint*)(s_methods + LockAt) = (nint)(delegate* unmanaged[Cdecl]<nint, int, int>)&Lock;
            *(nint*)(s_methods + UnlockAt) = (nint)(delegate* unmanaged[Cdecl]<nint, int, int>)&Unlock;
            *(nint*)(s_methods + 72) = (nint)(delegate* unmanaged[Cdecl]<nint, int*, int>)&Reserved;
            *(nint*)(s_methods + 80) = (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, int>)&Control;
            *(nint*)(s_methods + 88) = (nint)(delegate* unmanaged[Cdecl]<nint, int>)&Sector;
            *(nint*)(s_methods + 96) = (nint)(delegate* unmanaged[Cdecl]<nint, int>)&Characteristics;
            *(nint*)(s_methods + 104) = (nint)(delegate* unmanaged[Cdecl]<nint, int, int, int, nint*, int>)&ShmMap;
            *(nint*)(s_methods + 112) = (nint)(delegate* unmanaged[Cdecl]<nint, int, int, int, int>)&ShmLock;
            *(nint*)(s_methods + 120) = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&ShmBarrier;
            *(nint*)(s_methods + 128) = (nint)(delegate* unmanaged[Cdecl]<nint, int, int>)&ShmUnmap;
            nint vfs = sqlite3_malloc64(VfsSize);
            if (vfs == 0) return false;
            Buffer.MemoryCopy((void*)s_realVfs, (void*)vfs, VfsSize, VfsSize);
            int realFileSize = *(int*)(s_realVfs + VfsFileSizeAt);
            *(int*)(vfs + VfsFileSizeAt) = sizeof(File);
            var name = System.Text.Encoding.UTF8.GetBytes(Name + "\0");
            nint nameBytes = sqlite3_malloc64((ulong)name.Length);
            if (nameBytes == 0) return false;
            Marshal.Copy(name, 0, nameBytes, name.Length);
            *(nint*)(vfs + VfsNameAt) = nameBytes;
            *(nint*)(vfs + VfsDataAt) = realFileSize;
            *(nint*)(vfs + VfsOpenAt) = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int, int*, int>)&Open;
            if (sqlite3_vfs_register(vfs, 0) != Ok) return false;
            s_registered = true;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Open(nint vfs, nint name, nint file, int flags, int* outFlags)
    {
        var shim = (File*)file;
        *shim = default;
        shim->Real = sqlite3_malloc64((ulong)(int)*(nint*)(vfs + VfsDataAt));
        if (shim->Real == 0) return CantOpen;
        var open = (delegate* unmanaged[Cdecl]<nint, nint, nint, int, int*, int>)(*(nint*)(s_realVfs + VfsOpenAt));
        int result = open(s_realVfs, name, shim->Real, flags, outFlags);
        if (result != Ok) { sqlite3_free(shim->Real); shim->Real = 0; return result; }
        s_realMethods = *(nint*)shim->Real;
        shim->Kind = (flags & OpenMainDb) != 0 ? KindDatabase : (flags & OpenWal) != 0 ? KindLog : KindOther;
        if (shim->Kind == KindLog)
        {
            var log = s_log;
            if (log.Length > 0 && (shim->Log = sqlite3_malloc64((ulong)log.Length)) != 0)
            {
                Marshal.Copy(log, 0, shim->Log, log.Length);
                shim->LogLength = log.Length;
            }
        }
        shim->Methods = s_methods;
        return Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Close(nint file)
    {
        var shim = (File*)file;
        int result = Ok;
        if (shim->Real != 0)
        {
            result = ((delegate* unmanaged[Cdecl]<nint, int>)(*(nint*)(s_realMethods + CloseAt)))(shim->Real);
            sqlite3_free(shim->Real); shim->Real = 0;
        }
        if (shim->Log != 0)
        {
            CryptographicOperations.ZeroMemory(new Span<byte>((void*)shim->Log, (int)shim->LogLength));
            sqlite3_free(shim->Log); shim->Log = 0; shim->LogLength = 0;
        }
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Read(nint file, nint buffer, int amount, long offset)
    {
        var shim = (File*)file;
        if (amount <= 0 || offset < 0) return ShortRead;
        var read = (delegate* unmanaged[Cdecl]<nint, nint, int, long, int>)(*(nint*)(s_realMethods + ReadAt));
        if (shim->Kind == KindLog)
        {
            long length = shim->LogLength;
            int got = (int)Math.Clamp(length - offset, 0, amount);
            if (got > 0) Buffer.MemoryCopy((void*)(shim->Log + (nint)offset), (void*)buffer, amount, got);
            if (got < amount) { new Span<byte>((byte*)buffer + got, amount - got).Clear(); return ShortRead; }
            return Ok;
        }
        if (shim->Kind != KindDatabase) return read(shim->Real, buffer, amount, offset);
        // Whole pages are read, decrypted and the asked-for part handed over: a page is the unit the store is encrypted in,
        // and SQLite's first read of a database is its 100-byte header.
        long first = offset / DingTalkStore.PageSize, last = (offset + amount - 1) / DingTalkStore.PageSize;
        int span = (int)(last - first + 1) * DingTalkStore.PageSize;
        var pages = new byte[span];
        try
        {
            fixed (byte* start = pages)
            {
                int result = read(shim->Real, (nint)start, span, first * DingTalkStore.PageSize);
                if (result != Ok && result != ShortRead) return result;
                var key = s_key;
                if (key.Length == 0) return ShortRead;
                using var aes = Aes.Create();
                aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.Key = key;
                for (int at = 0; at + DingTalkStore.PageSize <= span; at += DingTalkStore.PageSize)
                {
                    var page = pages.AsSpan(at, DingTalkStore.PageSize);
                    aes.DecryptEcb(page, page, PaddingMode.None);
                }
                Buffer.MemoryCopy(start + (offset - first * DingTalkStore.PageSize), (void*)buffer, amount, amount);
            }
            return Ok;
        }
        catch (CryptographicException) { return ShortRead; }
        finally { CryptographicOperations.ZeroMemory(pages); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Size(nint file, long* size)
    {
        var shim = (File*)file;
        if (shim->Kind == KindLog) { *size = shim->LogLength; return Ok; }
        return ((delegate* unmanaged[Cdecl]<nint, long*, int>)(*(nint*)(s_realMethods + SizeAt)))(shim->Real, size);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Lock(nint file, int level)
        => ((delegate* unmanaged[Cdecl]<nint, int, int>)(*(nint*)(s_realMethods + LockAt)))(((File*)file)->Real, level);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Unlock(nint file, int level)
        => ((delegate* unmanaged[Cdecl]<nint, int, int>)(*(nint*)(s_realMethods + UnlockAt)))(((File*)file)->Real, level);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Reserved(nint file, int* held) { *held = 0; return Ok; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Control(nint file, int op, nint argument) => NotFound;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Sector(nint file) => DingTalkStore.PageSize;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Characteristics(nint file) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ShmMap(nint file, int page, int pageSize, int extend, nint* map) { *map = 0; return ReadOnlyCantInit; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ShmLock(nint file, int offset, int count, int flags) => Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ShmBarrier(nint file) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ShmUnmap(nint file, int delete) => Ok;

    private static List<string?[]>? Rows(nint database, string sql)
    {
        nint statement = 0;
        try
        {
            if (sqlite3_prepare_v2(database, sql, -1, out statement, 0) != Ok) return null;
            var rows = new List<string?[]>();
            int columns = sqlite3_column_count(statement), step;
            while ((step = sqlite3_step(statement)) == Row)
            {
                var row = new string?[columns];
                for (int at = 0; at < columns; at++) row[at] = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, at));
                rows.Add(row);
                if (rows.Count > 2000) break;
            }
            return step is Row or Done ? rows : null;
        }
        finally { if (statement != 0) sqlite3_finalize(statement); }
    }

    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern nint sqlite3_vfs_find([MarshalAs(UnmanagedType.LPUTF8Str)] string? name);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_vfs_register(nint vfs, int makeDefault);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern nint sqlite3_malloc64(ulong size);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_free(nint pointer);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint database, int flags, [MarshalAs(UnmanagedType.LPUTF8Str)] string? vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(nint database, int milliseconds);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out nint statement, nint tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(nint statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern nint sqlite3_column_text(nint statement, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(nint statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(nint statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(nint database);
}
