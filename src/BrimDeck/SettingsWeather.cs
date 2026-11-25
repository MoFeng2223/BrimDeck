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
        string autoName="当前位置";if(state.AutoKey.Length>0&&state.Cache.TryGetValue(state.AutoKey,out var autoSnap))autoName=autoSnap.Name;
        var preview=new WeatherSettingsCard(state);
        // 城市配额徽章：挂在「当前城市」标题右侧的细描边胶囊里，还有空位用绿色、加满转红色。
        var cityQuotaText=TextLine("",11,TextPrimary);cityQuotaText.VerticalAlignment=VerticalAlignment.Center;
        var cityQuota=new Border {Child=cityQuotaText,CornerRadius=new CornerRadius(10),Padding=new Thickness(9,2,9,2),Background=Brushes.Transparent,BorderBrush=(Brush)FindResource("SettingsBorder"),BorderThickness=new Thickness(1),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,0,0)};
        void UpdateCityQuota(){cityQuotaText.Text=$"{state.Cities.Count}/{DeckSettings.MaxWeatherCities}";cityQuotaText.Foreground=UI.Brush(state.Cities.Count<DeckSettings.MaxWeatherCities?_palette.Positive:_palette.Negative);}
        void Save() {try {_app.QueueSave(); WeatherData.Save(state);}catch {}}
        void Fill()
        {
            UpdateCityQuota();
            cityRows.Children.Clear();
            var movable = new StackPanel();
            const double cityRowHeight = 54;
            for(int i=-1;i<state.Cities.Count;i++) {
                int index=i;bool selected=i==state.Selected;var city=i<0?null:state.Cities[i];
                string? temperature=null;int code=3;bool day=true;
                string name=city is null?Loc.T("自动定位","Auto location"):City.SplitLevels(city.Name)[0];
                string? subtitle=city is null?null:city.Name;
                if(city is null){
                    if(autoName.Trim().Length>0&&!autoName.Contains("当前位置"))subtitle=autoName;
                    if(state.Cache.TryGetValue(state.AutoKey,out var snap)){temperature=$"{snap.Temperature:0}°";code=snap.WeatherCode;day=snap.IsDay;if(!snap.Name.Contains("当前位置"))subtitle=snap.Name;}
                } else if(state.Cache.TryGetValue(state.KeyFor(city),out var snap)){temperature=$"{snap.Temperature:0}°";code=snap.WeatherCode;day=snap.IsDay;}
                var controls=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};var sources=WeatherSources.All;
                var picker=new ComboBox {Width=160, Margin=new Thickness(4,0,14,0), Style=(Style)FindResource("SettingsCombo"),
                    ItemsSource=sources.Select(source=>source.Info.Name).ToArray(), SelectedIndex=Array.FindIndex(sources,source=>source.Info.Id==state.Schedule.SourceFor(city))};
                PopupMotion.Attach(picker);
                picker.SelectionChanged+=(_,_)=>{
                    if(picker.SelectedIndex<0)return;
                    state.Schedule.CitySources[city?.ToString() ?? "auto"]=sources[picker.SelectedIndex].Info.Id;
                    Save();preview.RenderWeatherCard();Fill();
                    _=RefreshCityWeatherAsync(city);
                };
                controls.Children.Add(picker);
                var temperatureLabel=TextLine(temperature ?? "",14,TextPrimary);
                temperatureLabel.Width=40;
                temperatureLabel.VerticalAlignment=VerticalAlignment.Center;
                controls.Children.Add(temperatureLabel);
                void Select(){state.Selected=city is null ? -1 : state.Cities.IndexOf(city);Save();preview.RenderWeatherCard();Fill();}
                Button? remove=null;
                if(city is not null){
                    remove=new Button {Style=(Style)FindResource("SettingsGlyphButton"),ToolTip=Loc.T("删除城市","Remove city"),Width=24,Height=26,Padding=new Thickness(0),VerticalAlignment=VerticalAlignment.Center};
                    var cross=Stroke("M 0,0 L 8,8 M 8,0 L 0,8",8,8,TextSecondary);
                    cross.SetBinding(Shape.StrokeProperty,new System.Windows.Data.Binding("Foreground"){RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor,typeof(Button),1)});
                    remove.Content=cross;
                    remove.Click+=(_,_)=>{int at=state.Cities.IndexOf(city);if(at<0)return;state.Cities.RemoveAt(at);if(state.Selected==at)state.Selected=-1;else if(state.Selected>at)state.Selected--;Save();preview.RenderWeatherCard();Fill();UpdateAdd();};
                }
                var row=Row(name,controls,subtitle is null?null:TextLine(subtitle,11,TextSecondary));row.Margin=new Thickness(42,0,8,0);
                var grid=new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(30)});
                grid.Children.Add(row);
                if(remove is not null){
                    Grid.SetColumn(remove,1);
                    remove.HorizontalAlignment=HorizontalAlignment.Right;
                    grid.Children.Add(remove);
                }
                grid.Children.Add(new WeatherGlyph(code,day) {Width=28,Height=28,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(4,0,0,0),IsHitTestVisible=false});
                if(selected){var indicator=new Border {Width=3,CornerRadius=new CornerRadius(0,2,2,0),Background=(Brush)FindResource("SettingsAccent"),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Stretch};grid.Children.Add(indicator);}
                var border=new Border {Child=grid,Background=selected?(Brush)FindResource("SettingsAccentTint"):Brushes.Transparent,CornerRadius=new CornerRadius(6),Cursor=System.Windows.Input.Cursors.Hand};border.MouseLeftButtonUp+=(_,e)=>{if(!picker.IsMouseOver && e.OriginalSource is not Button)Select();};
                var line=new Grid();
                line.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(24) });
                line.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(border,1);
                line.Children.Add(border);
                var frame=new Border { Child=line, Height=cityRowHeight, Tag=city, RenderTransform=new TranslateTransform() };
                if(city is null) { cityRows.Children.Add(frame); continue; }
                var grip=new GripHandle { Width=22, Background=Brushes.Transparent, Cursor=Cursors.SizeAll, Child=Grip(TextTertiary) };
                line.Children.Add(grip);
                movable.Children.Add(frame);
                AttachDrag(grip,name,()=>movable,cityRowHeight,list=>
                {
                    var current=state.Selected>=0 ? state.Cities[state.Selected] : null;
                    state.Cities=list.Children.OfType<Border>().Select(row=>(City)row.Tag).ToList();
                    state.Selected=current is null ? -1 : state.Cities.IndexOf(current);
                    Save();
                });
            }
            cityRows.Children.Add(movable);
        }
        Target.Children.Add(preview);SectionTitle(Loc.T("当前城市","Current city"),cityQuota);
        // 设置页打开即尝试定位：成功则刷新「自动定位」行展示当地城市名，不干扰其余行。
        async Task DetectAsync() { if(!active)return;try {var local=await WeatherData.LocateAsync(state.Schedule,CancellationToken.None);if(active&&local is not null&&!local.Name.Contains("当前位置")){autoName=local.Name;Fill();}}catch{}}
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
            if(on) spinTransform.BeginAnimation(RotateTransform.AngleProperty,spinAnimation);
            else {spinTransform.BeginAnimation(RotateTransform.AngleProperty,null);spinTransform.Angle=0;}
            loading.Visibility=on?Visibility.Visible:Visibility.Collapsed;
            if(on) empty.Visibility=Visibility.Collapsed;
        }
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
            options.SelectedIndex=-1;options.Visibility=Visibility.Visible;empty.Text=noResultText;empty.Visibility=Visibility.Collapsed;
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
                popup.IsOpen=true;
            }
            catch(OperationCanceledException){ }
            catch { if(pending is not null&&token==pending.Token){options.ItemsSource=null;options.Visibility=Visibility.Collapsed;empty.Text=failedText;empty.Visibility=Visibility.Visible;popup.IsOpen=true;} }
            finally { if(pending is not null&&token==pending.Token)ShowSearching(false); }
        };
        var controlsRow=new StackPanel {Orientation=Orientation.Horizontal};controlsRow.Children.Add(inputHost);controlsRow.Children.Add(add);
        var searchRow=Row(Loc.T("搜索城市","Search city"),controlsRow);searchRow.Margin=new Thickness(42,0,8,0);
        Group(cityRows,searchRow);
        WeatherChipControls();
        var hefengCard = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(16, 14, 16, 16), Margin = new Thickness(0, 0, 0, 28) };
        hefengCard.SetResourceReference(Border.BackgroundProperty, "SettingsSurface");
        hefengCard.SetResourceReference(Border.BorderBrushProperty, "SettingsBorder");
        var cardBody = new StackPanel();
        var cardHeading = TextLine(Loc.T("和风天气", "QWeather"), 14, TextPrimary, FontWeights.SemiBold);
        cardHeading.Margin = new Thickness(0, 0, 0, 14);
        cardBody.Children.Add(cardHeading);
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
        PasswordBox SecretField(string value, Action<string> save)
        {
            // Password 在这之后才挂事件，初始装载不会误触发一次保存。
            var box = new PasswordBox { Style = (Style)FindResource("SettingsPassword"), MaxLength = 4096, Password = value };
            box.PasswordChanged += (_, _) => save(box.Password);
            return box;
        }
        // 已用/上限计数：挂在字段标签右侧；上限为 0 时分母显示 ∞。改动上限后立即刷新。
        var dailyCounter=TextLine("",11,TextSecondary);dailyCounter.VerticalAlignment=VerticalAlignment.Center;
        var monthlyCounter=TextLine("",11,TextSecondary);monthlyCounter.VerticalAlignment=VerticalAlignment.Center;
        void UpdateCounters()
        {
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
            TextField(state.Schedule.HeFengHost, text => { state.Schedule.HeFengHost = text; Save(); }), null, 0, 0));
        cardGrid.Children.Add(CardField(Loc.T("API Key", "API Key"),
            SecretField(state.Schedule.HeFengKey, text => { state.Schedule.HeFengKey = text; Save(); }), null, 0, 2));
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
        var used=WeatherSources.All.Select(source=>(Name:source.Info.Id=="hefeng"?Loc.T("和风","QWeather"):source.Info.Name,Count:RequestGate.Today(source.Info.Id)))
            .Append((Name:ChinaDailyForecast.Name,Count:RequestGate.Today(ChinaDailyForecast.Id))).Where(item=>item.Count>0).ToArray();
        Group(Row(Loc.T("今日请求","Requests today"),TextLine(used.Length==0?Loc.T("尚未发起请求","No requests yet"):string.Join(" · ",used.Select(item=>$"{item.Name} {item.Count}")),12,TextSecondary)),
              Row(Loc.T("本地缓存","Local cache"),TextLine(Loc.T($"{usage.Count} 项 · {usage.Bytes/1024.0:0} KB（14 天未用自动清理）",$"{usage.Count} entries · {usage.Bytes/1024.0:0} KB (unused entries purged after 14 days)"),12,TextSecondary)));
        preview.RefreshRequested += async () =>
        {
            using var fresh = WeatherCache.Fresh();
            await RefreshCityWeatherAsync(state.Selected >= 0 && state.Selected < state.Cities.Count ? state.Cities[state.Selected] : null);
        };
        cityRows.Unloaded+=(_,_)=>{active=false;pending?.Cancel();pending?.Dispose();pending=null;fetchCts?.Cancel();fetchCts?.Dispose();fetchCts=null;popup.IsOpen=false;ShowSearching(false);};

        // 添加城市后立即拉取一次天气写入缓存，列表温度随之刷新；未就绪时该行不显示温度，删除或离开页面的请求一律作废。
        async Task RefreshCityWeatherAsync(City? selectedCity)
        {
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
                var key = WeatherData.CacheKey(city, sourceId);
                state.Cache[key] = snapshot;
                if (selectedCity is null) state.AutoKey = key;
                Save();
                Fill();
                preview.RenderWeatherCard();
            }
            catch { }
            finally { if (ReferenceEquals(fetchCts, request)) fetchCts = null; }
        }
    }
    private void WeatherChipControls()
    {
        SectionTitle(Loc.T("主页右上角胶囊", "Weather summary chips"));
        var grid = new UniformGrid { Rows = 2, Columns = 6 };
        var hint = Loc.T("点击显示或隐藏，拖动调整顺序。", "Click to show or hide. Drag to reorder.");
        Target.Children.Add(grid);
        void Draw()
        {
            grid.Children.Clear();
            foreach (var item in S.Weather.ChipRows(6).SelectMany(row => row.OrderBy(chip => chip.Visible)))
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
                var refresh = new List<Action>();
                var chip = PickChip(label, [], hint, refresh);
                foreach (var update in refresh) update();
                chip.IsChecked = item.Visible;
                chip.Tag = item.Metric;
                chip.RenderTransform = new TranslateTransform();
                chip.Click += (_, _) =>
                {
                    Resize(settings =>
                {
                        var chips = settings.Weather.Chips;
                        int index = chips.FindIndex(c => c.Metric == item.Metric);
                        chips[index] = chips[index] with { Visible = chip.IsChecked == true };
                    }, DeckPage.Weather);
                    Draw();
                };
                AttachWeatherChipDrag(chip, grid, () =>
                {
                    var buttons = grid.Children.Cast<ToggleButton>().ToDictionary(c => (WeatherMetric)c.Tag);
                    Resize(settings =>
                    {
                        var chips = settings.Weather.Chips.ToDictionary(c => c.Metric);
                        settings.Weather.Chips = grid.Children.Cast<ToggleButton>().Select(c => chips[(WeatherMetric)c.Tag]).ToList();
                    }, DeckPage.Weather);
                    var ordered = S.Weather.ChipRows(6).SelectMany(row => row.OrderBy(c => c.Visible));
                    foreach (var (entry, index) in ordered.Select((entry, index) => (entry, index)))
                    {
                        var button = buttons[entry.Metric];
                        if (grid.Children.IndexOf(button) == index) continue;
                        grid.Children.Remove(button);
                        grid.Children.Insert(index, button);
                    }
                });
                grid.Children.Add(chip);
            }
        }
        Draw();
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
// Settings-only preview: draws the same glass card as the island's summary, but never hosts or mutates the island view.
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
        var key=state.Selected>=0&&state.Selected<state.Cities.Count?state.KeyFor(state.Cities[state.Selected]):state.AutoKey;
        if (key != displayedKey) selectedDay = 0;
        displayedKey = key;
        WeatherSnapshot? snap=null;
        if(state.Cache.TryGetValue(key,out var cached)) snap=cached;
        int code=snap?.WeatherCode??0;bool day=snap?.IsDay??true;
        string cityName=state.Selected>=0&&state.Selected<state.Cities.Count?state.Cities[state.Selected].Name:"当前位置";
        string Place(string n)=>n=="当前位置"?Loc.T("当前位置","Current location"):n;
        string Deg(double value)=>double.IsNaN(value)?"--":$"{value:0}°";
        string Num(double value,string suffix)=>double.IsNaN(value)?"--":value.ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)+suffix;
        string Clock(string[] times,int index)=>index>=0&&index<times.Length&&times[index].Length>=16?times[index][11..16]:"--:--";
        int today=snap is null?0:WeatherData.TodayIndex(snap);
        int dateIndex=today+selectedDay;
        bool future=selectedDay>0;
        if(future && snap is not null){code=(uint)dateIndex < (uint)snap.DayCodes.Length ? snap.DayCodes[dateIndex] : -1;day=true;}
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
        var first=new StackPanel {Orientation=Orientation.Horizontal};var icon=new Border {Background=Brushes.Transparent, Width=66,Height=66,Margin=new Thickness(0,0,12,0),
            Child=snap is null || code<0
                ? new TextBlock {Text="↻", FontSize=40,Foreground=primary,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}
                : new WeatherGlyph(code,day,animated:true), ToolTip=Loc.T("刷新天气","Refresh weather")};
        AttachAction(icon,()=>RefreshRequested?.Invoke());
        first.Children.Add(icon);
        first.Children.Add(new TextBlock {Text=snap is null?"":Deg(future ? dateIndex<snap.DayHighC.Length?snap.DayHighC[dateIndex]:double.NaN : snap.Temperature),FontSize=46,FontWeight=FontWeights.Light,Foreground=primary,VerticalAlignment=VerticalAlignment.Center});now.Children.Add(first);
        now.Children.Add(new TextBlock {Text=snap is null?"":code<0?"":WeatherVisuals.Condition(code),FontSize=15,FontWeight=FontWeights.Medium,Foreground=secondary});
        var forecast=new UniformGrid {Rows=1,Columns=3,Margin=new Thickness(12,0,14,0),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Right};

        for(int i=0;i<3;i++) {
            string dayTitle="";int dCode=0;string rain="";string high="";string low="";
            if(snap is not null&&snap.DayDates.Length>0) {
                int idx=today+i; if(idx<0||idx>=snap.DayDates.Length) break;
                if (!DateTime.TryParse(snap.DayDates[idx],out var date)) continue;
         dayTitle=i==0?Loc.T("今天","Today"):i==1?Loc.T("明天","Tomorrow"):Loc.T(new[]{"周日","周一","周二","周三","周四","周五","周六"}[(int)date.DayOfWeek],new[]{"Sun","Mon","Tue","Wed","Thu","Fri","Sat"}[(int)date.DayOfWeek]);
                dCode=snap.DayCodes.Length>idx&&snap.DayCodes[idx]>=0?snap.DayCodes[idx]:3;
                int r=snap.DayRainProb.Length>idx?snap.DayRainProb[idx]:-1;rain=r>=20?r+"%":"–";
                high=Deg(snap.DayHighC.Length>idx?snap.DayHighC[idx]:double.NaN);low=Deg(snap.DayLowC.Length>idx?snap.DayLowC[idx]:double.NaN);
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
            if(snap is not null) AttachAction(holder,()=>{selectedDay=offset;RenderWeatherCard();});
            else col.Visibility=Visibility.Hidden;
            forecast.Children.Add(holder);
        }
        body.Children.Add(now);Grid.SetColumn(forecast,1);body.Children.Add(forecast);
        var meta=new WrapPanel {Margin=new Thickness(16,0,14,0),VerticalAlignment=VerticalAlignment.Center};
        var footer=new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        var humidity=future ? snap?.DayHumidity.ElementAtOrDefault(dateIndex) : snap?.HumidityPercent;
        if(future && (snap is null || dateIndex>=snap.DayHumidity.Length)) humidity=null;
        double wind=future && snap is not null ? dateIndex<snap.DayWindMaxKmh.Length?snap.DayWindMaxKmh[dateIndex]:double.NaN : snap?.WindKmh??double.NaN;
        // 风向文字优先，缺失时按度数经 8 方位归一，与实况风速卡同一套规则（DegreesFromText → Compass8Angle）。
        string windDir=future && snap is not null ? dateIndex<snap.DayWindDir.Length?snap.DayWindDir[dateIndex]:"" : snap?.WindDirection??"";
        double windDeg=future && snap is not null ? dateIndex<snap.DayWindDirDeg.Length?snap.DayWindDirDeg[dateIndex]:double.NaN : snap?.WindDirectionDeg??double.NaN;
        if(string.IsNullOrWhiteSpace(windDir)&&!double.IsNaN(windDeg)) windDir=WeatherCodes.DegreeToCompass(windDeg)+Loc.T("风","");
        double windAngle=WeatherCodes.Wind8Angle(windDir,windDeg);
        string meta1=snap is null?"":future ? Loc.T($"最低 {Deg(dateIndex<snap.DayLowC.Length?snap.DayLowC[dateIndex]:double.NaN)}",$"Low {Deg(dateIndex<snap.DayLowC.Length?snap.DayLowC[dateIndex]:double.NaN)}") : Loc.T($"体感 {Num(snap.FeelsLike,"°")}",$"Feels like {Num(snap.FeelsLike,"°")}");
        string meta2=snap is null?"":Loc.T($"湿度 {(humidity>=0?humidity+"%":"--")}",$"Humidity {(humidity>=0?humidity+"%":"--")}");
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
        sun.Children.Add(new TextBlock {Text=snap is null?"":Loc.T($"日升 {Clock(snap.SunriseTimes,dateIndex)} · 日落 {Clock(snap.SunsetTimes,dateIndex)}",$"Sunrise {Clock(snap.SunriseTimes,dateIndex)} · Sunset {Clock(snap.SunsetTimes,dateIndex)}"),FontSize=11,Foreground=secondary,VerticalAlignment=VerticalAlignment.Center});
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