using System.IO;
using System.Runtime.InteropServices;
using BrimDeck.Core.Notices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BrimDeck.Native;

// Plays the notices' sounds (NoticeSounds) on Windows' default output device, in shared mode: a built-in sound from the samples
// NoticeSoundSynth makes, an imported one read from its file as it plays. One sound plays at a time. A loop goes on, its end
// joining its start, until Loop(null); a sound played once ends by itself, after NoticeSounds.OnceLimit at the latest. What
// stops early fades out within a tenth of a second instead of breaking off.
//
// Nothing here runs on the caller's thread: making or opening a sound and starting the device take a moment, so every step is
// queued and the steps run one after another in the background. While nothing plays there is no device, no thread and no buffer.
// A sound that cannot play (no output device, a file that is gone or not audio) is skipped and logged; it never reaches the caller.
internal sealed class NoticeSoundPlayer : IDisposable
{
    private readonly Func<string> _folder;
    private readonly Func<int> _volume;
    private readonly Action<Exception> _failed;
    private readonly object _gate = new();
    private Task _steps = Task.CompletedTask;
    // The loop asked for; null while none is.
    private string? _loop;
    private bool _disposed;
    // What plays or fades out; only the queued steps touch it.
    private Voice? _voice;

    // folder: where the imported files are. volume: the settings' 0 to 100, read as each sound starts.
    public NoticeSoundPlayer(Func<string> folder, Func<int> volume, Action<Exception> failed) { _folder = folder; _volume = volume; _failed = failed; }

    public bool Looping { get { lock (_gate) return _loop is not null; } }

    // The loop that plays from now on (NoticeSounds.Of, of level 3), or null for none. Asking for the one that plays changes nothing.
    public void Loop(string? sound)
    {
        if (sound is { Length: 0 }) sound = null;
        lock (_gate)
        {
            if (_disposed || _loop == sound) return;
            _loop = sound;
        }
        Queue(() => { if (sound is null) { if (_voice is { Loops: true }) Silence(); } else Start(sound, loop: true); });
    }

    // Plays the sound once, in place of one that still plays once. Nothing plays over a loop.
    public void Once(string sound)
    {
        if (sound.Length == 0) return;
        lock (_gate) if (_disposed || _loop is not null) return;
        Queue(() => { if (!Looping) Start(sound, loop: false); });
    }

    // Whether the file can be played: opened and read a little, in the background. For the settings, before a file is kept.
    public static Task<bool> PlayableAsync(string path) => Task.Run(() =>
    {
        try
        {
            using var source = Source.FromFile(path, loop: false, gain: 1);
            return source.Read(new byte[8192], 0, 8192) > 0;
        }
        catch (Exception ex) when (Expected(ex)) { return false; }
    });

    private void Queue(Action step)
    {
        lock (_gate)
            _steps = _steps.ContinueWith(_ =>
            {
                try { step(); }
                catch (Exception ex) when (Expected(ex)) { _failed(ex); }
            }, TaskScheduler.Default);
    }

    // What a missing device, a device taken away while it plays, or a file that is not audio raise.
    private static bool Expected(Exception ex) => ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException
        or FormatException or NotSupportedException or ArgumentException or InvalidCastException;

    private void Silence() { _voice?.End(); _voice = null; }

    private void Start(string sound, bool loop)
    {
        Silence();
        double gain = NoticeSounds.Gain(_volume());
        if (gain <= 0) return;
        var source = Open(sound, loop, (float)gain);
        WasapiOut? output = null;
        try
        {
            output = new WasapiOut(AudioClientShareMode.Shared, true, 80);
            var voice = new Voice(output, source, loop);
            // Raised on the device's own thread once the sound has ended or faded out; it is let go on another thread.
            output.PlaybackStopped += (_, stopped) =>
            {
                if (stopped.Exception is { } error && Expected(error)) _failed(error);
                Task.Run(voice.Dispose);
            };
            output.Init(source);
            output.Play();
            _voice = voice;
        }
        catch
        {
            output?.Dispose(); source.Dispose();
            throw;
        }
    }

