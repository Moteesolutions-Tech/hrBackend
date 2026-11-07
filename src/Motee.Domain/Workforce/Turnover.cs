using Motee.Domain.Offboarding;

namespace Motee.Domain.Workforce;

// One departure, reduced to the only thing turnover needs from it.
public readonly record struct Exit(ExitReason? Reason);

public sealed record TurnoverTally
{
    public required int Total { get; init; }

    public required int Voluntary { get; init; }

    public required int Involuntary { get; init; }

    // Exits with no reason recorded. Reported rather than folded into either
    // side: a gap in the data is not a statement about why anybody left.
    public required int Unclassified { get; init; }

    // Null where there was nobody to leave. A department that did not exist
    // yet has no rate, which is not the same as a rate of zero.
    public decimal? RatePercent { get; init; }
}

// Who left, why, and against how many people.
//
// The voluntary split is the whole value of the measure. Resignations are
// the company's problem to fix; redundancies are its own decision. A single
// turnover percentage hides which it is having, and the same 12% means
// opposite things depending on the answer.
public static class Turnover
{
    // They chose to go. Retirement sits here because leaving at the end of a
    // career is the person's decision, even though nobody is going to act on
    // it as a retention failure.
    public static bool IsVoluntary(ExitReason? reason) =>
        reason is ExitReason.Resignation or ExitReason.Retirement;

    public static bool IsInvoluntary(ExitReason? reason) =>
        reason is ExitReason.Termination or ExitReason.Redundancy or ExitReason.ContractEnd;

    public static TurnoverTally Of(IReadOnlyCollection<Exit> exits, int averageHeadcount)
    {
        int voluntary = exits.Count(exit => IsVoluntary(exit.Reason));
        int involuntary = exits.Count(exit => IsInvoluntary(exit.Reason));

        return new TurnoverTally
        {
            Total = exits.Count,
            Voluntary = voluntary,
            Involuntary = involuntary,
            Unclassified = exits.Count - voluntary - involuntary,
            RatePercent = averageHeadcount <= 0
                ? null
                : Math.Round(
                    exits.Count / (decimal)averageHeadcount * 100m,
                    1,
                    MidpointRounding.AwayFromZero),
        };
    }

    // The mean of the opening and closing figures.
    //
    // Crude, and the standard way every HR report does it. A daily mean would
    // be more exact and would disagree with the number the reader has seen
    // everywhere else, which is a worse failure than the imprecision.
    public static int AverageHeadcount(int opening, int closing) =>
        (int)Math.Round((opening + closing) / 2m, MidpointRounding.AwayFromZero);
}
