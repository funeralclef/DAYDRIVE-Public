using System.Runtime.InteropServices;
using Avalonia;
using IntelligentScheduler.Desktop.Platform;
using Xunit;

namespace IntelligentScheduler.Desktop.Tests;

public sealed class LinuxScrollTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, 1)]
    [InlineData(1, 0, 0, 0, -1)]
    [InlineData(2, 0, 0, 1, 0)]
    [InlineData(3, 0, 0, -1, 0)]
    [InlineData(4, 0, 1, 0, -1)]
    [InlineData(4, 0, -0.25, 0, 0.25)]
    [InlineData(4, 0.5, 0, -0.5, 0)]
    [InlineData(4, 0, 0, 0, 0)] // The stop event must not become an upward tick.
    public void ActualGdkEventsPreserveWheelDirectionAndSmoothStop(int direction, double x, double y, double expectedX, double expectedY)
    {
        if (!OperatingSystem.IsLinux()) return;
        var ev = gdk_event_new(31);
        try
        {
            Marshal.StructureToPtr(new ScrollEvent { Type = 31, Direction = direction, DeltaX = x, DeltaY = y,
                Stop = direction == 4 && x == 0 && y == 0 ? 1u : 0u }, ev, false);
            Assert.Equal(new Vector(expectedX, expectedY), LinuxLayerInput.ScrollDelta(ev));
        }
        finally { gdk_event_free(ev); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollEvent
    {
        public int Type;
        public nint Window;
        public sbyte Sent;
        public uint Time;
        public double X, Y;
        public uint State;
        public int Direction;
        public nint Device;
        public double RootX, RootY, DeltaX, DeltaY;
        public uint Stop;
    }

    [DllImport("libgdk-3.so.0")] private static extern nint gdk_event_new(int type);
    [DllImport("libgdk-3.so.0")] private static extern void gdk_event_free(nint ev);
}
