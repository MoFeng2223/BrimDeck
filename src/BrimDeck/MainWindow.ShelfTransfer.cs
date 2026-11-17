using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Core.ClipboardSync;

namespace BrimDeck;

// The file transfer shown as a page of its own: files on the left, the device to send them to and the actions on the
// right, progress and protocol details below. It replaces the clipboard and file columns while it runs and gives them
// back when closed, so the island stays the one place a transfer lives.
public partial class MainWindow
{
    private sealed class TransferCardState
    {
        public bool Receive = false;
        public bool Sending;
        public string ReceiveDetail = "";
        public IReadOnlyList<string> Paths = [];
        public bool[] Done = [];
        public IReadOnlyList<WeTypePeer> Peers = [];
        public ulong? TargetUin;
        public FileTransferPhase Phase = FileTransferPhase.Preparing;
        public string Title = "";
        public string Detail = "";
        public string Debug = "";
        public double Fraction;
        public bool Finished;
        public bool Started;
        public bool Hidden;
        public bool Details;
    }

    private TransferCardState? _transfer;
    private TextBlock? _transferDetail;
    private ProgressBar? _transferProgress;
    private TextBlock? _transferPercent;
    private TextBox? _transferDebug;
    private StackPanel? _transferFilesHost;
    private StackPanel? _receiveListHost;
    // 接收历史仅保存在内存：接收完成时记一条，程序退出即释放，不落盘。列表刷新只检查文件是否还在原位。
    private sealed record ReceiveRecord(string Path, string Name, DateTime At);
    private readonly List<ReceiveRecord> _receiveHistory = [];
    private void AppendReceive(string path)
    {
        string name = System.IO.Path.GetFileName(path);
        _receiveHistory.Insert(0, new ReceiveRecord(path, name, DateTime.Now));
        if (_receiveHistory.Count > 50) _receiveHistory.RemoveRange(50, _receiveHistory.Count - 50);
    }

    // Ctrl + drop on the island, or a file invite from the phone: the page comes forward. A drop only sends when a file
    // session is already open (the Send button and a Ctrl+drag are both just "send"), so connecting stays a separate event.
    private void BeginFileTransferSend(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        // A send page is already open: fold the dropped files in and, if connected, send them.
        if (_transfer is { Receive: false, Finished: false } current)
        {
            current.Paths = [.. current.Paths, .. paths];
            current.Done = [.. current.Done, .. new bool[paths.Count]];
            current.Detail = Loc.T("点击“连接”建立会话，确认后再点“发送”传输。", "Connect to open the session, then Send.");
            if (current.Hidden) { ShowTransferPage(); return; }
            // 页面已在前台：只重建左侧待发送列表，不刷新整页。
            RefreshTransferFiles();
            if (_app.ClipboardSync.FileTransferConnected) { SendFiles(); return; }
            return;
        }
        _transfer = new TransferCardState
        {
            Paths = [.. paths],
            Done = new bool[paths.Count],
            Title = Loc.T("文件传输会话", "File transfer session"),
            Detail = Loc.T("正在读取已关联设备…", "Reading linked devices…"),
        };
        ShowTransferPage();
        _ = LoadTransferPeersAsync(_transfer);
    }

    private async Task LoadTransferPeersAsync(TransferCardState state)
    {
        try
        {
            var peers = _app.ClipboardSync.CachedWeTypePeers;
            if (peers.Count == 0)
                peers = await _app.ClipboardSync.WeTypePeersAsync(_app.Lifetime.Token);
            if (!ReferenceEquals(state, _transfer)) return;
            state.Peers = peers;
            if (peers.Count == 0)
            {
                state.Phase = FileTransferPhase.Failed;
                state.Finished = true;
                state.Title = Loc.T("没有可发送的设备", "No device to send to");
                state.Detail = Loc.T("请先在设置中关联微信输入法。", "Link WeType in settings first.");
            }
            else
            {
                // Files went to a device before: that one is selected again. Only a first send, or a device no longer
                // in the group, waits for the person to choose.
                var recent = _app.ClipboardSync.LastFilePeer is { } uin
                    ? peers.FirstOrDefault(peer => peer.Uin == uin) : null;
                if (recent is not null)
                {
                    state.TargetUin = recent.Uin;
                }
                else
                {
                    // The device picked before is no longer in the group: it is not counted any more, and the person chooses again.
                    if (_app.ClipboardSync.LastFilePeer is not null) _app.ClipboardSync.LastFilePeer = null;
                    state.TargetUin = peers.FirstOrDefault(peer => peer.Platform == 1)?.Uin ?? peers[0].Uin;
                }
                // Connecting and sending are separate events: no file leaves until Connect then Send.
                state.Detail = Loc.T("点击“连接”建立会话，确认后再点“发送”传输。", "Connect to open the session, then Send.");
            }
            RenderShelf();
        }
        catch (Exception error)
        {
            if (!ReferenceEquals(state, _transfer)) return;
            state.Phase = FileTransferPhase.Failed;
            state.Finished = true;
            state.Title = Loc.T("读取设备失败", "Could not read devices");
            state.Detail = error.Message;
            RenderShelf();
        }
    }

