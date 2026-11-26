using BrimDeck;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

namespace ReadmeMedia;

// The notices page with a ringing call in the focus: WeChat's ("挂断" and "接听") and Feishu's video call ("挂断", "语音接听" and
// "接听"), at the default size and a narrow one, at the display scale. No call window is watched: each watch is made with a call
// window that is not there, so that the page offers to answer it. For checking, not for the README.
internal sealed partial class Renderer
{
    private async Task NoticeCallScene()
    {
        _settings.Animations = false; _settings.NoticeIsland = true; _settings.NoticesPage = true;
        // WeChat and Feishu are listed by default; a notice of an application that is not listed is not received.
        foreach (var listed in DeckSettings.DefaultNoticeApps()) if (_settings.NoticeRule(listed.Key) is null) _settings.NoticeApps.Add(listed);
        _settings.NoticeAppsListed = true;
        var center = Activator.CreateInstance(Type.GetType("BrimDeck.NoticeCenter, BrimDeck")!, Program.Any, null, [app], null)!;
        Program.SetProperty(app, "Notices", center);
        var hub = (NoticeHub)center.GetType().GetProperty("Hub", Program.Any)!.GetValue(center)!;
        var watchType = Type.GetType("BrimDeck.Native.CallWindowWatch, BrimDeck")!;
        int window = 0;
        void Ring(string field, string kind, string appKey, string appName, string key, string title, string body, bool voice)
        {
            var watch = Program.Field(center, field) ?? Activator.CreateInstance(watchType, Program.Any, null,
                [watchType.GetField(kind, Program.Any)!.GetValue(null), (Action<NoticeInput>)(_ => { }), (Action<string>)(_ => { })], null)!;
            Program.SetField(center, field, watch);
            var calls = (Dictionary<nint, string>)Program.Field(watch, "_calls")!;
            calls[++window] = key;
            if (voice) ((HashSet<nint>)Program.Field(watch, "_voice")!).Add(window);
            hub.Receive(new NoticeInput
            {
                Origin = NoticeOrigin.Windows, AppKey = appKey, AppName = appName, InstanceKey = key, Title = title, Body = body,
                Category = NoticeCategory.Message, Call = true, CreatedAt = _now
            }, _now.ToUniversalTime());
        }
        SelectPage(DeckPage.Notices);
        async Task Shots(string name)
        {
            foreach (var (width, height) in new (double, double)[] { (760, 330), (460, 280) })
            {
                _settings.NoticesWidth = width; _settings.NoticesHeight = height; Refresh();
                _deck.SetExpanded(true, true);
                await Settle();
                // At the scale the words were placed for: the display scale of the screen the panel would show on.
                double scale = System.Windows.Media.VisualTreeHelper.GetDpi(_deck).PixelsPerDip;
                await Save($"notice-call-{name}-{width}x{height}@{scale * 100:0}", scale);
            }
        }
        Ring("_wechatCalls", "WeChat", WeChatRows.AppKey, Loc.T("微信", "WeChat"), "wechatcall:1:1", Loc.T("泡泡", "Pao"), Loc.T("邀请你语音通话", "Incoming voice call"), false);
        await Shots("wechat");
        Ring("_feishuCalls", "Feishu", FeishuBanners.AppKey, Loc.T("飞书", "Feishu"), "feishu:call:1:1", Loc.T("用户546325", "User 546325"), Loc.T("邀请你进行视频通话", "Incoming video call"), true);
        await Shots("feishu");
        foreach (var field in new[] { "_wechatCalls", "_feishuCalls" }) { ((IDisposable)Program.Field(center, field)!).Dispose(); Program.SetField(center, field, null); }
        SelectPage(DeckPage.Usage);
        _deck.SetExpanded(false, true);
        _settings.Animations = true;
    }
}
