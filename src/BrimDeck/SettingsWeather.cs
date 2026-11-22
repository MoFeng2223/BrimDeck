using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BrimDeck.Core;
namespace BrimDeck;
public sealed partial class SettingsWindow
{
    internal const int WeatherSettingsPage = 19;
    private void WeatherSettings()
    {
        var state=WeatherData.Load(() => S);var cityRows=new StackPanel();bool active=true;
        string autoName="当前位置";if(state.AutoKey.Length>0&&state.Cache.TryGetValue(state.AutoKey,out var autoSnap))autoName=autoSnap.Name;
        var preview=new ContentControl {Content=new WeatherSettingsPreview(state)};
        // 天气配置现在就是 DeckSettings 的一部分：改动后走与其他设置页相同的保存队列写 settings.json。
        void Save() {try {_app.QueueSave(); WeatherData.Save(state);}catch {}}
        void Fill()
        {
            cityRows.Children.Clear();
            for(int i=-1;i<state.Cities.Count;i++) {
                int index=i;bool selected=i==state.Selected;var city=i<0?null:state.Cities[i];
                string? temperature=null;int code=3;bool day=true;
                // 主标题只显示第一段（洪山），副标题显示完整层级（洪山 · 武汉 · 湖北省 · 中国）；
                // 自动定位行主标题固定「自动定位」，副标题是定位到的完整路径。
                string name=city is null?Loc.T("自动定位","Auto location"):City.SplitLevels(city.Name)[0];
                string? subtitle=city is null?null:city.Name;
                if(city is null){
                    if(autoName.Trim().Length>0&&!autoName.Contains("当前位置"))subtitle=autoName;
                    if(state.Cache.TryGetValue(state.AutoKey,out var snap)){temperature=$"{snap.Temperature:0}°";code=snap.WeatherCode;day=snap.IsDay;if(!snap.Name.Contains("当前位置"))subtitle=snap.Name;}
                } else if(state.Cache.TryGetValue(city.ToString(),out var snap)){temperature=$"{snap.Temperature:0}°";code=snap.WeatherCode;day=snap.IsDay;}
                var controls=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};if(temperature is not null)controls.Children.Add(TextLine(temperature,14,TextPrimary));
                void Select(){state.Selected=index;Save();preview.Content=new WeatherSettingsPreview(state);Fill();}
                var brush=(SolidColorBrush)FindResource("SettingsTextSecondary");
                if(city is not null){var remove=new Button {Content=new WeatherMetaIcon(WeatherMetaKind.Close,brush.Color){Width=12,Height=12},Style=(Style)FindResource("SettingsGlyphButton"),ToolTip=Loc.T("删除城市","Remove city")};remove.Click+=(_,_)=>{state.Cities.RemoveAt(index);if(state.Selected==index)state.Selected=-1;else if(state.Selected>index)state.Selected--;Save();preview.Content=new WeatherSettingsPreview(state);Fill();UpdateAdd();};controls.Children.Add(remove);}
                var row=Row(name,controls,subtitle is null?null:TextLine(subtitle,11,TextSecondary));row.Margin=new Thickness(42,0,8,0);
                var grid=new Grid();grid.Children.Add(row);grid.Children.Add(new WeatherGlyph(code,day) {Width=28,Height=28,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(4,0,0,0),IsHitTestVisible=false});
                if(selected){var indicator=new Border {Width=3,CornerRadius=new CornerRadius(0,2,2,0),Background=(Brush)FindResource("SettingsAccent"),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Stretch};grid.Children.Add(indicator);}
                var border=new Border {Child=grid,Background=selected?(Brush)FindResource("SettingsAccentTint"):Brushes.Transparent,CornerRadius=new CornerRadius(6),Cursor=System.Windows.Input.Cursors.Hand};border.MouseLeftButtonUp+=(_,e)=>{if(e.OriginalSource is not Button)Select();};cityRows.Children.Add(border);
            }
        }
        Target.Children.Add(preview);SectionTitle(Loc.T("当前城市","Current city"));Group(cityRows);
        // 设置页打开即尝试定位：成功则刷新「自动定位」行展示当地城市名，不干扰其余行。
        async Task DetectAsync() { if(!active)return;try {var local=await WeatherData.LocateAsync(state.Schedule,CancellationToken.None);if(active&&local is not null&&!local.Name.Contains("当前位置")){autoName=local.Name;Fill();}}catch{}}
        SectionTitle(Loc.T("添加城市","Add city"));
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
        Button add=null!; add=ActionButton(Loc.T("添加","Add"),()=> {if(picked is null||state.Cities.Contains(picked)||!CanAdd())return;var city=picked;state.Cities.Add(city);Save();Fill();UpdateAdd();input.Clear();_=FetchCityAsync(city);});add.Style=(Style)FindResource("SettingsPrimaryButton");add.IsEnabled=false;add.Margin=new Thickness(10,0,0,0);
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
        var controlsRow=new StackPanel {Orientation=Orientation.Horizontal};controlsRow.Children.Add(inputHost);controlsRow.Children.Add(add);Group(Row(Loc.T("搜索城市","Search city"),controlsRow));
        // 数据来源：8 类数据能力逐一可配；区域切换放在明细表的正上方。
        // 每行只列出「该区域能覆盖这一类」的来源，不支持的不进下拉；每个域由用户明确指定一家，不再有自动回退。
        SectionTitle(Loc.T("数据来源","Data sources"));
        bool showChina=true;
        // 日出日落/月升月落/月相属于「每天预报」，不单列配置。
        var abilities=new (WeatherDomain[] Domains,string Label,string Hint)[]
        {
            ([WeatherDomain.Current],Loc.T("实时预报","Current"),Loc.T("温度、体感、湿度、风、气压等实况。","Now: temperature, feels-like, humidity, wind, pressure.")),
            ([WeatherDomain.Minutely],Loc.T("分钟预报","Minutely"),Loc.T("未来数小时的分钟级降水。","Minute-by-minute precipitation for the next hours.")),
            ([WeatherDomain.Hourly],Loc.T("小时预报","Hourly"),Loc.T("未来 24 小时逐小时天气。","Hour by hour for the next 24 hours.")),
            ([WeatherDomain.Daily],Loc.T("每天预报","Daily"),Loc.T("每日高低温、降水、风、紫外线与日出日落月相。","Daily highs, lows, rain, wind, UV and sun/moon times.")),
            ([WeatherDomain.History],Loc.T("时光机","History"),Loc.T("过去一段时间的天气。","Past weather.")),
            ([WeatherDomain.Warning],Loc.T("预警","Warnings"),Loc.T("官方极端天气预警。","Official severe weather warnings.")),
            ([WeatherDomain.Indices],Loc.T("天气指数","Indices"),Loc.T("穿衣、紫外线、运动等生活指数。","Lifestyle indices: clothing, UV, sports and more.")),
            ([WeatherDomain.Air],Loc.T("空气质量","Air quality"),Loc.T("AQI、PM2.5 与 PM10。","AQI, PM2.5 and PM10."))
        };
        bool loadingSources=false;
        var sourceTable=new StackPanel();

