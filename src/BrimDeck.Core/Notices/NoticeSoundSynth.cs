namespace BrimDeck.Core.Notices;

// A built-in sound as samples: left and right interleaved, at NoticeSoundSynth.Rate. Loop: played end to start again and again
// it has no seam (its notes and its reverberation run across the end into the start).
public sealed record NoticeSoundClip(float[] Samples, bool Loop)
{
    public int Frames => Samples.Length / 2;
    public double Seconds => (double)Frames / NoticeSoundSynth.Rate;
}

// The built-in notice sounds (NoticeSounds.BuiltIn), made from decaying sine partials when one is to play, so that no audio file
// ships with BrimDeck. Every sound is the same on every run. A loop is written on a ring: a note that rings past the end goes on
// from the start, and the reverberation is run round the ring until it repeats, so the end joins the start exactly.
public static class NoticeSoundSynth
{
    public const int Rate = 44100;
    // Room after the last note of a sound that plays once, for its notes and the reverberation to die away; the silence left
    // over is cut off.
    private const double Tail = 2.2;

    // once: a loop as it plays a single time (tried in the settings, or shown on the open panel's page): the notes of one round,
    // dying away after the last instead of running into the start.
    public static NoticeSoundClip Render(string id, bool once = false)
    {
        var info = NoticeSounds.BuiltIn.FirstOrDefault(known => known.Id == id) ?? NoticeSounds.BuiltIn[0];
        var score = ScoreOf(info.Id);
        bool ring = info.Kind == NoticeSoundKind.Loop && !once;
        var track = new Track(ring ? score.Seconds : score.Seconds + Tail, ring);
        score.Write(track);
        Reverberate(track, score.Wet);
        return new NoticeSoundClip(track.Finish(score.Peak), ring);
    }

    // Seconds: a loop's length, or until a short sound's last note starts. Wet: how much reverberation. Peak: the loudest sample.
    private sealed record Score(double Seconds, Action<Track> Write, double Wet, double Peak);

    private sealed class Track(double seconds, bool ring)
    {
        public readonly float[] Left = new float[(int)Math.Round(seconds * Rate)], Right = new float[(int)Math.Round(seconds * Rate)];
        public bool Ring => ring;
        public int Length => Left.Length;

        public void Add(long index, double left, double right)
        {
            if (ring) index %= Length; else if (index >= Length) return;
            Left[index] += (float)left; Right[index] += (float)right;
        }

        public float[] Finish(double peak)
        {
            float loudest = 0;
            for (int i = 0; i < Length; i++) loudest = Math.Max(loudest, Math.Max(Math.Abs(Left[i]), Math.Abs(Right[i])));
            if (loudest <= 0) return new float[2 * Length];
            int length = Length;
            if (!ring)
            {
                // It ends where it has died away to 1 % of its loudest (40 dB down), its last 60 ms fading to nothing.
                int last = Length - 1;
                while (last > 0 && Math.Abs(Left[last]) < loudest * .01f && Math.Abs(Right[last]) < loudest * .01f) last--;
                length = Math.Min(Length, last + 1 + (int)(.06 * Rate));
                int fade = Math.Min(length, (int)(.06 * Rate));
                for (int i = 0; i < fade; i++) { float part = (float)i / fade; Left[length - 1 - i] *= part; Right[length - 1 - i] *= part; }
            }
            float scale = (float)(peak / loudest);
            var samples = new float[2 * length];
            for (int i = 0; i < length; i++) { samples[2 * i] = Left[i] * scale; samples[2 * i + 1] = Right[i] * scale; }
            return samples;
        }
    }

    private static double Hz(int midi) => 440 * Math.Pow(2, (midi - 69) / 12.0);
    // pan: -1 left, 0 the middle, 1 right, at equal power.
    private static (double Left, double Right) Pan(double gain, double pan) => (gain * Math.Cos((pan + 1) * Math.PI / 4), gain * Math.Sin((pan + 1) * Math.PI / 4));

