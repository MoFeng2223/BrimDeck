using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Modules;
using BrimDeck.ReadingProgress;

internal static partial class Program
{
    private sealed class ReadingStateReader(ModuleServiceScope scope, ModuleSnapshotSource<ModuleHostStateSnapshot> source) : IModuleHostState
    {
        public ModuleHostStateSnapshot GetSnapshot() => scope.Read(source);
        public IDisposable Subscribe(Action<ModuleHostStateSnapshot> listener) => scope.Subscribe(source, listener);
    }
    private sealed class ReadingControls : IModuleHostControl, IModulePageControl, IModulePanelControl, IModuleAccentControl
    {
        internal int ActiveAccents, AccentRequests, ExpandCalls;
        internal Action? AccentRequested;
        internal string? LastPage;
        internal ModuleCommandOrigin LastOrigin;
        public IModulePageControl Pages => this;
        public IModulePanelControl Panel => this;
        public IModuleAccentControl Accent => this;
        public IModuleMediaControl Media => throw new InvalidOperationException("The reading sample must never request media control.");
        public Task<ModuleHostResult> OpenOwnPageAsync(string pageId, CancellationToken cancellation = default)
        { LastPage = pageId; return Task.FromResult(pageId == ReadingModule.PageId ? ModuleHostResult.Completed : ModuleHostResult.Rejected("page-not-own")); }
        public Task<ModuleHostResult> RequestExpandAsync(ModuleCommandOrigin origin, CancellationToken cancellation = default)
        { LastOrigin = origin; if (origin == ModuleCommandOrigin.User) ExpandCalls++; return Task.FromResult(origin == ModuleCommandOrigin.User ? ModuleHostResult.Completed : ModuleHostResult.Rejected("user-initiated-only")); }
        public ModuleAccentResult RequestAccentOverride(string color) { ActiveAccents++; AccentRequests++; AccentRequested?.Invoke(); return new(ModuleHostResultKind.Completed, new ReadingAccent(this, color)); }
        private sealed class ReadingAccent(ReadingControls owner, string color) : ModuleAccentOverride
        {
            private bool _ended;
            public string Color => color;
            public void Dispose() { if (_ended) return; _ended = true; owner.ActiveAccents--; }
        }
    }
    private sealed class ReadingCommands(ModuleServiceScope scope, Func<ModuleHost> host, Action invoked) : IModuleCommands
    {
        public IReadOnlyList<ModuleCommand> List() => host().Registry.List<IModuleCommand>().Where(e => e.ModuleId == scope.ModuleId).Select(e => e.Value.Descriptor).ToArray();
        public Task<ModuleCommandResult> InvokeAsync(string commandId, IReadOnlyDictionary<string, string>? parameters = null, ModuleCommandOrigin origin = ModuleCommandOrigin.User, CancellationToken cancellation = default)
        {
            scope.RequireLive(); invoked();
            var command = host().Registry.List<IModuleCommand>().Single(e => e.ModuleId == scope.ModuleId && e.Id == commandId).Value;
            return command.CanExecute ? command.ExecuteAsync(parameters, cancellation) : Task.FromResult(ModuleCommandResult.Rejected("unavailable"));
        }
        public bool ShowOwnPage(string pageId, bool userInitiated = true) => throw new InvalidOperationException("The sample exercises the scoped host-control API.");
    }