        // 区域切换：放在 8 类明细表的上方；切换后只重建下方下拉。
        var regionStrip=new UniformGrid {Columns=2,Width=240};
        foreach(var (china,label) in new[]{(true,Loc.T("国内城市","Chinese cities")),(false,Loc.T("国外城市","Outside China"))})
        {
            var opt=new RadioButton {Content=label,Style=(Style)FindResource("SettingsSegment"),GroupName="WeatherRegion",IsChecked=china};
            opt.Checked+=(_,_)=>{showChina=china;FillSources();};
            regionStrip.Children.Add(opt);
        }
        Group(Row(Loc.T("城市区域","City region"),SegmentBorder(regionStrip),
            TextLine(Loc.T("切换区域后，下方只列出该区域能提供的来源。","Switching region refreshes the sources available below."),11,TextSecondary)));
        Target.Children.Add(sourceTable);

        void FillSources()
        {
            loadingSources=true;
            sourceTable.Children.Clear();
            var rows=new List<FrameworkElement>();
            var candidates=WeatherSources.Candidates(showChina).ToArray();
            // 下拉展示顺序固定：和风 → 腾讯 → 中国天气网 → Open-Meteo。
            string[] displayOrder=["hefeng","tencent","weathercomcn","openmeteo"];
            foreach(var (domains,label,hint) in abilities)
            {
                // 只列能覆盖整行所有域的来源；缺任一项就不放入下拉，按 displayOrder 排列。
                var able=candidates.Where(s=>domains.All(d=>s.Info.Domains.Contains(d)))
                    .OrderBy(s=>{int i=Array.IndexOf(displayOrder,s.Info.Id);return i<0?int.MaxValue:i;})
                    .ToArray();
                var ids=able.Select(s=>s.Info.Id).ToList();
                var labels=able.Select(s=>s.Info.Name).ToList();
                var chosen=domains.Select(state.Schedule.Choice).FirstOrDefault(c=>c.Length>0)??"";
                var picker=new ComboBox {Width=210,Style=(Style)FindResource("SettingsCombo"),ItemsSource=labels,SelectedIndex=ids.Count>0?Math.Max(0,ids.IndexOf(chosen)):-1};
                PopupMotion.Attach(picker);
                picker.SelectionChanged+=(_,_)=>{
                    if(loadingSources||picker.SelectedIndex<0||picker.SelectedIndex>=ids.Count)return;
                    string id=ids[picker.SelectedIndex];
                    foreach(var domain in domains) state.Schedule.Sources[domain.ToString()]=id;
                    Save();preview.Content=new WeatherSettingsPreview(state);
                };
                string scope=able.Length==0?Loc.T("该区域暂无可用来源","No source in this region"):able.Length==candidates.Length?Loc.T("各家都能提供","from every source"):Loc.T($"仅 {able.Length} 家提供",$"from {able.Length} sources");
                rows.Add(Row(label,picker,TextLine(hint+" · "+scope,11,TextSecondary)));
            }
            GroupInto(sourceTable,rows.ToArray());
            loadingSources=false;
        }
        FillSources();


