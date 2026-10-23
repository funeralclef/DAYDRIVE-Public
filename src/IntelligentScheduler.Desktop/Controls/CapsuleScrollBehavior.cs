using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace IntelligentScheduler.Desktop.Controls;

/// <summary>
/// Keeps a hover scrollbar visible while it is being dragged and hides it only after a short
/// grace period. Visuals remain in XAML; this behavior owns interaction state only.
/// </summary>
public sealed class CapsuleScrollBehavior : AvaloniaObject
{
    private static readonly ConditionalWeakTable<ScrollViewer, InteractionState> States = new();

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<CapsuleScrollBehavior, ScrollViewer, bool>("IsEnabled");

    static CapsuleScrollBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>((viewer, change) =>
        {
            if (change.GetNewValue<bool>()) Enable(viewer);
            else Disable(viewer);
        });
    }

    public static bool GetIsEnabled(ScrollViewer viewer) => viewer.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(ScrollViewer viewer, bool value) => viewer.SetValue(IsEnabledProperty, value);

    private static void Enable(ScrollViewer viewer)
    {
        if (States.TryGetValue(viewer, out _)) return;
        var state = new InteractionState(viewer);
        States.Add(viewer, state);
        state.Attach();
    }

    private static void Disable(ScrollViewer viewer)
    {
        if (!States.TryGetValue(viewer, out var state)) return;
        state.Detach();
        States.Remove(viewer);
    }

    private sealed class InteractionState(ScrollViewer viewer)
    {
        private const double HoverZoneWidth = 18;
        private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(720) };
        private bool _isDragging;
        private bool _isPointerNearBar;

        public void Attach()
        {
            _hideTimer.Tick += OnHideTimer;
            viewer.PointerEntered += OnPointerEntered;
            viewer.PointerMoved += OnPointerMoved;
            viewer.PointerExited += OnPointerExited;
            viewer.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged,
                RoutingStrategies.Tunnel, handledEventsToo: true);
            viewer.AddHandler(Thumb.DragStartedEvent, OnDragStarted,
                RoutingStrategies.Bubble, handledEventsToo: true);
            viewer.AddHandler(Thumb.DragCompletedEvent, OnDragCompleted,
                RoutingStrategies.Bubble, handledEventsToo: true);
        }

        public void Detach()
        {
            _hideTimer.Stop();
            _hideTimer.Tick -= OnHideTimer;
            viewer.PointerEntered -= OnPointerEntered;
            viewer.PointerMoved -= OnPointerMoved;
            viewer.PointerExited -= OnPointerExited;
            viewer.RemoveHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged);
            viewer.RemoveHandler(Thumb.DragStartedEvent, OnDragStarted);
            viewer.RemoveHandler(Thumb.DragCompletedEvent, OnDragCompleted);
            SetClass("scrollVisible", false);
            SetClass("scrollInteracting", false);
        }

        private void OnPointerEntered(object? sender, PointerEventArgs e)
        {
            UpdatePointerZone(e);
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            UpdatePointerZone(e);
        }

        private void OnPointerExited(object? sender, PointerEventArgs e)
        {
            _isPointerNearBar = false;
            if (!_isDragging) ScheduleHide();
        }

        private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            Show();
            if (!_isDragging) ScheduleHide();
        }

        private void OnDragStarted(object? sender, VectorEventArgs e)
        {
            _hideTimer.Stop();
            _isDragging = true;
            Show();
            SetClass("scrollInteracting", true);
        }

        private void OnDragCompleted(object? sender, VectorEventArgs e)
        {
            _isDragging = false;
            SetClass("scrollInteracting", false);
            Show();
            if (!_isPointerNearBar) ScheduleHide();
        }

        private void UpdatePointerZone(PointerEventArgs e)
        {
            var point = e.GetPosition(viewer);
            var nearBar = point.X >= Math.Max(0, viewer.Bounds.Width - HoverZoneWidth);
            if (nearBar == _isPointerNearBar) return;
            _isPointerNearBar = nearBar;
            if (nearBar)
            {
                _hideTimer.Stop();
                Show();
            }
            else if (!_isDragging)
            {
                ScheduleHide();
            }
        }

        private void Show() => SetClass("scrollVisible", true);

        private void ScheduleHide()
        {
            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void OnHideTimer(object? sender, EventArgs e)
        {
            _hideTimer.Stop();
            if (_isDragging || _isPointerNearBar) return;
            SetClass("scrollVisible", false);
            SetClass("scrollInteracting", false);
        }

        private void SetClass(string name, bool value)
        {
            if (value)
            {
                if (!viewer.Classes.Contains(name)) viewer.Classes.Add(name);
            }
            else
            {
                viewer.Classes.Remove(name);
            }
        }
    }
}
