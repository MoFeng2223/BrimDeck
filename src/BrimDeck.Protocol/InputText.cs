using System.Runtime.InteropServices;
using System.Text;

namespace BrimDeck.Protocol;

// The bytes brim is given as text (a hook's input, the JSON of --json) are UTF-8. On Windows they may come in the PC's own
// code page instead: cmd writes what "echo" pipes in the console's code page, and a file saved as "ANSI" is in the system's.
// Which code page that is differs from PC to PC (GBK, Big5, Shift_JIS, Windows-1252, ...), so Windows is asked rather than one
// assumed. Bytes that are valid UTF-8 are never touched.
public static class InputText
{
    private const int Utf8Page = 65001;

    // The bytes as UTF-8. file: they were read from a file, which is in the system's code page; else they were piped in, in
    // the console's code page where there is a console, then the system's. Bytes no code page can read are returned as they
    // are, for the reader to refuse.
    public static byte[] Utf8(byte[] bytes, bool file = false)
    {
        if (!OperatingSystem.IsWindows() || System.Text.Unicode.Utf8.IsValid(bytes)) return bytes;
        return Utf8(bytes, file ? [(int)GetACP()] : [(int)GetConsoleCP(), (int)GetACP()]);
    }

    // The bytes read in the first of the code pages that has no byte wrong for it, as UTF-8; the bytes themselves when they
    // are UTF-8 already or none of the code pages reads them. 0 (no console) and UTF-8 itself are passed over.
    public static byte[] Utf8(byte[] bytes, IReadOnlyList<int> codePages)
    {
        if (System.Text.Unicode.Utf8.IsValid(bytes)) return bytes;
        // .NET has only UTF-8, UTF-16, UTF-32, ASCII and Latin-1 until the others are registered.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        foreach (int page in codePages.Distinct())
        {
            if (page is 0 or Utf8Page) continue;
            try { return Encoding.UTF8.GetBytes(Encoding.GetEncoding(page, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes)); }
            catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException or NotSupportedException) { }
        }
        return bytes;
    }

    // The code page of the console this process is attached to, 0 without one; and the system's ("ANSI") code page.
    [DllImport("kernel32.dll")] private static extern uint GetConsoleCP();
    [DllImport("kernel32.dll")] private static extern uint GetACP();
}