        SectionTitle(Loc.T("和风天气配置","QWeather setup"));
        var hefengHost=KeyField(state.Schedule.HeFengHost,text=>{state.Schedule.HeFengHost=text;Save();});
        var hefengKey=KeyField(state.Schedule.HeFengKey,text=>{state.Schedule.HeFengKey=text;Save();});
        Group(Row(Loc.T("和风 API Host","QWeather host"),hefengHost,TextLine(Loc.T("必填：控制台分配的项目 Host，例如 abcxyz.re.qweatherapi.com；留空则不启用和风。","Required: the project host from your console, e.g. abcxyz.re.qweatherapi.com; blank disables QWeather."),11,TextSecondary)),
              Row(Loc.T("和风天气 Key","QWeather key"),hefengKey,TextLine(Loc.T("和风需要 Key：填入后优先使用其全维度数据。","QWeather needs a key: with one it is preferred for all domains."),11,TextSecondary)));
        // 更新与请求状态放页尾：城市与来源才是高频改动项，运行状态看一眼就够。
        SectionTitle(Loc.T("更新与数据","Updates and data"));Group(Row(Loc.T("自动更新","Auto update"),TextLine(Loc.T("每 15 分钟","Every 15 minutes"),12,TextSecondary)),Row(Loc.T("离线时","Offline"),TextLine(Loc.T("自动使用本地缓存","Falls back to the local cache"),12,TextSecondary)));
        // 请求与缓存：按用户指定来源请求 + 分域缓存，这里把当天的实际请求量和磁盘占用摆出来，配额一目了然。
        SectionTitle(Loc.T("请求与缓存","Requests and cache"));
        var usage=WeatherCache.Usage();
        var used=WeatherSources.All.Select(source=>(Name:source.Info.Id switch{"tencent"=>Loc.T("腾讯","Tencent"),"weathercomcn"=>Loc.T("中国天气网","ChinaWx"),"hefeng"=>Loc.T("和风","QWeather"),_=>source.Info.Name},Count:RequestGate.Today(source.Info.Id))).Where(item=>item.Count>0).ToArray();
        Group(Row(Loc.T("今日请求","Requests today"),TextLine(used.Length==0?Loc.T("尚未发起请求","No requests yet"):string.Join(" · ",used.Select(item=>$"{item.Name} {item.Count}")),12,TextSecondary)),
              Row(Loc.T("本地缓存","Local cache"),TextLine(Loc.T($"{usage.Count} 项 · {usage.Bytes/1024.0:0} KB（14 天未用自动清理）",$"{usage.Count} entries · {usage.Bytes/1024.0:0} KB (unused entries purged after 14 days)"),12,TextSecondary)));
        cityRows.Unloaded+=(_,_)=>{active=false;pending?.Cancel();pending?.Dispose();pending=null;fetchCts?.Cancel();fetchCts?.Dispose();fetchCts=null;popup.IsOpen=false;ShowSearching(false);};

