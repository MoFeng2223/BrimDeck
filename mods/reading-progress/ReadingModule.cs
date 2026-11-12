using System.IO;
using BrimDeck.Modules;

namespace BrimDeck.ReadingProgress;

// A useful, self-contained example of compact parts and the stage-five APIs. The module owns the reading session;
// every page/display mounts its own controls. It needs no network, account, hardware polling or private host API.
public sealed class ReadingModule : IModule
{
    public const string Id = "sample-reading-progress";
    public const string PageId = Id + ":book";

    public ValueTask ActivateAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = new ReadingSession(context);
        context.Own(session);
        context.Register("book", new ModulePage("阅读进度", "Reading progress", view => new ReadingPage(session, view), 450)
        {
            Size = new ModulePageSize(580, 310, 440, 900, 230, 500),
            IconPath = "M12,5 C8,2 4,2 1,3 L1,20 C5,19 8,19 12,22 C16,19 19,19 23,20 L23,3 C19,2 16,2 12,5 Z M12,5 L12,22"
        });
        context.Register<IModuleCompactPart>("progress", new ReadingPart(session));
        context.Register<IModuleSettings>("preferences", new ReadingSettings(session));
        foreach (string action in new[] { "next-page", "advance", "undo", "open-page", "celebrate", "release-accent", "check-background", "check-page-scope" })
            context.Register<IModuleCommand>(action, new ReadingCommand(session, action));
        return ValueTask.CompletedTask;
    }
}

public sealed class ReadingData
{
    public string Book { get; set; } = "我的阅读目标";
    public int Target { get; set; } = 100;
    public int Page { get; set; }
    public int Step { get; set; } = 5;
    public int? PreviousPage { get; set; }
    public string Accent { get; set; } = "#57B8A6";
}

internal sealed class ReadingSession : IDisposable
{
    private readonly IModuleContext _context;
    private readonly IModuleHostControl _controls;
    private readonly bool _canSave;
    private ModuleAccentOverride? _accent;
    private bool _disposed;
    internal ReadingData Data { get; private set; }
    internal ModuleHostStateSnapshot? Host { get; private set; }
    internal string ResultChinese { get; private set; } = "进度自动保存在本模组的数据目录。";
    internal string ResultEnglish { get; private set; } = "Progress is saved in this module's data directory.";
    internal bool CanSave => _canSave;
    internal event Action? Changed;

    internal ReadingSession(IModuleContext context)
    {
        _context = context;
        _controls = context.GetService<IModuleHostControl>();
        var saved = context.Storage.ReadResult<ReadingData>("reading");
        _canSave = saved.Status is ModuleReadStatus.Missing or ModuleReadStatus.Value;
        Data = saved.Value ?? new();
        Normalize();
        if (!_canSave)
        {
            ResultChinese = "保存的数据无法读取，已保留原文件；本次修改不会写入。";
            ResultEnglish = "Saved data cannot be read. The original file is preserved; changes will not be saved.";
            context.Report("read reading progress", new InvalidDataException(ResultEnglish));
        }
        // One subscription per activation, replayed after activation commits. Views share this snapshot and never
        // create a sampling timer. The returned lease is also explicitly owned by the activation.
        context.Own(context.GetService<IModuleHostState>().Subscribe(value => { if (!_disposed) { Host = value; Notify(); } }));
    }