    private void StartTransfer()
    {
        if (_transfer is not { Receive: false, Finished: false, Started: false } state) return;
        var peer = state.Peers.FirstOrDefault(item => item.Uin == state.TargetUin)
            ?? state.Peers.FirstOrDefault(item => item.Platform == 1) ?? state.Peers.FirstOrDefault();
        if (peer is null) return;
        state.TargetUin = peer.Uin;
        state.Phase = FileTransferPhase.Preparing;
        state.Started = true;
        state.Title = Loc.T("准备发送", "Preparing");
        state.Detail = Loc.T("正在连接微信输入法…", "Connecting to WeType…");
        state.Fraction = 0;
        RenderShelf();
        _ = SendAsync(state, peer);
    }

    // Connect is its own event: it opens the file session with the phone (the phone confirms) but sends nothing.
    private async void ConnectToDevice()
    {
        var state = _transfer;
        if (state is not { Receive: false }) return;
        // Already connected to this device: nothing to rebuild, just refresh the footer so it shows「断开」.
        if (_app.ClipboardSync.FileTransferConnected) { if (ReferenceEquals(state, _transfer)) RenderShelf(); return; }
        if (state.TargetUin == 0) return;
        // A connect is already in flight (waiting for the phone): a second click re-shows the attempt instead of failing it.
        if (_app.ClipboardSync.FileTransferConnecting) { if (ReferenceEquals(state, _transfer)) RenderShelf(); return; }
        state.Started = true; state.Finished = false; state.Fraction = 0;
        state.Title = Loc.T("正在连接", "Connecting");
        state.Detail = Loc.T("正在建立文件会话…", "Opening a file session…");
        RenderShelf();
        await _app.ClipboardSync.OpenWeTypeFileTransferAsync(state.TargetUin, update => ApplyTransferUpdate(update, incoming: false));
        // The session is open (or failed) now: rebuild so the toggle turns into「断开」and the Send button lights up.
        if (ReferenceEquals(state, _transfer)) RenderShelf();
    }

    private async void DisconnectSend()
    {
        var state = _transfer;
        if (state is not { Receive: false }) return;
        await _app.ClipboardSync.CloseOpenFileTransferAsync();
        if (!ReferenceEquals(state, _transfer)) return;
        state.Started = false; state.Finished = false; state.Phase = FileTransferPhase.Preparing; state.Fraction = 0;
        state.Title = Loc.T("文件传输会话", "File transfer session");
        state.Detail = Loc.T("点击“连接”建立会话，确认后再点“发送”传输。", "Connect to open the session, then Send.");
        RenderShelf();
    }

    // Sending is a separate event and only works once the session is open: either the Send button or a Ctrl+drag.
    private void SendFiles()
    {
        var state = _transfer;
        if (state is null || state.Sending || state.Paths.Count == 0 || state.TargetUin == 0) return;
        var peer = state.Peers.FirstOrDefault(item => item.Uin == state.TargetUin)
            ?? state.Peers.FirstOrDefault(item => item.Platform == 1) ?? state.Peers.FirstOrDefault();
        if (peer is null || !_app.ClipboardSync.FileTransferConnected) return;
        state.Started = true; state.Finished = false; state.Fraction = 0;
        state.Phase = FileTransferPhase.Preparing;
        state.Title = Loc.T("正在发送到 ", "Sending to ") + peer.Name;
        state.Detail = Loc.T("正在连接微信输入法…", "Connecting to WeType…");
        RenderShelf();
        _ = SendAsync(state, peer);
    }

