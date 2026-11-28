using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;
namespace BrimDeck;
public sealed partial class SettingsWindow
{
    internal const int WeatherSettingsPage = 19;
    private void WeatherSettings()
    {
        var state=WeatherData.Load(() => S);var cityRows=new StackPanel();bool active=true;
        string autoName="当前位置";if(state.AutoKey.Length>0&&state.TrySnapshot(state.AutoKey,out var autoSnap))autoName=autoSnap.Name;
        var preview=new WeatherSettingsCard(state);
        var lifetime = new CancellationTokenSource();
        int locationVersion = 0;
        // 城市配额徽章：挂在「当前城市」标题右侧的细描边胶囊里，还有空位用绿色、加满转红色。
        var cityQuotaText=TextLine("",11,TextPrimary);cityQuotaText.VerticalAlignment=VerticalAlignment.Center;
        var cityQuota=new Border {Child=cityQuotaText,CornerRadius=new CornerRadius(10),Padding=new Thickness(9,2,9,2),Background=Brushes.Transparent,BorderBrush=(Brush)FindResource("SettingsBorder"),BorderThickness=new Thickness(1),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,0,0)};
        void UpdateCityQuota(){cityQuotaText.Text=$"{state.Cities.Count}/{DeckSettings.MaxWeatherCities}";cityQuotaText.Foreground=UI.Brush(state.Cities.Count<DeckSettings.MaxWeatherCities?_palette.Positive:_palette.Negative);}
        void Save() {try {_app.QueueSave(); }catch {}}
        void NotifyWeatherSelection(){foreach(var deck in _app.Decks) deck.WeatherSelectionChanged();}
        // 城市清单用同一套列宽，间隔也是真实的列：拖拽位 / 间隔 / 城市 / 间隔 / 天气 / 间隔 / 温度 / 间隔 / 数据源 / 间隔 / 删除。
        // 列头、数据行、搜索页脚共用它，文字和控件落在同一列而不是靠相邻列硬挤。
        Grid CityGrid()
        {
            var grid=new Grid();
            foreach(double width in new[]{24.0,12.0,0.0,16.0,48.0,12.0,68.0,14.0,132.0,12.0,28.0})
                grid.ColumnDefinitions.Add(new ColumnDefinition {Width=width==0 ? new GridLength(1,GridUnitType.Star) : new GridLength(width)});
            return grid;
        }
        static (string Title, string? Detail) SplitName(string full)
        {
            var levels=City.SplitLevels(full);
            return levels.Length==0 ? (full,null) : (levels[0],levels.Length>1 ? string.Join(City.Separator,levels.Skip(1)) : null);
        }
        // 接口给的城市级名称常不带「市」（合肥·安徽省·中国）。只在显示层补后缀，不改动保存的完整名称。
        static string DisplayCityName(string full)
        {
            var levels=City.SplitLevels(full);
            if(levels.Length<2) return full;
            string first=levels[0],second=levels[1];
            bool province=second.EndsWith("省")||second.Contains("自治区")||second.EndsWith("特别行政区")||second.EndsWith("地区");
            bool bare=!first.EndsWith("市")&&!first.EndsWith("省")&&!first.EndsWith("区")&&!first.EndsWith("县")&&!first.EndsWith("旗")&&!first.EndsWith("盟");
            if(province&&bare&&first is not ("香港" or "澳门" or "北京" or "上海" or "天津" or "重庆"))levels[0]=first+"市";
            return string.Join(City.Separator,levels);
        }
        void Fill()
        {
            UpdateCityQuota();
            cityRows.Children.Clear();
            var movable=new StackPanel();
            const double cityRowHeight=54;
            for(int i=-1;i<state.Cities.Count;i++) {
                bool selected=i==state.Selected;var city=i<0?null:state.Cities[i];
                string? temperature=null;int code=-1;bool day=true;
                string name=Loc.T("自动定位","Auto location");string? subtitle=null;
                if(city is null){
                    string local=autoName;
                    if(state.TrySnapshot(state.DisplayAutoKey,out var snap)){
                        temperature=snap.Temperature is { } t && double.IsFinite(t)?$"{t:0}°":"--";code=snap.WeatherCode??-1;day=snap.IsDay;
                        if(!snap.Name.Contains("当前位置"))local=snap.Name;
                    }
                    if(local.Trim().Length>0&&!local.Contains("当前位置"))subtitle=DisplayCityName(local);
                } else {
                    var parts=SplitName(city.Name);name=parts.Title;subtitle=DisplayCityName(city.Name);
                    if(state.TrySnapshot(state.KeyFor(city),out var snap)){temperature=snap.Temperature is { } t && double.IsFinite(t)?$"{t:0}°":"--";code=snap.WeatherCode??-1;day=snap.IsDay;}
                }
                var grid=CityGrid();var sources=WeatherSources.All;
                if(selected){
                    var bar=new Border {Width=3,CornerRadius=new CornerRadius(0,2,2,0),Background=(Brush)FindResource("SettingsAccent"),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Stretch,IsHitTestVisible=false};
                    Grid.SetColumn(bar,0);grid.Children.Add(bar);
                }
                if(city is not null){
                    var grip=new GripHandle {Width=22,Background=Brushes.Transparent,Cursor=Cursors.SizeAll,Child=Grip(TextTertiary),VerticalAlignment=VerticalAlignment.Center};
                    Grid.SetColumn(grip,0);grid.Children.Add(grip);
                }
                var labels=new StackPanel {VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,10,0)};
                var title=TextLine(name,13,TextPrimary,FontWeights.SemiBold);title.TextTrimming=TextTrimming.CharacterEllipsis;labels.Children.Add(title);
                if(subtitle is not null){var detail=TextLine(subtitle,11,TextSecondary);detail.TextTrimming=TextTrimming.CharacterEllipsis;labels.Children.Add(detail);}
                Grid.SetColumn(labels,2);grid.Children.Add(labels);
                var glyph=new WeatherGlyph(code,day) {Width=30,Height=30,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false};
                Grid.SetColumn(glyph,4);grid.Children.Add(glyph);
                var temperatureLabel=TextLine(temperature ?? "",14,TextPrimary,FontWeights.SemiBold);
                temperatureLabel.HorizontalAlignment=HorizontalAlignment.Right;temperatureLabel.VerticalAlignment=VerticalAlignment.Center;temperatureLabel.Margin=new Thickness(0,0,8,0);
                Grid.SetColumn(temperatureLabel,6);grid.Children.Add(temperatureLabel);
                var picker=new ComboBox {Width=108,Height=28,FontSize=11,Margin=new Thickness(0),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Style=(Style)FindResource("SettingsCombo"),
                    ItemsSource=sources.Select(source=>source.Info.Name).ToArray(),SelectedIndex=Array.FindIndex(sources,source=>source.Info.Id==state.Schedule.SourceFor(city))};
                PopupMotion.Attach(picker);
                picker.SelectionChanged+=(_,_)=>{
                    if(picker.SelectedIndex<0)return;
                    state.Schedule.CitySources[city?.StorageKey() ?? "auto"]=sources[picker.SelectedIndex].Info.Id;
                    Save();preview.RenderWeatherCard();Fill();
                    if (state.Selected < 0 ? city is null : city is not null && state.Cities.IndexOf(city)==state.Selected) NotifyWeatherSelection();
                    _=RefreshCityWeatherAsync(city);
                };
                Grid.SetColumn(picker,8);grid.Children.Add(picker);
                if(city is not null){
                    var remove=new Button {Style=(Style)FindResource("SettingsGlyphButton"),ToolTip=Loc.T("删除城市","Remove city"),Width=22,Height=24,Padding=new Thickness(0),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,0,10,0)};
                    var cross=Stroke("M 0,0 L 8,8 M 8,0 L 0,8",8,8,TextSecondary);
                    cross.SetBinding(Shape.StrokeProperty,new System.Windows.Data.Binding("Foreground"){RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor,typeof(Button),1)});
                    remove.Content=cross;
                    remove.Click+=(_,_)=>{int at=state.Cities.IndexOf(city);if(at<0)return;state.Cities.RemoveAt(at);if(state.Selected==at)state.Selected=-1;else if(state.Selected>at)state.Selected--;Save();NotifyWeatherSelection();preview.RenderWeatherCard();Fill();UpdateAdd();};
                    Grid.SetColumn(remove,10);grid.Children.Add(remove);
                }
                void Select(){state.Selected=city is null ? -1 : state.Cities.IndexOf(city);Save();NotifyWeatherSelection();preview.RenderWeatherCard();Fill();}
                var frame=new Border {Child=grid,Height=cityRowHeight,Tag=city,RenderTransform=new TranslateTransform(),
                    Background=selected?(Brush)FindResource("SettingsAccentTint"):Brushes.Transparent,
                    BorderBrush=UI.Brush(_palette.RowDivider),BorderThickness=new Thickness(0,city is null?0:1,0,0),Cursor=System.Windows.Input.Cursors.Hand};
                frame.Focusable = true;
                System.Windows.Automation.AutomationProperties.SetName(frame, name);
                frame.KeyDown += (_, e) => { if (frame.IsKeyboardFocused && e.Key is Key.Enter or Key.Space) { Select(); e.Handled = true; } };
                frame.MouseLeftButtonUp+=(_,e)=>{
                    bool overGrip=grid.Children.OfType<GripHandle>().Any(handle=>handle.IsMouseOver);
                    if(!picker.IsMouseOver && !overGrip && e.OriginalSource is not Button)Select();
                };
                if(city is null) { cityRows.Children.Add(frame); continue; }
                movable.Children.Add(frame);
                var gripHandle=grid.Children.OfType<GripHandle>().First();
                AttachDrag(gripHandle,name,()=>movable,cityRowHeight,list=>
                {
                    var current=state.Selected>=0 ? state.Cities[state.Selected] : null;
                    state.Cities=list.Children.OfType<Border>().Select(row=>(City)row.Tag).ToList();
                    state.Selected=current is null ? -1 : state.Cities.IndexOf(current);
                    foreach(var row in list.Children.OfType<Border>())row.BorderThickness=new Thickness(0,1,0,0);
                    Save();
                });
            }
            cityRows.Children.Add(movable);
        }
        Target.Children.Add(preview);SectionTitle(Loc.T("当前城市","Current city"),cityQuota);
        // 设置页打开即尝试定位：成功则刷新「自动定位」行展示当地城市名，不干扰其余行。
        async Task DetectAsync()
        {
            try
            {
                int version = ++locationVersion;
                var source = state.Schedule.SourceFor(null);
                var local = await WeatherData.LocateAsync(state.Schedule, lifetime.Token);
                if (!active || version != locationVersion || local is null || source != state.Schedule.SourceFor(null)) return;
                state.LocatedCity = local;
                autoName = local.Name;
                Fill();
                preview.RenderWeatherCard();
            }
            catch { }
        }
        var input=new TextBox {Width=250,Height=31,Style=(Style)FindResource("SettingsField")};
        var placeholder=new TextBlock {Text=Loc.T("支持中英文","Chinese or English"),FontSize=13,Foreground=(Brush)FindResource("SettingsTextSecondary"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,0,0),IsHitTestVisible=false};void UpdatePlaceholder()=>placeholder.Visibility=input.Text.Length==0?Visibility.Visible:Visibility.Collapsed;input.TextChanged+=(_,_)=>UpdatePlaceholder();
        var inputHost=new Grid {Width=250};inputHost.Children.Add(input);inputHost.Children.Add(placeholder);UpdatePlaceholder();
        // 搜索反馈：输入框右侧转圈 + 下拉里明确写「搜索中/未找到」，避免用户以为界面没反应。
        var spinTransform=new RotateTransform();
        var spinner=new System.Windows.Shapes.Path {
            Data=Geometry.Parse("M 8,1.6 A 6.4,6.4 0 1 1 1.6,8"),
            Stroke=(Brush)FindResource("SettingsAccent"),StrokeThickness=2,StrokeStartLineCap=PenLineCap.Round,
            Width=16,Height=16,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center,
            Margin=new Thickness(0,0,10,0),IsHitTestVisible=false,Visibility=Visibility.Collapsed,
            RenderTransformOrigin=new Point(.5,.5),RenderTransform=spinTransform
        };
        var spinAnimation=new System.Windows.Media.Animation.DoubleAnimation(0,360,TimeSpan.FromMilliseconds(750)) {RepeatBehavior=System.Windows.Media.Animation.RepeatBehavior.Forever};
        inputHost.Children.Add(spinner);
        var loading=TextLine(Loc.T("搜索中…","Searching…"),11,TextSecondary);loading.Margin=new Thickness(8,7,8,7);
        // 「空结果」与「请求失败」分开提示：超时/限流不是「没有这个城市」，文案必须说清楚。
        string noResultText=Loc.T("未找到匹配城市","No matching city");
        string failedText=Loc.T("搜索失败，请稍后重试","Search failed, try again later");
        var empty=TextLine(noResultText,11,TextSecondary);empty.Margin=new Thickness(8,7,8,7);empty.Visibility=Visibility.Collapsed;
        // 绑定城市模型唯一的全称字段：用 nameof 让字段改名在编译期暴露，避免搜索结果再次显示为空。
        var options=new ListBox {MaxHeight=240,DisplayMemberPath=nameof(City.Name),Style=(Style)FindResource("SettingsSearchList")};
        var popupBody=new StackPanel();popupBody.Children.Add(loading);popupBody.Children.Add(options);popupBody.Children.Add(empty);
        var popup=new Popup {PlacementTarget=input,Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true,Child=new Border {Width=250,Background=(Brush)FindResource("SettingsInput"),BorderBrush=(Brush)FindResource("SettingsBorder"),BorderThickness=new Thickness(1),Padding=new Thickness(4),Margin=new Thickness(0,4,0,0),CornerRadius=new CornerRadius(6),Child=popupBody}};
        PopupMotion.Attach(popup);
        void ShowSearching(bool on)
        {
            spinner.Visibility=on?Visibility.Visible:Visibility.Collapsed;
            if(on && Soft.Enabled) spinTransform.BeginAnimation(RotateTransform.AngleProperty,spinAnimation);
            else {spinTransform.BeginAnimation(RotateTransform.AngleProperty,null);spinTransform.Angle=0;}
            loading.Visibility=on?Visibility.Visible:Visibility.Collapsed;
            if(on) empty.Visibility=Visibility.Collapsed;
        }
        // 状态按钮与配额计数器提前声明：按钮回调和添加城市触发的刷新都会引用到它们，晚声明会被判定为未赋值。
        var requestsToday = TextLine("",12,TextSecondary);
        var dailyCounter=TextLine("",11,TextSecondary);dailyCounter.VerticalAlignment=VerticalAlignment.Center;
        var monthlyCounter=TextLine("",11,TextSecondary);monthlyCounter.VerticalAlignment=VerticalAlignment.Center;
        var statusDot = new Ellipse {Width = 7, Height = 7, Fill = UI.Brush(TextTertiary)};
        var statusText = TextLine(Loc.T("未测试", "Not tested"), 11, TextSecondary);
        statusText.VerticalAlignment = VerticalAlignment.Center; statusText.Margin = new Thickness(6, 0, 0, 0);
        var statusContent = new StackPanel {Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center};
        statusContent.Children.Add(statusDot); statusContent.Children.Add(statusText);
        // 一个按钮同时承担两件事：圆点与文字是连通性状态，点击它就发一次真实请求手动测试。
        var statusButton = new Button {Style = (Style)FindResource("SettingsOutlineButton"), Content = statusContent,
            Height = 28, MinWidth = 108, Padding = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center};
        bool testing = false;
        statusButton.Click += async (_, _) =>
        {
            if (testing) return;
            testing = true; statusButton.IsEnabled = false;
            statusText.Text = Loc.T("测试中…", "Testing…");
            statusText.Foreground = UI.Brush(TextSecondary); statusDot.Fill = UI.Brush(TextSecondary);
            int version = HeFengMonitor.Generation;
            HeFengCheck? result = null;
            try { result = await HeFengSource.ProbeAsync(state.Schedule, lifetime.Token); }
            catch (OperationCanceledException) when (!active) { }
            catch { result = new(HeFengState.Failed, Loc.T("无法完成测试", "Test could not complete"), default); }
            finally
            {
                testing = false;
                if (active)
                {
                    statusButton.IsEnabled = true;
                    UpdateCounters();
                    RenderStatus(version == HeFengMonitor.Generation && result is not null ? result : HeFengMonitor.Current);
                }
            }
        };
        // 状态由 HeFengMonitor 按真实 HTTP 结果回报，设置页只负责把状态画到按钮上。
        void RenderStatus(HeFengCheck check)
        {
            if (testing) return;
            var (text, brush) = check.State switch
            {
                HeFengState.Connected => (Loc.T("已连通", "Connected"), UI.Brush(_palette.Positive)),
                HeFengState.NotSent => (Loc.T("未发出", "Not sent"), UI.Brush(TextSecondary)),
                HeFengState.Failed => (Loc.T("连接失败", "Failed"), UI.Brush(_palette.Negative)),
                _ => (Loc.T("未测试", "Not tested"), UI.Brush(TextTertiary))
            };
            statusText.Text = text; statusText.Foreground = brush; statusDot.Fill = brush;
            statusButton.ToolTip = check.State == HeFengState.Untested
                ? Loc.T("点击测试连通性（计入配额）；刷新天气时也会自动检测", "Click to test connectivity (counts toward quota); a weather refresh also checks it")
                : $"{check.Detail} · {check.Elapsed.TotalSeconds:0.0}s · " + Loc.T("点击重新测试", "Click to test again");
        }
        void OnStatusChanged(HeFengCheck check)
        {
            // HTTP 回调在线程池上，WPF 控件只能在 UI 线程更新。
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => RenderStatus(HeFengMonitor.Current))); return; }
            RenderStatus(HeFengMonitor.Current);
        }

        RenderStatus(HeFengMonitor.Current);
        City? picked=null;bool programmatic=false;CancellationTokenSource? pending=null;CancellationTokenSource? fetchCts=null;
        // 城市上限与 DeckSettings.Normalize 共用同一常量：满了就禁用添加并说明原因，不会出现「加了却被规范化丢掉」。
        bool CanAdd() => state.Cities.Count < DeckSettings.MaxWeatherCities;
        string AtCapTip() => Loc.T($"最多添加 {DeckSettings.MaxWeatherCities} 个城市，请先删除一个", $"At most {DeckSettings.MaxWeatherCities} cities; remove one first");
        Button add=null!; add=ActionButton(Loc.T("添加","Add"),()=> {if(picked is null||state.Cities.Contains(picked)||!CanAdd())return;var city=picked;state.Cities.Add(city);Save();Fill();UpdateAdd();input.Clear();_=RefreshCityWeatherAsync(city);});add.Style=(Style)FindResource("SettingsPrimaryButton");add.IsEnabled=false;add.Margin=new Thickness(10,0,0,0);
        void UpdateAdd(){add.IsEnabled=picked is not null&&!state.Cities.Contains(picked)&&CanAdd();System.Windows.Controls.ToolTipService.SetToolTip(add,CanAdd()?null:AtCapTip());}
        Fill();
        _ = DetectAsync();
        void PickCity(City city){picked=city;popup.IsOpen=false;programmatic=true;input.Text=city.Name;programmatic=false;UpdateAdd();}
        options.SelectionChanged+=(_,_)=>{if(options.SelectedItem is City city)PickCity(city);};
        // Popup StaysOpen=false 时点列表项会先让输入框失焦、弹窗提前关闭，SelectionChanged 可能来不及触发；
        // 在隧道阶段的 PreviewMouseLeftButtonDown 直接取命中项 DataContext 选中，保证一点就中。
        options.PreviewMouseLeftButtonDown+=(_,e)=>{var fe=e.OriginalSource as FrameworkElement;if(fe?.DataContext is City city){PickCity(city);e.Handled=true;}};
        input.TextChanged+=async (_,_)=>{
            if(programmatic)return;
            pending?.Cancel();pending?.Dispose();pending=new();var token=pending.Token;picked=null;add.IsEnabled=false;
            options.ItemsSource=null;options.SelectedIndex=-1;options.Visibility=Visibility.Visible;empty.Text=noResultText;empty.Visibility=Visibility.Collapsed;
            var query=input.Text.Trim();
            if(query.Length<2){ShowSearching(false);popup.IsOpen=false;return;}
            ShowSearching(true);popup.IsOpen=true;
            try {
                await Task.Delay(350,token);
                var found=await WeatherData.Search(query,state.Schedule,token);
                if(pending is null||token!=pending.Token||!active)return;
                options.ItemsSource=found;
                bool any=found.Count>0;
                options.Visibility=any?Visibility.Visible:Visibility.Collapsed;
                empty.Text=noResultText;
                empty.Visibility=any?Visibility.Collapsed:Visibility.Visible;

            }
            catch(OperationCanceledException){ }
            catch { if(pending is not null&&token==pending.Token){options.ItemsSource=null;options.Visibility=Visibility.Collapsed;empty.Text=failedText;empty.Visibility=Visibility.Visible;} }
            finally { if(active){UpdateCounters(); if(pending is not null&&token==pending.Token)ShowSearching(false);} }
        };
        // 搜索行与城市行共用列宽，作为清单页脚；添加按钮落在数据源列，整组收进一张卡。
        var foot=CityGrid();
        var glass=new Grid {Width=15,Height=15,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false};
        glass.Children.Add(new Ellipse {Width=10,Height=10,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,Fill=Brushes.Transparent,Stroke=UI.Brush(TextSecondary),StrokeThickness=1.2});
        glass.Children.Add(new Line {X1=7.5,Y1=7.5,X2=12.5,Y2=12.5,Stroke=UI.Brush(TextSecondary),StrokeThickness=1.2,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round});
        Grid.SetColumn(glass,0);foot.Children.Add(glass);
        Grid.SetColumn(inputHost,2);Grid.SetColumnSpan(inputHost,6);foot.Children.Add(inputHost);
        Grid.SetColumn(add,8);add.Width=108;add.Height=28;add.Margin=new Thickness(0);add.HorizontalAlignment=HorizontalAlignment.Center;foot.Children.Add(add);
        var footBorder=new Border {Child=foot,Height=48,BorderBrush=UI.Brush(_palette.RowDivider),BorderThickness=new Thickness(0,1,0,0)};
        var cityHeader=CityGrid();
        void Head(string text,int column,TextAlignment alignment=TextAlignment.Left,double right=0,double left=0)
        {
            var label=TextLine(text,11,TextTertiary);label.VerticalAlignment=VerticalAlignment.Center;label.TextAlignment=alignment;
            if(right>0||left>0)label.Margin=new Thickness(left,0,right,0);
            Grid.SetColumn(label,column);cityHeader.Children.Add(label);
        }
        Head(Loc.T("城市","City"),2);Head(Loc.T("天气","Weather"),4,TextAlignment.Center);Head(Loc.T("温度","Temperature"),6,TextAlignment.Right,8);Head(Loc.T("数据源","Source"),8,TextAlignment.Center);
        var headBorder=new Border {Child=cityHeader,Height=30,BorderBrush=UI.Brush(_palette.RowDivider),BorderThickness=new Thickness(0,0,0,1)};
        var cityList=new StackPanel();cityList.Children.Add(headBorder);cityList.Children.Add(cityRows);cityList.Children.Add(footBorder);
        var cityCard=new Border {Child=cityList,CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(1),Margin=new Thickness(0,0,0,28)};
        cityCard.SetResourceReference(Border.BackgroundProperty,"SettingsSurface");
        cityCard.SetResourceReference(Border.BorderBrushProperty,"SettingsBorder");
        Target.Children.Add(cityCard);
        WeatherChipControls();
        LifestyleControls();
        var hefengCard = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(16, 14, 16, 16), Margin = new Thickness(0, 0, 0, 28) };
        hefengCard.SetResourceReference(Border.BackgroundProperty, "SettingsSurface");
        hefengCard.SetResourceReference(Border.BorderBrushProperty, "SettingsBorder");
        var cardBody = new StackPanel();
        var cardHeading = TextLine(Loc.T("和风天气", "QWeather"), 14, TextPrimary, FontWeights.SemiBold);
        cardHeading.VerticalAlignment = VerticalAlignment.Center;
        // 标题右侧的按钮既显示连通性状态，也负责发起手动测试。
        var cardHeadingRow = new DockPanel {Margin = new Thickness(0, 0, 0, 14)};
        DockPanel.SetDock(statusButton, Dock.Right); cardHeadingRow.Children.Add(statusButton); cardHeadingRow.Children.Add(cardHeading);
        cardBody.Children.Add(cardHeadingRow);
        var cardGrid = new Grid();
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition());
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition());
        cardGrid.RowDefinitions.Add(new RowDefinition());
        cardGrid.RowDefinitions.Add(new RowDefinition());
        FrameworkElement CardField(string label, FrameworkElement input, FrameworkElement? counter, int row, int column)
        {
            var cell = new StackPanel();
            if (row > 0) cell.Margin = new Thickness(0, 14, 0, 0);
            var caption = TextLine(label, 12, TextSecondary);
            caption.VerticalAlignment = VerticalAlignment.Center;
            var captionLine = new DockPanel();
            if (counter is not null)
            {
                DockPanel.SetDock(counter, Dock.Right); counter.VerticalAlignment = VerticalAlignment.Center;
                captionLine.Children.Add(counter);
            }
            captionLine.Children.Add(caption);
            captionLine.Margin = new Thickness(0, 0, 0, 6);
            cell.Children.Add(captionLine);
            input.HorizontalAlignment = HorizontalAlignment.Stretch;
            cell.Children.Add(input);
            Grid.SetRow(cell, row); Grid.SetColumn(cell, column);
            return cell;
        }
        TextBox TextField(string value, Action<string> save)
        {
            var box = new TextBox { Style = (Style)FindResource("SettingsField"), Text = value };
            box.TextChanged += (_, _) => save(box.Text);
            return box;
        }
        PasswordBox SecretField()
        {
            var box = new PasswordBox { Style = (Style)FindResource("SettingsPassword"), MaxLength = 4096 };
            string saved;
            try { saved = WeatherSecrets.Key; }
            catch
            {
                box.IsEnabled = false;
                var message = Loc.T("无法读取已保存的密钥。请检查密钥文件后重新打开设置。", "Cannot read the saved key. Check the secret store and reopen settings.");
                cardBody.Children.Add(TextLine(message, 11, TextSecondary));
                return box;
            }
            box.Password = saved;
            bool restoring = false;
            box.PasswordChanged += (_, _) =>
            {
                if (restoring) return;
                string value = box.Password;
                try { WeatherSecrets.Write(value); }
                catch
                {
                    restoring = true;
                    try { box.Password = saved; } finally { restoring = false; }
                    MessageBox.Show(this, Loc.T("密钥保存失败。请检查写入权限后重试。", "Could not save the key. Check write permissions and retry."));
                    return;
                }
                saved = value;
                state.Schedule.HeFengKeyConfigured = value.Trim().Length > 0;
                Save();
                HeFengMonitor.Invalidate();
            };
            return box;
        }
        // 已用/上限计数：挂在字段标签右侧；上限为 0 时分母显示 ∞。改动上限后立即刷新。
        void UpdateCounters()
        {
            var used=WeatherSources.All.Select(source=>(Name:source.Info.Id=="hefeng"?Loc.T("和风","QWeather"):source.Info.Name,Count:RequestGate.Today(source.Info.Id)))
                .Append((Name:ChinaDailySupplement.Name,Count:RequestGate.Today(ChinaDailySupplement.Id))).Append((Name:Loc.T("和风地名","QWeather search"),Count:RequestGate.Today(HeFengSource.GeoId))).Where(item=>item.Count>0).ToArray();
            requestsToday.Text = used.Length==0?Loc.T("尚未发起请求","No requests yet"):string.Join(" · ",used.Select(item=>$"{item.Name} {item.Count}"));
            string Ratio(int used,int limit)=>$"{used}/{(limit==0?"∞":limit.ToString())}";
            dailyCounter.Text=Ratio(RequestGate.Today("hefeng"),state.Schedule.HeFengDailyLimit);
            monthlyCounter.Text=Ratio(RequestGate.ThisMonth("hefeng"),state.Schedule.HeFengMonthlyLimit);
        }
        UpdateCounters();
        TextBox LimitField(Func<int> read, Action<int> write, Action? changed = null)
        {
            // 0 在界面上显示成「无限」，写回去的仍然是 int 0；聚焦时清空占位文字，直接输入数字即可。
            string unlimited = Loc.T("无限", "Unlimited");
            var input = new TextBox { Style = (Style)FindResource("SettingsField") };
            void Show() => input.Text = read() == 0 ? unlimited : read().ToString();
            Show();
            void Commit()
            {
                string text = input.Text.Trim();
                int value;
                if (text.Length == 0 || text == unlimited) value = 0;
                else if (!int.TryParse(text, out value) || value < 0) value = read();
                if (value != read()) { write(value); Save(); changed?.Invoke(); }
                Show();
            }
            input.GotKeyboardFocus += (_, _) => { if (read() == 0) input.Clear(); };
            input.LostKeyboardFocus += (_, _) => Commit();
            input.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Commit(); e.Handled = true; } };
            return input;
        }
        cardGrid.Children.Add(CardField(Loc.T("API Host", "API Host"),
            TextField(state.Schedule.HeFengHost, text => { state.Schedule.HeFengHost = text; Save(); HeFengMonitor.Invalidate(); }), null, 0, 0));
        cardGrid.Children.Add(CardField(Loc.T("API Key", "API Key"),
            SecretField(), null, 0, 2));
        cardGrid.Children.Add(CardField(Loc.T("每日上限", "Daily limit"),
            LimitField(() => state.Schedule.HeFengDailyLimit, v => state.Schedule.HeFengDailyLimit = v, UpdateCounters), dailyCounter, 1, 0));
        cardGrid.Children.Add(CardField(Loc.T("每月上限", "Monthly limit"),
            LimitField(() => state.Schedule.HeFengMonthlyLimit, v => state.Schedule.HeFengMonthlyLimit = v, UpdateCounters), monthlyCounter, 1, 2));
        cardBody.Children.Add(cardGrid);
        hefengCard.Child = cardBody;
        Target.Children.Add(hefengCard);
        // 更新与请求状态放页尾：城市与来源才是高频改动项，运行状态看一眼就够。
        SectionTitle(Loc.T("更新与数据","Updates and data"));Group(Row(Loc.T("自动更新","Auto update"),TextLine(Loc.T("每 15 分钟","Every 15 minutes"),12,TextSecondary)),Row(Loc.T("离线时","Offline"),TextLine(Loc.T("自动使用本地缓存","Falls back to the local cache"),12,TextSecondary)));
        // 请求与缓存：按用户指定来源请求 + 分域缓存，这里把当天的实际请求量和磁盘占用摆出来，配额一目了然。
        SectionTitle(Loc.T("请求与缓存","Requests and cache"));
        var usage=WeatherCache.Usage();
        Group(Row(Loc.T("今日请求","Requests today"),requestsToday),
              Row(Loc.T("本地缓存","Local cache"),TextLine(Loc.T($"{usage.Count} 项 · {usage.Bytes/1024.0:0} KB（14 天未用自动清理）",$"{usage.Count} entries · {usage.Bytes/1024.0:0} KB (unused entries purged after 14 days)"),12,TextSecondary)));
        preview.RefreshRequested += async () =>
        {
            using var fresh = WeatherCache.Fresh();
            await RefreshCityWeatherAsync(state.Selected >= 0 && state.Selected < state.Cities.Count ? state.Cities[state.Selected] : null);
        };
        void UpdateSnapshots() { Fill(); preview.RenderWeatherCard(); }

        cityRows.Unloaded+=(_,_)=>{WeatherData.SnapshotChanged -= UpdateSnapshots;HeFengMonitor.Changed -= OnStatusChanged;active=false;lifetime.Cancel();lifetime.Dispose();pending?.Cancel();pending?.Dispose();pending=null;fetchCts?.Cancel();fetchCts?.Dispose();fetchCts=null;popup.IsOpen=false;ShowSearching(false);};

        // 添加城市后立即拉取一次天气写入缓存，列表温度随之刷新；未就绪时该行不显示温度，删除或离开页面的请求一律作废。
        HeFengMonitor.Changed += OnStatusChanged;
        WeatherData.SnapshotChanged += UpdateSnapshots;

        async Task RefreshCityWeatherAsync(City? selectedCity)
        {
            if (selectedCity is null) locationVersion++;
            fetchCts?.Cancel();
            using var request = new CancellationTokenSource();
            fetchCts = request;
            var token = request.Token;
            var sourceId = state.Schedule.SourceFor(selectedCity);
            try
            {
                var city = selectedCity ?? await WeatherData.LocateAsync(state.Schedule, token);
                if (city is null) return;
                var snapshot = await WeatherData.CurrentAsync(city, state.Schedule, sourceId, token);
                if (!active || token.IsCancellationRequested || sourceId != state.Schedule.SourceFor(selectedCity)
                    || (selectedCity is not null && !state.Cities.Contains(selectedCity))) return;
                if (selectedCity is null) { state.LocatedCity = city; autoName = city.Name; }
                var key = WeatherData.CacheKey(city, sourceId);
                if (WeatherData.HasWeather(snapshot)) WeatherData.SaveSnapshot(key, snapshot, selectedCity is null ? key : null);
            }
            catch { }
            finally { UpdateCounters(); if (ReferenceEquals(fetchCts, request)) fetchCts = null; }
        }
    }
    // 排版区标题沿用「显示的内容/页面顺序」一档的样式：13px 主色半粗，比通用小节标题更醒目。
    private void ChipSectionTitle(string title, FrameworkElement? action = null, double top = 0)
    {
        var label = TextLine(title, 13, TextPrimary, FontWeights.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel { Margin = new Thickness(0, top, 0, 10) };
        if (action is not null) { DockPanel.SetDock(action, Dock.Right); action.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(action); }
        row.Children.Add(label);
        Target.Children.Add(row);
    }

    // 天气选择胶囊：用各项自己的图标，不复用监控页的角色圆点胶囊。
    private ToggleButton WeatherPickChip(UIElement icon, string name, string help)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = name, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var chip = new ToggleButton { Style = (Style)FindResource("SettingsPickChip"), Content = content, Margin = new Thickness(0, 0, 8, 8), ToolTip = help };
        AutomationProperties.SetName(chip, Loc.T("显示 ", "Show: ") + name);
        AutomationProperties.SetHelpText(chip, help);
        return chip;
    }

    private void WeatherChipControls()
    {
        ChipSectionTitle(Loc.T("实况信息", "Current conditions"));
        var grid = new UniformGrid { Rows = 2, Columns = 6 };
        var hint = Loc.T("点击显示或隐藏，拖动调整顺序。", "Click to show or hide. Drag to reorder.");
        Target.Children.Add(grid);
        void Draw()
        {
            grid.Children.Clear();
            // 设置页是固定开关矩阵：隐藏只改选中态，不按可见性重排，也不借用主页的跨行补齐逻辑。
            foreach (var item in S.Weather.Chips)
            {
                var label = item.Metric switch
                {
                    WeatherMetric.Wind => Loc.T("风速", "Wind"), WeatherMetric.Feels => Loc.T("体感", "Feels like"),
                    WeatherMetric.Humidity => Loc.T("湿度", "Humidity"), WeatherMetric.Gust => Loc.T("阵风", "Gusts"),
                    WeatherMetric.DewPoint => Loc.T("露点", "Dew point"), WeatherMetric.Celestial => Loc.T("天文", "Astronomy"),
                    WeatherMetric.Precip => Loc.T("降水", "Precipitation"), WeatherMetric.Pressure => Loc.T("气压", "Pressure"),
                    WeatherMetric.Aqi => Loc.T("空气质量", "Air quality"), WeatherMetric.Cloud => Loc.T("云量", "Cloud"),
                    WeatherMetric.Visibility => Loc.T("能见度", "Visibility"), _ => Loc.T("紫外线", "UV")
                };
                var icon = new WeatherMetaIcon(WeatherVisuals.MetricKind(item.Metric), WeatherVisuals.MetricInk(item.Metric))
                { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
                var chip = WeatherPickChip(icon, label, hint);
                chip.IsChecked = item.Visible;
                chip.Tag = item.Metric;
                chip.RenderTransform = new TranslateTransform();
                chip.Click += (_, _) => Resize(settings =>
                {
                    var chips = settings.Weather.Chips;
                    int index = chips.FindIndex(c => c.Metric == item.Metric);
                    chips[index] = chips[index] with { Visible = chip.IsChecked == true };
                }, DeckPage.Weather);
                // 拖拽已经改好 grid.Children，直接按当前顺序存回去即可，不要再按可见性重排。
                AttachWeatherChipDrag(chip, grid, () => Resize(settings =>
                {
                    var chips = settings.Weather.Chips.ToDictionary(c => c.Metric);
                    settings.Weather.Chips = grid.Children.Cast<ToggleButton>().Select(c => chips[(WeatherMetric)c.Tag]).ToList();
                }, DeckPage.Weather));
                grid.Children.Add(chip);
            }
        }
        Draw();
    }

    private void LifestyleControls()
    {
        ChipSectionTitle(Loc.T("生活指数", "Lifestyle indices"), top: 16);
        static string Label(LifestyleIndex type) => type switch
        {
            LifestyleIndex.Sport => Loc.T("运动", "Sport"),
            LifestyleIndex.CarWash => Loc.T("洗车", "Car wash"),
            LifestyleIndex.Clothing => Loc.T("穿衣", "Clothing"),
            LifestyleIndex.Fishing => Loc.T("钓鱼", "Fishing"),
            LifestyleIndex.Ultraviolet => Loc.T("紫外线", "UV"),
            LifestyleIndex.Travel => Loc.T("旅游", "Travel"),
            LifestyleIndex.Pollen => Loc.T("花粉过敏", "Pollen"),
            LifestyleIndex.Comfort => Loc.T("舒适度", "Comfort"),
            LifestyleIndex.ColdRisk => Loc.T("感冒", "Cold risk"),
            LifestyleIndex.AirDispersion => Loc.T("空气扩散", "Air dispersion"),
            LifestyleIndex.AirConditioning => Loc.T("空调开启", "Air conditioning"),
            LifestyleIndex.Sunglasses => Loc.T("太阳镜", "Sunglasses"),
            LifestyleIndex.Makeup => Loc.T("化妆", "Makeup"),
            LifestyleIndex.DryingClothes => Loc.T("晾晒", "Drying clothes"),
            LifestyleIndex.Traffic => Loc.T("交通", "Traffic"),
            LifestyleIndex.SunProtection => Loc.T("防晒", "Sun protection"),
            _ => type.ToString()
        };
        var grid = new UniformGrid { Rows = 8, Columns = 2 };
        Target.Children.Add(grid);
        void SaveLayout() => Resize(settings => settings.Weather.LifestyleIndices = grid.Children.Cast<ToggleButton>()
            .Select(button => new LifestyleSlot((LifestyleIndex)button.Tag, button.IsChecked == true)).ToList(), DeckPage.Weather);
        foreach (var slot in S.Weather.LifestyleIndices)
        {
            var icon = new TextBlock { Text = WeatherVisuals.LifestyleIcon(slot.Type), FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol"),
                FontSize = 14, Width = 18, VerticalAlignment = VerticalAlignment.Center };
            var chip = WeatherPickChip(icon, Label(slot.Type), Loc.T("点击显示或隐藏，拖动调整顺序", "Click to show or hide. Drag to reorder."));
            chip.Tag = slot.Type;
            chip.IsChecked = slot.Visible;
            chip.RenderTransform = new TranslateTransform();
            chip.Click += (_, _) => SaveLayout();
            AttachWeatherChipDrag(chip, grid, SaveLayout);
            grid.Children.Add(chip);
        }
    }

    private static void AttachWeatherChipDrag(ToggleButton chip, UniformGrid grid, Action saveOrder)
    {
        Point? press = null;
        bool dragging = false;
        int from = 0, target = 0;
        Point Slot(int i) => new((i % grid.Columns) * grid.ActualWidth / grid.Columns,
                                 (i / grid.Columns) * grid.ActualHeight / grid.Rows);
        static void Offset(UIElement element, Vector delta, bool glide)
        {
            var transform = (TranslateTransform)element.RenderTransform;
            void Set(System.Windows.DependencyProperty property, double value)
            {
                double start = (double)transform.GetValue(property);
                transform.BeginAnimation(property, null);
                transform.SetValue(property, value);
                if (glide && Soft.Enabled && Math.Abs(start-value) > .1)
                    transform.BeginAnimation(property, new DoubleAnimation(start,value,TimeSpan.FromMilliseconds(Soft.Choose))
                        { EasingFunction=Soft.Out, FillBehavior=FillBehavior.Stop });
            }
            Set(TranslateTransform.XProperty,delta.X);
            Set(TranslateTransform.YProperty,delta.Y);
        }
        void Finish(bool commit)
        {
            press = null;
            if (!dragging) return;
            dragging = false;
            if (chip.IsMouseCaptured) chip.ReleaseMouseCapture();
            Panel.SetZIndex(chip,0);
            var positions = grid.Children.Cast<UIElement>().Select((c,i) =>
                (c, pos: Slot(i) + new Vector(((TranslateTransform)c.RenderTransform).X, ((TranslateTransform)c.RenderTransform).Y))).ToArray();
            if (commit && from != target)
            {
                if (from / grid.Columns != target / grid.Columns)
                {
                    var other = grid.Children[target];
                    grid.Children.RemoveAt(target);
                    grid.Children.Remove(chip);
                    if (from < target) { grid.Children.Insert(from,other); grid.Children.Insert(target,chip); }
                    else { grid.Children.Insert(target,chip); grid.Children.Insert(from,other); }
                }
                else
                {
                    grid.Children.RemoveAt(from);
                    grid.Children.Insert(target,chip);
                }
            }
            if (commit && from != target) saveOrder();
            foreach (var (c,pos) in positions)
            {
                Offset(c,pos-Slot(grid.Children.IndexOf(c)),false);
                Offset(c,new Vector(),true);
            }

        }
        chip.PreviewMouseLeftButtonDown += (_,e) => press=e.GetPosition(grid);
        chip.PreviewMouseMove += (_,e) =>
        {
            if (press is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
            var delta=e.GetPosition(grid)-start;
            if (!dragging)
            {
                if (Math.Abs(delta.X)<SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(delta.Y)<SystemParameters.MinimumVerticalDragDistance) return;
                from=target=grid.Children.IndexOf(chip);
                dragging=true;
                Panel.SetZIndex(chip,2);
            }
            Offset(chip,delta,false);
            var center=Slot(from)+delta;
            int column=Math.Clamp((int)Math.Floor(center.X*grid.Columns/grid.ActualWidth+.5),0,grid.Columns-1);
            int row=Math.Clamp((int)Math.Floor(center.Y*grid.Rows/grid.ActualHeight+.5),0,grid.Rows-1);
            int next=Math.Min(grid.Children.Count-1,row*grid.Columns+column);
            if (next==target) return;
            target=next;
            for(int i=0;i<grid.Children.Count;i++)
            {
                if(i==from) continue;
                int slot = from / grid.Columns != target / grid.Columns
                    ? (i == target ? from : i)
                    : i>from && i<=target ? i-1 : i<from && i>=target ? i+1 : i;
                Offset(grid.Children[i],Slot(slot)-Slot(i),true);
            }
        };
        chip.PreviewMouseLeftButtonUp += (_,e) =>
        {
            if (dragging) e.Handled=true;
            Finish(true);
        };
        chip.LostMouseCapture += (_,_) => Finish(false);
        chip.PreviewKeyDown += (_,e) => { if(dragging && e.Key==Key.Escape){Finish(false);e.Handled=true;} };
    }

    private void WeatherSizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension(Loc.T("宽度", "Width"), S.WeatherWidth, 440, 1200, value => Resize(s => s.WeatherWidth = value, DeckPage.Weather)); width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.WeatherHeight, 140, 400, value => Resize(s => s.WeatherHeight = value, DeckPage.Weather)); height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
    }
}
// 设置页天气预览卡：用拟真的天气渐变绘制，不跟随设置主题；只展示快照，不承载也不修改灵动岛视图。
internal sealed class WeatherSettingsCard : Border
{
    private readonly WeatherData.State state;
    private int selectedDay;
    private string? displayedKey;
    internal event Action? RefreshRequested;

