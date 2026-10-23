using IntelligentScheduler.Domain;
using Xunit;

namespace IntelligentScheduler.Application.Tests;

public sealed class AssistantPermissionPolicyTests
{
    private static readonly AssistantScheduleAction Safe = new(AssistantActionKind.CreateEvent);
    private static readonly AssistantScheduleAction Critical = new(AssistantActionKind.DeleteEvent);

    [Fact]
    public void ConfirmAll_AlwaysRequiresReview()
    {
        Assert.False(AssistantPermissionPolicy.MayApplyAutomatically(AssistantAccessLevel.ConfirmAll, Safe));
        Assert.False(AssistantPermissionPolicy.MayApplyAutomatically(AssistantAccessLevel.ConfirmAll, Critical));
    }

    [Fact]
    public void AutoSafe_AppliesSafeButNotDestructiveActions()
    {
        Assert.True(AssistantPermissionPolicy.MayApplyAutomatically(AssistantAccessLevel.AutoSafe, Safe));
        Assert.False(AssistantPermissionPolicy.MayApplyAutomatically(AssistantAccessLevel.AutoSafe, Critical));
    }

    [Fact]
    public void AutoSafe_RequiresConfirmationForWholeDayChanges()
    {
        Assert.False(AssistantPermissionPolicy.MayApplyAutomatically(
            AssistantAccessLevel.AutoSafe, new AssistantScheduleAction(AssistantActionKind.ShiftDay)));
        Assert.False(AssistantPermissionPolicy.MayApplyAutomatically(
            AssistantAccessLevel.AutoSafe, new AssistantScheduleAction(AssistantActionKind.CopyDay)));
    }

    [Fact]
    public void FullAccess_AppliesEveryAction()
    {
        Assert.True(AssistantPermissionPolicy.MayApplyAutomatically(AssistantAccessLevel.FullAccess, Safe));
        Assert.True(AssistantPermissionPolicy.MayApplyAutomatically(AssistantAccessLevel.FullAccess, Critical));
    }
}
