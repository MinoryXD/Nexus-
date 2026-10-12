namespace NexusTyper.Core;

public static class TyperUiRules
{
    public static bool CanStart(TyperState state, bool hasValidInput) =>
        state is TyperState.Ready or TyperState.Completed or TyperState.Stopped or TyperState.Error && hasValidInput;

    public static bool CanStop(TyperState state) =>
        state is TyperState.Countdown or TyperState.Typing;
}
