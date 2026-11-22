using BrimDeck.Core;

internal static class ColorMathTests
{
    internal static void Run(Action<string, bool> check)
    {
        check("HSV primaries convert to RGB",
            ColorMath.FromHsv(0, 1, 1) == (255, 0, 0) && ColorMath.FromHsv(120, 1, 1) == (0, 255, 0) && ColorMath.FromHsv(240, 1, 1) == (0, 0, 255));
        check("HSV hue wraps and out-of-range values clamp",
            ColorMath.FromHsv(360, 1, 1) == (255, 0, 0) && ColorMath.FromHsv(-120, 1, 1) == (0, 0, 255) && ColorMath.FromHsv(30, 2, -1) == (0, 0, 0));
        check("Zero saturation gives gray", ColorMath.FromHsv(200, 0, .5) == (128, 128, 128));
        var (h, s, v) = ColorMath.ToHsv(0xB9, 0xA8, 0xFF);
        check("RGB converts to HSV", Math.Abs(h - 251.7) < .1 && Math.Abs(s - .341) < .001 && v == 1);
        check("Grays report no hue or saturation", ColorMath.ToHsv(128, 128, 128) is (0, 0, _) && ColorMath.ToHsv(0, 0, 0) == (0, 0, 0));
        bool roundTrip = true;
        for (int r = 0; r < 256; r += 15) for (int g = 0; g < 256; g += 17) for (int b = 0; b < 256; b += 51)
        {
            var hsv = ColorMath.ToHsv((byte)r, (byte)g, (byte)b);
            roundTrip &= ColorMath.FromHsv(hsv.H, hsv.S, hsv.V) == ((byte)r, (byte)g, (byte)b);
        }
        check("RGB to HSV and back is lossless", roundTrip);
        foreach (var preset in AppPresets.Palette)
        {
            var c = Convert.FromHexString(preset[1..]);
            var hsv = ColorMath.ToHsv(c[0], c[1], c[2]); var back = ColorMath.FromHsv(hsv.H, hsv.S, hsv.V);
            roundTrip &= ColorMath.ToHex(back.R, back.G, back.B) == preset;
        }
        check("Preset colors survive the picker round trip", roundTrip);
        check("Hex input is normalized for the picker",
            AppPresets.NormalizeColor("b9a8ff") == "#B9A8FF" && AppPresets.NormalizeColor(" #b9a8ff ") == "#B9A8FF"
            && AppPresets.NormalizeColor("b9a8f") == "" && AppPresets.NormalizeColor("zzzzzz") == "" && ColorMath.ToHex(1, 171, 255) == "#01ABFF");

        // Pictures of 16 by 16 BGRA pixels, as the notice icons are sampled.
        static byte[] Picture(params (int Count, byte R, byte G, byte B, byte A)[] parts)
        {
            var pixels = new List<byte>();
            foreach (var (count, r, g, b, a) in parts) for (int i = 0; i < count; i++) pixels.AddRange([b, g, r, a]);
            return [.. pixels];
        }
        check("A gray, white or black picture has no main color",
            ColorMath.Dominant(Picture((256, 128, 128, 128, 255))) is null && ColorMath.Dominant(Picture((128, 255, 255, 255, 255), (128, 0, 0, 0, 255))) is null);
        check("A transparent picture has no main color", ColorMath.Dominant(Picture((256, 255, 0, 0, 0))) is null && ColorMath.Dominant([]) is null);
        check("A few colored pixels on a gray picture are not its main color", ColorMath.Dominant(Picture((250, 230, 230, 230, 255), (6, 255, 0, 0, 255))) is null);
        // A white avatar with a blue figure over most of it, a red badge and a black letter: the blue, not a mix of blue and red.
        var avatar = ColorMath.Dominant(Picture((80, 255, 255, 255, 255), (120, 40, 110, 230, 255), (30, 230, 40, 40, 255), (26, 0, 0, 0, 255)));
        check("A picture's main color is its largest colored part, not a mix", avatar == (40, 110, 230));
        // A blue that falls on both sides of a hue bin's edge (hues 220.5 and 226.5, bins of 15 degrees) is one color: each half
        // alone weighs less than the red, together they weigh more.
        var split = ColorMath.Dominant(Picture((60, 30, 95, 230, 255), (60, 30, 75, 230, 255), (90, 230, 60, 40, 255), (46, 255, 255, 255, 255)));
        check("A color split across a hue bin's edge counts as one", split is { } blue && blue.B > blue.R && blue.B > blue.G);
        check("Pixels under half opacity are left out",
            ColorMath.Dominant(Picture((200, 230, 40, 40, 60), (56, 40, 110, 230, 255))) == (40, 110, 230));
    }
}