    private void Normalize()
    {
        Data.Book = string.IsNullOrWhiteSpace(Data.Book) ? "我的阅读目标" : Data.Book.Trim()[..Math.Min(Data.Book.Trim().Length, 48)];
        Data.Target = Math.Clamp(Data.Target, 1, 9999);
        Data.Page = Math.Clamp(Data.Page, 0, Data.Target);
        Data.Step = Math.Clamp(Data.Step, 1, 100);
        Data.PreviousPage = Data.PreviousPage is { } previous ? Math.Clamp(previous, 0, Data.Target) : null;
        Data.Accent = ValidColor(Data.Accent) ? Data.Accent.ToUpperInvariant() : "#57B8A6";
    }
    internal static bool ValidColor(string? value) => value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).ToArray().All(Uri.IsHexDigit);
    internal bool CanExecute(string action) => !_disposed && action switch
    {
        "next-page" or "advance" => Data.Page < Data.Target,
        "undo" => Data.PreviousPage is not null,
        "celebrate" => Data.Page == Data.Target,
        _ => true
    };
    internal async Task<ModuleCommandResult> Invoke(string action, CancellationToken cancellation)
    {
        var result = await _context.GetService<IModuleCommands>().InvokeAsync(ReadingModule.Id + ":" + action, cancellation: cancellation);
        // Rejections can happen before Execute runs (missing capability, busy command, retirement). Show the host's
        // returned explanation as well, rather than leaving a page button apparently doing nothing.
        if (!_disposed && result.Kind is not (ModuleCommandResultKind.Completed or ModuleCommandResultKind.Cancelled))
        {
            ResultChinese = result.Message ?? "当前无法执行此操作。";
            ResultEnglish = result.MessageEn ?? result.Message ?? "This action is not available right now.";
            Notify();
        }
        return result;
    }

    internal async Task<ModuleCommandResult> Execute(string action, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!CanExecute(action)) return ModuleCommandResult.Rejected("当前无法执行此操作。", "This action is not available right now.");
        switch (action)
        {
            case "next-page": case "advance":
                Data.PreviousPage = Data.Page;
                Data.Page = Math.Min(Data.Target, Data.Page + (action == "next-page" ? 1 : Data.Step));
                Save();
                return Finish(ModuleHostResult.Completed, $"已记到第 {Data.Page} 页。", $"Moved to page {Data.Page}.");
            case "undo":
                Data.Page = Data.PreviousPage!.Value; Data.PreviousPage = null;
                _accent?.Dispose(); _accent = null; Save();
                return Finish(ModuleHostResult.Completed, "已撤销上次页码变更。", "Undid the last page update.");
            case "open-page":
                var opened = await _controls.Panel.RequestExpandAsync(ModuleCommandOrigin.User, cancellation);
                if (opened.Ok) opened = await _controls.Pages.OpenOwnPageAsync(ReadingModule.PageId, cancellation);
                return Finish(opened, "已打开阅读进度。", "Opened reading progress.");
            case "celebrate":
                // Reapply explicitly rather than assuming an earlier lease is still effective: changing the user's
                // theme clears host overrides even while this module remains enabled.
                _accent?.Dispose();
                var request = _controls.Accent.RequestAccentOverride(Data.Accent);
                _accent = request.Override;
                return Finish(new(request.Kind, request.Reason), "目标已完成，已申请临时高亮；切换主题会清除。", "Goal complete. Applied a temporary accent; changing the theme clears it.");
            case "release-accent":
                _accent?.Dispose(); _accent = null;
                return Finish(ModuleHostResult.Completed, "已释放本模组的临时高亮。", "Released this module's temporary accent.");
            case "check-background":
                return CheckRefusal(await _controls.Panel.RequestExpandAsync(ModuleCommandOrigin.Host, cancellation), "user-initiated-only",
                    "后台展开请求已按策略拦截。", "The background expand request was blocked as expected.");
            case "check-page-scope":
                return CheckRefusal(await _controls.Pages.OpenOwnPageAsync("other-sample:book", cancellation), "page-not-own",
                    "访问其他模组页面的请求已被拦截。", "Opening another module's page was blocked as expected.");
            default: return ModuleCommandResult.Rejected("未知阅读操作。", "Unknown reading action.");
        }
    }

    private ModuleCommandResult CheckRefusal(ModuleHostResult result, string reason, string chinese, string english)
        => result.Kind == ModuleHostResultKind.Rejected && result.Reason == reason
            ? Finish(ModuleHostResult.Completed, chinese, english)
            : Finish(ModuleHostResult.Failed("unexpected-policy-result"), chinese, english);
    private ModuleCommandResult Finish(ModuleHostResult result, string chinese, string english)
    {
        if (_disposed) return ModuleCommandResult.Cancelled;
        ResultChinese = result.Ok ? chinese : "操作未完成：" + (result.Reason ?? result.Kind.ToString());
        ResultEnglish = result.Ok ? english : "Action not completed: " + (result.Reason ?? result.Kind.ToString());
        Notify();
        return result.Kind switch
        {
            ModuleHostResultKind.Completed => ModuleCommandResult.Completed,
            ModuleHostResultKind.Rejected => ModuleCommandResult.Rejected(ResultChinese, ResultEnglish),
            ModuleHostResultKind.CapabilityMissing => ModuleCommandResult.Missing(ResultChinese, ResultEnglish),
            ModuleHostResultKind.Cancelled => ModuleCommandResult.Cancelled,
            _ => ModuleCommandResult.Failed(ResultChinese, ResultEnglish)
        };
    }

    internal void Save()
    {
        Normalize();
        if (Data.Page < Data.Target) { _accent?.Dispose(); _accent = null; }
        if (!_disposed && _canSave) _context.Storage.Write("reading", Data);
        Notify();
    }
    internal void Report(Exception error)
    {
        if (_disposed || _context.Lifetime.IsCancellationRequested) return;
        _context.Report("reading action", error); Finish(ModuleHostResult.Failed("action-failed"), "", "");
    }
    internal void Reset() { _accent?.Dispose(); _accent = null; Data = new(); Save(); }
    private void Notify() { if (!_disposed) Changed?.Invoke(); }
    public void Dispose() { if (_disposed) return; _disposed = true; _accent?.Dispose(); _accent = null; Changed = null; }
}

