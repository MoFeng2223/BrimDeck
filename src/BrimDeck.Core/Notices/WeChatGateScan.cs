using System.Buffers.Binary;

namespace BrimDeck.Core.Notices;

// Where, in desktop WeChat 4.x's Weixin.dll, the byte lies that says whether Qt's accessibility is on. WeChat draws its window
// with Qt, and Qt answers UI Automation with the window's real controls (the chat list, the input box) only while that byte is 1;
// it is 0 unless a screen reader was running when WeChat started. Check is the address of the instruction that reads the byte
// and Flag the address of the byte, both relative to where the module is loaded.
//
// The byte is found by the shape of the code that reads it, in the file on disk, so no table of WeChat versions is kept:
//   test rcx, rcx / jz … / cmp byte ptr [rip+disp], 0 / jz …       48 85 C9 0F 84 ?? ?? ?? ?? 80 3D <disp32> 00 0F 84
// in an executable section, whose byte lies in a writable one. Several places have that shape; the one nearest to a use of the
// text "qt.accessibility.core" comes first. The shape and the ordering follow wechatauto-replica (Apache-2.0,
// https://github.com/fanyuantaier/wechatauto-replica, wechatauto/accessibility.py). Nothing here touches a running WeChat.
public readonly record struct WeChatGateSite(uint Check, uint Flag);

public static class WeChatGateScan
{
    // The length of the shape, and where in it the cmp instruction starts.
    public const int ShapeLength = 18;
    public const int CompareAt = 9;
    // At most this many places are handed out; a place farther than Near from every use of the text is one only when none is nearer.
    public const int MaxSites = 4;
    private const uint Near = 0x20000;
    private const uint Executable = 0x20000000, Writable = 0x80000000;
    private const int Block = 1 << 20, Overlap = 32;
    private static ReadOnlySpan<byte> Core => "qt.accessibility.core"u8;
    private static ReadOnlySpan<byte> Head => [0x48, 0x85, 0xC9, 0x0F, 0x84];

    private readonly record struct Section(uint Rva, uint VirtualSize, uint RawSize, uint RawAt, uint Flags)
    {
        public bool HoldsRva(uint rva) => rva >= Rva && rva - Rva < Math.Max(VirtualSize, RawSize);
        public bool HoldsFile(long at) => at >= RawAt && at - RawAt < RawSize;
    }

    // True when the bytes are the shape; target is then how far the byte is from the start of the shape.
    public static bool IsShape(ReadOnlySpan<byte> code, out long target)
    {
        target = 0;
        if (code.Length < ShapeLength || !code.StartsWith(Head) || code[9] != 0x80 || code[10] != 0x3D || code[15] != 0 || code[16] != 0x0F || code[17] != 0x84) return false;
        // The cmp is 7 bytes long and its address is relative to the instruction after it.
        target = CompareAt + 7 + BinaryPrimitives.ReadInt32LittleEndian(code[11..]);
        return true;
    }

