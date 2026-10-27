using System.Windows;
using System.Windows.Controls;
using BrimDeck.Modules;
using BrimDeck.Sample.Helper;

namespace BrimDeck.Sample;

// Two entries and a separate helper assembly exercise the public SDK and resolver.
public sealed class NotesModule : IModule
{
    private string _draft = "";
    private bool _notesEnabled = true, _clock = true;
    public ValueTask ActivateAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        _draft = context.Storage.Read<string>("draft") ?? "";
        _notesEnabled = context.Storage.Read<bool?>("notes-enabled") ?? true;
        _clock = context.Storage.Read<bool?>("clock") ?? true;
        IDisposable? notes = null;
        void RegisterNotes() => notes = context.Register("notes", new ModulePage("便笺", "Notes", view => new NotesView(view, _draft, text => _draft = text), 200));
        if (_notesEnabled) RegisterNotes();
        context.Register("about", ModulePage.Async("示例说明", Greeting.Text, async view =>
        {
            // A short, cancellable delay makes the async lifecycle visible in this SDK demonstration.
            await Task.Delay(150, view.Lifetime);
            return new InfoView(view, () => _clock);
        }, 300));
        context.Register<IModuleSettings>("preferences", new Preferences(this, context));
        context.Register("notes", new ModuleWidget("便笺", "Notes", context.ModuleId + ":preferences", () => _notesEnabled, enabled =>
        {
            _notesEnabled = enabled; context.Storage.Write("notes-enabled", enabled);
            notes?.Dispose(); notes = null; if (enabled) RegisterNotes();
        }));
        context.OnDispose(() => context.Storage.Write("draft", _draft));
        return ValueTask.CompletedTask;
    }
    private sealed class NotesView : IModuleView
    {
        private readonly Grid _root = new();
        public FrameworkElement Element => _root;
        internal NotesView(ModuleViewContext context, string draft, Action<string> changed)
        {
            _root.RowDefinitions.Add(new() { Height = GridLength.Auto }); _root.RowDefinitions.Add(new());
            var title = ModuleUi.Text("便笺 · 页面切换后保留内容", 16); title.Margin = new Thickness(0, 0, 0, 12); _root.Children.Add(title);
            var editor = new TextBox { Text = draft, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) };
            TextChangedEventHandler handler = (_, _) => changed(editor.Text); editor.TextChanged += handler;
            context.OnDispose(() => editor.TextChanged -= handler); Grid.SetRow(editor, 1); _root.Children.Add(editor);
        }
        public void SetViewport(double width, double height, double scale = 1) { }
        public void Refresh() { }
        public void Dispose() => _root.Children.Clear();
    }
    private sealed class InfoView : IModuleView
    {
        private readonly TextBlock _text;
        public FrameworkElement Element => _text;
        internal InfoView(ModuleViewContext context, Func<bool> clock)
        {
            _text = ModuleUi.Text(Greeting.Text, 16);
            // Deliberately do not retain the timer handle: the view context must clean it up.
            void RefreshClock() => _text.Text = Greeting.Text + (clock() ? " · " + DateTime.Now.ToLongTimeString() : "");
            context.OnVisibilityChanged(visible => { if (visible) RefreshClock(); });
            context.EveryVisible(TimeSpan.FromSeconds(1), RefreshClock);
        }
        public void SetViewport(double width, double height, double scale = 1) { }
        public void Refresh() { }
        public void Dispose() { }
    }
    private sealed class Preferences(NotesModule module, IModuleContext context) : IModuleSettingsNavigation
    {
        public ModuleSettingsPlacement Placement => ModuleSettingsPlacement.Modules;
        public string? IconPath => "M 2,1 H 11 L 14,4 V 15 H 2 Z M 10,1 V 5 H 14 M 5,8 H 11 M 5,11 H 11";
        public string Name => "便笺示例";
        public string NameEn => "Notes sample";
        public bool Visible => module._notesEnabled;
        public IReadOnlyList<ModuleSetting> Fields => [new("clock", "显示示例时钟", "Show sample clock", ModuleSettingKind.Toggle)];
        public object Read(string key) => module._clock;
        public void Write(string key, object value) { module._clock = (bool)value; context.Storage.Write("clock", module._clock); }
        public void Reset() => Write("clock", true);
    }
}