    internal WeatherSettingsCard(WeatherData.State state)
    {
        this.state = state;
        Height = 184;
        RenderWeatherCard();
    }

    internal void RenderWeatherCard()
    {
        CornerRadius=new CornerRadius(14);Margin=new Thickness(0,0,0,18);ClipToBounds=true;
        var key=state.Selected>=0&&state.Selected<state.Cities.Count?state.KeyFor(state.Cities[state.Selected]):state.DisplayAutoKey;
        if (key != displayedKey) selectedDay = 0;
        displayedKey = key;
        WeatherSnapshot? snap=null;
        if(state.TrySnapshot(key,out var cached)) snap=cached;
        int code=snap?.WeatherCode??-1;bool day=snap?.IsDay??true;
        string cityName=state.Selected>=0&&state.Selected<state.Cities.Count?state.Cities[state.Selected].Name:state.LocatedCity?.Name ?? "当前位置";
        string Place(string n)=>n=="当前位置"?Loc.T("当前位置","Current location"):n;
        string Deg(double? value)=>value is { } v && double.IsFinite(v)?$"{v:0}°":"--";
        string Num(double? value,string suffix)=>value is { } v && double.IsFinite(v)?v.ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)+suffix:"--";
        string Clock(DayPoint[] days,int index,Func<DayPoint,string?> pick)=>index>=0&&index<days.Length&&pick(days[index]) is { Length: >= 16 } text?text[11..16]:"--:--";
        int liveToday=snap is null?0:WeatherClock.LiveIndex(snap);
        bool staleForecast=snap is not null&&liveToday<0;
        // 跨日缓存仍用抓取时的日期轴取数，但标签不再冒充今天/明天。
        int today=snap is null?0:staleForecast?WeatherClock.SnapshotIndex(snap):liveToday;
        int dateIndex=today+selectedDay;
        bool future=selectedDay>0;
        if(future && snap is not null){code=dateIndex>=0&&dateIndex<snap.Days.Length?(snap.Days[dateIndex].Code??-1):-1;day=true;}
        // 天空底色保持拟真的天气渐变，不掺设置页主题色；只有前景文字/图标按天空深浅做对比度校正。
        // 这样晴昼是通透的蓝天、夜晚是深邃蓝黑，浅天空（雾、雪）时前景自动压深保证可读。
        var sky=WeatherVisuals.Sky(code,day);
        Background=snap is null ? new SolidColorBrush(Color.FromRgb(49,63,81)) : sky;
        var stops=sky.GradientStops.Select(stop=>new ThemeColor(255,stop.Color.R,stop.Color.G,stop.Color.B)).ToArray();
        Color InkColor(string hex)=>PanelTheme.ToColor(ThemeColor.Parse(hex).EnsureContrast(stops,PanelThemes.TextContrast));
        Brush Ink(string hex)=>new SolidColorBrush(InkColor(hex));
        var primary=Ink("#F4F8FF");
        var secondary=Ink("#D3E3F7");

