using System.Globalization;
using Avalonia.Data.Converters;

namespace IntelligentScheduler.Desktop.Controls;

/// <summary>
/// Keeps the scrollbar thumb compact while still making its length reflect how much content
/// remains outside the viewport. The returned value is an equivalent viewport size understood
/// by Avalonia's Track; the real ScrollViewer viewport and page size are left untouched.
/// </summary>
public sealed class CompactScrollViewportConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not double maximum || values[1] is not double viewport ||
            !double.IsFinite(maximum) || !double.IsFinite(viewport) || viewport <= 0)
        {
            return double.NaN;
        }

        maximum = Math.Max(0, maximum);
        if (maximum <= 0) return viewport;

        var visibleRatio = viewport / (maximum + viewport);
        // 6.5% for very long documents, gradually growing to at most 25% for small overflows.
        // It remains informative without turning into an almost full-height system thumb.
        var thumbRatio = Math.Clamp(0.065 + 0.18 * visibleRatio, 0.065, 0.25);
        return thumbRatio * maximum / (1 - thumbRatio);
    }
}
