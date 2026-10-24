using Xunit;

namespace IntelligentScheduler.Application.Tests;

public sealed class EventTimeRangeTests
{
    [Fact]
    public void ResolveLocal_KeepsOrdinaryEventOnSelectedDay()
    {
        var date = new DateOnly(2026, 8, 17);

        var range = EventTimeRange.ResolveLocal(date, TimeSpan.FromHours(9), TimeSpan.FromHours(10.5));

        Assert.Equal(new DateTime(2026, 8, 17, 9, 0, 0), range.StartLocal);
        Assert.Equal(new DateTime(2026, 8, 17, 10, 30, 0), range.EndLocal);
    }

    [Fact]
    public void ResolveLocal_MovesOvernightEndToFollowingDay()
    {
        var date = new DateOnly(2026, 8, 17);

        var range = EventTimeRange.ResolveLocal(date, TimeSpan.FromHours(23), TimeSpan.FromMinutes(30));

        Assert.Equal(new DateTime(2026, 8, 17, 23, 0, 0), range.StartLocal);
        Assert.Equal(new DateTime(2026, 8, 18, 0, 30, 0), range.EndLocal);
    }

    [Fact]
    public void ResolveLocal_CrossDaySpanFromEveningToMorning()
    {
        var date = new DateOnly(2026, 8, 19);

        var range = EventTimeRange.ResolveLocal(date, TimeSpan.FromHours(22), TimeSpan.FromHours(3));

        Assert.Equal(new DateTime(2026, 8, 19, 22, 0, 0), range.StartLocal);
        Assert.Equal(new DateTime(2026, 8, 20, 3, 0, 0), range.EndLocal);
        Assert.Equal(TimeSpan.FromHours(5), range.EndLocal - range.StartLocal);
    }
}