internal sealed class ReadingCommand(ReadingSession session, string action) : IModuleCommand
{
    public ModuleCommand Descriptor => action switch
    {
        "next-page" => new("阅读：前进一页", "Reading: next page"),
        "advance" => new("阅读：按步长前进", "Reading: advance by step"),
        "undo" => new("阅读：撤销页码变更", "Reading: undo page update"),
        "open-page" => new("打开阅读进度", "Open reading progress") { Requires = [ModuleApi.HostControl, ModuleHostControlActions.Page, ModuleHostControlActions.Expand] },
        "celebrate" => new("阅读：完成目标高亮", "Reading: highlight completed goal") { Requires = [ModuleApi.HostControl, ModuleHostControlActions.Accent] },
        "release-accent" => new("阅读：释放高亮", "Reading: release accent") { Requires = [ModuleApi.HostControl, ModuleHostControlActions.Accent] },
        "check-background" => new("阅读示例：验证后台展开拦截", "Reading sample: check background expand policy") { Requires = [ModuleApi.HostControl, ModuleHostControlActions.Expand] },
        _ => new("阅读示例：验证页面归属", "Reading sample: check page ownership") { Requires = [ModuleApi.HostControl, ModuleHostControlActions.Page] }
    };
    public bool CanExecute => session.CanExecute(action);
    public Task<ModuleCommandResult> ExecuteAsync(IReadOnlyDictionary<string, string>? parameters, CancellationToken cancellation)
        => session.Execute(action, cancellation);
}

internal sealed class ReadingSettings(ReadingSession session) : IModuleSettings
{
    public string Name => "阅读进度";
    public string NameEn => "Reading progress";
    public bool Visible => true;
    public IReadOnlyList<ModuleSetting> Fields =>
    [
        new("book", "书名或阅读目标", "Book or reading goal", ModuleSettingKind.Text) { MaximumLength = 48 },
        new("target", "目标页数", "Target pages", ModuleSettingKind.Number, 1, 9999, "页", "pages"),
        new("page", "当前页码", "Current page", ModuleSettingKind.Number, 0, session.Data.Target, "页", "pages"),
        new("step", "每次前进页数", "Advance step", ModuleSettingKind.Number, 1, 100, "页", "pages"),
        new("accent", "完成目标的高亮色", "Completed-goal accent", ModuleSettingKind.Color)
    ];
    public object Read(string key) => key switch
    { "book" => session.Data.Book, "target" => session.Data.Target, "page" => session.Data.Page, "step" => session.Data.Step, "accent" => session.Data.Accent, _ => throw new ArgumentException(key) };
    public void Write(string key, object value)
    {
        if (key == "book" && value is string book && !string.IsNullOrWhiteSpace(book)) session.Data.Book = book;
        else if (key == "accent" && value is string color && ReadingSession.ValidColor(color)) session.Data.Accent = color.ToUpperInvariant();
        else if (key is "target" or "page" or "step" && value is IConvertible)
        {
            double number = Convert.ToDouble(value);
            if (!double.IsFinite(number)) throw new ArgumentException("Page counts must be finite.");
            int count = (int)Math.Clamp(Math.Round(number), 0, 9999);
            if (key == "target") session.Data.Target = Math.Max(1, count);
            else if (key == "step") session.Data.Step = Math.Clamp(count, 1, 100);
            else { session.Data.PreviousPage = session.Data.Page; session.Data.Page = count; }
        }
        else throw new ArgumentException("Invalid reading setting.");
        session.Save();
    }
    public void Reset() => session.Reset();
}