        var grid=new Grid();grid.RowDefinitions.Add(new RowDefinition {Height=new GridLength(34)});grid.RowDefinitions.Add(new RowDefinition());grid.RowDefinitions.Add(new RowDefinition {Height=new GridLength(1,GridUnitType.Auto)});
        var head=new Grid {VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,12,0)};
        head.ColumnDefinitions.Add(new ColumnDefinition());head.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        var locIcon=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        locIcon.Children.Add(new WeatherMetaIcon(WeatherMetaKind.Location,InkColor("#CDDFF7")){Width=12,Height=12,VerticalAlignment=VerticalAlignment.Center});
        // 城市名只有一份完整名称：选中城市取 City.Name，自动定位退回快照 Name（也是完整名称）。
        string headerName=state.Selected>=0&&state.Selected<state.Cities.Count?cityName:(snap?.Name??cityName);
        locIcon.Children.Add(new TextBlock {Text=Place(headerName),FontSize=12,FontWeight=FontWeights.SemiBold,Foreground=secondary,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,0,0,0),TextTrimming=TextTrimming.CharacterEllipsis});
        var headRight=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};
        var upd=new TextBlock {Text=snap is null?"":Loc.T($"更新于 {snap.FetchedAt:HH:mm}",$"Updated {snap.FetchedAt:HH:mm}"),FontSize=10.5,Foreground=secondary,VerticalAlignment=VerticalAlignment.Center};
        string source=snap is not null&&snap.Sources.TryGetValue("Daily",out var daily)&&daily.Length>0?daily:WeatherSources.Name(state.Schedule.SourceFor(state.Selected>=0&&state.Selected<state.Cities.Count?state.Cities[state.Selected]:null));
        var src=new TextBlock {Text=snap is null?"":source,FontSize=10.5,Foreground=secondary,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(10,0,0,0),TextTrimming=TextTrimming.CharacterEllipsis};
        headRight.Children.Add(upd);headRight.Children.Add(src);
        head.Children.Add(locIcon);Grid.SetColumn(headRight,1);head.Children.Add(headRight);
        var headBar=new Border {Height=34,CornerRadius=new CornerRadius(13,13,0,0),Background=new SolidColorBrush(Color.FromArgb(46,0,0,0)),BorderBrush=new SolidColorBrush(Color.FromArgb(24,255,255,255)),BorderThickness=new Thickness(0,0,0,1),Child=head};
        // 左侧「现在」占星号列，右侧「三天」按固定列宽靠右收成一小簇。
        // 三天如果一起放进星号列，UniformGrid 会把可用宽度三等分：内容只有几十像素、格子却有一百多，
        // 数字被拉开、今天的高亮框也胀成一大块——这就是它看起来「胖」的原因。
        var body=new Grid();body.ColumnDefinitions.Add(new ColumnDefinition());body.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        var now=new StackPanel {Margin=new Thickness(20,14,0,14),VerticalAlignment=VerticalAlignment.Center};
        // 一行排开：天气图标 → 天气名 → 当前温度，高度由 66px 图标独占，天气名不再另起一行垫在图标下方。
        var first=new StackPanel {Orientation=Orientation.Horizontal};var icon=new Border {Background=Brushes.Transparent, Width=66,Height=66,Margin=new Thickness(0,0,14,0),VerticalAlignment=VerticalAlignment.Center,
            Child=snap is null || code<0
                ? new TextBlock {Text="↻", FontSize=40,Foreground=primary,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}
                : new WeatherGlyph(code,day,animated:true), ToolTip=Loc.T("刷新天气","Refresh weather")};
        AttachAction(icon,()=>RefreshRequested?.Invoke());
        first.Children.Add(icon);
        first.Children.Add(new TextBlock {Text=snap is null?"":code<0?"":WeatherVisuals.Condition(code),FontSize=17,FontWeight=FontWeights.Medium,Foreground=secondary,VerticalAlignment=VerticalAlignment.Center});
        first.Children.Add(new TextBlock {Text=snap is null?"":Deg(future ? dateIndex>=0&&dateIndex<snap.Days.Length?snap.Days[dateIndex].HighC:null : snap.Temperature),FontSize=46,FontWeight=FontWeights.Light,Foreground=primary,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,0,0)});
        now.Children.Add(first);
        var forecast=new UniformGrid {Rows=1,Columns=3,Margin=new Thickness(12,0,14,0),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Right};

        for(int i=0;i<3;i++) {
            bool selectable = false;
            string dayTitle="";int dCode=-1;string rain="";string high="";string low="";
            if(snap is not null&&snap.Days.Length>0) {
                int idx=today+i; if(idx<0||idx>=snap.Days.Length) break;
                if (!DateTime.TryParse(snap.Days[idx].Date,out var date)) continue;
         string weekday=Loc.T(new[]{"周日","周一","周二","周三","周四","周五","周六"}[(int)date.DayOfWeek],new[]{"Sun","Mon","Tue","Wed","Thu","Fri","Sat"}[(int)date.DayOfWeek]);
         dayTitle=staleForecast?(i==0?Loc.T("已过期","Expired"):weekday):i==0?Loc.T("今天","Today"):i==1?Loc.T("明天","Tomorrow"):weekday;
                var dayPoint=snap.Days[idx];
                dCode=dayPoint.Code??-1;
                int r=dayPoint.RainProb??-1;rain=r>=20?r+"%":"–";
                selectable = dCode >= 0 || r >= 0 || dayPoint.HighC is { } hi && double.IsFinite(hi)
                    || dayPoint.LowC is { } lo && double.IsFinite(lo) || i == 0 && snap.Temperature is { } ct && double.IsFinite(ct);
                high=Deg(dayPoint.HighC);low=Deg(dayPoint.LowC);
            }
            // 每列固定 64px：正好装下「今天 / 图标 / 降水 / 15°/4°」，三列合计 192，右边留出呼吸位。
            var col=new StackPanel {Width=64,VerticalAlignment=VerticalAlignment.Center};
            col.Children.Add(new TextBlock {Text=dayTitle,FontSize=11.5,FontWeight=FontWeights.SemiBold,Foreground=secondary,TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.NoWrap});
            col.Children.Add(new WeatherGlyph(dCode,true) {Width=28,Height=28,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,5,0,3)});
            col.Children.Add(new TextBlock {Text=rain,FontSize=10.5,Foreground=secondary,TextAlignment=TextAlignment.Center,Height=13,TextWrapping=TextWrapping.NoWrap});
            // 高低温并成一行：每列只有一天，分成两行会把格子撑得又高又空，两个数字还各占一侧。
            var range=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,1,0,0)};
            range.Children.Add(new TextBlock {Text=high,FontSize=14.5,FontWeight=FontWeights.SemiBold,Foreground=primary,TextWrapping=TextWrapping.NoWrap});
            range.Children.Add(new TextBlock {Text="/",FontSize=10.5,Foreground=secondary,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(2,0,2,2)});
            range.Children.Add(new TextBlock {Text=low,FontSize=11.5,Foreground=secondary,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(0,0,0,2),TextWrapping=TextWrapping.NoWrap});
            col.Children.Add(range);
            // 三列套同一层壳，只有今天有底色与描边：保持等宽，不会因为高亮框多出 2px 而串位。
            var holder=new Border {Child=col,Padding=new Thickness(0,6,0,6),CornerRadius=new CornerRadius(11),BorderThickness=new Thickness(1),
                Margin=new Thickness(2,0,2,0),Background=new SolidColorBrush(Color.FromArgb((byte)(i==selectedDay?34:14),255,255,255)),
                BorderBrush=i==selectedDay?new SolidColorBrush(Color.FromArgb(56,255,255,255)):Brushes.Transparent};
            int offset=i;
            if(selectable) AttachAction(holder,()=>{selectedDay=offset;RenderWeatherCard();});
            else if(snap is null) col.Visibility=Visibility.Hidden;
            forecast.Children.Add(holder);
        }
        body.Children.Add(now);Grid.SetColumn(forecast,1);body.Children.Add(forecast);
        var meta=new WrapPanel {Margin=new Thickness(16,0,14,0),VerticalAlignment=VerticalAlignment.Center};
        var footer=new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        var selectedDayPoint=snap is not null && dateIndex>=0 && dateIndex<snap.Days.Length ? snap.Days[dateIndex] : null;
        var humidity=future ? selectedDayPoint?.Humidity : snap?.HumidityPercent;
        double wind=future ? selectedDayPoint?.WindMaxKmh??double.NaN : snap?.WindKmh??double.NaN;
        // 风向文字优先，缺失时按度数经 8 方位归一，与实况风速卡同一套规则（DegreesFromText → Compass8Angle）。
        string windDir=future ? selectedDayPoint?.WindDir??"" : snap?.WindDirection??"";
        double windDeg=future ? selectedDayPoint?.WindDirDeg??double.NaN : snap?.WindDirectionDeg??double.NaN;
        if(string.IsNullOrWhiteSpace(windDir)&&!double.IsNaN(windDeg)) windDir=WeatherCodes.DegreeToCompass(windDeg)+Loc.T("风","");
        double windAngle=WeatherCodes.Wind8Angle(windDir,windDeg);
        string meta1=snap is null?"":future ? Loc.T($"最低 {Deg(selectedDayPoint?.LowC)}",$"Low {Deg(selectedDayPoint?.LowC)}") : Loc.T($"体感 {Num(snap.FeelsLike,"°")}",$"Feels like {Num(snap.FeelsLike,"°")}");
        string meta2=snap is null?"":Loc.T($"湿度 {(humidity is { } h && h>=0?h+"%":"--")}",$"Humidity {(humidity is { } h2 && h2>=0?h2+"%":"--")}");
        // 风速文本与灵动岛风速卡同构：有方位先出方位 + 速度，没有才退回「风 + 速度」。
        string meta3=snap is null?"":windDir.Length>0
            ? Loc.T($"{windDir} {Num(wind," km/h")}",$"{windDir} {Num(wind," km/h")}")
            : Loc.T($"风 {Num(wind," km/h")}",$"Wind {Num(wind," km/h")}");
        foreach(var (kind,ink,text) in new[] {
            (WeatherMetaKind.Thermo,InkColor("#FFB066"),meta1),
            (WeatherMetaKind.Droplet,InkColor("#70B2FF"),meta2),
            (WeatherMetaKind.Wind,InkColor("#8CC8DC"),meta3) }) {
            var item=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,16,0)};
            // 风条目用 8 方位旋转箭头指示风向，与灵动岛实况风速卡一致；没有有效角度才退回通用风图标。
            if(kind==WeatherMetaKind.Wind && !double.IsNaN(windAngle))
            {
                var windArrow=WeatherVisuals.WindArrowIcon(windAngle,ink);
                windArrow.Width=14;windArrow.Height=14;windArrow.Margin=new Thickness(0,0,5,0);
                item.Children.Add(windArrow);
            }
            else
                item.Children.Add(new WeatherMetaIcon(kind,ink) {Width=14,Height=14,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,5,0)});
            item.Children.Add(new TextBlock {Text=text,FontSize=11,Foreground=secondary,VerticalAlignment=VerticalAlignment.Center});
            meta.Children.Add(item);
        }
        // 日升日落条目与其他元信息行水平居中对齐：图标与文本各自垂直居中，整块在 footer 右侧垂直居中。
        var sun=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};
        sun.Children.Add(new WeatherMetaIcon(WeatherMetaKind.Sunrise,InkColor("#FFC85A")){Width=14,Height=14,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,5,0)});
        sun.Children.Add(new TextBlock {Text=snap is null?"":Loc.T($"日升 {Clock(snap.Days,dateIndex,d=>d.Sunrise)} · 日落 {Clock(snap.Days,dateIndex,d=>d.Sunset)}",$"Sunrise {Clock(snap.Days,dateIndex,d=>d.Sunrise)} · Sunset {Clock(snap.Days,dateIndex,d=>d.Sunset)}"),FontSize=11,Foreground=secondary,VerticalAlignment=VerticalAlignment.Center});
        footer.Children.Add(meta);
        Grid.SetColumn(sun,1);
        sun.Margin=new Thickness(0,0,14,0);
        footer.Children.Add(sun);
        var strip=new Border {Height=34,Padding=new Thickness(0,6,0,6),CornerRadius=new CornerRadius(0,0,13,13),Background=new SolidColorBrush(Color.FromArgb(36,0,0,0)),BorderBrush=new SolidColorBrush(Color.FromArgb(24,255,255,255)),BorderThickness=new Thickness(0,1,0,0),Child=footer};
        grid.Children.Add(headBar);Grid.SetRow(body,1);grid.Children.Add(body);Grid.SetRow(strip,2);grid.Children.Add(strip);Child=grid;
    }
    private static void AttachAction(FrameworkElement target, Action action)
    {
        target.Cursor=System.Windows.Input.Cursors.Hand;
        target.Focusable=true;
        WeatherMotion.Attach(target);
        target.MouseLeftButtonUp+=(_,e)=>{e.Handled=true;action();};
        target.KeyDown+=(_,e)=>{
            if(e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space){e.Handled=true;action();}
        };
    }}