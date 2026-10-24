using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Application;

public static class AssistantPermissionPolicy
{
    public static bool MayApplyAutomatically(AssistantAccessLevel level, AssistantScheduleAction action)
        => level switch
        {
            AssistantAccessLevel.ConfirmAll => false,
            AssistantAccessLevel.AutoSafe => !action.IsCritical,
            AssistantAccessLevel.FullAccess => true,
            _ => false
        };
}
