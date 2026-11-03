using Loc = BrimDeck.ModuleLocalization;
using UI = BrimDeck.ModuleUi;
using Soft = BrimDeck.ModuleMotion;
using PanelTheme = BrimDeck.ModuleTheme;

namespace BrimDeck.Timers;

internal sealed class TimerSettings(TimerWorkspace workspace, TimerMode mode) : IModuleSettings
{
    public string Name => mode switch { TimerMode.Pomodoro => "番茄钟", TimerMode.Countdown => "倒计时", _ => "秒表" };
    public string NameEn => mode.ToString();
    public bool Visible => workspace.Options.Allows(mode);
    public IReadOnlyList<ModuleSetting> Fields => mode switch
    {
        TimerMode.Pomodoro => [new("focus", "专注时长", "Focus duration", ModuleSettingKind.Number, 1, 180, "分", "min"), new("short", "短休息", "Short break", ModuleSettingKind.Number, 1, 60, "分", "min"), new("long", "长休息", "Long break", ModuleSettingKind.Number, 1, 120, "分", "min"), new("rounds", "长休息间隔", "Sessions per long break", ModuleSettingKind.Number, 1, 12, "轮", "sessions")],
        TimerMode.Countdown => [new("hours", "小时", "Hours", ModuleSettingKind.Number, 0, 99), new("minutes", "分钟", "Minutes", ModuleSettingKind.Number, 0, 59), new("seconds", "秒", "Seconds", ModuleSettingKind.Number, 0, 59)],
        _ => [new("laps", "允许记录分段", "Allow lap recording", ModuleSettingKind.Toggle), new("limit", "最多记录", "Lap limit", ModuleSettingKind.Number, 1, 200, "条", "laps"), new("hundredths", "分段显示百分之一秒", "Show hundredths in laps", ModuleSettingKind.Toggle)]
    };
    public object Read(string key)
    {
        var o = workspace.Options;
        return key switch { "focus" => o.FocusMinutes, "short" => o.ShortBreakMinutes, "long" => o.LongBreakMinutes, "rounds" => o.Rounds,
            "hours" => o.CountdownSeconds / 3600, "minutes" => o.CountdownSeconds / 60 % 60, "seconds" => o.CountdownSeconds % 60,
            "laps" => o.StopwatchLaps, "limit" => o.StopwatchLapLimit, "hundredths" => o.StopwatchHundredths, _ => throw new ArgumentException(key) };
    }
    public void Write(string key, object value)
    {
        var o = workspace.Options;
        int number = value is bool ? 0 : (int)Convert.ToDouble(value);
        switch (key)
        {
            case "focus": o.FocusMinutes = number; break; case "short": o.ShortBreakMinutes = number; break; case "long": o.LongBreakMinutes = number; break; case "rounds": o.Rounds = number; break;
            case "hours": o.CountdownSeconds += (number - o.CountdownSeconds / 3600) * 3600; break;
            case "minutes": o.CountdownSeconds += (number - o.CountdownSeconds / 60 % 60) * 60; break;
            case "seconds": o.CountdownSeconds += number - o.CountdownSeconds % 60; break;
            case "laps": o.StopwatchLaps = (bool)value; break; case "limit": o.StopwatchLapLimit = number; break; case "hundredths": o.StopwatchHundredths = (bool)value; break;
            default: throw new ArgumentException(key);
        }
        workspace.SaveOptions();
    }
    public void Reset()
    {
        var o = workspace.Options; var d = new TimerOptions();
        switch (mode)
        {
            case TimerMode.Pomodoro: o.FocusMinutes = d.FocusMinutes; o.ShortBreakMinutes = d.ShortBreakMinutes; o.LongBreakMinutes = d.LongBreakMinutes; o.Rounds = d.Rounds; break;
            case TimerMode.Countdown: o.CountdownSeconds = d.CountdownSeconds; break;
            default: o.StopwatchLaps = d.StopwatchLaps; o.StopwatchLapLimit = d.StopwatchLapLimit; o.StopwatchHundredths = d.StopwatchHundredths; break;
        }
        workspace.SaveOptions();
    }
}
