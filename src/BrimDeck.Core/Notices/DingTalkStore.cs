using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrimDeck.Core.Notices;

// One conversation as DingTalk's own store last recorded it (the tblastmsg row joined to the conversation and the sender).
// Unread is the conversation's unread count, Mentioned whether the message names the user, Group whether it is a group chat.
public sealed record DingTalkRow(string Cid, string Chat, string Sender, string Text, long At, int Unread, bool Mentioned, bool Group, bool Mine);

// What BrimDeck reads of DingTalk and how. DingTalk sends no Windows notification and shows message content only in the popup
// it draws itself, which it does not draw for a group message that does not name the user. Everything it has is in its own
// store instead: %APPDATA%\DingTalk\<account>_v3\DBFiles\dingtalk.db, a SQLite database encrypted a page at a time
// (AES-128-ECB over 4096-byte pages), with its write-ahead log encrypted the same way. The key is derived from the account's
// own id and the salt DingTalk keeps beside the database; BrimDeck derives it on this PC only, decrypts pages in memory as
// SQLite asks for them (DingTalkVfs) and never writes anything of DingTalk's to disk.
// Only three small tables are read -- the last message of each conversation, the conversation names and the senders' names --
// so what it costs does not grow with the chat history: measured 2026-11-26 on a 3.6 MB store, 77 pages (308 KB) and 35 ms,
// of which 27 ms was the log. The chat history itself (tbmsg_000 to tbmsg_255) is never read.
public static class DingTalkStore
{
    public const string AppKey = "flash:dingtalk";
    public const int PageSize = 4096;
    // DingTalk's own derivation: PBKDF2-HMAC-SHA1 over the account id and its salt, then the first 16 characters of the
    // result's MD5 in lower-case hexadecimal, as ASCII, are the AES key.
    private static readonly byte[] KeySalt = "666DingT"u8.ToArray();
    private const int KeyRounds = 1000;

