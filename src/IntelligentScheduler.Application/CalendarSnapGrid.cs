namespace IntelligentScheduler.Application;

/// <summary>
/// The grid a drag across the week timeline lands on.
///
/// Five minutes is what a plan is made of. Dragging over an hour drawn fifty-two pixels tall
/// otherwise lands on a minute nobody chose, and 10:07–11:23 is not a meeting anyone meant to
/// book. Holding Shift hands the minutes back: the free step starts at one, and the wheel walks
/// it up the ladder without leaving the drag, so a board that wants quarter hours can have them
/// and a board that wants exact minutes can have those.
/// </summary>
public static class CalendarSnapGrid
{
    /// <summary>What a drag rounds to when nothing is held down.</summary>
    public const int CoarseStepMinutes = 5;

    /// <summary>The steps the wheel walks through while Shift is down, in minutes.</summary>
    public static IReadOnlyList<int> FreeSteps { get; } = [1, 2, 3, 5, 10, 15, 20, 30, 60];

    /// <summary>The step one notch up (positive) or down (negative) the ladder, stopping at its ends.</summary>
    public static int StepAfter(int current, int direction)
    {
        var index = FreeSteps.ToList().IndexOf(current);
        if (index < 0) index = 0;
        return FreeSteps[Math.Clamp(index + direction, 0, FreeSteps.Count - 1)];
    }

    /// <summary>
    /// Rounds a vertical offset to the nearest step and keeps it on the board.
    /// </summary>
    /// <param name="offset">Distance from the top of the timeline, in pixels.</param>
    /// <param name="hourHeight">How tall one hour is drawn.</param>
    /// <param name="stepMinutes">The grid to round to.</param>
    /// <param name="maximum">The bottom of the timeline.</param>
    public static double Snap(double offset, double hourHeight, int stepMinutes, double maximum)
    {
        var step = hourHeight / 60d * Math.Max(1, stepMinutes);
        return Math.Clamp(Math.Round(offset / step) * step, 0, maximum);
    }
}