    // The sound as samples: an imported file, or a built-in sound. A file that is gone or cannot be read plays the default
    // sound of its kind instead, so that an alarm is not silent because a file was moved.
    private Source Open(string sound, bool loop, float gain)
    {
        if (NoticeSounds.FileName(sound) is { } name)
        {
            try { return Source.FromFile(Path.Combine(_folder(), name), loop, gain); }
            catch (Exception ex) when (Expected(ex)) { _failed(ex); }
            sound = NoticeSounds.Default(loop ? NoticeSoundKind.Loop : NoticeSoundKind.Short);
        }
        return Source.FromClip(NoticeSoundSynth.Render(sound, once: !loop), gain);
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _loop = null; }
        // The last step: what plays is cut off at once, the process is leaving.
        _steps.ContinueWith(_ => { var voice = _voice; _voice = null; voice?.Dispose(); }, TaskScheduler.Default);
    }

    // One playing sound: the device stream and what it reads.
    private sealed class Voice(WasapiOut output, Source source, bool loops) : IDisposable
    {
        private int _disposed;
        public bool Loops => loops;
        // Fades out; the device stops once the fade has played, and PlaybackStopped lets go of both.
        public void End() => source.End();
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { output.Dispose(); } catch (Exception ex) when (Expected(ex)) { }
            source.Dispose();
        }
    }

    // The samples of one sound as the device reads them: 32-bit float, two channels, at the sound's own rate (Windows converts to
    // the device's). A loop starts over at its end; a sound played once ends there, or fades out at NoticeSounds.OnceLimit.
    internal sealed class Source : IWaveProvider, IDisposable
    {
        internal const double FadeSeconds = .12;
        private readonly float[]? _clip;
        private int _clipAt;
        private readonly WaveStream? _stream;
        private readonly ISampleProvider? _samples;
        private readonly int _channels;
        private float[] _read = [];
        private readonly bool _loop;
        private readonly float _gain, _fadeStep;
        // Frames still to play before the fade at the limit starts; a loop has no limit.
        private long _left;
        private float _fade = 1;
        private volatile bool _ending;
        private bool _ended;

        public WaveFormat WaveFormat { get; }

        private Source(float[]? clip, WaveStream? stream, int rate, bool loop, float gain)
        {
            _clip = clip; _stream = stream; _loop = loop; _gain = gain;
            if (stream is not null) { _samples = stream.ToSampleProvider(); _channels = _samples.WaveFormat.Channels; }
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
            _fadeStep = (float)(1 / (FadeSeconds * rate));
            _left = loop ? long.MaxValue : (long)((NoticeSounds.OnceLimit.TotalSeconds - FadeSeconds) * rate);
        }

        public static Source FromClip(NoticeSoundClip clip, float gain) => new(clip.Samples, null, NoticeSoundSynth.Rate, clip.Loop, gain);

        // A .wav of plain samples is read directly, so that it loops to the sample; everything else goes through Windows' decoders.
        public static Source FromFile(string path, bool loop, float gain)
        {
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException(null, path);
            if (file.Length > NoticeSounds.MaxFileBytes) throw new InvalidDataException();
            WaveStream? stream = null;
            try
            {
                if (file.Extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    var wave = new WaveFileReader(path);
                    if (wave.WaveFormat.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat) stream = wave; else wave.Dispose();
                }
                stream ??= new MediaFoundationReader(path);
                var format = stream.WaveFormat;
                if (format.Channels < 1 || format.SampleRate < 8000) throw new InvalidDataException();
                return new Source(null, stream, format.SampleRate, loop, gain);
            }
            catch { stream?.Dispose(); throw; }
        }

        public void End() => _ending = true;

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_ended) return 0;
            var output = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count - count % 8));
            int frames = Fill(output);
            for (int i = 0; i < frames; i++)
            {
                if (_ending || _left <= 0)
                {
                    _fade -= _fadeStep;
                    if (_fade <= 0) { _ended = true; frames = i; break; }
                }
                else _left--;
                float scale = _gain * _fade;
                output[2 * i] *= scale; output[2 * i + 1] *= scale;
            }
            if (frames == 0) _ended = true;
            return frames * 8;
        }

        // Fills left and right samples; fewer frames than asked for only at the end of a sound that plays once.
        private int Fill(Span<float> output)
        {
            int frames = output.Length / 2, filled = 0, rewinds = 0;
            if (_clip is { } clip)
            {
                while (filled < frames)
                {
                    int take = Math.Min(frames - filled, clip.Length / 2 - _clipAt);
                    clip.AsSpan(2 * _clipAt, 2 * take).CopyTo(output[(2 * filled)..]);
                    filled += take; _clipAt += take;
                    if (_clipAt < clip.Length / 2) continue;
                    if (!_loop || clip.Length == 0) break;
                    _clipAt = 0;
                }
                return filled;
            }
            while (filled < frames)
            {
                int want = (frames - filled) * _channels;
                if (_read.Length < want) _read = new float[want];
                int got = _samples!.Read(_read, 0, want) / _channels;
                if (got == 0)
                {
                    // The file's end: a loop goes back to its start. A file that gives nothing twice in a row has nothing to play.
                    if (!_loop || ++rewinds > 1) break;
                    _stream!.Position = 0;
                    continue;
                }
                rewinds = 0;
                for (int i = 0; i < got; i++)
                {
                    float left = _read[i * _channels];
                    output[2 * (filled + i)] = left;
                    output[2 * (filled + i) + 1] = _channels > 1 ? _read[i * _channels + 1] : left;
                }
                filled += got;
            }
            return filled;
        }

        public void Dispose() => _stream?.Dispose();
    }
}