    public static byte[] Key(string accountId, string salt)
    {
        var derived = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(accountId + salt), KeySalt, KeyRounds, HashAlgorithmName.SHA1, 32);
        try { return Encoding.ASCII.GetBytes(Convert.ToHexStringLower(MD5.HashData(derived))[..16]); }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }

    // The salt from the account's user_config (base64 of a small JSON object); null when it is not there.
    public static string? Salt(string userConfig)
    {
        try
        {
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(userConfig.Trim())));
            return document.RootElement.TryGetProperty("salt", out var salt) && salt.GetString() is { Length: > 0 } value ? value : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or DecoderFallbackException) { return null; }
    }

    // The account's own numeric id, as DingTalk's logs record it. The folder is named after a hash of the account, not the id.
    public static string? AccountId(string log)
        => System.Text.RegularExpressions.Regex.Match(log, @"(?:real_uid|myOpenId)=(\d{8,14})") is { Success: true } found ? found.Groups[1].Value : null;

    // ----- the write-ahead log -----
    // DingTalk encrypts each page of the log as well, and its frame checksums are over the pages as stored. Decrypting the
    // pages therefore means writing the checksums again, or SQLite reads the log as damaged and misses the newest messages.
    // A frame whose own checksum does not hold ends the log, exactly as SQLite decides it: while DingTalk is writing, the
    // frame it is in the middle of is left out instead of being handed on as if it were whole.
    public static byte[] DecryptLog(byte[] log, byte[] key)
    {
        if (log.Length < 32) return log;
        bool big = (ReadBE(log, 0) & 1) == 1;
        int size = (int)ReadBE(log, 8);
        if (size == 1) size = 65536;
        if (size <= 0 || size % 16 != 0) return log[..32];
        uint s0 = 0, s1 = 0;
        Checksum(log.AsSpan(0, 24), big, ref s0, ref s1);
        if (ReadBE(log, 24) != s0 || ReadBE(log, 28) != s1) return log[..32];
        WriteBE(log, 24, s0); WriteBE(log, 28, s1);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.Key = key;
        uint stored0 = s0, stored1 = s1;
        int end = 32;
        for (int at = 32; at + 24 + size <= log.Length; at += 24 + size)
        {
            Checksum(log.AsSpan(at, 8), big, ref stored0, ref stored1);
            Checksum(log.AsSpan(at + 24, size), big, ref stored0, ref stored1);
            if (ReadBE(log, at + 16) != stored0 || ReadBE(log, at + 20) != stored1) break;
            var page = log.AsSpan(at + 24, size);
            aes.DecryptEcb(page, page, PaddingMode.None);
            Checksum(log.AsSpan(at, 8), big, ref s0, ref s1);
            Checksum(page, big, ref s0, ref s1);
            WriteBE(log, at + 16, s0); WriteBE(log, at + 20, s1);
            end = at + 24 + size;
        }
        return end == log.Length ? log : log[..end];
    }

    private static void Checksum(ReadOnlySpan<byte> data, bool big, ref uint s0, ref uint s1)
    {
        for (int at = 0; at + 8 <= data.Length; at += 8)
        {
            uint first = big ? ReadBE(data, at) : BitConverter.ToUInt32(data[at..]);
            uint second = big ? ReadBE(data, at + 4) : BitConverter.ToUInt32(data[(at + 4)..]);
            s0 += first + s1; s1 += second + s0;
        }
    }

    public static uint ReadBE(ReadOnlySpan<byte> bytes, int at) => (uint)(bytes[at] << 24 | bytes[at + 1] << 16 | bytes[at + 2] << 8 | bytes[at + 3]);
    public static void WriteBE(Span<byte> bytes, int at, uint value)
    {
        bytes[at] = (byte)(value >> 24); bytes[at + 1] = (byte)(value >> 16); bytes[at + 2] = (byte)(value >> 8); bytes[at + 3] = (byte)value;
    }

    // ----- what is read -----
    // The newest message of every conversation, with the conversation's name, the sender's name and the unread count. A
    // conversation of type 2 is a group. The sender's name falls back to the conversation's for a one-to-one chat.
    public const string Query = """
        SELECT m.cid, COALESCE(NULLIF(c.title,''), NULLIF(c.originTitle,''), ''), COALESCE(NULLIF(p.nick,''), ''),
               m.contentType, m.content, m.createdAt, COALESCE(c.unreadCount,0), COALESCE(m.atIds,''), COALESCE(c.type,1), m.senderId
        FROM tblastmsg m LEFT JOIN tbconversation c ON c.cid = m.cid LEFT JOIN tbuser_profile_v2 p ON p.uid = m.senderId
        """;

    // One row of that query. accountId is the user's own id, so that what the user sent is not told back to them.
    public static DingTalkRow? Parse(string?[] row, string accountId)
    {
        if (row.Length < 10 || row[0] is not { Length: > 0 } cid) return null;
        if (!long.TryParse(row[5], out long at) || at <= 0) return null;
        string sender = (row[2] ?? "").Trim();
        bool group = row[8] == "2";
        // A one-to-one chat is named after the other person; DingTalk keeps their id as the chat's own name, which is no
        // name to show, so the sender's is taken instead.
        string chat = Named(row[1]);
        if (!group) chat = sender.Length > 0 ? sender : chat;
        string text = Text(row[3], row[4]);
        if (text.Length == 0) return null;
        int.TryParse(row[6], out int unread);
        bool mentioned = (row[7] ?? "").Length > 0 && (row[7]!.Contains(accountId, StringComparison.Ordinal) || row[7] == "-1");
        return new DingTalkRow(cid, chat, sender, text, at, unread, mentioned, group, row[9] == accountId);
    }

    // A name to show, or nothing: DingTalk writes an account's or a conversation's own id where a chat has no name.
    private static string Named(string? title)
    {
        string name = (title ?? "").Trim();
        return name.Length == 0 || name.All(char.IsAsciiDigit) ? "" : name;
    }

    // What the message says, in a line. DingTalk keeps the body as JSON whose shape follows the kind; anything but text is
    // named by its kind rather than opened (a picture, a file, a voice message).
    public static string Text(string? contentType, string? content)
    {
        string kind = contentType ?? "";
        if (content is { Length: > 0 } body && kind is "1")
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } said) return said.Trim();
            }
            catch (JsonException) { }
        }
        return kind switch
        {
            // A card is what DingTalk writes beside a call that ended and what its robots send; the message itself is told
            // by the line that comes with it, so a card on its own says nothing.
            "1" or "1101" => "",
            "2" => Loc.T("[图片]", "[Picture]"),
            "3" => Loc.T("[文件]", "[File]"),
            "4" => Loc.T("[语音]", "[Voice message]"),
            "5" => Loc.T("[视频]", "[Video]"),
            "6" => Loc.T("[位置]", "[Location]"),
            "10" => Loc.T("[链接]", "[Link]"),
            "500" => Loc.T("[DING]", "[DING]"),
            _ => Loc.T("[消息]", "[Message]")
        };
    }

    // The notice a row stands for. A group's message carries who sent it; a message the user sent themselves is never told,
    // and neither is one older than since (the mark BrimDeck keeps of what it has already told).
    public static NoticeInput? Notice(DingTalkRow row, long since)
    {
        if (row.Mine || row.At <= since) return null;
        string title = row.Chat.Length > 0 ? row.Chat : row.Sender.Length > 0 ? row.Sender : Loc.T("钉钉", "DingTalk");
        string body = row.Group && row.Sender.Length > 0 ? row.Sender + ": " + row.Text : row.Text;
        if (row.Mentioned) body = Loc.T("[有人@你] ", "[Mentioned you] ") + body;
        // Each message is its own notice: two that say the same thing are two messages, and the notices page keeps a chat's
        // together by its name anyway.
        return new NoticeInput
        {
            Origin = NoticeOrigin.Windows, AppKey = AppKey, AppName = Loc.T("钉钉", "DingTalk"),
            InstanceKey = $"dingtalk:msg:{row.Cid}:{row.At}",
            Title = title, Body = body, Category = NoticeCategory.Message,
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.At)
        };
    }
}
