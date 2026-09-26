using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public partial class App
{
    private static IEnumerable<DependencyObject> SettingsElements(DependencyObject root)
    {
        yield return root;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var child in SettingsElements(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
    private async Task VerifySettingsAsync(string output,List<string> checks)
    {
        void Check(string name,bool result)=>checks.Add((result?"PASS ":"FAIL ")+name);
        var saved=Settings.Copy();
        var startupTestKey=@"Software\BrimDeck.Tests\Startup-"+Guid.NewGuid().ToString("N");
        try
        {
            using var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(startupTestKey,true);
            key.SetValue("Unrelated","keep");
            var executable=Path.Combine(AppContext.BaseDirectory,"BrimDeck.exe");
            Native.StartupRegistration.SetEnabled(key,true,executable);
            Check("startup registration writes a quoted executable path",Native.StartupRegistration.IsEnabled(key)&&Equals(key.GetValue("BrimDeck"),'"'+executable+'"'));
            Native.StartupRegistration.SetEnabled(key,false,executable);
            Check("startup registration disables only its own value",!Native.StartupRegistration.IsEnabled(key)&&Equals(key.GetValue("Unrelated"),"keep"));
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(startupTestKey,false); }
        if(Environment.GetCommandLineArgs().Contains("--catalog-live"))
        {
            await Prices.RefreshAsync(true,Lifetime.Token);
            Check("live public model catalog loads without touching account quotas",Prices.ModelCount>0&&Prices.LastError is null);
        }
        else
        {
            foreach(var id in new[]{"anthropic/claude-sonnet-example","google/gemini-example","openai/gpt-example","local/custom-model"})
                Prices.TrySetManual(id,new(.000003m,.000015m,.0000003m,.00000375m,null),false,out _);
            // Exercise a full catalog without network access or the user's price files.
            for(int i=0;i<421;i++)
                Prices.TrySetManual($"test/model-{i:000}",new(.000002m,.000008m,null,null,null),false,out _);
        }
        OpenSettings();await Task.Delay(80);
        var window=_settingsWindow!;
        T Named<T>(DependencyObject root,string name) where T:DependencyObject
        { if(root is UIElement element)element.UpdateLayout();return SettingsElements(root).OfType<T>().Single(e=>AutomationProperties.GetName(e)==name); }
        void Click(DependencyObject root,string name)=>Named<Button>(root,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        FrameworkElement? PopupRoot(Func<FrameworkElement,bool> match)=>PresentationSource.CurrentSources.Cast<PresentationSource>().Select(s=>s.RootVisual).OfType<FrameworkElement>().FirstOrDefault(match);
        var bounds=new Size(window.Width,window.Height);
        foreach(var theme in new[]{SettingsTheme.Light,SettingsTheme.Dark})
        {
            var prefix=Path.Combine(output,$"settings-{theme.ToString().ToLowerInvariant()}");
            void CheckOnce(string name,bool result){ if(theme==SettingsTheme.Light)Check(name,result); }
            window.ShowPage(0);
            var themeOption=Named<RadioButton>(window.RootVisual,theme==SettingsTheme.Light?"浅色":"深色");
            themeOption.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonDownEvent});
            themeOption.IsChecked=true;await Task.Delay(40);
            // The theme choice rebuilds the page; its highlight must still be sliding on the rebuilt control.
            Check($"{theme} theme highlight slides across the rebuilt page",SettingsElements(window.RootVisual).OfType<Border>().Any(b=>b.RenderTransform is System.Windows.Media.TranslateTransform t&&t.HasAnimatedProperties));
            await Task.Delay(240);
            Check($"{theme} theme control updates persistent settings and palette",Settings.Theme==theme&&window.IsDarkTheme==(theme==SettingsTheme.Dark));
            for(int page=0;page<8;page++)
            {
                window.ShowPage(page);await Task.Delay(180);window.UpdateLayout();
                Capture(window.RootVisual,$"{prefix}-{page}.png");
            }
            window.Width=window.MinWidth;window.Height=window.MinHeight;window.ShowPage(0);await Task.Delay(25);
            Capture(window.RootVisual,$"{prefix}-minimum.png");
            SettingsElements(window.RootVisual).OfType<ScrollViewer>().First(s=>s.Content is StackPanel).ScrollToBottom();await Task.Delay(50);
            Capture(window.RootVisual,$"{prefix}-minimum-bottom.png");
            for(int compactPage=1;compactPage<8;compactPage++)
            {
                window.ShowPage(compactPage);await Task.Delay(160);
                Capture(window.RootVisual,$"{prefix}-minimum-{compactPage}.png");
            }
            window.Width=bounds.Width;window.Height=bounds.Height;window.ShowPage(4);await Task.Delay(25);
            var editor=window.CreatePriceEditor(Prices.Models[0]);editor.Show();await Task.Delay(45);
            Capture((FrameworkElement)editor.Content,$"{prefix}-price-editor.png");
            // A click on the dialog's empty space takes focus off the price field, as a click in the settings window does.
            editor.Activate();var inputPrice=SettingsElements((FrameworkElement)editor.Content).OfType<TextBox>().Single(t=>AutomationProperties.GetName(t)=="输入单价");
            inputPrice.Focus();await Task.Delay(20);bool focusedBefore=inputPrice.IsKeyboardFocused;
            SettingsElements((FrameworkElement)editor.Content).OfType<TextBlock>().First(t=>t.Text=="美元 / 百万 Token")
                .RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.MouseDownEvent});
            await Task.Delay(20);
            CheckOnce("a click on empty space in the price editor takes focus off the field",focusedBefore&&!inputPrice.IsKeyboardFocused&&editor.IsKeyboardFocusWithin);
            // On a short screen the dialog stops at its maximum height: the fields scroll and the button stays inside.
            editor.MaxHeight=320;await Task.Delay(60);editor.UpdateLayout();
            var editorRoot=(FrameworkElement)editor.Content;
            var doneButton=SettingsElements(editorRoot).OfType<Button>().Single(b=>AutomationProperties.GetName(b)=="完成价格编辑");
            var doneBottom=doneButton.TransformToAncestor(editorRoot).Transform(new Point(0,doneButton.ActualHeight)).Y;
            var fieldsScroller=SettingsElements(editorRoot).OfType<ScrollViewer>().Single(s=>s.Content is StackPanel);
            CheckOnce("a short screen keeps the price editor's button inside and scrolls its fields",
                editor.ActualHeight<=320.5&&doneBottom<=editorRoot.ActualHeight&&fieldsScroller.ScrollableHeight>0);
            editor.Close();
            window.ShowPage(3);await Task.Delay(20);Click(window.RootVisual,"配额来源 Claude");await Task.Delay(50);
            var appMenuRoot=PopupRoot(root=>SettingsElements(root).OfType<Button>().Any(b=>AutomationProperties.GetName(b)=="选择 Claude"));
            CheckOnce("application selector opens its provider menu",appMenuRoot is not null);
            if(appMenuRoot is not null)
            {
                Capture(appMenuRoot,$"{prefix}-application-menu.png");
                Click(appMenuRoot,"选择 Claude");await Task.Delay(25);
            }
            Click(window.RootVisual,"Claude 主题颜色");await Task.Delay(50);
            var colorRoot=PopupRoot(root=>SettingsElements(root).OfType<TextBox>().Any(t=>AutomationProperties.GetName(t)=="颜色值"));
            CheckOnce("application color picker opens",colorRoot is not null);
            if(colorRoot is not null)
            {
                Capture(colorRoot,$"{prefix}-color-picker.png");
                Click(colorRoot,"颜色 #9CB9FF");await Task.Delay(25);
                CheckOnce("color picker applies the selected color",Settings.Entry(ProviderId.Claude)!.ThemeColor=="#9CB9FF");
                var originalColor=Settings.Copy();originalColor.Entry(ProviderId.Claude)!.ThemeColor="";UpdateSettings(originalColor);
            }
        }
        window.ShowPage(4);await Task.Delay(60);
        var priceGrid=Named<DataGrid>(window.RootVisual,"模型单价");
        var priceScroll=SettingsElements(priceGrid).OfType<ScrollViewer>().First();
        var priceBar=SettingsElements(priceGrid).OfType<ScrollBar>().Single(b=>b.Orientation==Orientation.Vertical&&b.IsVisible);
        ScrollBar.PageDownCommand.Execute(null,priceBar);await Task.Delay(60);
        Check("price table scrolls through a large catalog",priceScroll.VerticalOffset>0);
        priceScroll.ScrollToTop();
        var edit=window.CreatePriceEditor(Prices.Models[0]);edit.Show();await Task.Delay(25);
        Named<TextBox>(edit,"输入单价").Text="7.125";
        Click(edit,"完成价格编辑");await Task.Delay(25);
        Check("price editor converts per-million to per-token",Prices.Models[0].Prices.Input==.000007125m);
        var autosave=window.CreatePriceEditor(Prices.Models[0]);autosave.Show();await Task.Delay(25);
        Named<TextBox>(autosave,"输入单价").Text="7.25";
        Named<TextBox>(autosave,"输出单价").Focus();await Task.Delay(25);
        Check("existing model edits apply on focus loss",Prices.Models[0].Prices.Input==.00000725m);autosave.Close();
        window.ShowPage(0);
        var animation=Named<CheckBox>(window.RootVisual,"启用展开与收起动画");animation.IsChecked=false;
        Check("animation toggle disables duration controls",!Settings.Animations&&!Named<Slider>(window.RootVisual,"展开与收起动画时长").IsEnabled);
        animation.IsChecked=true;
        var previewSettings=Settings.Copy();previewSettings.Width=760;previewSettings.Height=240;UpdateSettings(previewSettings);
        window.ShowPage(2);Click(window.RootVisual,"展开 AI 用量尺寸");Click(window.RootVisual,"展开音乐尺寸");await Task.Delay(50);
        Capture(window.RootVisual,Path.Combine(output,"settings-expanded-open.png"));
        // A row opens whether or not its page is shown; sizes of a hidden page are saved without a preview.
        bool MusicSizesShown()=>SettingsElements(window.RootVisual).OfType<RadioButton>().Any(r=>AutomationProperties.GetName(r)=="音乐快捷尺寸 紧凑"&&r.IsVisible);
        Named<CheckBox>(window.RootVisual,"显示音乐").IsChecked=false;await Task.Delay(30);
        Capture(window.RootVisual,Path.Combine(output,"settings-expanded-music-off.png"));
        bool offOpen=!Settings.MusicPage&&MusicSizesShown();
        int StartOptions()=>SettingsElements(window.RootVisual).OfType<ComboBox>().Single(c=>AutomationProperties.GetName(c)=="每次打开时显示").Items.Count;
        bool offStarts=StartOptions()==2;
        Click(window.RootVisual,"展开音乐尺寸");bool offCloses=!MusicSizesShown();
        Click(window.RootVisual,"展开音乐尺寸");Deck.Preview(false);await Task.Delay(30);
        Named<RadioButton>(window.RootVisual,"音乐快捷尺寸 紧凑").IsChecked=true;await Task.Delay(50);
        bool savedOnly=(Settings.MusicWidth,Settings.MusicHeight)==MusicSizes.For(PanelSize.Compact)&&!window.IsPreviewing&&!Deck.IsExpanded;
        Check("a hidden page's row still opens and saves sizes without a preview",offOpen&&offCloses&&savedOnly);
        Click(window.RootVisual,"展开音乐尺寸");Named<CheckBox>(window.RootVisual,"显示音乐").IsChecked=true;await Task.Delay(30);
        Check("switching a page on leaves its row closed",Settings.MusicPage&&!MusicSizesShown());
        Check("the start page list offers the last viewed page and each shown page",offStarts&&StartOptions()==3);
        Capture(window.RootVisual,Path.Combine(output,"settings-expanded-start.png"));
        // The AI dimensions precede the music dimensions, which also have a width control.
        var width=SettingsElements(window.RootVisual).OfType<Slider>().First(s=>AutomationProperties.GetName(s)=="宽度");width.Value=820;await Task.Delay(50);
        Check("width slider updates the real island",Settings.Width==820&&Deck.IsExpanded);
        Capture(Deck.PanelVisual,Path.Combine(output,"settings-real-island.png"));
        window.ShowPage(1);await Task.Delay(50);
        Check("leaving expanded settings ends preview",!Deck.IsExpanded);
        // Each style keeps its own options; choosing another card slides them across.
        var styleStart=Settings.Copy();styleStart.Style=CompactStyle.Notch;UpdateSettings(styleStart);window.ShowPage(1);await Task.Delay(50);
        bool Showing(string name)=>SettingsElements(window.RootVisual).OfType<CheckBox>().Any(c=>AutomationProperties.GetName(c)==name&&c.IsVisible);
        Named<CheckBox>(window.RootVisual,"配额环").IsChecked=false;
        Check("notch and capsule keep separate options",!Settings.NotchSummary&&Settings.CapsuleSummary);
        Named<RadioButton>(window.RootVisual,"胶囊").IsChecked=true;await Task.Delay(60);
        Capture(window.RootVisual,Path.Combine(output,"settings-style-slide.png"));
        int sliding=SettingsElements(window.RootVisual).OfType<StackPanel>().Count(p=>p.RenderTransform is TranslateTransform t&&t.HasAnimatedProperties);
        Check("choosing another style slides the options across",Settings.Style==CompactStyle.Capsule&&sliding==2);
        await Task.Delay(400);
        Check("capsule options replace the notch options",Named<CheckBox>(window.RootVisual,"配额环").IsChecked==true&&Showing("正在播放的音乐"));
        Named<RadioButton>(window.RootVisual,"指示条").IsChecked=true;await Task.Delay(400);
        Check("indicator shows only the playback progress",Showing("显示播放进度")&&!Showing("配额环")&&!Showing("正在播放的音乐"));
        var styleEnd=Settings.Copy();styleEnd.Style=CompactStyle.Notch;styleEnd.NotchSummary=true;UpdateSettings(styleEnd);
        window.ShowPage(4);
        var add=window.CreatePriceEditor(null);add.Show();await Task.Delay(30);
        Named<TextBox>(add,"模型 ID").Text="local/added-in-editor";Named<TextBox>(add,"输入单价").Text="2";Named<TextBox>(add,"输出单价").Text="8";
        Click(add,"确认添加模型");await Task.Delay(25);
        Check("price editor creates a persistent model",Prices.Find("local/added-in-editor")?.Prices==new TokenPrices(.000002m,.000008m,null,null,null));
        var duplicate=window.CreatePriceEditor(null);duplicate.Show();await Task.Delay(25);
        Named<TextBox>(duplicate,"模型 ID").Text="LOCAL/ADDED-IN-EDITOR";Named<TextBox>(duplicate,"输入单价").Text="9";Named<TextBox>(duplicate,"输出单价").Text="9";
        Click(duplicate,"确认添加模型");
        Check("duplicate model stays in editor and cannot overwrite prices",duplicate.IsVisible&&Prices.Find("local/added-in-editor")!.Prices.Input==.000002m);
        Capture((FrameworkElement)duplicate.Content,Path.Combine(output,"settings-price-duplicate-error.png"));duplicate.Close();
        var search=Named<TextBox>(window.RootVisual,"搜索模型");search.Text="added-in-editor";
        Check("model search filters the table",Named<DataGrid>(window.RootVisual,"模型单价").Items.Count==1);
        window.ShowPage(0);Named<RadioButton>(window.RootVisual,"跟随系统").IsChecked=true;
        Check("system theme resolves current Windows preference",Settings.Theme==SettingsTheme.System&&window.IsDarkTheme==SettingsPalette.IsDark(SettingsTheme.System));
        // Only smoke settings change; the real startup registry is never modified.
        Named<CheckBox>(window.RootVisual,"开机自启动").IsChecked=true;FlushSettings();
        Check("startup switch language and theme persist in isolated settings",Store.Load().LaunchAtStartup&&Store.Load().Theme==SettingsTheme.System&&Store.Load().Language=="zh-CN");
        window.Close();UpdateSettings(saved);
    }
}
