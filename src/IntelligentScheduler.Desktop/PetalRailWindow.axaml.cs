using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using IntelligentScheduler.Desktop.Controls;

namespace IntelligentScheduler.Desktop;

/// <summary>
/// The petal's controls, beside the petal.
///
/// They were briefly inside it, and that was the wrong place for exactly one reason: the petal is
/// a sliver, and a sliver with three keys in it is not a sliver. The quick widget solved the same
/// problem years earlier by putting its rail alongside its card rather than in it, and this is
/// that rail — the same seats, the same keys, the same styles — standing next to whichever of
/// the two surfaces the pointer is currently on.
///
/// Like the petal, it is placed once and then left alone. Its size is fixed at the widest
/// arrangement the rail can take, so a session that has just been started — which swaps one key
/// for two — changes what is drawn inside the window and never the window itself. A key does not
/// move out from under the hand that is reaching for the next one.
/// </summary>
public sealed partial class PetalRailWindow : Window
{
    /// <summary>
    /// Slack around the seats. It is the room their shadows fall into, and it is also what keeps
    /// the short journey between the petal and the keys inside something that counts as the rail
    /// — a gap that is only screen is a gap that ends the hover.
    /// </summary>
    public const double CardInset = 13;

    /// <summary>
    /// How far the keys stand off the petal. Close enough to read as belonging to it, far enough
    /// that the capsule keeps an edge of its own.
    /// </summary>
    public const double Gap = 9;

    private readonly WindowRevealAnimator _reveal;

    public PetalRailWindow()
    {
        InitializeComponent();
        _reveal = new WindowRevealAnimator(this, RevealOrigin.Panel);
        // Watched rather than handled. PointerExited is raised while IsPointerOver is still
        // true, so anything that asks "is the pointer still here?" from inside that handler is
        // told yes — and a rail that asks exactly that before deciding to leave never leaves.
        // The property changing is the one signal that is settled by the time it arrives.
        RailCard.PropertyChanged += (_, change) =>
        {
            if (change.Property != IsPointerOverProperty) return;
            if (RailCard.IsPointerOver) PointerReturned?.Invoke(this, EventArgs.Empty);
            else PointerDeparted?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>
    /// Whether the pointer is on the keys. Asked of the card rather than of the window: the
    /// window is wider than the keys by the room their shadows fall into, and that room is part
    /// of the rail as far as a hand crossing towards it is concerned.
    /// </summary>
    public bool IsHovered => RailCard.IsPointerOver;

    /// <summary>The pointer is on the keys, so nothing is to start leaving.</summary>
    public event EventHandler? PointerReturned;

    /// <summary>The pointer left them; the grace period starts.</summary>
    public event EventHandler? PointerDeparted;

    /// <summary>True while it is on its way out and a returning pointer should bring it back.</summary>
    public bool IsDismissing => _reveal.IsDismissing;

    /// <summary>
    /// Where the rail belongs, worked out from the same working area the petal was placed in and
    /// from how wide the petal's own capsule is standing. Called by the shell's placement, which
    /// is the only thing that sizes or moves this window.
    /// </summary>
    public void PlaceBeside(PixelRect area, double scale, double petalWidth)
    {
        var width = (int)Math.Ceiling((ClientSize.Width > 1 ? ClientSize.Width : Width) * scale);
        var height = (int)Math.Ceiling((ClientSize.Height > 1 ? ClientSize.Height : Height) * scale);
        // The seats end Gap short of the capsule; the window reaches CardInset further out than
        // its seats do, and that inset is not part of what the eye lines up against the petal.
        var right = area.Right - (int)Math.Round((petalWidth + Gap - CardInset) * scale);
        var target = new PixelPoint(
            Math.Max(area.X, right - width),
            area.Y + Math.Max(0, (area.Height - height) / 2));
        if (Position != target) Position = target;
    }

    public void Reveal(Action show) => _reveal.Reveal(show);

    /// <summary>Folds away and hides itself once the keys have gone.</summary>
    public void Dismiss() => _reveal.Dismiss(Hide);
}
