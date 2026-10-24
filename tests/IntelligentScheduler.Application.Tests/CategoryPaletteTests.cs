using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Xunit;

namespace IntelligentScheduler.Application.Tests;

/// <summary>
/// Every palette the shell ships, measured in OKLab, where distance means roughly what it looks
/// like. Two things are being held at once, and they pull against each other: no two categories
/// may arrive at colours a reader has to stop and compare, and each palette has to still look
/// like the word on the settings page.
///
/// That is why the floor is not one number. A palette free to use the whole hue circle at real
/// chroma has no excuse for a close pair. A palette whose *name* is a constraint — one hue, one
/// family, muted earth — has less room by definition, and holding it to the same figure would
/// only mean quietly making it something else. Each mode is asked for what its own name can
/// honour, and every one of them is far clear of what it replaced.
///
/// For scale, the palettes this replaced measured, on five categories rather than nine: 0.104
/// for the default set, 0.076 for pastel, 0.049 for earth, and between 0.015 and 0.040 for the
/// accent palette, whose whole spread was thirty-four degrees of hue and one twelve-per-cent mix
/// with white.
///
/// The numbers below are the ones <c>ShellThemeService.BuildVisualPalette</c> passes, repeated
/// rather than shared: this project cannot see the desktop assembly, and a test that read the
/// same constant it is checking would pass whatever that constant became. What it asserts is
/// that these settings hold up, so a change on either side has to be made in both places.
/// </summary>
public sealed class CategoryPaletteTests
{
    /// <summary>Free to use the whole circle at real chroma. No excuse for a close pair.</summary>
    private const double Open = 0.080;

    /// <summary>Muted by definition: at a tenth of the chroma, weight has to carry it.</summary>
    private const double Muted = 0.070;

    /// <summary>One hue, or one family. The name is the constraint.</summary>
    private const double SingleHue = 0.060;

    public static TheoryData<string, double, PaletteColor[]> Palettes()
    {
        var data = new TheoryData<string, double, PaletteColor[]>();
        foreach (var (name, floor, colours) in Readings()) data.Add(name, floor, colours);
        return data;
    }

