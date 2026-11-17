using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
        public bool Receive;
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
    private TextBlock? _transferTitle;
    private TextBlock? _transferDetail;
    private ProgressBar? _transferProgress;
    private TextBlock? _transferPercent;
    private TextBox? _transferDebug;
    private Button? _transferStart;

    // Ctrl + drop, or a file invite from the phone: the page comes forward and the transfer page does the rest.
    private void BeginFileTransferSend(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        _transfer = new TransferCardState
        {
            Paths = [.. paths],
            Done = new bool[paths.Count],
            Title = Loc.T("发送到微信输入法", "Send to WeType"),
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
                // Files went to a device before: that one is used again at once. Only a first send, or a device no longer
                // in the group, waits for the person to choose.
                var recent = _app.ClipboardSync.LastFilePeer is { } uin
                    ? peers.FirstOrDefault(peer => peer.Uin == uin) : null;
                if (recent is not null)
                {
                    state.TargetUin = recent.Uin;
                    StartTransfer();
                    return;
                }
                // The device picked before is no longer in the group: it is not counted any more, and the person chooses again.
                if (_app.ClipboardSync.LastFilePeer is not null) _app.ClipboardSync.LastFilePeer = null;
                state.TargetUin = peers.FirstOrDefault(peer => peer.Platform == 1)?.Uin ?? peers[0].Uin;
                state.Detail = Loc.T("选择目标设备后开始。手机端需要确认接收。", "Choose a target; confirm on the phone.");
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

    private async Task SendAsync(TransferCardState state, WeTypePeer peer)
    {
        try
        {
            await _app.ClipboardSync.SendWeTypeFilesAsync(state.Paths, peer.Uin, update => ApplyTransferUpdate(update, incoming: false));
        }
        catch (Exception error)
        {
            if (ReferenceEquals(state, _transfer) && !state.Finished)
                ApplyTransferUpdate(new FileTransferUpdate(FileTransferPhase.Failed, Loc.T("发送失败", "Send failed"), error.Message, 0, state.Debug), incoming: false);
        }
    }

    private void ApplyTransferUpdate(FileTransferUpdate update, bool incoming)
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(() => ApplyTransferUpdate(update, incoming)); return; }
        if (_transfer is null)
        {
            if (!incoming) return;
            _transfer = new TransferCardState { Receive = true };
            ShowTransferPage();
        }
        else if (incoming && !_transfer.Receive)
        {
            // A phone transfer while our own send is running: keep the send page, it is the one the person started.
            if (!_transfer.Finished) return;
            _transfer = new TransferCardState { Receive = true };
            ShowTransferPage();
        }
        else if (incoming && _transfer.Finished && update.Phase is FileTransferPhase.Preparing or FileTransferPhase.Waiting or FileTransferPhase.Receiving)
        {
            _transfer = new TransferCardState { Receive = true };
            ShowTransferPage();
        }

        var state = _transfer;
        if (incoming && state.Hidden && update.Phase is FileTransferPhase.Preparing or FileTransferPhase.Waiting or FileTransferPhase.Receiving)
        {
            state.Hidden = false;
            ShowTransferPage();
        }
        state.Phase = update.Phase;
        state.Title = update.Title;
        state.Detail = update.Detail;
        state.Debug = update.Debug;
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

    private async void DismissTransfer()
    {
        if (_transfer is not { } state) return;
        if (state.Finished && !state.Receive) await _app.ClipboardSync.CloseOpenFileTransferAsync();
        if (!ReferenceEquals(state, _transfer)) return;
        if (state.Finished) _transfer = null;
        else _transfer.Hidden = true;
        // Closing a finished send is what releases the phone's receive slot.

        RenderShelf();
    }

    // A live page updates in place: rebuilding the page for every chunk would make the file list flicker.
    private void UpdateTransferControls()
    {
        if (_transfer is not { } state) return;
        if (_transferTitle is null) { RenderShelf(); return; }
        _transferTitle.Text = state.Title;
        _transferTitle.Foreground = state.Phase switch
        {
            FileTransferPhase.Failed => UI.Brush("#D9534F"),
            FileTransferPhase.Completed => UI.Brush("#3FA37C"),
            _ => UI.Brush(UI.Primary),
        };
        if (_transferDetail is not null) _transferDetail.Text = state.Detail;
        if (_transferProgress is not null) _transferProgress.Value = Math.Clamp(state.Fraction, 0, 1);
        if (_transferPercent is not null) _transferPercent.Text = state.Fraction.ToString("P0");
        if (_transferDebug is not null && _transferDebug.Text != state.Debug) _transferDebug.Text = state.Debug;
        if (_transferStart is not null && (state.Finished || state.Started)) _transferStart.Visibility = Visibility.Collapsed;
    }

    // One flat page with a hairline dividing the device choice from the file list; no islands, it is one continuous task.
    private FrameworkElement TransferView(ShelfLayout layout)
    {
        var state = _transfer!;
        var secondary = UI.Brush(UI.Secondary);

        _transferTitle = new TextBlock
        {
            Text = state.Title, FontFamily = UI.PanelFont, FontSize = layout.Body + 2, FontWeight = FontWeights.SemiBold,
            Foreground = state.Phase switch
            {
                FileTransferPhase.Failed => UI.Brush("#D9534F"),
                FileTransferPhase.Completed => UI.Brush("#3FA37C"),
                _ => UI.Brush(UI.Primary),
            },
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _transferDetail = new TextBlock
        {
            Text = state.Detail, FontFamily = UI.PanelFont, FontSize = layout.Aux, Foreground = secondary,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(_transferTitle);
        var close = PillButton(Loc.T("关闭", "Close"), false, DismissTransfer);
        close.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(close, 1); head.Children.Add(close);

        bool narrow = ContentCanvas.Width < 420;
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(6, layout.Stacked ? 8 : 14, 6, 16),
        };
        content.Children.Add(_transferDetail);
        if (!state.Receive)
        {
            var connection = new TextBlock { Foreground = UI.Brush(UI.Secondary), FontSize = 12,
                Margin = new Thickness(0, 4, 0, 4), Text = _app.ClipboardSync.FileConnectionDisplay };
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) => connection.Text = _app.ClipboardSync.FileConnectionDisplay;
            connection.Loaded += (_, _) => timer.Start();
            connection.Unloaded += (_, _) => timer.Stop();
            content.Children.Add(connection);
        }

        if (!state.Receive)
        {
            // Files on the left, the device choice on the right, split by a vertical hairline and no islands.
            var files = TransferFiles(state, layout);
            var right = new StackPanel { Children = { TransferDevices(state, layout), TransferActions(state) } };
            if (narrow)
            {
                content.Children.Add(files);
                content.Children.Add(new Border { Height = 1, Background = UI.Brush(UI.Hairline), Margin = new Thickness(0, 12, 0, 12) });
                content.Children.Add(right);
            }
            else
            {
                var columns = new Grid { Margin = new Thickness(0, 12, 0, 0) };
                columns.ColumnDefinitions.Add(new ColumnDefinition());
                columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                columns.ColumnDefinitions.Add(new ColumnDefinition());
                columns.Children.Add(files);
                var divider = new Border { Width = 1, Background = UI.Brush(UI.Hairline), Margin = new Thickness(12, 0, 12, 0) };
                Grid.SetColumn(divider, 1); columns.Children.Add(divider);
                Grid.SetColumn(right, 2); columns.Children.Add(right);
                content.Children.Add(columns);
            }
        }
        else content.Children.Add(TransferActions(state));

        var progressRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
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
        content.Children.Add(progressRow);

        _transferDebug = new TextBox
        {
            Text = state.Debug, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 150, FontFamily = new FontFamily("Consolas"),
            FontSize = 11, Background = UI.Brush(UI.FillSelected), Foreground = secondary, BorderThickness = new Thickness(0),
            Padding = new Thickness(6), Margin = new Thickness(0, 8, 0, 0),
            Visibility = state.Details ? Visibility.Visible : Visibility.Collapsed,
        };
        content.Children.Add(_transferDebug);

        // The title and the close button sit on the bottom row of the card.
        head.Margin = new Thickness(0, 10, 0, 0);
        content.Children.Add(head);

        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = content,
        };
    }

    // ---- Files section: what is going ----
    private FrameworkElement TransferFiles(TransferCardState state, ShelfLayout layout)
    {
        var panel = new StackPanel();
        panel.Children.Add(SectionLabel(Loc.T("发送文件", "Files"), layout));
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
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = list,
        });
        string summary = Loc.T("共 " + state.Paths.Count + " 项", state.Paths.Count + " item(s)");
        if (total > 0) summary += " · " + SizeText(total);
        if (folders > 0) summary += Loc.T(" · 含 " + folders + " 个文件夹", " · " + folders + " folder(s)");
        panel.Children.Add(new TextBlock
        {
            Text = summary, FontFamily = UI.PanelFont, FontSize = layout.Aux, Foreground = UI.Brush(UI.Secondary),
            Margin = new Thickness(0, 6, 0, 0),
        });
        return panel;
    }

    private static FrameworkElement SectionLabel(string text, ShelfLayout layout) => new TextBlock
    {
        Text = text, FontFamily = UI.PanelFont, FontSize = layout.Aux, FontWeight = FontWeights.SemiBold,
        Foreground = UI.Brush(UI.Secondary), Margin = new Thickness(0, 0, 0, 6),
    };

    // ---- Devices section: where it goes, and the actions ----
    // The devices of the paired group, as rows that can be picked. A send shows them until it starts; afterwards only the
    // one it went to stays. Only the platform and the name are shown: the Uin is an implementation detail.
    private FrameworkElement TransferDevices(TransferCardState state, ShelfLayout layout)
    {
        var panel = new StackPanel();
        panel.Children.Add(SectionLabel(Loc.T("发送到", "Send to"), layout));
        if (state.Peers.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = state.Finished ? Loc.T("没有可发送的设备", "No device to send to") : Loc.T("正在读取已关联设备…", "Reading linked devices…"),
                FontFamily = UI.PanelFont, FontSize = layout.Aux, Foreground = UI.Brush(UI.Tertiary), TextWrapping = TextWrapping.Wrap,
            });
            return panel;
        }

        var accent = Look.ColorOf("accent");
        var selectedFill = new SolidColorBrush(Color.FromArgb(24, accent.R, accent.G, accent.B));
        bool only = state.Started || state.Finished;
        foreach (var peer in state.Peers)
        {
            bool selected = peer.Uin == state.TargetUin;
            if (only && !selected) continue;
            var name = new TextBlock
            {
                Text = peer.Name, FontFamily = UI.PanelFont, FontSize = layout.Body, FontWeight = FontWeights.SemiBold,
                Foreground = UI.Brush(UI.Primary), TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var texts = new StackPanel { Children = { name }, VerticalAlignment = VerticalAlignment.Center };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(DeviceIcon(peer.Platform == 1 ? DeviceKind.Phone : DeviceKind.Other, 22));
            Grid.SetColumn(texts, 1); grid.Children.Add(texts);
            var mark = new TextBlock
            {
                Text = selected ? "✓" : "", FontFamily = UI.PanelFont, FontSize = 14, Foreground = UI.Brush(UI.Accent),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(mark, 2); grid.Children.Add(mark);
            var row = new Border
            {
                Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 6),
                CornerRadius = new CornerRadius(Look.Corner(8)), Child = grid,
                Background = selected ? selectedFill : UI.Brush(UI.Fill),
                BorderBrush = selected ? UI.Brush(UI.Accent) : UI.Brush(UI.Hairline),
                BorderThickness = new Thickness(1), Cursor = only ? Cursors.Arrow : Cursors.Hand,
            };
            if (!only) row.MouseLeftButtonUp += (_, e) => { e.Handled = true; state.TargetUin = peer.Uin; _app.ClipboardSync.LastFilePeer = peer.Uin; RenderShelf(); };
            panel.Children.Add(row);
        }
        return panel;
    }

    private FrameworkElement TransferActions(TransferCardState state)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        if (!state.Receive && !state.Finished && !state.Started)
        {
            _transferStart = PillButton(Loc.T("开始传送", "Send"), true, StartTransfer);
            _transferStart.Margin = new Thickness(0, 0, 6, 0);
            actions.Children.Add(_transferStart);
        }
        if (state.Finished && !state.Receive)
        {
            var disconnect = PillButton(Loc.T("断开连接", "Disconnect"), false, async () =>
            {
                await _app.ClipboardSync.CloseOpenFileTransferAsync();
                if (ReferenceEquals(state, _transfer)) UpdateTransferControls();
            });
            disconnect.Margin = new Thickness(0, 0, 6, 0);
            actions.Children.Add(disconnect);
        }
        if (state.Finished && !state.Receive && state.Peers.Count > 1)
        {
            var change = PillButton(Loc.T("更换设备", "Change device"), false, async () =>
            {
                await _app.ClipboardSync.CloseOpenFileTransferAsync();
                if (!ReferenceEquals(state, _transfer)) return;
                // The remembered device is dropped so the next send does not silently reuse it.
                _app.ClipboardSync.LastFilePeer = null;
                state.Started = false;
                state.Finished = false;
                state.Phase = FileTransferPhase.Preparing;
                state.Fraction = 0;
                state.Title = Loc.T("发送到微信输入法", "Send to WeType");
                state.Detail = Loc.T("选择目标设备后开始。手机端需要确认接收。", "Choose a target; confirm on the phone.");

                RenderShelf();
            });
            change.Margin = new Thickness(6, 0, 0, 0);
            actions.Children.Add(change);
        }
        var details = PillButton(state.Details ? Loc.T("收起详情", "Hide details") : Loc.T("详情", "Details"), false, () =>
        {
            state.Details = !state.Details;
            RenderShelf();
        });
        details.Margin = new Thickness(0, 0, 6, 0);
        actions.Children.Add(details);
        if (state.Debug.Length > 0)
        {
            actions.Children.Add(PillButton(Loc.T("复制调试", "Copy debug"), false, () =>
            {
                try { System.Windows.Clipboard.SetText(state.Debug); } catch (Exception) { }
            }));
        }
        return actions;
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

    private static string SizeText(long bytes) => bytes >= 1 << 30 ? (bytes / (double)(1 << 30)).ToString("0.0") + " GiB"
        : bytes >= 1 << 20 ? (bytes / (double)(1 << 20)).ToString("0.0") + " MiB"
        : bytes >= 1 << 10 ? (bytes / (double)(1 << 10)).ToString("0.0") + " KiB" : bytes + " B";
}