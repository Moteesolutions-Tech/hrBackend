namespace Motee.Domain.Authorization;

// Who is asking, and how much of a register they may see.
//
// A separate argument rather than a field on the query, deliberately. Every one of
// these list queries is bound from the request with [FromQuery], so a scope living on
// the query record is a scope the caller can put in the query string and widen for
// themselves. Keeping it apart means the only thing that can set it is the endpoint,
// from what the permission handler resolved.
//
// Required rather than optional for the same reason the whole class of bug existed: an
// omitted scope used to mean "no narrowing", which reads as working code right up until
// somebody reads a register that was never theirs. A caller now has to say.
public sealed record Viewer
{
    public required DataScope Scope { get; init; }

    // Needed for the scopes that are relative to the holder — Self and DirectReports
    // mean nothing without knowing whose.
    public Guid? EmployeeId { get; init; }

    // For callers with nobody behind them: a background job, a scheduled recalculation,
    // one service reading another's register to do its own already-scoped work. Named so
    // that it is obvious in a diff what has just been waved through.
    public static readonly Viewer Everything = new() { Scope = DataScope.Everything };

    // Reaches nothing. The safe answer when there is no signed-in user to resolve.
    public static readonly Viewer Nobody = new() { Scope = DataScope.Nothing };

    // Whether narrowing can be skipped entirely. Worth asking before composing a
    // subquery that the database would then have to prove reaches everybody.
    public bool ReachesEveryone => Scope.Kind == DataScopeKind.All;

    public static Viewer Of(DataScope scope, Guid? employeeId) =>
        new() { Scope = scope, EmployeeId = employeeId };

    // One person, themselves. What a /mine route passes: those routes already pin the
    // query to the caller's own id, and saying it here as well means the narrowing does
    // not depend on remembering that filter.
    public static Viewer Themselves(Guid employeeId) =>
        new() { Scope = new DataScope { Kind = DataScopeKind.Self }, EmployeeId = employeeId };
}
