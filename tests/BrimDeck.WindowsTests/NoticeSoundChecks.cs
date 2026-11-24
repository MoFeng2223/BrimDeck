using System.IO;
using System.Runtime.InteropServices;
using BrimDeck.Core.Notices;
using BrimDeck.Native;
using NAudio.Wave;

// The notices' sounds as the output device reads them (NoticeSoundPlayer.Source): a built-in clip and an imported file, played
// once and as a loop. With device, a sound is also played through Windows' default output, 80 dB down (the volume slider at 1),
// which cannot be heard; nothing else here makes a sound.
internal static class NoticeSoundChecks
{
    public static void Run(Action<string, bool> check, bool device)
    {
        string folder = Path.Combine(Path.GetTempPath(), "BrimDeck-SoundChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Clips(check);
            WaveFiles(check, folder);
            Decoded(check, folder);
            if (device) Device(check, folder);
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }

    // Reads up to frames frames of left and right samples, in buffers of an uneven size as a device asks for them.
    private static float[] Read(NoticeSoundPlayer.Source source, int frames)
    {
        var all = new List<float>();
        var buffer = new byte[8 * 1111];
        while (all.Count < 2 * frames)
        {
            int want = Math.Min(buffer.Length, 8 * (frames - all.Count / 2));
            int got = source.Read(buffer, 0, want);
            if (got == 0) break;
            all.AddRange(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, got)).ToArray());
        }
        return [.. all];
    }

    private static void Clips(Action<string, bool> check)
    {
        var once = NoticeSoundSynth.Render("ding");
        using (var source = NoticeSoundPlayer.Source.FromClip(once, .5f))
        {
            var read = Read(source, once.Frames * 3);
            bool same = read.Length == once.Samples.Length;
            for (int i = 0; same && i < read.Length; i++) same = Math.Abs(read[i] - once.Samples[i] * .5f) < 1e-6f;
            check("Sound source: a clip played once gives its samples at the chosen volume and then ends",
                same && source.WaveFormat is { Encoding: WaveFormatEncoding.IeeeFloat, Channels: 2, SampleRate: NoticeSoundSynth.Rate } && source.Read(new byte[64], 0, 64) == 0);
        }
        var loop = NoticeSoundSynth.Render("beeps");
        using (var source = NoticeSoundPlayer.Source.FromClip(loop, 1))
        {
            int frames = loop.Frames;
            var read = Read(source, frames * 2 + 500);
            bool rounds = read.Length == 2 * (frames * 2 + 500);
            for (int i = 0; rounds && i < 2 * (frames + 500); i++) rounds = read[i] == read[i % (2 * frames)] && read[i] == loop.Samples[i % (2 * frames)];
            // Asked to end, it fades to nothing within the fade's time and then gives no more.
            source.End();
            var tail = Read(source, NoticeSoundSynth.Rate);
            int fade = (int)(NoticeSoundPlayer.Source.FadeSeconds * NoticeSoundSynth.Rate);
            check("Sound source: a loop goes round from its end to its start, sample for sample, until it is asked to end, and then fades out",
                rounds && tail.Length / 2 is > 0 and var faded && faded <= fade + 1 && faded >= fade - 2 && source.Read(new byte[64], 0, 64) == 0);
        }
    }

    // A file of 16-bit samples that count up, so that every frame is known by its place.
    private static string Ramp(string folder, string name, int rate, int channels, int frames)
    {
        string path = Path.Combine(folder, name);
        using var writer = new WaveFileWriter(path, new WaveFormat(rate, 16, channels));
        var samples = new short[frames * channels];
        for (int i = 0; i < frames; i++) for (int c = 0; c < channels; c++) samples[i * channels + c] = (short)((i % 20000 + 1) * (c == 0 ? 1 : -1));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private static void WaveFiles(Action<string, bool> check, string folder)
    {
        string mono = Ramp(folder, "mono.wav", 22050, 1, 1000), stereo = Ramp(folder, "stereo.wav", 48000, 2, 1000);
        using (var source = NoticeSoundPlayer.Source.FromFile(mono, loop: true, gain: 1))
        {
            var read = Read(source, 2600);
            bool rounds = read.Length == 5200;
            for (int i = 0; rounds && i < 2600; i++) rounds = Math.Abs(read[2 * i] - (i % 1000 + 1) / 32768f) < 1e-6f && read[2 * i + 1] == read[2 * i];
            check("Sound file: a .wav loops from its last sample to its first, at its own rate, a single channel in both ears", rounds && source.WaveFormat is { SampleRate: 22050, Channels: 2 });
        }
        using (var source = NoticeSoundPlayer.Source.FromFile(stereo, loop: false, gain: 1))
        {
            var read = Read(source, 5000);
            bool whole = read.Length == 2000;
            for (int i = 0; whole && i < 1000; i++) whole = Math.Abs(read[2 * i] - (i + 1) / 32768f) < 1e-6f && Math.Abs(read[2 * i + 1] + (i + 1) / 32768f) < 1e-6f;
            check("Sound file: played once a .wav gives every frame, left and right apart, and ends", whole && source.WaveFormat.SampleRate == 48000);
        }
        // Twelve seconds at 8 kHz: played once it stops at the limit, the last tenth of a second fading out.
        string longer = Ramp(folder, "long.wav", 8000, 1, 12 * 8000);
        using (var source = NoticeSoundPlayer.Source.FromFile(longer, loop: false, gain: 1))
        {
            var read = Read(source, 13 * 8000);
            int limit = (int)(NoticeSounds.OnceLimit.TotalSeconds * 8000);
            check("Sound file: a long file played once ends at the limit, fading out", Math.Abs(read.Length / 2 - limit) <= 2 && Math.Abs(read[^2]) < Math.Abs(read[2 * (limit - 2000)]) / 20);
        }
        string text = Path.Combine(folder, "notes.wav"), missing = Path.Combine(folder, "gone.wav");
        File.WriteAllText(text, "not audio at all, only words that happen to be in a file named like a sound");
        check("Sound file: a file that is no audio, or is not there, is told apart before it is kept",
            NoticeSoundPlayer.PlayableAsync(mono).GetAwaiter().GetResult() && !NoticeSoundPlayer.PlayableAsync(text).GetAwaiter().GetResult() && !NoticeSoundPlayer.PlayableAsync(missing).GetAwaiter().GetResult());
    }

    // Compressed files go through Windows' own decoders. A machine without the encoder to make the test file (a server
    // edition) cannot run this part; it says so instead of failing.
    private static void Decoded(Action<string, bool> check, string folder)
    {
        foreach (var (name, encode) in new (string, Action<IWaveProvider, string>)[] { ("tone.mp3", (wave, path) => MediaFoundationEncoder.EncodeToMp3(wave, path)), ("tone.m4a", (wave, path) => MediaFoundationEncoder.EncodeToAac(wave, path)) })
        {
            string path = Path.Combine(folder, name);
            try
            {
                // One second of a 440 Hz tone.
                var samples = new float[44100 * 2];
                for (int i = 0; i < 44100; i++) samples[2 * i] = samples[2 * i + 1] = (float)(Math.Sin(2 * Math.PI * 440 * i / 44100) * .5);
                var bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
                using var raw = new RawSourceWaveStream(new MemoryStream(bytes), WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
                NAudio.MediaFoundation.MediaFoundationApi.Startup();
                encode(new Wave16(raw), path);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                Console.WriteLine($"SKIPPED: {name} could not be made here ({ex.GetType().Name}); reading compressed files is not checked.");
                continue;
            }
            using var source = NoticeSoundPlayer.Source.FromFile(path, loop: true, gain: 1);
            // Three seconds of a one-second file: it went round at least twice, and what it gives is the tone, not silence.
            var read = Read(source, 3 * source.WaveFormat.SampleRate);
            check($"Sound file: {name} is decoded and loops", read.Length == 6 * source.WaveFormat.SampleRate && read.Max() > .3f && read.Skip(read.Length * 2 / 3).Max() > .3f
                && NoticeSoundPlayer.PlayableAsync(path).GetAwaiter().GetResult());
        }
    }

    // The encoders take 16-bit samples.
    private sealed class Wave16(WaveStream source) : IWaveProvider
    {
        private readonly IWaveProvider _converted = source.ToSampleProvider().ToWaveProvider16();
        public WaveFormat WaveFormat => _converted.WaveFormat;
        public int Read(byte[] buffer, int offset, int count) => _converted.Read(buffer, offset, count);
    }

    // Through the default output device, 80 dB down: a sound played once, a loop that is started and ended, and a file. Nothing
    // may fail; a machine without an output device reports it and the check is skipped.
    private static void Device(Action<string, bool> check, string folder)
    {
        var failures = new List<Exception>();
        File.Copy(Ramp(folder, "device.wav", 44100, 2, 44100), Path.Combine(folder, "bell.wav"));
        using var player = new NoticeSoundPlayer(() => folder, () => 1, error => { lock (failures) failures.Add(error); });
        player.Once(NoticeSounds.DefaultShort);
        Thread.Sleep(1500);
        player.Loop(NoticeSounds.DefaultLoop);
        Thread.Sleep(1200);
        bool looping = player.Looping;
        // Played once over a loop: refused.
        player.Once("ding");
        player.Loop(null);
        Thread.Sleep(600);
        player.Once(NoticeSounds.ForFile("bell.wav"));
        Thread.Sleep(1600);
        // A file that is gone plays the default instead, and says so.
        player.Loop(NoticeSounds.ForFile("gone.mp3"));
        Thread.Sleep(700);
        player.Loop(null);
        Thread.Sleep(500);
        List<Exception> seen;
        lock (failures) seen = [.. failures];
        if (seen.Any(error => error is COMException))
        {
            Console.WriteLine("SKIPPED: no usable audio output device here (" + string.Join(", ", seen.Select(error => $"{error.GetType().Name} 0x{error.HResult:X8}")) + ").");
            return;
        }
        check("Sound device: a sound, a loop and a file play through the default output without a failure; a missing file is the only one reported",
            looping && !player.Looping && seen.Count == 1 && seen[0] is FileNotFoundException);
    }
}
