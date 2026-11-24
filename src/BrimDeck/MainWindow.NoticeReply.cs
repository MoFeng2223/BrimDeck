using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using BrimDeck.Core;
using BrimDeck.Core.Notices;
using BrimDeck.Native;

namespace BrimDeck;

// The notices page's reply to a WeChat chat (shown while WeChat's chat list is read, NoticeCenter.CanReply): a box above the
// focus's buttons and a send button; Enter sends too. The words go to that chat through WeChat's own input box and send button
// (WeChatReader.Send). The box of a chat is built once and kept while the chat has notices, so what was typed stays when the
// page is drawn again; while it has the keyboard the page is not drawn again at all (ReplyKeepsPage), as with a question's box.
public partial class MainWindow
{
    private sealed record ReplyBox(Grid Root, TextBox Box, TextBlock Note) { public bool Sending { get; set; } }
    private readonly Dictionary<string, ReplyBox> _replies = new(StringComparer.Ordinal);

    private FrameworkElement? NoticeReplyStrip(Notice notice, NoticeCenter center)
    {
        foreach (string gone in _replies.Keys.Where(chat => !center.Hub.Notices.Any(n => WeChatRows.ChatOf(n) == chat)).ToList()) _replies.Remove(gone);
        if (!center.CanReply(notice) || WeChatRows.ChatOf(notice) is not { } chat) return null;
        if (!_replies.TryGetValue(chat, out var reply)) _replies[chat] = reply = NewReply(chat);
        if (reply.Root.Parent is Panel panel) panel.Children.Remove(reply.Root);
        return reply.Root;
    }

    private bool ReplyKeepsPage(NoticeCenter center)
        => FocusedNotice(center) is { } notice && WeChatRows.ChatOf(notice) is { } chat && _replies.TryGetValue(chat, out var reply) && (reply.Sending || reply.Root.IsKeyboardFocusWithin);

    private ReplyBox NewReply(string chat)
    {
        var box = AgendaFields.Box(Look, AgendaKeyboard, 12.5);
        box.AcceptsReturn = false; box.TextWrapping = TextWrapping.Wrap; box.MinHeight = 32; box.MaxHeight = 70; box.MaxLength = 2000;
        box.Padding = new Thickness(11, 6, 11, 6); box.Background = UI.Brush(UI.Fill);
        string hint = Loc.T("回复", "Reply");
        AutomationProperties.SetName(box, Loc.T("回复 ", "Reply to ") + chat);
        var shown = AgendaFields.Hint(box, hint, UI.Tertiary);
        var field = new Grid();
        field.Children.Add(box); field.Children.Add(shown);
        var send = GlyphPill("", Loc.T("发送", "Send"), () => _ = SendReply(chat));
        send.Margin = new Thickness(8, 0, 0, 0); send.VerticalAlignment = VerticalAlignment.Bottom; send.IsEnabled = false; send.Opacity = .4;
        var note = UI.Fixed("", 11.5, 18, UI.Tertiary); note.TextTrimming = TextTrimming.CharacterEllipsis; note.Margin = new Thickness(2, 0, 0, 4); note.Visibility = Visibility.Collapsed;
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(field); Grid.SetColumn(send, 1); line.Children.Add(send);
        var root = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(note); Grid.SetRow(line, 1); root.Children.Add(line);
        box.TextChanged += (_, _) =>
        {
            bool any = box.Text.Trim().Length > 0;
            shown.Visibility = box.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            send.IsEnabled = any; send.Opacity = any ? 1 : .4;
            if (any) note.Visibility = Visibility.Collapsed;
        };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = SendReply(chat); } };
        return new ReplyBox(root, box, note);
    }

    private async Task SendReply(string chat)
    {
        if (!_replies.TryGetValue(chat, out var reply) || reply.Sending || reply.Box.Text.Trim() is not { Length: > 0 } text) return;
        reply.Sending = true; reply.Box.IsReadOnly = true; reply.Root.Opacity = .6;
        WeChatSend result;
        try { result = await _app.Notices.ReplyAsync(chat, text); }
        finally { reply.Sending = false; reply.Box.IsReadOnly = false; reply.Root.Opacity = 1; }
        bool sent = result == WeChatSend.Sent;
        reply.Note.Text = sent ? Loc.T("已发送", "Sent") : NoticeCenter.SendFailure(result);
        reply.Note.Foreground = UI.Brush(sent ? UI.Tertiary : PanelTheme.Hex("caution"));
        reply.Note.Visibility = Visibility.Visible;
        if (!sent) return;
        bool typing = reply.Root.IsKeyboardFocusWithin;
        reply.Box.Text = "";
        // TextChanged hid the note with the words; it says "已发送" until the next words are typed.
        reply.Note.Visibility = Visibility.Visible;
        if (typing) { Keyboard.ClearFocus(); EndTyping(); }
        if (_expanded && CurrentPage == DeckPage.Notices) RenderNotices();
    }
}
