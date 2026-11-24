using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using BrimDeck.Core.Notices;

namespace BrimDeck.Native;

internal enum WeChatSend { Sent, NoWeChat, NoChat, NotOpened, Draft, NotTyped, NotSent }

// Reads desktop WeChat's main window through UI Automation, once Qt's accessibility is on in it (WeChatGate): the rows of the
// chat list, and for a reply the open chat's input box and its send button. Nothing is read from WeChat's files or memory, the
// pointer is not moved and no key is pressed; WeChat's window may be minimized, but not closed to the tray (it then has no
// controls to read). Element ids and class names are those of WeChat 4.2.0.13. Every call goes to WeChat's process and may
// take a while or fail when its window changes, so they are made on a worker thread, one at a time (WeChatSource).
internal sealed partial class WeChatReader
{
    public readonly record struct Window(nint Handle, int Pid, bool Visible);

    private static readonly string[] Titles = ["微信", "Weixin", "WeChat"];
    private static readonly TimeSpan ChatWait = TimeSpan.FromSeconds(2), TypeWait = TimeSpan.FromSeconds(1), SendWait = TimeSpan.FromSeconds(3);
    // The input box is named after the open chat; some builds add the placeholder of its voice button to the name.
    [GeneratedRegex(@"^\s*(?:(?:长按|按住)鼠标\s*)?语音输入(?:文字)?\s*$")] private static partial Regex NameTail();
    private AutomationElement? _list;
    private nint _listWindow;