    private static IEnumerable<(string Name, double Floor, PaletteColor[] Colours)> Readings()
    {
        yield return ("vibrant/dark", Open, CategoryPalette.Read(0.780, 0.150, 0.050));
        yield return ("vibrant/light", Open, CategoryPalette.Read(0.555, 0.145, 0.050));
        yield return ("pastel/dark", Open, CategoryPalette.Read(0.830, 0.105, 0.065));
        yield return ("pastel/light", Open, CategoryPalette.Read(0.690, 0.105, 0.062));
        yield return ("neon/dark", Open, CategoryPalette.Neon(0.62, 0.86, 0.030));
        yield return ("neon/light", Open, CategoryPalette.Neon(0.42, 0.68, 0.030));
        yield return ("colourblind/dark", Open, CategoryPalette.SafeSet(0.30, 0.66));
        yield return ("colourblind/light", Open, CategoryPalette.SafeSet(0.00, 1.00));
        yield return ("earth/dark", Muted, CategoryPalette.Earth(0.45, 0.91, 0.30));
        yield return ("earth/light", Muted, CategoryPalette.Earth(0.32, 0.80, 0.28));
        yield return ("monochrome/dark", SingleHue, CategoryPalette.ByWeight(0.42, 0.96, 0.014, 265));
        yield return ("monochrome/light", SingleHue, CategoryPalette.ByWeight(0.28, 0.82, 0.014, 265));
        // The accent palette follows whatever colour the user picked, so it is checked all the
        // way round the circle rather than at one convenient anchor.
        for (var anchor = 0; anchor < 360; anchor += 15)
        {
            yield return ($"accent {anchor}/dark", SingleHue, CategoryPalette.Around(anchor, 0.46, 0.95, 0.060, 0.190, 14));
            yield return ($"accent {anchor}/light", SingleHue, CategoryPalette.Around(anchor, 0.30, 0.80, 0.060, 0.200, 14));
        }
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void EveryCategoryIsTellableFromEveryOther(string name, double floor, PaletteColor[] colours)
    {
        Assert.Equal(Enum.GetValues<EventCategory>().Length, colours.Length);

        for (var first = 0; first < colours.Length; first++)
        for (var second = first + 1; second < colours.Length; second++)
        {
            var distance = CategoryPalette.Distance(colours[first], colours[second]);
            Assert.True(distance >= floor,
                $"{name}: {(EventCategory)first} and {(EventCategory)second} are {distance:0.000} apart " +
                $"({colours[first].ToHex()} vs {colours[second].ToHex()}); {floor:0.000} is the floor.");
        }
    }

    /// <summary>
    /// Pastel has to be pale and quiet, or it is only the default palette under another name.
    /// This is the assertion that stops the separation floor from being met by making every mode
    /// louder until it passes.
    /// </summary>
    [Theory]
    [InlineData(0.830, 0.105, 0.065, 0.76, 0.92)]
    [InlineData(0.690, 0.105, 0.062, 0.62, 0.78)]
    public void PastelStaysPale(double lightness, double chroma, double lean, double floor, double ceiling)
    {
        foreach (var colour in CategoryPalette.Read(lightness, chroma, lean))
        {
            var (l, c, _) = CategoryPalette.ToOklch(colour);
            Assert.InRange(l, floor, ceiling);
            Assert.True(c <= 0.115, $"{colour.ToHex()} carries {c:0.000} of colour; a pastel stops well short of that.");
        }
    }

    /// <summary>
    /// Earth has to be muted, and it has to be a range of weight rather than a ring of hues.
    /// Both are what the word means; either one alone would let it drift back into the default.
    /// </summary>
    [Theory]
    [InlineData(0.45, 0.91, 0.30)]
    [InlineData(0.32, 0.80, 0.28)]
    public void EarthStaysMutedAndRangesInWeight(double darkest, double lightest, double wobble)
    {
        var colours = CategoryPalette.Earth(darkest, lightest, wobble);
        var chromas = colours.Select(colour => CategoryPalette.ToOklch(colour).C).ToList();
        var levels = colours.Select(CategoryPalette.Lightness).ToList();

        Assert.True(chromas.Average() < 0.085,
            $"average chroma {chromas.Average():0.000} — earth is a muted palette.");
        Assert.True(levels.Max() - levels.Min() > 0.40,
            $"lightness spans {levels.Max() - levels.Min():0.000} — earth separates by weight, so it needs the range.");
        // The stone: every natural palette has something in it with almost no colour at all.
        Assert.True(chromas.Min() < 0.045, $"the quietest stop carries {chromas.Min():0.000}.");
    }

    /// <summary>
    /// Neon has to be at the edge of what the screen can do. Held at one lightness it could not
    /// be — half the circle cannot reach saturation there — which is why each hue is placed at
    /// its own cusp, and why this checks the chroma rather than the parameters.
    /// </summary>
    [Theory]
    [InlineData(0.62, 0.86)]
    [InlineData(0.42, 0.68)]
    public void NeonRunsAtTheEdgeOfTheGamut(double floor, double ceiling)
    {
        var chromas = CategoryPalette.Neon(floor, ceiling, 0.030)
            .Select(colour => CategoryPalette.ToOklch(colour).C).ToList();

        Assert.True(chromas.Average() > 0.145,
            $"average chroma {chromas.Average():0.000} — neon should be louder than the default palette.");
        Assert.True(chromas.Max() > 0.240, $"the loudest stop only reaches {chromas.Max():0.000}.");
    }

    /// <summary>
    /// The colour-blind palette is the published set and must stay it: hue and chroma pass
    /// through untouched, and only the lightness is remapped for a dark ground. A generated
    /// lookalike would be the one change here that quietly undoes the reason for the mode.
    /// </summary>
    [Fact]
    public void TheColourBlindSetIsThePublishedOne()
    {
        var published = CategoryPalette.SafeSet(0.00, 1.00);
        Assert.Contains(published, colour => colour.ToHex() == "#E69F00");  // Okabe-Ito orange
        Assert.Contains(published, colour => colour.ToHex() == "#0072B2");  // Okabe-Ito blue
        Assert.Contains(published, colour => colour.ToHex() == "#CC79A7");  // Okabe-Ito reddish purple

        var lifted = CategoryPalette.SafeSet(0.30, 0.66);
        for (var index = 0; index < published.Length; index++)
        {
            var (_, chroma, hue) = CategoryPalette.ToOklch(published[index]);
            var (_, liftedChroma, liftedHue) = CategoryPalette.ToOklch(lifted[index]);
            if (chroma < 0.01) continue;   // the grey has no hue to preserve
            Assert.InRange(liftedHue, hue - 2.5, hue + 2.5);
            Assert.InRange(liftedChroma, chroma - 0.02, chroma + 0.02);
        }
    }

    /// <summary>
    /// Monochrome has to work without hue, because it has none. Take the colour away and the
    /// nine are still nine.
    /// </summary>
    [Theory]
    [InlineData(0.42, 0.96)]
    [InlineData(0.28, 0.82)]
    public void SingleHueLaddersSeparateOnLightnessAlone(double darkest, double lightest)
    {
        var levels = CategoryPalette.ByWeight(darkest, lightest, 0.014, 265)
            .Select(CategoryPalette.Lightness).OrderBy(value => value).ToList();

        for (var index = 1; index < levels.Count; index++)
            Assert.True(levels[index] - levels[index - 1] >= 0.045,
                $"steps {index - 1} and {index} are {levels[index] - levels[index - 1]:0.000} apart in lightness.");
    }

    /// <summary>
    /// The accent palette has to stay one family. Widening the fan is the easy way to pass the
    /// separation check and the one way to stop meaning what the mode is called, so the width
    /// is asserted rather than left to whoever tunes it next.
    /// </summary>
    [Fact]
    public void TheAccentPaletteStaysOneFamily()
    {
        var hues = CategoryPalette.Around(120, 0.46, 0.95, 0.060, 0.190, 14)
            .Select(colour => CategoryPalette.ToOklch(colour).H).ToList();

        Assert.True(hues.Max() - hues.Min() <= 120,
            $"the fan spans {hues.Max() - hues.Min():0} degrees — past this it is not one colour any more.");
    }

    /// <summary>
    /// A colour that fell outside sRGB and was clamped would come back at the wrong lightness —
    /// which is the failure the chroma search exists to prevent, and the reason the spacing can
    /// be trusted at all.
    /// </summary>
    [Fact]
    public void ReadingTheWheelKeepsTheLightnessItWasAskedFor()
    {
        foreach (var colour in CategoryPalette.Read(0.780, 0.150, 0.050))
            Assert.InRange(CategoryPalette.Lightness(colour), 0.780 - 0.060, 0.780 + 0.060);
    }

    [Fact]
    public void EveryCategoryHasAPlaceInEveryPalette()
    {
        foreach (var (name, _, colours) in Readings())
        foreach (var category in Enum.GetValues<EventCategory>())
            Assert.True(colours[(int)category] != default, $"{name} has nothing for {category}.");
    }
}