    private static void ReadingProgressChecks(string repository)
    {
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(Path.Combine(repository, "mods/reading-progress/module.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        string dataRoot = Path.Combine(_root, "reading-sample"), dataFile = Path.Combine(dataRoot, ReadingModule.Id, "reading.json");
        var scheduler = new Scheduler(); var controls = new ReadingControls(); int routed = 0;
        var source = new ModuleSnapshotSource<ModuleHostStateSnapshot>(new(7, DateTimeOffset.UtcNow) { Expanded = true, PageId = ReadingModule.PageId, PageIsOwn = true, ThemeId = "dark" });
        ModuleHost? configured = null;
        using var host = configured = new ModuleHost(dataRoot, scheduler, [], App.ModuleKinds(),
        [
            ModuleService.Of<IModuleHostState>(ModuleApi.HostState, scope => new ReadingStateReader(scope, source)),
            ModuleService.Of<IModuleHostControl>(ModuleApi.HostControl, ModuleApi.HostControl, _ => controls),
            ModuleService.Of<IModuleCommands>(ModuleApi.CommandsInvoke, scope => new ReadingCommands(scope, () => configured!, () => routed++))
        ]);
        var definition = new ModuleDefinition(manifest, "sample-v1", true, () => new ReadingModule());
        host.ReconcileAsync([definition]).GetAwaiter().GetResult();
        Check("reading sample activates using only its declared public services", host.Modules.Single().Status == ModuleStatus.Ready);
        Check("bound host controls advertise their granular command capabilities", manifest.Capabilities.Where(c => c.StartsWith(ModuleApi.HostControl + ".", StringComparison.Ordinal)).All(host.AvailableCapabilities.Contains));
        using (var withoutControls = Host())
            Check("a host without the control binding advertises no control sub-actions", !withoutControls.AvailableCapabilities.Any(c => c.StartsWith(ModuleApi.HostControl + ".", StringComparison.Ordinal)));
        Check("reading sample needs no system/media capability or sampling timer", scheduler.Callbacks.Count == 0 && !manifest.Capabilities.Contains(ModuleApi.SystemRead) && !manifest.Capabilities.Contains(ModuleApi.MediaRead));
        Check("reading sample subscribes to panel state exactly once", source.SubscriberCount == 1);
        Check("activation does not expand or apply an accent", controls.ExpandCalls == 0 && controls.ActiveAccents == 0);
        Check("sample assembly has no private host references", !typeof(ReadingModule).Assembly.GetReferencedAssemblies().Any(a => a.Name is "BrimDeck" or "BrimDeck.Core" or "BrimDeck.Modules"));

        IModuleCommand Command(string action) => host.Registry.List<IModuleCommand>().Single(e => e.Id == ReadingModule.Id + ":" + action).Value;
        ModuleCommandResult Run(string action) => Command(action).ExecuteAsync(null, CancellationToken.None).GetAwaiter().GetResult();
        var settings = host.Registry.List<IModuleSettings>().Single().Value;
        var page = host.Registry.List<ModulePage>().Single().Value;
        ModuleViewContext Context()
        {
            var owner = new Grid();
            owner.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BrimDeck.UI;component/PanelControls.xaml", UriKind.Relative) });
            owner.Resources.MergedDictionaries.Add(PanelTheme.CreateResources(PanelThemes.Resolve(PanelThemes.BuiltIn(PanelThemes.Dark)!)));
            return new(owner, _ => { }, _ => { }, _ => { });
        }
        void MountResources(IModuleView view, ModuleViewContext context)
        { foreach (var resources in context.Owner.Resources.MergedDictionaries) view.Element.Resources.MergedDictionaries.Add(resources); }
        using var firstContext = Context();
        using var secondContext = Context();
        firstContext.SetAppearance(new(1, true, false, "zh-CN", 1, 1, true, false));
        using var first = page.CreateView(firstContext);
        using var second = page.CreateView(secondContext);
        MountResources(first, firstContext); MountResources(second, secondContext);
        void Layout(IModuleView view, double width = 580, double height = 310)
        { view.SetViewport(width, height); view.Element.Measure(new(width, height)); view.Element.Arrange(new Rect(0, 0, width, height)); view.Element.UpdateLayout(); }
        T FindReading<T>(IModuleView view, string id) where T : FrameworkElement => Descendants(view.Element).OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
        Layout(first); Layout(second);
        Check("independent reading page mounts share initial progress", !ReferenceEquals(first.Element, second.Element) && FindReading<TextBlock>(first, "reading-page-count").Text == "0 / 100" && FindReading<TextBlock>(second, "reading-page-count").Text == "0 / 100");
        settings.Write("book", "验收用阅读目标"); settings.Write("target", 7d); settings.Write("step", 5d);
        FindReading<Button>(first, "reading-next-page").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("page button goes through the shared command service", routed == 1 && (int)settings.Read("page") == 1);
        Check("a second mount updates without recreating its controls", FindReading<TextBlock>(second, "reading-page-count").Text == "1 / 7");
        controls.AccentRequested = () => source.Publish(source.Read() with { Revision = source.Read().Revision + 1 });
        Run("advance"); Run("advance");
        Check("reading commands clamp at the goal and disable further advance", (int)settings.Read("page") == 7 && !Command("next-page").CanExecute && !Command("advance").CanExecute);
        Check("reaching the goal automatically requests exactly one accent despite reentrant host updates", controls.ActiveAccents == 1 && controls.AccentRequests == 1);
        source.Publish(source.Read() with { Revision = 20 }); settings.Write("book", "验收用阅读目标");
        Check("an unchanged completed goal does not repeatedly request its accent", controls.AccentRequests == 1);
        Run("release-accent"); source.Publish(source.Read() with { Revision = 21 });
        Check("explicit accent release survives subsequent host updates", controls.ActiveAccents == 0 && controls.AccentRequests == 1);
        Run("undo");
        Check("undo below the goal releases the accent and retains earlier history", (int)settings.Read("page") == 6 && Command("undo").CanExecute && controls.ActiveAccents == 0);
        Run("next-page"); Check("completing the goal again requests a new accent", controls.ActiveAccents == 1 && controls.AccentRequests == 2);
        Run("undo"); Run("undo");
        Check("consecutive undo walks back through earlier page updates", (int)settings.Read("page") == 1 && Command("undo").CanExecute && controls.ActiveAccents == 0);
        Run("undo"); Check("undo stops at the start of history", (int)settings.Read("page") == 0 && !Command("undo").CanExecute);
        settings.Write("page", 2d); settings.Write("page", 5d); Run("undo");
        Check("page edits in settings also support consecutive undo", (int)settings.Read("page") == 2 && Command("undo").CanExecute);
        Run("undo"); settings.Write("page", 6d);
        Check("progress persists in the module storage", JsonSerializer.Deserialize<ReadingData>(File.ReadAllText(dataFile)) is { Page: 6, Target: 7, Book: "验收用阅读目标" });
        Run("check-background");
        Check("background probe sends the Host origin without expanding", controls.LastOrigin == ModuleCommandOrigin.Host && controls.ExpandCalls == 0);
        Run("check-page-scope");
        Check("page-scope probe requests a foreign page and reports its refusal", controls.LastPage == "other-sample:book" && FindReading<TextBlock>(first, "reading-result").Text.Contains("拦截", StringComparison.Ordinal));
        Run("open-page"); Check("only open-page requests user expansion and its own registered page", controls.ExpandCalls == 1 && controls.LastOrigin == ModuleCommandOrigin.User && controls.LastPage == ReadingModule.PageId);
        source.Publish(source.Read() with { Revision = 8, ContentScale = 1.5 });
        // Open the optional details to materialize its visual tree.
        Descendants(first.Element).OfType<Expander>().Single().IsExpanded = true; Layout(first);
        Check("sample presents the initial and subsequent panel snapshots", FindReading<TextBlock>(first, "reading-host-state").Text.Contains("修订 8", StringComparison.Ordinal));
        Descendants(first.Element).OfType<Expander>().Single().IsExpanded = false;

        using var compactContext = Context();
        using var compact = host.Registry.List<IModuleCompactPart>().Single().Value.CreateView(compactContext);
        MountResources(compact, compactContext);
        foreach (var size in new[] { new Size(24, 24), new Size(18, 24), new Size(28, 24) })
        { Layout(compact, size.Width, size.Height); Check("reading compact part stays bounded at " + size, compact.Element.DesiredSize.Width <= size.Width + .1 && compact.Element.DesiredSize.Height <= size.Height + .1); }
        Check("views do not add snapshot demand or timers", source.SubscriberCount == 1 && scheduler.Callbacks.Count == 0);
        Check("the compact ring uses the reading page's book icon and exposes exact progress", FindReading<System.Windows.Shapes.Path>(compact, "reading-compact-icon").Data.ToString() == Geometry.Parse(ReadingModule.BookGlyph).ToString() && AutomationProperties.GetName(compact.Element).Contains("6/7", StringComparison.Ordinal));
        SaveReadingPreview(first, Path.Combine(_root, "reading-page-zh.png"), 580, 310);
        SaveReadingPreview(second, Path.Combine(_root, "reading-page-en.png"), 580, 310);
        SaveReadingPreview(compact, Path.Combine(_root, "reading-compact.png"), 24, 24);
        Run("next-page"); Layout(compact, 24, 24);
        Check("the completed ring icon takes the configured completion color", FindReading<System.Windows.Shapes.Path>(compact, "reading-compact-icon").Stroke is SolidColorBrush brush && brush.Color == (Color)ColorConverter.ConvertFromString((string)settings.Read("accent")));
        SaveReadingPreview(compact, Path.Combine(_root, "reading-compact-complete.png"), 24, 24); Run("undo");
        var oldAdvance = Command("advance");
        host.ReconcileAsync([definition with { Enabled = false }]).GetAwaiter().GetResult();
        Check("disable releases state subscription and contributions", source.SubscriberCount == 0 && host.Registry.List<ModulePage>().Count == 0 && controls.ActiveAccents == 0);
        Check("a retired command cannot change saved progress", oldAdvance.ExecuteAsync(null, CancellationToken.None).GetAwaiter().GetResult().Kind == ModuleCommandResultKind.Rejected && JsonSerializer.Deserialize<ReadingData>(File.ReadAllText(dataFile))?.Page == 6);
        FindReading<Button>(first, "reading-next-page").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("a late page click during retirement neither escapes nor writes", JsonSerializer.Deserialize<ReadingData>(File.ReadAllText(dataFile))?.Page == 6);
        host.ReconcileAsync([definition]).GetAwaiter().GetResult();
        Check("reenabling restores saved book, page and undo history", host.Registry.List<IModuleSettings>().Single().Value.Read("page") is 6 && Command("undo").CanExecute && source.SubscriberCount == 1);
        host.ReconcileAsync([definition with { Enabled = false }]).GetAwaiter().GetResult();
        File.WriteAllText(dataFile, JsonSerializer.Serialize(new ReadingData { Page = 7, Target = 7, PreviousPage = 5 }));
        int requests = controls.AccentRequests;
        host.ReconcileAsync([definition]).GetAwaiter().GetResult();
        Check("a restored completed goal highlights after activation becomes ready", controls.AccentRequests == requests + 1 && controls.ActiveAccents == 1);
        Run("undo");
        Check("legacy single-step undo migrates without losing saved progress", (int)host.Registry.List<IModuleSettings>().Single().Value.Read("page") == 5 && !Command("undo").CanExecute && controls.ActiveAccents == 0);
        host.ReconcileAsync([definition with { Enabled = false }]).GetAwaiter().GetResult();
        File.WriteAllText(dataFile, "{broken-reading-data");
        host.ReconcileAsync([definition]).GetAwaiter().GetResult(); Run("next-page");
        Check("corrupt saved progress is never overwritten by a fresh default", File.ReadAllText(dataFile) == "{broken-reading-data" && host.Faults.Any(f => f.ModuleId == ReadingModule.Id));
    }

    private static void SaveReadingPreview(IModuleView view, string path, int width, int height)
    {
        view.SetViewport(width, height); view.Element.Measure(new(width, height)); view.Element.Arrange(new Rect(0, 0, width, height)); view.Element.UpdateLayout();
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        { context.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 26, 30)), null, new Rect(0, 0, width, height)); }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing); bitmap.Render(view.Element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}