    // The places of a Weixin.dll (a 64-bit PE image as it is on disk), most likely first; empty for anything else. The file is
    // read through twice in blocks (it is about 200 MB), never as a whole.
    public static IReadOnlyList<WeChatGateSite> Sites(Stream image)
    {
        if (Sections(image) is not { Count: > 0 } sections) return [];
        uint? text = null;
        foreach (var section in sections)
        {
            if ((section.Flags & Executable) != 0 || text is not null) continue;
            Read(image, section, (block, at) =>
            {
                int found = block.IndexOf(Core);
                if (found >= 0 && text is null) text = section.Rva + (uint)(at + found - section.RawAt);
            });
        }
        var shapes = new List<WeChatGateSite>();
        var uses = new List<uint>();
        foreach (var section in sections)
        {
            if ((section.Flags & Executable) == 0) continue;
            Read(image, section, (block, at) =>
            {
                // A place is taken from the block it starts in, so one in the overlap is not taken twice.
                int limit = Math.Min(block.Length, Block);
                for (int i = 0; i < limit; i++)
                {
                    int next = block[i..limit].IndexOfAny((byte)0x48, (byte)0x8D);
                    if (next < 0) break;
                    i += next;
                    uint rva = section.Rva + (uint)(at + i - section.RawAt);
                    if (block[i] == 0x48)
                    {
                        if (IsShape(block[i..], out long target) && rva + target is > 0 and <= uint.MaxValue and var flag
                            && sections.Any(s => (s.Flags & Writable) != 0 && s.HoldsRva((uint)flag))) shapes.Add(new(rva, (uint)flag));
                    }
                    // lea reg, [rip+disp]: 8D, then a byte whose mod is 00 and r/m is 101. With or without a prefix byte the
                    // address is the one after the displacement.
                    else if (text is { } wanted && i + 6 <= block.Length && (block[i + 1] & 0xC7) == 0x05
                        && rva + 6 + BinaryPrimitives.ReadInt32LittleEndian(block[(i + 2)..]) == wanted) uses.Add(rva);
                }
            });
        }
        uint Distance(WeChatGateSite site) => uses.Count == 0 ? uint.MaxValue : uses.Min(use => use > site.Check ? use - site.Check : site.Check - use);
        var ordered = shapes.DistinctBy(site => site.Flag).OrderBy(Distance).ToList();
        var near = ordered.Where(site => Distance(site) <= Near).ToList();
        return [.. (near.Count > 0 ? near : ordered).Take(MaxSites)];
    }

    // The section table of a 64-bit image; null for a file that is not one.
    private static List<Section>? Sections(Stream image)
    {
        Span<byte> head = stackalloc byte[4096];
        image.Position = 0;
        int read = image.ReadAtLeast(head, head.Length, false);
        head = head[..read];
        if (read < 0x40 || head[0] != 'M' || head[1] != 'Z') return null;
        int pe = BinaryPrimitives.ReadInt32LittleEndian(head[0x3C..]);
        if (pe < 0 || pe + 24 + 2 > read || !head[pe..].StartsWith("PE\0\0"u8)) return null;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(head[(pe + 6)..]);
        int optional = BinaryPrimitives.ReadUInt16LittleEndian(head[(pe + 20)..]);
        // PE32+ only: the shape is 64-bit code.
        if (BinaryPrimitives.ReadUInt16LittleEndian(head[(pe + 24)..]) != 0x20B) return null;
        int table = pe + 24 + optional;
        var sections = new List<Section>();
        for (int i = 0; i < count && table + (i + 1) * 40 <= read; i++)
        {
            var row = head[(table + i * 40)..];
            var section = new Section(BinaryPrimitives.ReadUInt32LittleEndian(row[12..]), BinaryPrimitives.ReadUInt32LittleEndian(row[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(row[16..]), BinaryPrimitives.ReadUInt32LittleEndian(row[20..]), BinaryPrimitives.ReadUInt32LittleEndian(row[36..]));
            if (section.RawSize > 0 && section.RawAt + (long)section.RawSize <= image.Length) sections.Add(section);
        }
        return sections;
    }

    // Hands a section's bytes over in blocks; each block but the last also holds the first bytes of the next, so that what
    // crosses the border is whole in one of them. at is where the block starts in the file.
    private delegate void BlockAction(ReadOnlySpan<byte> block, long at);
    private static void Read(Stream image, Section section, BlockAction action)
    {
        byte[] buffer = new byte[Block + Overlap];
        long end = section.RawAt + (long)section.RawSize;
        for (long at = section.RawAt; at < end; at += Block)
        {
            image.Position = at;
            int read = image.ReadAtLeast(buffer.AsSpan(0, (int)Math.Min(buffer.Length, end - at)), 1, false);
            if (read <= 0) return;
            action(buffer.AsSpan(0, read), at);
        }
    }
}
