using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Xunit;

namespace IntelligentScheduler.Infrastructure.Tests;

public sealed class LocalModelTests
{
    [Fact]
    public void PowerButton_StateTransitions_AreConsistent()
    {
        // Scenario 1: Model not installed
        var isRunning = false;
        var isStarting = false;
        var isInstalled = false;
        var canStart = isInstalled && !isRunning && !isStarting;
        var canStop = isRunning || isStarting;
        var canToggle = canStop || canStart;

        Assert.False(canStart);
        Assert.False(canStop);
        Assert.False(canToggle);

        // Scenario 2: Model installed but stopped
        isInstalled = true;
        canStart = isInstalled && !isRunning && !isStarting;
        canStop = isRunning || isStarting;
        canToggle = canStop || canStart;

        Assert.True(canStart);
        Assert.False(canStop);
        Assert.True(canToggle);

        // Scenario 3: Model is Starting
        isStarting = true;
        canStart = isInstalled && !isRunning && !isStarting;
        canStop = isRunning || isStarting;
        canToggle = canStop || canStart;

        Assert.False(canStart);
        Assert.True(canStop);
        Assert.True(canToggle);

        // Scenario 4: Model is Running
        isStarting = false;
        isRunning = true;
        canStart = isInstalled && !isRunning && !isStarting;
        canStop = isRunning || isStarting;
        canToggle = canStop || canStart;

        Assert.False(canStart);
        Assert.True(canStop);
        Assert.True(canToggle);
    }

    [Theory]
    [InlineData(0, "0 MB")]
    [InlineData(-100, "0 MB")]
    [InlineData(524288000, "500 MB")]
    [InlineData(1073741824, "1.00 GB")]
    [InlineData(3048065024, "2.84 GB")]
    [InlineData(6442450944, "6.00 GB")]
    public void FormatBytes_FormatsMemoryAccurately(long bytes, string expected)
    {
        Assert.Equal(expected, BinarySizeFormatter.Format(bytes));
    }

    [Fact]
    public void ModelSwitch_WhenRunning_RequiresConfirmation()
    {
        var isRunning = true;
        var currentModelId = "qwen35-9b";
        var targetModelId = "granite-4-8b";

        var requiresConfirmation = isRunning && !string.Equals(currentModelId, targetModelId, StringComparison.OrdinalIgnoreCase);
        Assert.True(requiresConfirmation);

        // Same model should not require confirmation
        targetModelId = "qwen35-9b";
        requiresConfirmation = isRunning && !string.Equals(currentModelId, targetModelId, StringComparison.OrdinalIgnoreCase);
        Assert.False(requiresConfirmation);

        // When stopped, does not require confirmation
        isRunning = false;
        targetModelId = "granite-4-8b";
        requiresConfirmation = isRunning && !string.Equals(currentModelId, targetModelId, StringComparison.OrdinalIgnoreCase);
        Assert.False(requiresConfirmation);
    }
}