        TextBox KeyField(string value,Action<string> save)
        {
            var box=new TextBox {Width=250,Height=31,Style=(Style)FindResource("SettingsField"),Text=value};
            box.TextChanged+=(_,_)=>save(box.Text);
            return box;
        }
        // 添加城市后立即拉取一次天气写入缓存，列表温度随之刷新；未就绪时该行不显示温度，删除或离开页面的请求一律作废。
        async Task FetchCityAsync(City city) { fetchCts?.Cancel();fetchCts=new();var token=fetchCts.Token;try {var snap=await WeatherData.CurrentAsync(city,state.Schedule,token);if(!active||token.IsCancellationRequested||!state.Cities.Contains(city))return;state.Cache[city.ToString()]=snap;Save();Fill();if(state.Selected>=0&&state.Selected<state.Cities.Count&&state.Cities[state.Selected].ToString()==city.ToString())preview.Content=new WeatherSettingsPreview(state);}catch{}}
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
internal sealed class WeatherSettingsPreview : Border
{
    internal WeatherSettingsPreview(WeatherData.State state)
    {
        CornerRadius=new CornerRadius(14);Margin=new Thickness(0,0,0,18);ClipToBounds=true;
        var key=state.Selected>=0&&state.Selected<state.Cities.Count?state.Cities[state.Selected].ToString():state.AutoKey;
        WeatherSnapshot? snap=null;
        if(state.Cache.TryGetValue(key,out var cached)) snap=cached;
        int code=snap?.WeatherCode??0;bool day=snap?.IsDay??true;
        string cityName=state.Selected>=0&&state.Selected<state.Cities.Count?state.Cities[state.Selected].Name:"当前位置";
        string Place(string n)=>n=="当前位置"?Loc.T("当前位置","Current location"):n;
        string Deg(double value)=>double.IsNaN(value)?"--":$"{value:0}°";
        string Num(double value,string suffix)=>double.IsNaN(value)?"--":value.ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)+suffix;
        string Clock(string[] times,int index)=>index>=0&&index<times.Length&&times[index].Length>=16?times[index][11..16]:"--:--";
        Background=WeatherView.Sky(code,day);
        var grid=new Grid();grid.RowDefinitions.Add(new RowDefinition {Height=new GridLength(34)});grid.RowDefinitions.Add(new RowDefinition());grid.RowDefinitions.Add(new RowDefinition {Height=new GridLength(1,GridUnitType.Auto)});
        var head=new Grid {VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,12,0)};
        head.ColumnDefinitions.Add(new ColumnDefinition());head.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        var locIcon=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        locIcon.Children.Add(new WeatherMetaIcon(WeatherMetaKind.Location,Color.FromRgb(205,224,247)){Width=12,Height=12,VerticalAlignment=VerticalAlignment.Center});
        // 城市名只有一份完整名称：选中城市取 City.Name，自动定位退回快照 Name（也是完整名称）。
        string headerName=state.Selected>=0&&state.Selected<state.Cities.Count?cityName:(snap?.Name??cityName);
        locIcon.Children.Add(new TextBlock {Text=Place(headerName),FontSize=12,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(234,242,255)),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,0,0,0),TextTrimming=TextTrimming.CharacterEllipsis});
        var headRight=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};
        var upd=new TextBlock {Text=snap is null?"":Loc.T($"更新于 {snap.FetchedAt:HH:mm}",$"Updated {snap.FetchedAt:HH:mm}"),FontSize=10.5,Foreground=new SolidColorBrush(Color.FromRgb(188,208,234)),VerticalAlignment=VerticalAlignment.Center};
        string source=snap is not null&&snap.Sources.TryGetValue("Daily",out var daily)&&daily.Length>0?daily:"Open-Meteo";
        var src=new TextBlock {Text=snap is null?"":source,FontSize=10.5,Foreground=new SolidColorBrush(Color.FromRgb(188,208,234)),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(10,0,0,0),TextTrimming=TextTrimming.CharacterEllipsis};
        headRight.Children.Add(upd);headRight.Children.Add(src);
        head.Children.Add(locIcon);Grid.SetColumn(headRight,1);head.Children.Add(headRight);
        var headBar=new Border {Height=34,CornerRadius=new CornerRadius(13,13,0,0),Background=new SolidColorBrush(Color.FromArgb(46,0,0,0)),BorderBrush=new SolidColorBrush(Color.FromArgb(24,255,255,255)),BorderThickness=new Thickness(0,0,0,1),Child=head};
        // 左侧「现在」占星号列，右侧「三天」按固定列宽靠右收成一小簇。
        // 三天如果一起放进星号列，UniformGrid 会把可用宽度三等分：内容只有几十像素、格子却有一百多，
        // 数字被拉开、今天的高亮框也胀成一大块——这就是它看起来「胖」的原因。
        var body=new Grid();body.ColumnDefinitions.Add(new ColumnDefinition());body.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        var now=new StackPanel {Margin=new Thickness(20,0,0,0),VerticalAlignment=VerticalAlignment.Center};
        var first=new StackPanel {Orientation=Orientation.Horizontal};first.Children.Add(new WeatherGlyph(code,day) {Width=52,Height=52,Margin=new Thickness(0,0,8,0),VerticalAlignment=VerticalAlignment.Center});
        first.Children.Add(new TextBlock {Text=snap is null?"":Deg(snap.Temperature),FontSize=46,FontWeight=FontWeights.Light,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center});now.Children.Add(first);
        now.Children.Add(new TextBlock {Text=snap is null?"":WeatherView.Condition(code),FontSize=15,FontWeight=FontWeights.Medium,Foreground=new SolidColorBrush(Color.FromRgb(234,242,255))});
        var forecast=new UniformGrid {Rows=1,Columns=3,Margin=new Thickness(12,0,14,0),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Right};
        int today=snap is null?0:WeatherData.TodayIndex(snap);   // 正序数组中“今天”的索引
        for(int i=0;i<3;i++) {
            string dayTitle="";int dCode=0;string rain="";string high="";string low="";
            if(snap is not null&&snap.DayDates.Length>0) {
                int idx=Math.Min(today+i,snap.DayDates.Length-1);
                var date=DateTime.Parse(snap.DayDates[idx]);
         dayTitle=i==0?Loc.T("今天","Today"):i==1?Loc.T("明天","Tomorrow"):Loc.T(new[]{"周日","周一","周二","周三","周四","周五","周六"}[(int)date.DayOfWeek],new[]{"Sun","Mon","Tue","Wed","Thu","Fri","Sat"}[(int)date.DayOfWeek]);
                dCode=snap.DayCodes.Length>idx&&snap.DayCodes[idx]>=0?snap.DayCodes[idx]:3;
                int r=snap.DayRainProb.Length>idx?snap.DayRainProb[idx]:-1;rain=r>=20?r+"%":"–";
                high=Deg(snap.DayHighC.Length>idx?snap.DayHighC[idx]:double.NaN);low=Deg(snap.DayLowC.Length>idx?snap.DayLowC[idx]:double.NaN);
            }
            // 每列固定 64px：正好装下「今天 / 图标 / 降水 / 15°/4°」，三列合计 192，右边留出呼吸位。
            var col=new StackPanel {Width=64,VerticalAlignment=VerticalAlignment.Center};
            col.Children.Add(new TextBlock {Text=dayTitle,FontSize=11.5,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(205,224,247)),TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.NoWrap});
            col.Children.Add(new WeatherGlyph(dCode,true) {Width=28,Height=28,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,5,0,3)});
            col.Children.Add(new TextBlock {Text=rain,FontSize=10.5,Foreground=new SolidColorBrush(Color.FromRgb(143,208,255)),TextAlignment=TextAlignment.Center,Height=13,TextWrapping=TextWrapping.NoWrap});
            // 高低温并成一行：每列只有一天，分成两行会把格子撑得又高又空，两个数字还各占一侧。
            var range=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,1,0,0)};
            range.Children.Add(new TextBlock {Text=high,FontSize=14.5,FontWeight=FontWeights.SemiBold,Foreground=Brushes.White,TextWrapping=TextWrapping.NoWrap});
            range.Children.Add(new TextBlock {Text="/",FontSize=10.5,Foreground=new SolidColorBrush(Color.FromRgb(150,168,192)),VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(2,0,2,2)});
            range.Children.Add(new TextBlock {Text=low,FontSize=11.5,Foreground=new SolidColorBrush(Color.FromRgb(182,201,226)),VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(0,0,0,2),TextWrapping=TextWrapping.NoWrap});
            col.Children.Add(range);
            // 三列套同一层壳，只有今天有底色与描边：保持等宽，不会因为高亮框多出 2px 而串位。
            var holder=new Border {Child=col,Padding=new Thickness(0,6,0,6),CornerRadius=new CornerRadius(11),BorderThickness=new Thickness(1),
                Background=i==0?new SolidColorBrush(Color.FromArgb(26,255,255,255)):Brushes.Transparent,
                BorderBrush=i==0?new SolidColorBrush(Color.FromArgb(56,255,255,255)):Brushes.Transparent};
            forecast.Children.Add(holder);
        }
        body.Children.Add(now);Grid.SetColumn(forecast,1);body.Children.Add(forecast);
        var meta=new Grid {Margin=new Thickness(16,0,14,0),VerticalAlignment=VerticalAlignment.Center};
        var metaLeft=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center};
        var metaRight=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};
        string meta1=snap is null?"":Loc.T($"体感 {Num(snap.FeelsLike,"°")}",$"Feels like {Num(snap.FeelsLike,"°")}");
        string meta2=snap is null?"":Loc.T($"湿度 {(snap.HumidityPercent>=0?snap.HumidityPercent+"%":"--")}",$"Humidity {(snap.HumidityPercent>=0?snap.HumidityPercent+"%":"--")}");
        string meta3=snap is null?"":Loc.T($"风 {Num(snap.WindKmh," km/h")}",$"Wind {Num(snap.WindKmh," km/h")}");
        foreach(var (kind,ink,text) in new[] {
            (WeatherMetaKind.Thermo,Color.FromRgb(255,176,102),meta1),
            (WeatherMetaKind.Droplet,Color.FromRgb(112,178,255),meta2),
            (WeatherMetaKind.Wind,Color.FromRgb(140,200,220),meta3) }) {
            var item=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,16,0)};
            item.Children.Add(new WeatherMetaIcon(kind,ink) {Width=14,Height=14,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,5,0)});
            item.Children.Add(new TextBlock {Text=text,FontSize=11,Foreground=new SolidColorBrush(Color.FromRgb(213,227,245))});
            metaLeft.Children.Add(item);
        }
        var sun=new StackPanel {Orientation=Orientation.Horizontal};
        sun.Children.Add(new WeatherMetaIcon(WeatherMetaKind.Sunrise,Color.FromRgb(255,200,90)){Width=14,Height=14,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,5,0)});
        sun.Children.Add(new TextBlock {Text=snap is null?"":Loc.T($"日出 {Clock(snap.SunriseTimes,today)} · 日落 {Clock(snap.SunsetTimes,today)}",$"Sunrise {Clock(snap.SunriseTimes,today)} · Sunset {Clock(snap.SunsetTimes,today)}"),FontSize=11,Foreground=new SolidColorBrush(Color.FromRgb(213,227,245))});
        metaRight.Children.Add(sun);
        meta.Children.Add(metaLeft);meta.Children.Add(metaRight);
        var strip=new Border {Height=34,CornerRadius=new CornerRadius(0,0,13,13),Background=new SolidColorBrush(Color.FromArgb(36,0,0,0)),BorderBrush=new SolidColorBrush(Color.FromArgb(24,255,255,255)),BorderThickness=new Thickness(0,1,0,0),Child=meta};
        grid.Children.Add(headBar);Grid.SetRow(body,1);grid.Children.Add(body);Grid.SetRow(strip,2);grid.Children.Add(strip);Child=grid;
    }
}