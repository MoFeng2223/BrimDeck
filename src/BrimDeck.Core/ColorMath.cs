namespace BrimDeck.Core;

// Conversions between RGB and HSV (hue in degrees 0-360, saturation and value 0-1) for the settings color picker.
public static class ColorMath
{
    public static (byte R, byte G, byte B) FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360; s = Math.Clamp(s, 0, 1); v = Math.Clamp(v, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x)
        };
        static byte Byte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
        return (Byte(r + m), Byte(g + m), Byte(b + m));
    }

    // Grays have no hue and report 0; callers that track a hue keep their own for them.
    public static (double H, double S, double V) ToHsv(byte red, byte green, byte blue)
    {
        double r = red / 255d, g = green / 255d, b = blue / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h < 0 ? h + 360 : h, max == 0 ? 0 : d / max, max);
    }

    public static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    // The main color of a picture given as BGRA pixels: the hue that its colored pixels share most, as the mean of those pixels.
    // An average of every pixel mixed the colors of a many-colored picture into a muddy one, and gave a gray picture a hue it does
    // not have. Null when too little of the picture is colored (a white, gray or black icon, a transparent one).
    public static (byte R, byte G, byte B)? Dominant(ReadOnlySpan<byte> bgra)
    {
        const int Bins = 24;
        Span<double> weight = stackalloc double[Bins], red = stackalloc double[Bins], green = stackalloc double[Bins], blue = stackalloc double[Bins];
        int opaque = 0, colored = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] < 128) continue;
            opaque++;
            var (h, s, v) = ToHsv(bgra[i + 2], bgra[i + 1], bgra[i]);
            if (s < .25 || v < .25) continue;
            colored++;
            int bin = (int)(h / (360d / Bins)) % Bins;
            double w = s * v;
            weight[bin] += w; red[bin] += bgra[i + 2] * w; green[bin] += bgra[i + 1] * w; blue[bin] += bgra[i] * w;
        }
        if (opaque == 0 || colored < Math.Max(3, opaque * .08)) return null;
        // The hue with the most weight together with its two neighbors, so that a color split across a bin's edge is one color.
        int best = 0; double most = -1;
        for (int b = 0; b < Bins; b++)
        {
            double sum = weight[(b + Bins - 1) % Bins] + weight[b] + weight[(b + 1) % Bins];
            if (sum > most) { most = sum; best = b; }
        }
        double r = 0, g = 0, bl = 0, total = 0;
        foreach (int b in new[] { (best + Bins - 1) % Bins, best, (best + 1) % Bins }) { r += red[b]; g += green[b]; bl += blue[b]; total += weight[b]; }
        return ((byte)Math.Round(r / total), (byte)Math.Round(g / total), (byte)Math.Round(bl / total));
    }

    // How far apart two #RRGGBB colors are, as the straight distance between them in RGB (0 to about 441).
    public static double Distance(string first, string second)
    {
        double sum = 0;
        for (int i = 0; i < 3; i++) { double d = Channel(first, i) - Channel(second, i); sum += d * d; }
        return Math.Sqrt(sum);
    }

    // The color share of the way from one #RRGGBB color to another, per channel.
    public static string Mix(string from, string to, double share)
    {
        share = Math.Clamp(double.IsFinite(share) ? share : 0, 0, 1);
        byte At(int index) => (byte)Math.Round(Channel(from, index) + (Channel(to, index) - Channel(from, index)) * share);
        return ToHex(At(0), At(1), At(2));
    }
    // Red, green or blue (index 0, 1, 2) of a #RRGGBB color.
    private static int Channel(string hex, int index) => Convert.ToInt32(hex.Substring(1 + index * 2, 2), 16);
}
