using System.Diagnostics;
using System.Text.Json;
using BrimDeck.Core;
using BrimDeck.Core.Notices;

// Notice sounds: which sound a level and a notice play, the imported files' names, and the built-in sounds themselves.
internal static class NoticeSoundTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    public static void Run(Action<string, bool> check)
    {
        Choice(check);
        Files(check);
        Items(check);
        Pace(check);
        SettingsValues(check);
        BuiltIn(check);
    }

    private static void Choice(Action<string, bool> check)
    {
        check("Sound: level 1 and a notice that only goes to the list play none, whatever their style names",
            NoticeSounds.Of("ding", NoticeLevel.Plain) == "" && NoticeSounds.Of(null, NoticeLevel.Plain) == "" && NoticeSounds.Of("marimba", NoticeLevel.Silent) == "" && NoticeSounds.Of(null, NoticeLevel.Auto) == "");
        check("Sound: a style that names none plays its level's default; an empty name plays nothing",
            NoticeSounds.Of(null, NoticeLevel.Glow) == NoticeSounds.DefaultShort && NoticeSounds.Of(null, NoticeLevel.Lively) == NoticeSounds.DefaultShort
            && NoticeSounds.Of(null, NoticeLevel.Persistent) == NoticeSounds.DefaultLoop && NoticeSounds.Of("", NoticeLevel.Glow) == "" && NoticeSounds.Of("  ", NoticeLevel.Persistent) == "");
        check("Sound: level 2 plays the short sounds and level 3 the loops; a sound of the other kind or an unknown one is the default",
            NoticeSounds.Of("ding", NoticeLevel.Glow) == "ding" && NoticeSounds.Of("chimes", NoticeLevel.Persistent) == "chimes"
            && NoticeSounds.Of("chimes", NoticeLevel.Glow) == NoticeSounds.DefaultShort && NoticeSounds.Of("ding", NoticeLevel.Persistent) == NoticeSounds.DefaultLoop
            && NoticeSounds.Of("gone", NoticeLevel.Glow) == NoticeSounds.DefaultShort);
        check("Sound: the defaults are built-in sounds of their kind, and every id is its own",
            NoticeSounds.BuiltIn.Any(sound => sound.Id == NoticeSounds.DefaultShort && sound.Kind == NoticeSoundKind.Short)
            && NoticeSounds.BuiltIn.Any(sound => sound.Id == NoticeSounds.DefaultLoop && sound.Kind == NoticeSoundKind.Loop)
            && NoticeSounds.BuiltIn.Select(sound => sound.Id).Distinct().Count() == NoticeSounds.BuiltIn.Count
            && NoticeSounds.BuiltIn.All(sound => sound.Id.Length > 0 && !sound.Id.StartsWith(NoticeSounds.FilePrefix) && sound.Chinese.Length > 0 && sound.English.Length > 0));
        check("Sound: the volume slider's half is a quarter of the amplitude, and it stays within 0 and 1",
            NoticeSounds.Gain(0) == 0 && NoticeSounds.Gain(100) == 1 && Math.Abs(NoticeSounds.Gain(50) - .25) < 1e-9 && NoticeSounds.Gain(-5) == 0 && NoticeSounds.Gain(400) == 1);
    }

    private static void Files(Action<string, bool> check)
    {
        check("Sound file: an imported file is named by a plain file name with a known extension, at either level",
            NoticeSounds.FileName("file:bell.mp3") == "bell.mp3" && NoticeSounds.FileName("file:我的 铃声.WAV") == "我的 铃声.WAV" && NoticeSounds.FileName("ding") is null
            && NoticeSounds.Of("file:bell.mp3", NoticeLevel.Glow) == "file:bell.mp3" && NoticeSounds.Of("file:bell.mp3", NoticeLevel.Persistent) == "file:bell.mp3"
            && NoticeSounds.Of("file:bell.mp3", NoticeLevel.Plain) == "" && NoticeSounds.ForFile("a.wav") == "file:a.wav");
        check("Sound file: a name that leaves the sounds folder or is no audio file is never a file (the default plays)",
            new[] { "file:..\\secrets.dat", "file:../a.wav", "file:C:\\a.wav", "file:sub/a.wav", "file:a.exe", "file:a", "file:.wav", "file: a.wav", "file:", "file:a.wav:stream", "file:" + new string('a', 130) + ".wav" }
                .All(sound => NoticeSounds.FileName(sound) is null && NoticeSounds.Of(sound, NoticeLevel.Glow) == NoticeSounds.DefaultShort));
        check("Sound file: an import keeps the file's name, numbers it when the folder has one of that name, and lowers the extension",
            NoticeSounds.ImportName(@"C:\Music\Bell.MP3", []) == "Bell.mp3" && NoticeSounds.ImportName(@"C:\Music\bell.mp3", ["Bell.mp3"]) == "bell (2).mp3"
            && NoticeSounds.ImportName(@"C:\Music\bell.mp3", ["bell.mp3", "bell (2).mp3"]) == "bell (3).mp3"
            && NoticeSounds.SafeFileName(NoticeSounds.ImportName(@"C:\Music\" + new string('长', 200) + ".flac", []))
            && NoticeSounds.SafeFileName(NoticeSounds.ImportName(@"C:\Music\.hidden.wav", [])));
        string folder = Path.Combine(Path.GetTempPath(), "BrimDeck-Sounds-" + Guid.NewGuid().ToString("N"));
        try
        {
            bool none = NoticeSounds.Files(folder).Count == 0;
            Directory.CreateDirectory(folder);
            foreach (var name in new[] { "b.wav", "A.mp3", "notes.txt", "c.flac" }) File.WriteAllBytes(Path.Combine(folder, name), []);
            check("Sound file: the folder lists its audio files by name, and a missing folder lists none", none && NoticeSounds.Files(folder).SequenceEqual(["A.mp3", "b.wav", "c.flac"]));
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }

    private static AttentionItem Item(NoticeLevel level, NoticeOrigin origin = NoticeOrigin.Cli, string? sound = null, AttentionKind kind = AttentionKind.Notice)
        => new() { Lane = "a", Kind = kind, Key = "k", Level = level, Style = kind is AttentionKind.Notice or AttentionKind.Summary ? new NoticeStyle { Sound = sound } : null,
            Payload = kind == AttentionKind.Notice ? new Notice { Origin = origin } : null };

    private static void Items(Action<string, bool> check)
    {
        check("Sound: a notice plays the sound of its level's style; level 1 plays none",
            NoticeSounds.For(Item(NoticeLevel.Glow, sound: "ding")) == "ding" && NoticeSounds.For(Item(NoticeLevel.Persistent)) == NoticeSounds.DefaultLoop
            && NoticeSounds.For(Item(NoticeLevel.Plain, sound: "ding")) == "" && NoticeSounds.For(Item(NoticeLevel.Glow, sound: "")) == "");
        check("Sound: a notice of level 2 or 3 plays its sound whatever sent it, a Windows application's as BrimDeck's own, the command line's and WSL's; level 1 plays none",
            NoticeSounds.For(Item(NoticeLevel.Persistent, NoticeOrigin.Windows)) == NoticeSounds.DefaultLoop && NoticeSounds.For(Item(NoticeLevel.Plain, NoticeOrigin.Windows)) == ""
            && new[] { NoticeOrigin.Windows, NoticeOrigin.Cli, NoticeOrigin.Wsl, NoticeOrigin.Test, NoticeOrigin.Internal }.All(origin => NoticeSounds.For(Item(NoticeLevel.Glow, origin)) == NoticeSounds.DefaultShort));
        check("Sound: a quota alert and a track change play none; the summary after a quiet spell plays as a notice",
            NoticeSounds.For(Item(NoticeLevel.Lively, kind: AttentionKind.Quota)) == "" && NoticeSounds.For(Item(NoticeLevel.Plain, kind: AttentionKind.Track)) == ""
            && NoticeSounds.For(Item(NoticeLevel.Glow, kind: AttentionKind.Summary)) == NoticeSounds.DefaultShort);
        // Beyond the rate limit a notice shows as a plain one (AttentionCoordinator.Count): the fourth within ten seconds.
        var attention = new AttentionCoordinator();
        var started = new List<AttentionItem>();
        for (int i = 0; i < 4; i++)
        {
            var item = new AttentionItem { Lane = "a", Kind = AttentionKind.Notice, Key = "n" + i, Group = "g" + i, Level = NoticeLevel.Glow, Style = new NoticeStyle(), Payload = new Notice { Origin = NoticeOrigin.Cli }, Duration = TimeSpan.FromSeconds(6) };
            foreach (var change in attention.Submit(item, T0.AddSeconds(i * 2))) if (change.Started is { } shown) started.Add(shown);
        }
        check("Sound: a notice shown beyond the rate limit plays none, as it does not flash",
            started.Count == 4 && started.Take(3).All(item => NoticeSounds.For(item) == NoticeSounds.DefaultShort) && started[3].Subdued && NoticeSounds.For(started[3]) == "");
        check("Sound: an open panel that stays open is known, so that its notice's sound plays once without the island", !attention.Open("b")
            && attention.SetBusy("b", true, T0) is not null && attention.Open("b") && Leaving(attention, "b", true) && !attention.Open("b") && Leaving(attention, "b", false) && attention.Open("b")
            && attention.SetBusy("b", false, T0) is not null && !attention.Open("b"));
        static bool Leaving(AttentionCoordinator attention, string lane, bool leaving) { attention.SetLeaving(lane, leaving); return true; }
    }

    private static void Pace(Action<string, bool> check)
    {
        var pace = new NoticeSoundPace();
        check("Sound pace: a notice plays once however many islands show it, and the next sound waits a second",
            pace.Admit("a", false, T0) && !pace.Admit("a", false, T0) && !pace.Admit("a", false, T0.AddSeconds(5)) && pace.Admit("b", false, T0.AddSeconds(5.5))
            && !pace.Admit("c", false, T0.AddSeconds(5.9)) && pace.Admit("c", false, T0.AddSeconds(6.5)));
        check("Sound pace: nothing plays over a loop, and a notice shown again much later plays again",
            !pace.Admit("d", true, T0.AddSeconds(20)) && pace.Admit("d", false, T0.AddSeconds(20)) && pace.Admit("a", false, T0.AddMinutes(2)));
    }

    private static void SettingsValues(Action<string, bool> check)
    {
        var settings = new DeckSettings();
        settings.NoticeStyles.Flash.Sound = "ding"; settings.NoticeStyles.Stay.Sound = "file:alarm.mp3"; settings.NoticeSoundVolume = 250;
        var back = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(settings))!;
        back.Normalize();
        var old = JsonSerializer.Deserialize<DeckSettings>("""{"NoticeStyles":{"Flash":{"Seconds":9},"Stay":{"Liquid":true}}}""")!;
        old.Normalize();
        check("Sound settings: the chosen sounds are kept, and a file written before sounds existed plays the defaults at the default volume",
            back.NoticeStyles.Flash.Sound == "ding" && back.NoticeStyles.Stay.Sound == "file:alarm.mp3" && back.NoticeStyles.Text.Sound is null && back.NoticeSoundVolume == 100
            && old.NoticeStyles.Flash.Sound is null && old.NoticeStyles.Flash.Seconds == 9 && NoticeSounds.Of(old.NoticeStyles.Flash.Sound, NoticeLevel.Glow) == NoticeSounds.DefaultShort
            && NoticeSounds.Of(old.NoticeStyles.Stay.Sound, NoticeLevel.Persistent) == NoticeSounds.DefaultLoop && old.NoticeSoundVolume == NoticeSounds.DefaultVolume);
        check("Sound settings: a copy of a style keeps its sound, and the volume belongs to no profile",
            settings.NoticeStyles.Copy().Flash.Sound == "ding" && settings.NoticeStyles.Copy().Stay.Sound == "file:alarm.mp3"
            && !Profiles.IsProfileKey(nameof(DeckSettings.NoticeSoundVolume)));
    }

    private static void BuiltIn(Action<string, bool> check)
    {
        static float Peak(float[] samples) => samples.Max(Math.Abs);
        // The largest step between two samples that follow each other, for one ear (0 left, 1 right).
        static float Step(float[] samples, int ear) { float step = 0; for (int i = ear + 2; i < samples.Length; i += 2) step = Math.Max(step, Math.Abs(samples[i] - samples[i - 2])); return step; }
        foreach (var sound in NoticeSounds.BuiltIn)
        {
            var watch = Stopwatch.StartNew();
            var clip = NoticeSoundSynth.Render(sound.Id);
            long took = watch.ElapsedMilliseconds;
            var samples = clip.Samples;
            Console.WriteLine($"INFO: sound {sound.Id}: {clip.Seconds:0.00} s, peak {Peak(samples):0.00}, made in {took} ms");
            bool sane = samples.Length > 0 && samples.Length % 2 == 0 && samples.All(float.IsFinite) && Peak(samples) is > .3f and <= .8f;
            // Not silent for long anywhere but where a score rests: at least a fifth of the clip is above 1 % of full scale.
            bool heard = samples.Count(sample => Math.Abs(sample) > .01f) > samples.Length / 5;
            bool same = NoticeSoundSynth.Render(sound.Id).Samples.AsSpan().SequenceEqual(samples);
            if (sound.Kind == NoticeSoundKind.Short)
                check($"Built-in sound {sound.Id}: a short sound, from silence to silence, the same on every run",
                    sane && heard && same && !clip.Loop && clip.Seconds is > .25 and < 1.6 && Math.Abs(samples[0]) < .02f && Math.Abs(samples[^1]) < 1e-4f && Math.Abs(samples[^2]) < 1e-4f);
            else
            {
                // The seam: the step from the last sample to the first is no larger than the steps inside the loop.
                bool seamless = Math.Abs(samples[0] - samples[^2]) <= Step(samples, 0) && Math.Abs(samples[1] - samples[^1]) <= Step(samples, 1);
                var once = NoticeSoundSynth.Render(sound.Id, once: true);
                check($"Built-in sound {sound.Id}: a loop whose end joins its start, the same on every run",
                    sane && same && clip.Loop && clip.Seconds is >= 1 and <= 8.01 && seamless && samples.Count(sample => Math.Abs(sample) > .01f) > samples.Length / 12);
                check($"Built-in sound {sound.Id}: played once it dies away after its last note instead of running into its start",
                    !once.Loop && once.Seconds > .5 && once.Seconds < clip.Seconds + 2.3 && Math.Abs(once.Samples[^1]) < 1e-4f && Math.Abs(once.Samples[0]) < .02f && Peak(once.Samples) <= .8f);
            }
        }
        check("Built-in sound: a name that is not known renders the first sound instead of failing", NoticeSoundSynth.Render("gone").Samples.AsSpan().SequenceEqual(NoticeSoundSynth.Render(NoticeSounds.BuiltIn[0].Id).Samples));
    }
}
