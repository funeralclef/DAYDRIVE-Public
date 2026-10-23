using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Application;

/// <summary>One colour, in the only three numbers the platform layer needs from this file.</summary>
public readonly record struct PaletteColor(byte R, byte G, byte B)
{
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// The colours the event categories are told apart by, in every palette the shell offers.
///
/// Two things have to be true at once, and they pull against each other. No two categories may
/// arrive at colours a reader has to stop and compare — that was the state of the old
/// hand-picked lists, where "accent" gave two of five categories the same colour twelve per cent
/// apart. And each palette has to look like the word on the settings page: a palette called
/// "earth" that is only the vibrant one with the chroma turned down is not earth, it is vibrant
/// spoiled, and a reader who picked it did not get what they asked for.
///
/// So there is no single ladder here. Each palette says what it is in its own terms — where its
/// colours sit on the hue circle, how much colour they carry, and how their weight is spread —
/// and separation is bought differently in each. Vivid palettes buy it with hue, because they
/// have the whole circle. Muted ones buy it with weight, because that is all a muted palette
/// has. The one thing they share is the *order*: every palette runs through the categories in
/// the same sequence, so switching palettes changes the colours without reshuffling which
/// category is the warm one.
///
/// The arithmetic is OKLCH throughout. sRGB's own hue is not perceptually even — stepping forty
/// degrees around an HSV wheel spends half the circle in the greens — and chroma is fitted to
/// the gamut by search rather than by clamping the channels, because clamping moves the hue.
/// </summary>
public static class CategoryPalette
{
    /// <summary>
    /// The order every palette runs in, and where each category sits when a palette has the
    /// whole circle to spend.
    ///
    /// The lean is a second dimension for the palettes built on this ladder: it runs up, level,
    /// down and round again rather than simply alternating, because nine is odd and a two-step
    /// lean would leave one neighbouring pair at the same weight — with nine stops that pair is
    /// the pink and the coral either side of zero, the two most easily confused on the circle.
    /// Three levels divide nine exactly, so no neighbour anywhere shares a lean.
    /// </summary>
    private static readonly (EventCategory Category, double Hue, double Lean)[] Wheel =
    [
        (EventCategory.Social,    15, +1),  // coral
        (EventCategory.Errands,   55,  0),  // amber
        (EventCategory.Travel,    95, -1),  // lime
        (EventCategory.Health,   135, +1),  // green
        (EventCategory.Study,    175,  0),  // teal
        (EventCategory.Rest,     215, -1),  // azure
        (EventCategory.Work,     255, +1),  // blue
        (EventCategory.General,  295,  0),  // violet
        (EventCategory.Personal, 335, -1)   // pink
    ];

    /// <summary>
    /// Earth, in the order the wheel runs: brick, ochre, umber, moss, sage, slate, dusty blue,
    /// stone, clay.
    ///
    /// This is not the wheel warmed over. A natural palette is not an even ring — it clusters in
    /// the warm half, it skips the colours no material has, and it contains something with
    /// almost no colour in it at all, which is why the stone is here at a twentieth of the
    /// chroma of the brick beside it. The evenness that the vivid palettes get from hue, this
    /// one gets from weight: it is read as a ramp from a dark brick to a pale clay, which is
    /// also what a row of natural materials looks like laid out in daylight.
    /// </summary>
    private static readonly (double Hue, double Chroma)[] Ground =
    [
        (22, 0.100),   // brick
        (56, 0.092),   // ochre
        (88, 0.078),   // umber
        (118, 0.080),  // moss
        (162, 0.052),  // sage
        (206, 0.058),  // slate
        (238, 0.066),  // dusty blue
        (45, 0.028),   // stone
        (348, 0.075)   // clay
    ];

    /// <summary>
    /// The colour-blind palette, and it is not generated: these are the published values of the
    /// Okabe–Ito set, filled out to nine with two of Paul Tol's, in the wheel's order.
    ///
    /// Generating one would be the wrong kind of clever. These sets exist because somebody
    /// checked them against real dichromatic vision rather than against a distance formula, and
    /// a reader who chooses this palette is asking for that work, not for this file's opinion of
    /// it. What the code does to them is one thing only: on a dark shell their lightness is
    /// remapped upward — they were designed for ink on paper — while every hue and every chroma
    /// is left exactly as published.
    /// </summary>
    private static readonly string[] Safe =
    [
        "#D55E00",  // vermillion   (Okabe-Ito)
        "#E69F00",  // orange       (Okabe-Ito)
        "#F0E442",  // yellow       (Okabe-Ito)
        "#117733",  // green        (Tol)
        "#44AA99",  // teal         (Tol)
        "#56B4E9",  // sky blue     (Okabe-Ito)
        "#0072B2",  // blue         (Okabe-Ito)
        "#999999",  // grey         (Okabe-Ito)
        "#CC79A7"   // reddish purple (Okabe-Ito)
    ];

    /// <summary>
    /// The lightness at which each of the wheel's hues can hold the most colour. Computed once:
    /// it depends only on the hue and on sRGB, neither of which changes.
    /// </summary>
    private static readonly double[] Cusps = [.. Wheel.Select(entry => FindCusp(entry.Hue))];

    /// <summary>Every category, in the order the enum declares them.</summary>
    public static IReadOnlyList<EventCategory> Categories { get; } = Enum.GetValues<EventCategory>();

    public static int Count => Categories.Count;

    // ---------------------------------------------------------------- the vivid palettes ------

    /// <summary>
    /// The wheel at one lightness and chroma: the shape of "vibrant" and of "pastel", which
    /// differ only in how light and how coloured they ask to be. <paramref name="lean"/> is the
    /// alternating lightness step, <paramref name="hueShift"/> turns the whole circle.
    /// </summary>
    public static PaletteColor[] Read(double lightness, double chroma, double lean = 0.035, double hueShift = 0)
    {
        var colours = new PaletteColor[Count];
        foreach (var (category, hue, direction) in Wheel)
            colours[(int)category] = FromOklch(
                Math.Clamp(lightness + direction * lean, 0.06, 0.97), chroma, hue + hueShift);
        return colours;
    }

    /// <summary>
    /// Every hue at the lightness where it is most itself, asked for more colour than sRGB can
    /// give so that each one comes back at its own limit.
    ///
    /// This is what makes neon neon rather than vibrant-but-louder. Held at one lightness, half
    /// the circle cannot reach saturation — a magenta at the lightness of a bright yellow is
    /// pink, not magenta — so the old "neon" was pale wherever the gamut ran out. Letting each
    /// hue sit at its own cusp gives an electric lime, a hot magenta and a deep blue in the same
    /// palette, which is exactly the range of a row of neon tubes. The band keeps the darkest of
    /// them off the shell's own background.
    /// </summary>
    public static PaletteColor[] Neon(double floor, double ceiling, double lean)
    {
        var colours = new PaletteColor[Count];
        for (var rank = 0; rank < Wheel.Length; rank++)
        {
            var (category, hue, direction) = Wheel[rank];
            var lightness = Math.Clamp(Cusps[rank], floor, ceiling) + direction * lean;
            colours[(int)category] = FromOklch(Math.Clamp(lightness, 0.06, 0.97), 0.40, hue);
        }
        return colours;
    }

    // ---------------------------------------------------------------- the quiet palettes ------

    /// <summary>
    /// The earth palette: its own hues and its own chromas, laid on a ramp of weight from the
    /// darkest to the lightest. <paramref name="wobble"/> alternately lifts and drops the
    /// chroma, so a saturated brick sits next to a chalky ochre — which both looks like natural
    /// material and is where a low-chroma palette finds the last of its separation.
    /// </summary>
    public static PaletteColor[] Earth(double darkest, double lightest, double wobble)
    {
        var colours = new PaletteColor[Count];
        var step = (lightest - darkest) / (Wheel.Length - 1);
        for (var rank = 0; rank < Wheel.Length; rank++)
        {
            var (hue, chroma) = Ground[rank];
            colours[(int)Wheel[rank].Category] = FromOklch(
                darkest + step * rank, chroma * (rank % 2 == 0 ? 1 + wobble : 1 - wobble), hue);
        }
        return colours;
    }

    /// <summary>
    /// One colour family: a narrow fan of hues around the shell's accent, laid on a full scale
    /// of tints and shades, with the most colour in the middle of that scale and the least at
    /// either end — which is how a monochromatic scheme is built by hand.
    ///
    /// The fan is deliberately tight. Nine colours in one family are separated by weight, not by
    /// hue: widening the fan from fifty-six degrees to a hundred and sixty moves the closest
    /// pair by four thousandths, so the width buys nothing and costs the whole point of the
    /// palette. What it replaced spanned thirty-four degrees with no scale at all and put two
    /// categories on the same colour.
    /// </summary>
    public static PaletteColor[] Around(double anchorHue, double darkest, double lightest,
        double quietChroma, double loudChroma, double fanPerStep)
    {
        var colours = new PaletteColor[Count];
        var middle = (Wheel.Length - 1) / 2.0;
        var step = (lightest - darkest) / (Wheel.Length - 1);
        for (var rank = 0; rank < Wheel.Length; rank++)
        {
            var distance = Math.Abs(rank - middle) / middle;
            colours[(int)Wheel[rank].Category] = FromOklch(
                darkest + step * rank,
                loudChroma - (loudChroma - quietChroma) * distance,
                anchorHue + (rank - middle) * fanPerStep);
        }
        return colours;
    }

    /// <summary>
    /// Nine even steps of weight in a single hue: the monochrome palette, where the hue is the
    /// shell's own so the greys belong to the theme rather than being neutral by accident.
    /// </summary>
    public static PaletteColor[] ByWeight(double darkest, double lightest, double chroma, double hue)
    {
        var colours = new PaletteColor[Count];
        var step = (lightest - darkest) / (Wheel.Length - 1);
        for (var rank = 0; rank < Wheel.Length; rank++)
            colours[(int)Wheel[rank].Category] = FromOklch(darkest + step * rank, chroma, hue);
        return colours;
    }

    /// <summary>
    /// The published colour-blind-safe set, with its lightness remapped by
    /// <c>base + scale * lightness</c> and nothing else touched. Pass (0, 1) for the values
    /// exactly as they were designed.
    /// </summary>
    public static PaletteColor[] SafeSet(double lightnessBase, double lightnessScale)
    {
        var colours = new PaletteColor[Count];
        for (var rank = 0; rank < Wheel.Length; rank++)
        {
            var (lightness, chroma, hue) = ToOklch(Parse(Safe[rank]));
            colours[(int)Wheel[rank].Category] = FromOklch(
                Math.Clamp(lightnessBase + lightnessScale * lightness, 0.05, 0.97), chroma, hue);
        }
        return colours;
    }

    // ---------------------------------------------------------------- the colour space --------

    /// <summary>
    /// A colour from lightness, chroma and hue, guaranteed to be inside sRGB.
    ///
    /// The requested chroma is usually available; where it is not — a vivid yellow at a
    /// lightness sRGB cannot reach — the search walks it back to the gamut boundary along the
    /// same hue, which gives up saturation and keeps the colour that was asked for. Asking for
    /// more than the gamut can give is a legitimate way to say "as much as there is": that is
    /// how <see cref="Neon"/> is written.
    /// </summary>
    public static PaletteColor FromOklch(double lightness, double chroma, double hueDegrees)
    {
        var radians = hueDegrees * Math.PI / 180d;
        var a = Math.Cos(radians);
        var b = Math.Sin(radians);
        if (InGamut(lightness, a * chroma, b * chroma)) return Encode(lightness, a * chroma, b * chroma);

        var fitted = LargestChroma(lightness, a, b, chroma);
        return Encode(lightness, a * fitted, b * fitted);
    }

    /// <summary>
    /// How far apart two colours look, in OKLab units. Roughly: 0.02 is two shades of one
    /// colour, 0.08 is a colour most people would give a different name, 0.15 is unmistakable.
    /// </summary>
    public static double Distance(PaletteColor first, PaletteColor second)
    {
        var (l1, a1, b1) = ToOklab(first);
        var (l2, a2, b2) = ToOklab(second);
        return Math.Sqrt(Square(l1 - l2) + Square(a1 - a2) + Square(b1 - b2));
    }

    /// <summary>The lightness alone, which is the one channel every reader shares.</summary>
    public static double Lightness(PaletteColor colour) => ToOklab(colour).L;

    /// <summary>Lightness, chroma and hue in degrees.</summary>
    public static (double L, double C, double H) ToOklch(PaletteColor colour)
    {
        var (l, a, b) = ToOklab(colour);
        var hue = Math.Atan2(b, a) * 180d / Math.PI;
        return (l, Math.Sqrt(a * a + b * b), hue < 0 ? hue + 360 : hue);
    }

    public static (double L, double A, double B) ToOklab(PaletteColor colour)
    {
        var r = Decode(colour.R);
        var g = Decode(colour.G);
        var b = Decode(colour.B);
        var lCone = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var mCone = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var sCone = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * lCone + 0.7936177850 * mCone - 0.0040720468 * sCone,
                1.9779984951 * lCone - 2.4285922050 * mCone + 0.4505937099 * sCone,
                0.0259040371 * lCone + 0.7827717662 * mCone - 0.8086757660 * sCone);
    }

    public static PaletteColor Parse(string hex)
    {
        var text = hex.TrimStart('#');
        return new(Convert.ToByte(text[..2], 16), Convert.ToByte(text.Substring(2, 2), 16),
            Convert.ToByte(text.Substring(4, 2), 16));
    }

    /// <summary>The lightness at which a hue can hold the most colour, to a hundredth.</summary>
    private static double FindCusp(double hueDegrees)
    {
        var radians = hueDegrees * Math.PI / 180d;
        var a = Math.Cos(radians);
        var b = Math.Sin(radians);
        double best = 0, bestLightness = 0.5;
        for (var lightness = 0.10; lightness <= 0.98; lightness += 0.01)
        {
            var chroma = LargestChroma(lightness, a, b, 0.45);
            if (chroma <= best) continue;
            best = chroma;
            bestLightness = lightness;
        }
        return bestLightness;
    }

    private static double LargestChroma(double lightness, double a, double b, double ceiling)
    {
        double low = 0, high = ceiling;
        for (var step = 0; step < 20; step++)
        {
            var mid = (low + high) / 2;
            if (InGamut(lightness, a * mid, b * mid)) low = mid;
            else high = mid;
        }
        return low;
    }

    private static (double R, double G, double B) ToLinear(double l, double a, double b)
    {
        var lCone = l + 0.3963377774 * a + 0.2158037573 * b;
        var mCone = l - 0.1055613458 * a - 0.0638541728 * b;
        var sCone = l - 0.0894841775 * a - 1.2914855480 * b;
        var lc = lCone * lCone * lCone;
        var mc = mCone * mCone * mCone;
        var sc = sCone * sCone * sCone;
        return (4.0767416621 * lc - 3.3077115913 * mc + 0.2309699292 * sc,
                -1.2684380046 * lc + 2.6097574011 * mc - 0.3413193965 * sc,
                -0.0041960863 * lc - 0.7034186147 * mc + 1.7076147010 * sc);
    }

    private static bool InGamut(double l, double a, double b)
    {
        var (r, g, blue) = ToLinear(l, a, b);
        return r is >= -0.0005 and <= 1.0005 && g is >= -0.0005 and <= 1.0005 && blue is >= -0.0005 and <= 1.0005;
    }

    private static PaletteColor Encode(double l, double a, double b)
    {
        var (r, g, blue) = ToLinear(l, a, b);
        return new(Channel(r), Channel(g), Channel(blue));
    }

    private static byte Channel(double linear)
    {
        var clamped = Math.Clamp(linear, 0, 1);
        var encoded = clamped <= 0.0031308 ? 12.92 * clamped : 1.055 * Math.Pow(clamped, 1 / 2.4) - 0.055;
        return (byte)Math.Clamp(Math.Round(encoded * 255), 0, 255);
    }

    private static double Decode(byte channel)
    {
        var value = channel / 255d;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static double Square(double value) => value * value;
}
