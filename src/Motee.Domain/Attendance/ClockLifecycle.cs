namespace Motee.Domain.Attendance;

public enum ClockAction
{
    ClockIn,
    StartBreak,
    EndBreak,
    ClockOut,
}

// What the clock will accept next.
//
// Written down rather than left to a chain of ifs in the service, because every one of
// these is a refusal somebody will hit: the double clock-in from an impatient second
// tap, the break started after going home, the clock-out from a day never begun. Each
// needs a specific answer, and a service that grows them one at a time ends up with a
// different one in each branch.
//
// A day is terminal once clocked out. Re-opening it is a correction, which is HR's
// act and goes through its own path with its own audit — not something the person can
// do by tapping the button again.
public static class ClockLifecycle
{
    private static readonly Dictionary<ClockState, ClockAction[]> Allowed = new()
    {
        [ClockState.NotStarted] = [ClockAction.ClockIn],
        [ClockState.ClockedIn] = [ClockAction.StartBreak, ClockAction.ClockOut],
        [ClockState.OnBreak] = [ClockAction.EndBreak, ClockAction.ClockOut],
        [ClockState.ClockedOut] = [],
    };

    public static bool Can(ClockState from, ClockAction action) =>
        Allowed[from].Contains(action);

    // Why not, in words the person can act on rather than a state name. The button
    // they pressed is usually still on screen, so "you are already clocked in" beats
    // "invalid transition" by the whole distance.
    public static string Refusal(ClockState from, ClockAction action) => (from, action) switch
    {
        (ClockState.ClockedIn, ClockAction.ClockIn) => "You are already clocked in.",
        (ClockState.OnBreak, ClockAction.ClockIn) => "You are on a break. End it to carry on.",
        (ClockState.ClockedOut, ClockAction.ClockIn) =>
            "You have already finished for today. Ask HR to correct the day if that is wrong.",
        (ClockState.NotStarted, ClockAction.StartBreak) =>
            "You have not clocked in yet, so there is nothing to break from.",
        (ClockState.OnBreak, ClockAction.StartBreak) => "You are already on a break.",
        (ClockState.NotStarted, ClockAction.EndBreak) => "You have not clocked in yet.",
        (ClockState.ClockedIn, ClockAction.EndBreak) => "You are not on a break.",
        (ClockState.NotStarted, ClockAction.ClockOut) =>
            "You have not clocked in today, so there is nothing to clock out of.",
        (ClockState.ClockedOut, _) => "You have already finished for today.",
        _ => "That is not a move this day can make.",
    };
}