    // An instrument's partials: the multiple of the note's frequency, how loud, and the seconds its sound takes to fall to 37 %.
    private static readonly (double Ratio, double Amp, double Tau)[]
        Bell = [(1, 1, .55), (2.756, .32, .28), (5.404, .16, .14), (8.933, .07, .07)],
        Marimba = [(1, 1, .32), (4, .22, .07), (10, .06, .025)],
        Kalimba = [(1, 1, .45), (2, .16, .22), (3, .07, .12), (5.42, .14, .035)],
        Glass = [(1, 1, .38), (2, .1, .2), (3, .03, .1)],
        Wood = [(1, 1, .055), (2.57, .35, .03), (5.2, .15, .015)],
        Trill = [(1, 1, .07), (2, .3, .045), (3, .12, .03)];

    // One note: each partial a sine that starts from nothing within 3 ms and dies away; a higher note dies away sooner. stretch
    // makes every partial ring longer or shorter.
    private static void Tone(Track track, double at, double freq, double gain, double pan, (double Ratio, double Amp, double Tau)[] partials, double stretch = 1)
    {
        var (left, right) = Pan(gain, pan);
        long first = (long)Math.Round(at * Rate);
        double shorter = Math.Clamp(Math.Pow(660 / freq, .35), .5, 1.6);
        int rise = (int)(.003 * Rate);
        foreach (var (ratio, amp, tau) in partials)
        {
            double f = freq * ratio, lasts = tau * stretch * shorter;
            if (f >= Rate * .45) continue;
            // Until 60 dB down.
            int count = (int)(lasts * 6.9 * Rate);
            double step = 2 * Math.PI * f / Rate, decay = Math.Exp(-1 / (lasts * Rate)), level = amp;
            for (int i = 0; i < count; i++)
            {
                double value = Math.Sin(step * i) * level * (i < rise ? (double)i / rise : 1);
                level *= decay;
                track.Add(first + i, value * left, value * right);
            }
        }
    }

    // A drop of water: a sine whose pitch slides from one frequency to another within bend seconds as it dies away.
    private static void Glide(Track track, double at, double from, double to, double bend, double tau, double gain, double pan)
    {
        var (left, right) = Pan(gain, pan);
        long first = (long)Math.Round(at * Rate);
        int count = (int)(tau * 6.9 * Rate), rise = (int)(.004 * Rate);
        double phase = 0, level = 1, decay = Math.Exp(-1 / (tau * Rate)), offset = from - to, slide = Math.Exp(-1 / (bend * Rate));
        for (int i = 0; i < count; i++)
        {
            phase += 2 * Math.PI * (to + offset) / Rate; offset *= slide;
            double value = Math.Sin(phase) * level * (i < rise ? (double)i / rise : 1);
            level *= decay;
            track.Add(first + i, value * left, value * right);
        }
    }

    // An electric piano key: a sine whose own double bends it, strongly as it is struck and less as it rings, with a short
    // bright partial at the strike.
    private static void Key(Track track, double at, double freq, double gain, double pan, double tau = .55)
    {
        var (left, right) = Pan(gain, pan);
        long first = (long)Math.Round(at * Rate);
        int count = (int)(tau * 6.2 * Rate), rise = (int)(.004 * Rate);
        double step = 2 * Math.PI * freq / Rate, level = 1, decay = Math.Exp(-1 / (tau * Rate)), bend = 1.1, bendDecay = Math.Exp(-1 / (.22 * Rate)), strike = .12, strikeDecay = Math.Exp(-1 / (.05 * Rate));
        for (int i = 0; i < count; i++)
        {
            double theta = step * i;
            double value = (Math.Sin(theta + (bend + .15) * Math.Sin(theta)) + strike * Math.Sin(theta * 7)) * level * (i < rise ? (double)i / rise : 1);
            level *= decay; bend *= bendDecay; strike *= strikeDecay;
            track.Add(first + i, value * left, value * right);
        }
    }