    // WeChat's main window: a top-level Qt window of Weixin.exe or WeChat.exe with WeChat's title, as large as the main window
    // is (the sign-in window and WeChat's helper windows carry the same title). A visible one before a hidden one.
    public static Window? FindMain()
    {
        var pids = new HashSet<int>();
        foreach (string name in new[] { "Weixin", "WeChat" })
            foreach (var process in Process.GetProcessesByName(name)) { pids.Add(process.Id); process.Dispose(); }
        if (pids.Count == 0) return null;
        Window? found = null;
        var text = new StringBuilder(64);
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint pid);
            if (!pids.Contains((int)pid) || GetWindow(window, 4) != 0) return true;
            text.Clear(); GetClassName(window, text, text.Capacity);
            string kind = text.ToString();
            if (!kind.StartsWith("Qt", StringComparison.Ordinal) || kind.Contains("Tray", StringComparison.Ordinal)) return true;
            text.Clear(); GetWindowText(window, text, text.Capacity);
            if (!Titles.Contains(text.ToString())) return true;
            var placement = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(window, ref placement) || placement.Normal.Right - placement.Normal.Left < 400 || placement.Normal.Bottom - placement.Normal.Top < 300) return true;
            bool visible = IsWindowVisible(window);
            if (found is null || visible && !found.Value.Visible) found = new Window(window, (int)pid, visible);
            return true;
        }, 0);
        return found;
    }

    public static bool InFront(int pid)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint front);
        return front == (uint)pid;
    }

    // Whether WeChat hands out its own controls for the window (their class names start with "mmui::") rather than Qt's bare shell.
    public static bool TreeOpen(nint window)
    {
        try { return AutomationElement.FromHandle(window).Current.ClassName.StartsWith("mmui::", StringComparison.Ordinal); }
        catch (Exception ex) when (Gone(ex)) { return false; }
    }

    // The rows of the chat list that are in view (WeChat makes rows only for those); null when the list is not there (another
    // page of WeChat is open, the window is in the tray).
    public IReadOnlyList<WeChatRow>? Rows(nint window)
    {
        try
        {
            if (List(window) is not { } list) return null;
            var request = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.None };
            request.Add(AutomationElement.NameProperty); request.Add(AutomationElement.AutomationIdProperty); request.Add(SelectionItemPattern.IsSelectedProperty);
            AutomationElementCollection found;
            using (request.Activate()) found = list.FindAll(TreeScope.Children, Condition.TrueCondition);
            var rows = new List<WeChatRow>(found.Count);
            foreach (AutomationElement element in found)
                if (WeChatRows.Parse(element.Cached.AutomationId, element.Cached.Name, element.GetCachedPropertyValue(SelectionItemPattern.IsSelectedProperty, true) is true) is { } row) rows.Add(row);
            return rows;
        }
        catch (Exception ex) when (Gone(ex)) { _list = null; return null; }
    }

    private AutomationElement? List(nint window)
    {
        if (_list is not null && _listWindow == window) return _list;
        _listWindow = window;
        return _list = AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, WeChatRows.ListId));
    }

    // Opens a chat by selecting its row, as a click on it does. True once the input box is that chat's.
    public bool Open(nint window, string chat)
    {
        try
        {
            if (List(window) is not { } list) return false;
            var rows = list.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, WeChatRows.IdPrefix + chat));
            // Two chats of one name share the id: which is meant cannot be told.
            if (rows.Count != 1) return false;
            var row = rows[0];
            if (IsOpen(window, row, chat)) return true;
            if (row.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object select)) ((SelectionItemPattern)select).Select();
            if (Until(ChatWait, () => IsOpen(window, row, chat))) return true;
            if (row.TryGetCurrentPattern(InvokePattern.Pattern, out object invoke)) ((InvokePattern)invoke).Invoke();
            return Until(ChatWait, () => IsOpen(window, row, chat));
        }
        catch (Exception ex) when (Gone(ex)) { _list = null; return false; }
    }

    // Sends a text to a chat in the list: opens the chat, puts the text into the input box, checks that it is there and that the
    // box is still that chat's, and presses WeChat's send button. A box that already holds words the user typed is left alone.
    public WeChatSend Send(nint window, string chat, string text)
    {
        try
        {
            if (List(window) is null) return WeChatSend.NoChat;
            if (!Open(window, chat)) return WeChatSend.NotOpened;
            if (Input(window) is not { } input || !input.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern) || pattern is not ValuePattern box || box.Current.IsReadOnly) return WeChatSend.NotTyped;
            if (box.Current.Value.Length > 0) return WeChatSend.Draft;
            box.SetValue(text);
            if (!Until(TypeWait, () => Same(box.Current.Value, text)) || !Named(input, chat)) { Empty(box); return WeChatSend.NotTyped; }
            if (SendButton(input) is not { } button || !Until(TypeWait, () => button.Current.IsEnabled) || !button.TryGetCurrentPattern(InvokePattern.Pattern, out object press)) { Empty(box); return WeChatSend.NotSent; }
            ((InvokePattern)press).Invoke();
            if (Until(SendWait, () => box.Current.Value.Length == 0)) return WeChatSend.Sent;
            Empty(box);
            return WeChatSend.NotSent;
        }
        catch (Exception ex) when (Gone(ex)) { _list = null; return WeChatSend.NotSent; }
    }

    private static AutomationElement? Input(nint window)
        => AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, WeChatRows.InputId));

    private static bool IsOpen(nint window, AutomationElement row, string chat)
        => row.GetCurrentPropertyValue(SelectionItemPattern.IsSelectedProperty, true) is true && Input(window) is { } input && Named(input, chat);

    private static bool Named(AutomationElement input, string chat)
    {
        string name = input.Current.Name.Trim();
        return name == chat || name.StartsWith(chat, StringComparison.Ordinal) && NameTail().IsMatch(name[chat.Length..]);
    }

    // The send button: the outlined button of the input area the box is in.
    private static AutomationElement? SendButton(AutomationElement input)
    {
        var area = input;
        for (int level = 0; level < 8 && area is not null && area.Current.ClassName != "mmui::ChatInputView"; level++) area = TreeWalker.RawViewWalker.GetParent(area);
        return area?.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.ClassNameProperty, "mmui::XOutlineButton")));
    }

    private static void Empty(ValuePattern box)
    {
        try { box.SetValue(""); } catch (Exception ex) when (Gone(ex)) { }
    }

    private static bool Same(string a, string b) => a.ReplaceLineEndings("\n").Trim() == b.ReplaceLineEndings("\n").Trim();

    private static bool Until(TimeSpan limit, Func<bool> done)
    {
        var wait = Stopwatch.StartNew();
        while (true)
        {
            if (done()) return true;
            if (wait.Elapsed >= limit) return false;
            Thread.Sleep(60);
        }
    }

    // What UI Automation throws when the element, the window or WeChat itself went away or does not answer.
    private static bool Gone(Exception ex) => ex is ElementNotAvailableException or ElementNotEnabledException or InvalidOperationException or COMException or TimeoutException or ArgumentException or Win32Exception;

    private delegate bool EnumProc(nint window, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WINDOWPLACEMENT { public int Length, Flags, Show, MinX, MinY, MaxX, MaxY; public RECT Normal; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint relation);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(nint window, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
