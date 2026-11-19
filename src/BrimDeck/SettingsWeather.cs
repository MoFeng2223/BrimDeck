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
        var state=WeatherData.Load();var cityRows=new StackPanel();bool active=true;
        string autoName="当前位置";if(state.Cache.TryGetValue("auto",out var autoSnap))autoName=autoSnap.Name;
        var preview=new ContentControl {Content=new WeatherSettingsPreview(state)};
        void Save() {try {WeatherData.Save(state);}catch {}}
        void Fill()
        {
            cityRows.Children.Clear();
            for(int i=-1;i<state.Cities.Count;i++) {
                int index=i;bool selected=i==state.Selected;var city=i<0?null:state.Cities[i];
                string? temperature=null;int code=3;bool day=true;string? full=null;
                string name=city is null?Loc.T("自动定位","Auto location"):city.Name.Split(" · ")[0],detail=city?.Name??Loc.T("自动定位","Auto location");
                if(city is null&&autoName.Trim().Length>0&&!autoName.Contains("当前位置"))full=autoName;
                if(state.Cache.TryGetValue(city?.ToString()??"auto",out var snap)){temperature=$"{snap.Temperature:0}°";code=snap.WeatherCode;day=snap.IsDay;if(city is null&&!snap.Name.Contains("当前位置"))full=snap.Name;}
                if(city is null&&full is not null)detail=full;
                var controls=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};if(temperature is not null)controls.Children.Add(TextLine(temperature,14,TextPrimary));
                void Select(){state.Selected=index;Save();preview.Content=new WeatherSettingsPreview(state);Fill();}
                var brush=(SolidColorBrush)FindResource("SettingsTextSecondary");
                if(city is not null){var remove=new Button {Content=new WeatherMetaIcon(WeatherMetaKind.Close,brush.Color){Width=12,Height=12},Style=(Style)FindResource("SettingsGlyphButton"),ToolTip=Loc.T("删除城市","Remove city")};remove.Click+=(_,_)=>{state.Cities.RemoveAt(index);if(state.Selected==index)state.Selected=-1;else if(state.Selected>index)state.Selected--;Save();preview.Content=new WeatherSettingsPreview(state);Fill();};controls.Children.Add(remove);}
                var row=Row(name,controls,TextLine(detail,11,TextSecondary));row.Margin=new Thickness(42,0,8,0);
                var grid=new Grid();grid.Children.Add(row);grid.Children.Add(new WeatherGlyph(code,day) {Width=28,Height=28,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(4,0,0,0),IsHitTestVisible=false});
                if(selected){var indicator=new Border {Width=3,CornerRadius=new CornerRadius(0,2,2,0),Background=(Brush)FindResource("SettingsAccent"),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Stretch};grid.Children.Add(indicator);}
                var border=new Border {Child=grid,Background=selected?(Brush)FindResource("SettingsAccentTint"):Brushes.Transparent,CornerRadius=new CornerRadius(6),Cursor=System.Windows.Input.Cursors.Hand};border.MouseLeftButtonUp+=(_,e)=>{if(e.OriginalSource is not Button)Select();};cityRows.Children.Add(border);
            }
        }
        Target.Children.Add(preview);SectionTitle(Loc.T("当前城市","Current city"));Fill();Group(cityRows);
        _ = DetectAsync();
        // 设置页打开即尝试定位：成功则刷新「自动定位」行展示当地城市名，不干扰其余行。
        async Task DetectAsync() { if(!active)return;try {var local=await WeatherData.LocateAsync(CancellationToken.None);if(active&&local is not null&&!local.Name.Contains("当前位置")){autoName=local.Name;Fill();}}catch{}}
        SectionTitle(Loc.T("添加城市","Add city"));
        var input=new TextBox {Width=250,Height=31,Style=(Style)FindResource("SettingsField")};
        var placeholder=new TextBlock {Text=Loc.T("支持中英文","Chinese or English"),FontSize=13,Foreground=(Brush)FindResource("SettingsTextSecondary"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,0,0),IsHitTestVisible=false};void UpdatePlaceholder()=>placeholder.Visibility=input.Text.Length==0?Visibility.Visible:Visibility.Collapsed;input.TextChanged+=(_,_)=>UpdatePlaceholder();
        var inputHost=new Grid {Width=250};inputHost.Children.Add(input);inputHost.Children.Add(placeholder);UpdatePlaceholder();
        var options=new ListBox {MaxHeight=240,DisplayMemberPath="Name",Style=(Style)FindResource("SettingsSearchList")};
        var popup=new Popup {PlacementTarget=input,Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true,Child=new Border {Width=250,Background=(Brush)FindResource("SettingsInput"),BorderBrush=(Brush)FindResource("SettingsBorder"),BorderThickness=new Thickness(1),Padding=new Thickness(4),Margin=new Thickness(0,4,0,0),CornerRadius=new CornerRadius(6),Child=options}};
        PopupMotion.Attach(popup);
        City? picked=null;bool programmatic=false;CancellationTokenSource? pending=null;CancellationTokenSource? fetchCts=null;
        var add=ActionButton(Loc.T("添加","Add"),()=> {if(picked is null||state.Cities.Contains(picked))return;var city=picked;state.Cities.Add(city);Save();Fill();input.Clear();_=FetchCityAsync(city);});add.Style=(Style)FindResource("SettingsPrimaryButton");add.IsEnabled=false;add.Margin=new Thickness(10,0,0,0);
        options.SelectionChanged+=(_,_)=>{if(options.SelectedItem is City city){picked=city;popup.IsOpen=false;programmatic=true;input.Text=city.Name;programmatic=false;add.IsEnabled=!state.Cities.Contains(city);}};
        input.TextChanged+=async (_,_)=>{
            if(programmatic)return;
            pending?.Cancel();pending?.Dispose();pending=new();var token=pending.Token;picked=null;add.IsEnabled=false;popup.IsOpen=false;var query=input.Text.Trim();if(query.Length<2)return;
            try {await Task.Delay(350,token);var found=await WeatherData.Search(query,token);if(token.IsCancellationRequested||!active)return;options.ItemsSource=found;popup.IsOpen=found.Count>0;}catch(OperationCanceledException){}catch{return;}
        };
        var controlsRow=new StackPanel {Orientation=Orientation.Horizontal};controlsRow.Children.Add(inputHost);controlsRow.Children.Add(add);Group(Row(Loc.T("搜索城市","Search city"),controlsRow));
        SectionTitle(Loc.T("更新与数据","Updates and data"));Group(Row(Loc.T("自动更新","Auto update"),TextLine(Loc.T("每 15 分钟","Every 15 minutes"),12,TextSecondary)),Row(Loc.T("离线时","Offline"),TextLine(Loc.T("自动使用本地缓存","Falls back to the local cache"),12,TextSecondary)));
        // 请求与缓存：调度是「按需下探 + 分域缓存」，这里把当天的实际请求量和磁盘占用摆出来，配额一目了然。
        SectionTitle(Loc.T("请求与缓存","Requests and cache"));
        var usage=WeatherCache.Usage();
        var used=WeatherSources.All.Select(source=>(Name:source.Info.Id switch{"tencent"=>Loc.T("腾讯","Tencent"),"weathercomcn"=>Loc.T("中国天气网","ChinaWx"),"hefeng"=>Loc.T("和风","QWeather"),_=>source.Info.Name},Count:RequestGate.Today(source.Info.Id))).Where(item=>item.Count>0).ToArray();
        Group(Row(Loc.T("今日请求","Requests today"),TextLine(used.Length==0?Loc.T("尚未发起请求","No requests yet"):string.Join(" · ",used.Select(item=>$"{item.Name} {item.Count}")),12,TextSecondary)),
              Row(Loc.T("本地缓存","Local cache"),TextLine(Loc.T($"{usage.Count} 项 · {usage.Bytes/1024.0:0} KB（14 天未用自动清理）",$"{usage.Count} entries · {usage.Bytes/1024.0:0} KB (unused entries purged after 14 days)"),12,TextSecondary)));
        // 来源调度：每个数据域可单独指定来源；自动档在国内城市优先国内源，国外城市优先全球源，Open-Meteo 永远兜底。
        SectionTitle(Loc.T("数据来源调度","Data source scheduling"));
        var domestic=new ComboBox {Width=200,Style=(Style)FindResource("SettingsCombo"),ItemsSource=new[]{Loc.T("国内源优先","Domestic first"),Loc.T("全球源优先","Global first")},SelectedIndex=state.Schedule.PreferDomestic?0:1};
        PopupMotion.Attach(domestic);
        domestic.SelectionChanged+=(_,_)=>{if(domestic.SelectedIndex<0)return;bool value=domestic.SelectedIndex==0;if(state.Schedule.PreferDomestic==value)return;state.Schedule.PreferDomestic=value;Save();preview.Content=new WeatherSettingsPreview(state);};
        Group(Row(Loc.T("调度策略","Scheduling"),domestic,TextLine(Loc.T("每个域按需下探：首选来源够用就停手，缺哪块才找下一家；Open-Meteo 永远兜底。","Each domain stops at its first source that covers it; Open-Meteo always backs up."),11,TextSecondary)));
        var domainRows=new List<FrameworkElement>();
        foreach(var domain in new[]{WeatherDomain.Current,WeatherDomain.Hourly,WeatherDomain.Daily,WeatherDomain.Sun,WeatherDomain.Air,WeatherDomain.History})
        {
            var ids=new List<string>{"auto"};var labels=new List<string>{Loc.T("自动（推荐）","Automatic (recommended)")};
            foreach(var source in WeatherSources.All.Where(source=>source.Info.Domains.Contains(domain)))
            {
                ids.Add(source.Info.Id);
                labels.Add(source.Info.Name+(source.Info.Region==WeatherRegion.China?Loc.T(" · 国内"," · China"):"")+(source.Info.NeedsKey?Loc.T(" · 需 Key"," · key"):""));
            }
            var picker=new ComboBox {Width=200,Style=(Style)FindResource("SettingsCombo"),ItemsSource=labels,SelectedIndex=Math.Max(0,ids.IndexOf(state.Schedule.Choice(domain)))};
            PopupMotion.Attach(picker);
            picker.SelectionChanged+=(_,_)=>{if(picker.SelectedIndex<0)return;string id=ids[picker.SelectedIndex];if(id=="auto")state.Schedule.Sources.Remove(domain.ToString());else state.Schedule.Sources[domain.ToString()]=id;Save();preview.Content=new WeatherSettingsPreview(state);};
            domainRows.Add(Row(DomainLabel(domain),picker,TextLine(DomainHint(domain),11,TextSecondary)));
        }
        Group(domainRows.ToArray());
        SectionTitle(Loc.T("可选 API Key","Optional API keys"));
        var hefengKey=KeyField(state.Schedule.HeFengKey,text=>{state.Schedule.HeFengKey=text;Save();});
        var hefengHost=KeyField(state.Schedule.HeFengHost,text=>{state.Schedule.HeFengHost=text;Save();});
        Group(Row(Loc.T("和风天气 Key","QWeather key"),hefengKey,TextLine(Loc.T("填写后自动优先使用和风的全维度数据。","With a key, QWeather is preferred for all domains."),11,TextSecondary)),
              Row(Loc.T("和风 API Host","QWeather host"),hefengHost,TextLine(Loc.T("必填：控制台分配的项目 Host，例如 abcxyz.re.qweatherapi.com；留空则不启用和风。","Required: the project host from your console, e.g. abcxyz.re.qweatherapi.com; blank disables QWeather."),11,TextSecondary)));
        cityRows.Unloaded+=(_,_)=>{active=false;pending?.Cancel();pending?.Dispose();pending=null;fetchCts?.Cancel();fetchCts?.Dispose();fetchCts=null;popup.IsOpen=false;};

        string DomainLabel(WeatherDomain domain)=>domain switch
        {
            WeatherDomain.Current=>Loc.T("当前天气","Current"),WeatherDomain.Hourly=>Loc.T("逐时天气","Hourly"),WeatherDomain.Daily=>Loc.T("每日天气","Daily"),
            WeatherDomain.Sun=>Loc.T("日出日落","Sunrise & sunset"),WeatherDomain.Air=>Loc.T("空气质量","Air quality"),WeatherDomain.History=>Loc.T("历史天气","History"),_=>domain.ToString()
        };
        string DomainHint(WeatherDomain domain)=>domain switch
        {
            WeatherDomain.Current=>Loc.T("温度、体感、湿度、风、气压、能见度等实况。","Temperature, feels-like, humidity, wind, pressure and visibility."),
            WeatherDomain.Hourly=>Loc.T("未来 24 小时逐时天气。","Hour-by-hour for the next 24 hours."),
            WeatherDomain.Daily=>Loc.T("24 天每日天气、高低温、降水、风与紫外线。","24 days of highs, lows, rain, wind and UV."),
            WeatherDomain.Sun=>Loc.T("每天日出与日落时间。","Sunrise and sunset for each day."),
            WeatherDomain.Air=>Loc.T("AQI、PM2.5 与 PM10。","AQI, PM2.5 and PM10."),
            WeatherDomain.History=>Loc.T("过去 10 天；选「和风天气」会启用时光机，10 天 = 10 次请求。","The past 10 days; choosing QWeather enables the time machine, 10 days = 10 requests."),_=>""
        };
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
        var key=state.Selected>=0&&state.Selected<state.Cities.Count?state.Cities[state.Selected].ToString():"auto";
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
        locIcon.Children.Add(new TextBlock {Text=Place(snap?.Name??cityName),FontSize=12,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(234,242,255)),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,0,0,0),TextTrimming=TextTrimming.CharacterEllipsis});
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
            if(snap is not null) {
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