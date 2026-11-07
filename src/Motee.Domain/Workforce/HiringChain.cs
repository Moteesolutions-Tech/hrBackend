namespace Motee.Domain.Workforce;

// The six stages of filling a role, mirroring the lifecycle module's own.
//
// Numbered, because the tracker renders progress and a reader needs to know
// how far along six this is without a lookup.
public enum HiringStage
{
    WorkforcePlanning = 1,
    Requisition = 2,
    Attract = 3,
    Select = 4,
    PreEmployment = 5,
    Onboard = 6,
}

// Everything about one hiring effort that bears on where it has got to.
//
// A record of facts rather than the five entities themselves, so the
// precedence below stays pure and the service decides where each fact comes
// from.
public sealed record ChainFacts
{
    // No vacancy raised yet — the request is the whole of it so far.
    public bool HasVacancy { get; init; }

    // The vacancy has been advertised. Approved-but-unpublished is still the
    // requisition stage: nobody can apply to it.
    public bool VacancyPublished { get; init; }

    public bool VacancyApproved { get; init; }

    // Live candidates part-way through interviews.
    public int InSelection { get; init; }

    // Live candidates with an offer out.
    public int AtOffer { get; init; }

    // Hired, but nobody has sent them to onboarding. The gap the handoff
    // exists to close, and the one worth surfacing: a hire sitting here is a
    // person who said yes and has heard nothing.
    public int HiredNotInvited { get; init; }

    // Onboarding exists and they have not started yet.
    public bool PreBoarding { get; init; }

    // Onboarding exists and is under way.
    public bool Onboarding { get; init; }

    // Applicants on the vacancy who have not reached selection.
    public int Applicants { get; init; }
}

public static class HiringChain
{
    // Ten days without movement. Long enough that a quiet fortnight over
    // Christmas is not an alarm, short enough that a forgotten candidate is
    // caught inside a hiring round.
    public const int StallDays = 10;

    // Where this effort has got to.
    //
    // Resolved by precedence, furthest-along first, because one role can be
    // true of several stages at once: a vacancy with two people in interviews
    // and one accepted offer is at pre-employment, not in selection. Reading
    // the earliest true stage instead would report every nearly-finished hire
    // as though it had just started.
    public static HiringStage Resolve(ChainFacts facts)
    {
        if (facts.Onboarding)
        {
            return HiringStage.Onboard;
        }

        // All three are pre-employment: somebody is identified and the work
        // left is paperwork, checks or an answer.
        if (facts.PreBoarding || facts.HiredNotInvited > 0 || facts.AtOffer > 0)
        {
            return HiringStage.PreEmployment;
        }

        if (facts.InSelection > 0)
        {
            return HiringStage.Select;
        }

        if (!facts.HasVacancy)
        {
            return HiringStage.WorkforcePlanning;
        }

        // Advertised, so people can apply — whether or not anybody has.
        return facts.VacancyPublished ? HiringStage.Attract : HiringStage.Requisition;
    }

    public static int StepOf(HiringStage stage) => (int)stage;

    public static bool IsStalled(int daysSinceMoved) => daysSinceMoved >= StallDays;

    // What is actually happening, in words, because a stage name alone does
    // not tell somebody what to do next.
    public static string Detail(HiringStage stage, ChainFacts facts) => stage switch
    {
        HiringStage.Onboard => "Onboarding under way",
        HiringStage.PreEmployment when facts.PreBoarding =>
            "Checks in progress before they start",
        HiringStage.PreEmployment when facts.HiredNotInvited > 0 =>
            "Hired — onboarding invite not sent",
        HiringStage.PreEmployment => "Offer out — awaiting acceptance",
        HiringStage.Select => Count(facts.InSelection, "candidate") + " in selection",
        HiringStage.Attract => facts.Applicants > 0
            ? Count(facts.Applicants, "applicant") + " to screen"
            : "Advertised — no applicants yet",
        HiringStage.Requisition => facts.VacancyApproved
            ? "Approved — not advertised yet"
            : "Vacancy awaiting approval",
        _ => "Headcount requested",
    };

    private static string Count(int many, string noun) =>
        many == 1 ? $"1 {noun}" : $"{many} {noun}s";
}