    // A steady tone with soft edges (8 ms each), for an alarm clock's beep.
    private static void Beep(Track track, double at, double freq, double seconds, double gain)
    {
        var (left, right) = Pan(gain, 0);
        long first = (long)Math.Round(at * Rate);
        int count = (int)(seconds * Rate), edge = (int)(.008 * Rate);
        double step = 2 * Math.PI * freq / Rate;
        for (int i = 0; i < count; i++)
        {
            double shape = i < edge ? .5 - .5 * Math.Cos(Math.PI * i / edge) : i >= count - edge ? .5 - .5 * Math.Cos(Math.PI * (count - i) / edge) : 1;
            double theta = step * i, value = (Math.Sin(theta) + .22 * Math.Sin(2 * theta) + .08 * Math.Sin(3 * theta)) * shape;
            track.Add(first + i, value * left, value * right);
        }
    }

    // The tap of a knock: a few milliseconds of dulled noise, the same on every run.
    private static void Tap(Track track, double at, double gain, int seed)
    {
        var random = new Random(seed);
        long first = (long)Math.Round(at * Rate);
        int count = (int)(.012 * Rate);
        double dull = 0, level = gain, decay = Math.Exp(-1 / (.0025 * Rate));
        for (int i = 0; i < count; i++)
        {
            dull += .35 * (random.NextDouble() * 2 - 1 - dull);
            track.Add(first + i, dull * level * .7, dull * level * .7);
            level *= decay;
        }
    }

