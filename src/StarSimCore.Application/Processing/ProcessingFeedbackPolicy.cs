namespace StarSimCore.Application.Processing;

public static class ProcessingFeedbackPolicy
{
    public static readonly TimeSpan VisibilityDelay = TimeSpan.FromMilliseconds(200);

    public static bool ShouldBeVisible(TimeSpan activeDuration, bool isActive) =>
        isActive && activeDuration >= VisibilityDelay;
}