    private async Task SendAsync(TransferCardState state, WeTypePeer peer)
    {
        try
        {
            state.Sending = true;
            await _app.ClipboardSync.SendWeTypeFilesAsync(state.Paths, peer.Uin, update => ApplyTransferUpdate(update, incoming: false));
        }
        catch (Exception error)
        {
            if (ReferenceEquals(state, _transfer) && !state.Finished)
                ApplyTransferUpdate(new FileTransferUpdate(FileTransferPhase.Failed, Loc.T("发送失败", "Send failed"), error.Message, 0, state.Debug), incoming: false);
        }
        finally { state.Sending = false; if (ReferenceEquals(state, _transfer)) RenderShelf(); }
    }

    private void ApplyTransferUpdate(FileTransferUpdate update, bool incoming)
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(() => ApplyTransferUpdate(update, incoming)); return; }
        // A receiving update with Expand=false is a stale invitation that never connected: it must not open the board.
        if (incoming && _transfer is null && !update.Expand) return;
        if (_transfer is null)
        {
            if (!incoming) return;
            _transfer = new TransferCardState();
            ShowTransferPage();
            _ = LoadTransferPeersAsync(_transfer);
        }
        var state = _transfer;
        if (incoming && state.Hidden && update.Phase is FileTransferPhase.Preparing or FileTransferPhase.Waiting or FileTransferPhase.Receiving)
        {
            state.Hidden = false;
            ShowTransferPage();
        }
        if (incoming)
        {
            // 接收完成时把该文件记入内存历史，供右侧列表展示（不扫描目录、不落盘）。
            if (update.Phase == FileTransferPhase.Completed && !string.IsNullOrEmpty(update.ReceivedPath))
                AppendReceive(update.ReceivedPath);
            state.ReceiveDetail = update.Title + " · " + update.Detail;
            if (state.Hidden) return;
            // 页面已在前台：只刷新右侧接收列表，让新收到的文件立即出现，不做整页重建。
            if (_receiveListHost is null) RenderShelf();
            else RefreshReceiveList();
            return;
        }
        state.Phase = update.Phase;
        state.Title = update.Title;
        state.Detail = update.Detail;
        state.Debug = update.Debug;
        // A failure is why the person came: surface the debug trace right away instead of hiding it behind 详情.
        if (update.Phase == FileTransferPhase.Failed) state.Details = true;
        state.Fraction = update.Fraction;
        bool finished = update.Phase is FileTransferPhase.Completed or FileTransferPhase.Failed;
        bool wasFinished = state.Finished;
        state.Finished = finished;
        bool fileDone = false;
        if (!incoming && update.FileIndex is { } idx && idx >= 0 && idx < state.Done.Length && !state.Done[idx])
        {
            state.Done[idx] = true;
            fileDone = true;
        }
        if (state.Hidden) return;
        if (finished && !wasFinished) { RenderShelf(); return; }
        // A finished file turns green: rebuild the card so the row reflects it.
        if (fileDone) { RenderShelf(); return; }
        UpdateTransferControls();
    }

    private void ShowTransferPage()
    {
        _shelfDetail = null;
        _shelfEdit = null;
        if (!IsShelfPage) SelectPage(DeckPage.Clipboard, remember: false);
        SetExpanded(true);
        RenderShelf();
    }

    private void DismissTransfer()
    {
        if (_transfer is not { } state) return;

        if (!ReferenceEquals(state, _transfer)) return;
        _transfer.Hidden = true;
        // Closing a finished send is what releases the phone's receive slot.

        RenderShelf();
    }

    // A live page updates in place: rebuilding the page for every chunk would make the file list flicker.
    private void UpdateTransferControls()
    {
        if (_transfer is not { } state) return;
        if (_transferProgress is null) { RenderShelf(); return; }
        if (_transferDetail is not null) _transferDetail.Text = state.Detail;
        if (_transferProgress is not null) _transferProgress.Value = Math.Clamp(state.Fraction, 0, 1);
        if (_transferPercent is not null) _transferPercent.Text = state.Fraction.ToString("P0");
        if (_transferDebug is not null && _transferDebug.Text != state.Debug) _transferDebug.Text = state.Debug;
        if (_transferDebug is not null) _transferDebug.Visibility = state.Details ? Visibility.Visible : Visibility.Collapsed;
    }

    // One flat page with a hairline dividing the device choice from the file list; no islands, it is one continuous task.
    private FrameworkElement TransferView(ShelfLayout layout)
    {
        var state = _transfer!;
        var secondary = UI.Brush(UI.Secondary);

        _transferDetail = new TextBlock
        {
            Text = state.Detail, FontFamily = UI.PanelFont, FontSize = layout.Aux, Foreground = secondary,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        };

        bool narrow = ContentCanvas.Width < 420;
        // 布局参考音乐页：第 0 行是占满剩余高度的列表区（内部滚动），第 1 行是固定显示的进度条+控制按钮+调试区。
        // 底部高度变化（例如「详情」展开调试区）时，列表区高度随之自适应。
        var root = new Grid { Margin = new Thickness(6, 0, 6, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        // 发送与接收在同一页：左侧为发送，右侧为本机接收到的文件；窄窗口改为上下排列。
        var send = SendPanel(state, layout);
        var received = ReceivePanel(state, layout);
        if (narrow)
        {
            body.Children.Add(send);
            body.Children.Add(received);
        }
        else
        {
            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            columns.Children.Add(send);
            var divider = new Border { Width = 1, Background = UI.Brush(UI.Hairline), Margin = new Thickness(12, 0, 12, 0) };
            Grid.SetColumn(divider, 1); columns.Children.Add(divider);
            Grid.SetColumn(received, 2); columns.Children.Add(received);
            body.Children.Add(columns);
        }
        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetRow(scroll, 0); root.Children.Add(scroll);

        var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 12) };
        Grid.SetRow(bottom, 1); root.Children.Add(bottom);
        var progressRow = new Grid();
        progressRow.ColumnDefinitions.Add(new ColumnDefinition());
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _transferProgress = new ProgressBar
        {
            Height = 4, Minimum = 0, Maximum = 1, Value = Math.Clamp(state.Fraction, 0, 1),
            Foreground = UI.Brush(UI.Accent), Background = UI.Brush(UI.Track), BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        progressRow.Children.Add(_transferProgress);
        _transferPercent = new TextBlock
        {
            Text = state.Fraction.ToString("P0"), FontFamily = UI.PanelFont, FontSize = layout.Aux, Foreground = secondary,
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_transferPercent, 1); progressRow.Children.Add(_transferPercent);
        bottom.Children.Add(progressRow);

        bottom.Children.Add(TransferFooter(state, secondary));

        _transferDebug = new TextBox
        {
            Text = state.Debug, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 150, FontFamily = new FontFamily("Consolas"),
            FontSize = 11, Background = UI.Brush(UI.FillSelected), Foreground = secondary, BorderThickness = new Thickness(0),
            Padding = new Thickness(6), Margin = new Thickness(0, 8, 0, 0),
            Visibility = state.Details ? Visibility.Visible : Visibility.Collapsed,
        };
        bottom.Children.Add(_transferDebug);

        return root;
    }

    // ---- Files section: what is going ----
    private FrameworkElement TransferFiles(TransferCardState state, ShelfLayout layout)
    {
        _transferFilesHost = new StackPanel();
        BuildTransferFiles(state, layout);
        return _transferFilesHost;
    }

    private void BuildTransferFiles(TransferCardState state, ShelfLayout layout)
    {
        var host = _transferFilesHost ??= new StackPanel();
        host.Children.Clear();
        var list = new StackPanel();
        long total = 0;
        int folders = 0;
        for (int i = 0; i < state.Paths.Count; i++)
        {
            string path = state.Paths[i];
            bool folder = Directory.Exists(path);
            bool done = state.Done.Length > i && state.Done[i];
            long size = 0;
            if (folder) folders++;
            else
            {
                try { size = new FileInfo(path).Length; } catch (Exception) { }
                total += size;
            }
            var name = new TextBlock
            {
                Text = System.IO.Path.GetFileName(path) + (folder ? Loc.T("（文件夹）", " (folder)") : ""),
                FontFamily = UI.PanelFont, FontSize = layout.Aux, Foreground = UI.Brush(done ? "#3FA37C" : UI.Primary),
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            };
            var figure = new TextBlock
            {
                Text = folder ? Loc.T("文件夹", "Folder") : SizeText(size), FontFamily = UI.PanelFont, FontSize = layout.Aux,
                Foreground = UI.Brush(done ? "#3FA37C" : UI.Tertiary), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(name);
            Grid.SetColumn(figure, 1); row.Children.Add(figure);
            list.Children.Add(row);
        }
        // 标题行：左侧为「发送文件」，右侧汇总文件总数与总大小。
        var head = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(SectionLabel(Loc.T("发送文件", "Files"), layout, verticalAlignCenter: true));
        string summary = Loc.T("共 " + state.Paths.Count + " 项", state.Paths.Count + " item(s)");
        if (total > 0) summary += " · " + SizeText(total);
        if (folders > 0) summary += Loc.T(" · 含 " + folders + " 个文件夹", " · " + folders + " folder(s)");
        var summaryText = new TextBlock
        {
            Text = summary, FontFamily = UI.PanelFont, FontSize = layout.Aux, Foreground = UI.Brush(UI.Secondary),
            Margin = new Thickness(8, 0, 0, 6), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(summaryText, 1); head.Children.Add(summaryText);
        host.Children.Add(head);
        host.Children.Add(new ScrollViewer
        {
            MaxHeight = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = list,
        });
    }

    // 只重建左侧待发送列表（含标题行的个数汇总），不触碰页面其余部分。
    private void RefreshTransferFiles()
    {
        if (_transfer is not { Receive: false } state) return;
        BuildTransferFiles(state, _shelfLayout);
    }

    private static FrameworkElement SectionLabel(string text, ShelfLayout layout, bool verticalAlignCenter = false) => new TextBlock
    {
        Text = text, FontFamily = UI.PanelFont, FontSize = layout.Aux, FontWeight = FontWeights.SemiBold,
        Foreground = UI.Brush(UI.Secondary), Margin = new Thickness(0, 0, 0, 6),
        VerticalAlignment = verticalAlignCenter ? VerticalAlignment.Center : VerticalAlignment.Top,
    };

    // A device's phone or computer mark in the header picker, drawn with the devices' own glyphs in one ink.
    private sealed class PeerGlyph(DeviceKind kind, string ink) : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawing)
            => DeviceGlyphs.Draw(drawing, kind, new Rect(0, 0, ActualWidth, ActualHeight), ink);
    }

    // ---- Header device picker: the transfer lives in the toolbar, so the target device is chosen from the header. ----
    private bool FileTransferShown => _transfer is { Hidden: false };
    private void UpdateShelfDeviceButton()
    {
        if (ShelfDeviceButton.Visibility != Visibility.Visible || _transfer is not { } state) return;
        var peer = !state.Receive ? state.Peers.FirstOrDefault(p => p.Uin == state.TargetUin) : null;
        bool selectable = !state.Receive && !state.Started && !state.Finished;
        ShelfDeviceLabel.Text = peer?.Name ?? (state.Receive ? Loc.T("接收中", "Receiving") : Loc.T("选择设备", "Choose a device"));
        ShelfDeviceChevron.Visibility = selectable ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShelfDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_transfer is not { Receive: false, Hidden: false } state || state.Peers.Count == 0) return;
        if (ShelfDeviceButton.ContextMenu is { IsOpen: true } open) { open.IsOpen = false; return; }
        var menu = new ContextMenu { Style = (Style)FindResource("MediaSourceMenu") };
        foreach (var peer in state.Peers)
        {
            bool selected = peer.Uin == state.TargetUin;
            // 每行自带其设备标记（手机或电脑）；当前选中项用主题强调色（蓝）标注，其余用次要墨色。
            string ink = selected ? UI.Accent : UI.Secondary;
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new PeerGlyph(peer.Platform == 1 ? DeviceKind.Other : DeviceKind.Phone, ink)
                { Width = 13, Height = 13, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock
                { Text = peer.Name, FontFamily = UI.PanelFont, FontSize = 12.5, Foreground = UI.Brush(ink), VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem { Style = (Style)FindResource("ShelfDeviceMenuItem"), Header = header };
            item.Click += async (_, _) =>
            {
                if (_transfer is not { Receive: false } current || !ReferenceEquals(current, state)) return;
                if (current.TargetUin != peer.Uin)
                {
                    // 取消仍在尝试的连接（手机未确认），不再对原设备继续连接；
                    // 已建立的会话则主动断开，再改选新设备。
                    _app.ClipboardSync.CancelConnectingFile();
                    if (current.Started || current.Finished)
                    {
                        await _app.ClipboardSync.CloseOpenFileTransferAsync();
                        if (!ReferenceEquals(current, _transfer)) return;
                        current.Started = false; current.Finished = false; current.Phase = FileTransferPhase.Preparing; current.Fraction = 0;
                        current.Title = Loc.T("文件传输会话", "File transfer session");
                        current.Detail = Loc.T("选择目标设备后开始。手机端需要确认接收。", "Choose a target; confirm on the phone.");
                    }
                }
                current.TargetUin = peer.Uin; _app.ClipboardSync.LastFilePeer = peer.Uin;
                RenderShelf();
            };
            menu.Items.Add(item);
        }
        ShelfDeviceButton.ContextMenu = menu; menu.PlacementTarget = ShelfDeviceButton;
        // The picker hangs from the header's right edge, so the menu opens below the button, right-aligned to it.
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, targetSize.Height), PopupPrimaryAxis.Vertical)];
        menu.IsOpen = true;
    }

    // ---- Send & receive on one page: the left panel sends, the right lists what the PC received. ----
    private FrameworkElement SendPanel(TransferCardState state, ShelfLayout layout)
    {
        var send = new StackPanel();
        // 目标设备在顶栏选择，当前设备在底部左栏展示；左侧面板只列待发送文件。
        send.Children.Add(TransferFiles(state, layout));
        return send;
    }

    private FrameworkElement ReceivePanel(TransferCardState state, ShelfLayout layout)
    {
        _receiveListHost = new StackPanel();
        BuildReceiveList(state, layout);
        return _receiveListHost;
    }

    private void BuildReceiveList(TransferCardState state, ShelfLayout layout)
    {
        var host = _receiveListHost ??= new StackPanel();
        host.Children.Clear();
        var list = new StackPanel();
        // 接收历史是内存记录，不读文件目录也不落盘。刷新时每条只检查文件是否还在原位。
        var history = _receiveHistory;
        // 标题行：右侧显示接收记录条数。
        var head = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(SectionLabel(Loc.T("接收", "Receive"), layout, verticalAlignCenter: true));
        var summaryText = new TextBlock
        {
            Text = Loc.T("共 " + history.Count + " 项", history.Count + " item(s)"),
            FontFamily = UI.PanelFont, FontSize = layout.Aux, Foreground = UI.Brush(UI.Secondary),
            Margin = new Thickness(8, 0, 0, 6), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(summaryText, 1); head.Children.Add(summaryText);
        host.Children.Add(head);
        if (history.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = Loc.T("还没有收到的文件", "Nothing received yet"),
                FontFamily = UI.PanelFont, FontSize = layout.Aux, Foreground = UI.Brush(UI.Tertiary),
            });
        }
        else foreach (var r in history)
        {
            bool exists = File.Exists(r.Path);
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = r.Name, FontFamily = UI.PanelFont, FontSize = layout.Aux, Foreground = UI.Brush(UI.Primary), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };  // 文件名不换行，超长用省略号截断
            var time = new TextBlock { Text = r.At.ToString("MM-dd HH:mm"), FontFamily = UI.PanelFont, FontSize = layout.Aux, Foreground = UI.Brush(UI.Tertiary), TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            // 文件已被移动/删除则标记为「已移走」，否则说明仍在接收目录。
            var statusText = new TextBlock { Text = Loc.T(exists ? "在目录" : "已移走", exists ? "Saved" : "Moved"), FontFamily = UI.PanelFont, FontSize = layout.Aux, Foreground = UI.Brush(exists ? UI.Tertiary : "#B5473F"), TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(name);
            Grid.SetColumn(time, 1); row.Children.Add(time);
            Grid.SetColumn(statusText, 2); row.Children.Add(statusText);
            list.Children.Add(row);
        }
        // 接收状态（正在接收 / 完成 / 失败）由页面顶部的标题与详情实时显示，不混入右侧历史列表。
        host.Children.Add(list);
    }

    // 接收进度/完成时只重建右侧接收列表（含个数汇总），立即反映新收到的文件，不触碰页面其余部分。
    private void RefreshReceiveList()
    {
        if (_transfer is not { } state) return;
        BuildReceiveList(state, _shelfLayout);
    }

    private FrameworkElement TransferFooter(TransferCardState state, System.Windows.Media.Brush secondary)
    {
        // 左：链接状态 + 当前连接的设备；右：连接/断开、查看详情等按钮。
        var left = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var connection = new TextBlock { FontSize = 12 };
        Func<string> status; Func<System.Windows.Media.Brush> color;
        if (!state.Receive)
        {
            status = () => _app.ClipboardSync.FileConnectionDisplay;
            color = () => UI.Brush(_app.ClipboardSync.FileConnectionDisplay switch
            {
                var s when s.StartsWith("已连接") => "#3FA37C",
                "已断开" => "#D9534F",
                _ => UI.Secondary,
            });
        }
        else
        {
            status = () => _app.ClipboardSync.ReceiveConnectionStatus;
            color = () => UI.Brush(status() switch
            {
                string s when s.Contains("接收失败") => "#D9534F",
                string s when s.StartsWith("已连接") => "#3FA37C",
                _ => UI.Secondary,
            });
        }
        connection.Text = status(); connection.Foreground = color();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => { connection.Text = status(); connection.Foreground = color(); };
        connection.Loaded += (_, _) => timer.Start();
        connection.Unloaded += (_, _) => timer.Stop();
        // 只显示当前选择的/当前连接的设备（左上角不再显示设备名）：发送时为目标，接收时为对端。
        // 链接状态放在设备徽章的右边。
        var currentName = !state.Receive
            ? state.Peers.FirstOrDefault(p => p.Uin == state.TargetUin)?.Name ?? Loc.T("选择设备", "Choose a device")
            : Loc.T("手机 → 本机", "Phone → PC");
        var device = new Border
        {
            Background = UI.Brush(UI.FillSelected), CornerRadius = new CornerRadius(Look.Corner(4)),
            Padding = new Thickness(8, 3, 8, 3), Child = new TextBlock
            { Text = currentName, FontFamily = UI.PanelFont, FontSize = 11, Foreground = UI.Brush(UI.Secondary) },
        };
        connection.VerticalAlignment = VerticalAlignment.Center; connection.Margin = new Thickness(8, 0, 0, 0);
        var leftRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        leftRow.Children.Add(device);
        leftRow.Children.Add(connection);
        left.Children.Add(leftRow);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
        // 查看调试信息：详情开关控制下方详细面板；其中的内容可直接选中复制。
        var details = PillButton(state.Details ? Loc.T("收起详情", "Hide details") : Loc.T("详情", "Details"), false, () =>
        {
            state.Details = !state.Details;
            RenderShelf();
        });
        details.Margin = new Thickness(0, 0, 6, 0);
        buttons.Children.Add(details);
        if (!state.Receive)
        {
            // 发送与连接是两个独立事件：先「连接」建立会话（不传文件），再点「发送」才把列表里的文件传过去。
            bool joined = _app.ClipboardSync.FileTransferConnected;
            var send = PillButton(Loc.T("发送", "Send"), false, SendFiles);
            send.Margin = new Thickness(0, 0, 6, 0);
            if (!joined || state.Paths.Count == 0 || state.Sending) { send.IsEnabled = false; send.Opacity = .4; }
            buttons.Children.Add(send);
            var toggle = PillButton(joined ? Loc.T("断开", "Disconnect") : Loc.T("连接", "Connect"), !joined, joined ? DisconnectSend : ConnectToDevice);
            toggle.Margin = new Thickness(0, 0, 6, 0);
            buttons.Children.Add(toggle);
        }
        // 关闭与连接/断开同处右下角。
        var close = PillButton(Loc.T("关闭", "Close"), false, DismissTransfer);
        close.Margin = new Thickness(6, 0, 0, 0);
        buttons.Children.Add(close);

        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(left);
        Grid.SetColumn(buttons, 1); footer.Children.Add(buttons);
        return footer;
    }

    // A phone or a computer, the same Material Symbols outline the devices page uses.
    private static FrameworkElement DeviceIcon(DeviceKind kind, double size)
    {
        var group = new DrawingGroup();
        using (var drawing = group.Open()) DeviceGlyphs.Draw(drawing, kind, new Rect(0, 0, size, size), UI.Primary);
        group.Freeze();
        return new Image
        {
            Source = new DrawingImage(group), Width = size, Height = size,
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static string SizeText(long bytes) => bytes >= 1_000_000_000 ? (bytes / 1_000_000_000.0).ToString("0.0") + " GB"
        : bytes >= 1_000_000 ? (bytes / 1_000_000.0).ToString("0.0") + " MB"
        : bytes >= 1_000 ? (bytes / 1_000.0).ToString("0.0") + " KB" : bytes + " B";
}