    // A small room around the sound: eight feedback delays side by side and four all-pass delays after them for each ear (the
    // layout and the delay lengths of the public-domain Freeverb), short and quiet. On a ring it runs round until what it adds
    // repeats, and that round is kept.
    private static readonly int[] CombDelays = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617], PassDelays = [556, 441, 341, 225];

    private static void Reverberate(Track track, double wet)
    {
        if (wet <= 0) return;
        const float feedback = .72f, damp = .35f;
        int length = track.Length, rounds = track.Ring ? 1 + (int)Math.Ceiling(2.0 * Rate / length) : 1;
        var input = new float[length];
        for (int i = 0; i < length; i++) input[i] = (track.Left[i] + track.Right[i]) * .015f;
        for (int ear = 0; ear < 2; ear++)
        {
            var combs = CombDelays.Select(delay => new float[delay + ear * 23]).ToArray();
            var passes = PassDelays.Select(delay => new float[delay + ear * 23]).ToArray();
            int[] combAt = new int[combs.Length], passAt = new int[passes.Length];
            var kept = new float[combs.Length];
            var added = new float[length];
            for (int round = 0; round < rounds; round++)
                for (int i = 0; i < length; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < combs.Length; c++)
                    {
                        var line = combs[c];
                        float delayed = line[combAt[c]];
                        float held = delayed * (1 - damp) + kept[c] * damp;
                        kept[c] = Math.Abs(held) < 1e-18f ? 0 : held;
                        line[combAt[c]] = input[i] + kept[c] * feedback;
                        if (++combAt[c] == line.Length) combAt[c] = 0;
                        sum += delayed;
                    }
                    for (int p = 0; p < passes.Length; p++)
                    {
                        var line = passes[p];
                        float delayed = line[passAt[p]];
                        line[passAt[p]] = sum + delayed * .5f;
                        if (++passAt[p] == line.Length) passAt[p] = 0;
                        sum = delayed - sum;
                    }
                    if (round == rounds - 1) added[i] = sum;
                }
            var target = ear == 0 ? track.Left : track.Right;
            float amount = (float)(wet * 3);
            for (int i = 0; i < length; i++) target[i] += added[i] * amount;
        }
    }

    private static Score ScoreOf(string id) => id switch
    {
        // ----- Level 2: played once -----
        "ding" => new(.1, track =>
        {
            Tone(track, 0, Hz(88), 1, 0, Bell, .6);
            Tone(track, 0, Hz(76), .25, 0, Glass, .6);
        }, .4, .7),
        "duo" => new(.3, track =>
        {
            Tone(track, 0, Hz(79), .9, -.15, Marimba, .7); Tone(track, 0, Hz(79), .2, -.15, Bell, .3);
            Tone(track, .14, Hz(84), 1, .15, Marimba, .8); Tone(track, .14, Hz(84), .22, .15, Bell, .35);
        }, .35, .7),
        "arp" => new(.4, track =>
        {
            int[] notes = [74, 81, 86, 90];
            for (int i = 0; i < notes.Length; i++) Tone(track, i * .085, Hz(notes[i]), .75 + i * .08, -.3 + i * .2, Kalimba, .6);
        }, .4, .7),
        "knock" => new(.3, track =>
        {
            Tap(track, 0, .3, 7); Tone(track, 0, Hz(60), 1, -.1, Wood); Tone(track, 0, Hz(72), .45, -.1, Wood);
            Tap(track, .15, .26, 11); Tone(track, .15, Hz(62), .9, .1, Wood); Tone(track, .15, Hz(74), .4, .1, Wood);
        }, .2, .75),
        "chord" => new(.2, track =>
        {
            int[] notes = [72, 79, 84, 88];
            for (int i = 0; i < notes.Length; i++) Key(track, i * .025, Hz(notes[i]), .7, -.3 + i * .2, .28);
        }, .45, .65),
        // ----- Level 3: loops -----
        // Two bars of eighth notes at 120 beats a minute: C major, then F major.
        "marimba" => new(4, track =>
        {
            int[] notes = [72, 79, 76, 79, 84, 79, 76, 79, 69, 77, 72, 77, 81, 77, 72, 77];
            for (int i = 0; i < notes.Length; i++)
                Tone(track, i * .25, Hz(notes[i]), i % 4 == 0 ? 1 : i % 2 == 0 ? .8 : .62, i % 2 == 0 ? -.2 : .2, Marimba, 1.3);
            Tone(track, 0, Hz(60), .5, 0, Marimba, 2); Tone(track, 2, Hz(65), .5, 0, Marimba, 2);
        }, .35, .7),
        // Twelve slow beats of bells in E major, two of them rests, over two low notes.
        "chimes" => new(6, track =>
        {
            int[] notes = [88, 83, 85, 0, 80, 83, 85, 88, 90, 0, 85, 83];
            for (int i = 0; i < notes.Length; i++)
                if (notes[i] > 0) Tone(track, i * .5, Hz(notes[i]), i % 4 == 0 ? .9 : .7, i % 2 == 0 ? -.25 : .25, Bell, 1.5);
            Tone(track, 0, Hz(64), .35, 0, Glass, 4); Tone(track, 3, Hz(69), .35, 0, Glass, 4);
        }, .5, .65),
        // Four chords of two seconds each, their notes played one after another.
        "morning" => new(8, track =>
        {
            int[][] chords = [[60, 64, 67, 71], [57, 60, 64, 67], [53, 57, 60, 64], [55, 59, 62, 67]];
            for (int c = 0; c < chords.Length; c++)
            {
                var chord = chords[c];
                int[] notes = [chord[0], chord[1], chord[2], chord[3], chord[1] + 12, chord[3], chord[2], chord[1]];
                for (int i = 0; i < notes.Length; i++)
                    Key(track, c * 2 + i * .25, Hz(notes[i]), i == 0 ? 1 : i == 4 ? .85 : .65, i % 2 == 0 ? -.2 : .2);
            }
        }, .45, .65),
        // A telephone: two short rings of two notes changing quickly, then two seconds of quiet.
        "phone" => new(3, track =>
        {
            foreach (double start in new[] { 0, .6 })
                for (int i = 0; i < 10; i++) Tone(track, start + i * .04, Hz(i % 2 == 0 ? 88 : 91), .8, 0, Trill);
        }, .3, .6),
        // An alarm clock: four beeps, then quiet.
        "beeps" => new(1.2, track =>
        {
            for (int i = 0; i < 4; i++) Beep(track, i * .13, Hz(91), .075, 1);
        }, .12, .5),
        // "drop", and the fallback for a name that is not known.
        _ => new(.2, track =>
        {
            Glide(track, 0, 520, 1080, .035, .085, .9, -.1);
            Glide(track, .13, 760, 1560, .03, .07, .5, .15);
        }, .35, .7)
    };
}
