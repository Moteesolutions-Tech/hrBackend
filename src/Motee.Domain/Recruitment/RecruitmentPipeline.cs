namespace Motee.Domain.Recruitment;

// Why a move was refused.
//
// An enum rather than a string, so the API can map each to a message and the
// tests can assert on the reason rather than on wording. The frontend has the
// same rules in its own board, which is exactly why they are written down here
// too: a rule enforced only in the client is a rule any other client ignores.
public enum MoveRefusal
{
    None,

    AlreadyThere,

    // Rejected candidates do not move. Restoring them is a separate, deliberate
    // act, so nobody is quietly resurrected by a drag of a card.
    Rejected,

    // The target stage is switched off for this role.
    StageNotEnabled,

    // Two stages at once. Skipping loses the interview or offer record for that
    // candidate, which is the evidence the later stages are read through.
    SkipsAStage,

    // Nobody has rated the interview yet.
    NotScored,

    NoOffer,

    OfferNotAccepted,

    OfferDeclined,

    // The role is funded for a number of people and that number is seated.
    NoOpeningsLeft,
}

// How far a candidate has got, as a table rather than as conditionals spread
// across whichever screen happens to be moving them.
//
// Every gate here exists because of a way the evidence goes missing. Without
// the score gate, a candidate reaches an offer with nothing recorded and the
// score column on the later stages is decoration. Without the offer gate,
// somebody is onboarded before they have said yes. Without the capacity gate,
// a role approved for two seats three, and the vacancy's own "filled" badge
// then reports a number Finance never signed off.
public static class RecruitmentPipeline
{
    // Pipeline order. Applicants and Hired are always in it; the middle three
    // are per-role.
    public static readonly IReadOnlyList<CandidateStage> Order =
    [
        CandidateStage.Applicants,
        CandidateStage.Interview,
        CandidateStage.Interviewed,
        CandidateStage.Offer,
        CandidateStage.Hired,
    ];

    // A role's enabled stages, in order, with the two mandatory ones put back
    // if they are missing. A stored list written before a stage existed would
    // otherwise drop that stage from the role forever.
    public static IReadOnlyList<CandidateStage> StagesFor(IEnumerable<CandidateStage> enabled)
    {
        HashSet<CandidateStage> set = [.. enabled];

        set.Add(CandidateStage.Applicants);
        set.Add(CandidateStage.Hired);

        return [.. Order.Where(set.Contains)];
    }

    // IReadOnlyList has no IndexOf of its own, and the span overload does not
    // apply to an interface.
    private static int IndexOf(IReadOnlyList<CandidateStage> stages, CandidateStage stage)
    {
        for (int index = 0; index < stages.Count; index++)
        {
            if (stages[index] == stage)
            {
                return index;
            }
        }

        return -1;
    }

    public static MoveRefusal Refusal(
        Candidate candidate,
        CandidateStage to,
        IReadOnlyList<CandidateStage> stages,
        IReadOnlyCollection<Scorecard> scorecards,
        IReadOnlyCollection<CandidateOffer> offers,
        int openings,
        int hired)
    {
        if (candidate.Stage == to)
        {
            return MoveRefusal.AlreadyThere;
        }

        if (candidate.Status == CandidateStatus.Rejected)
        {
            return MoveRefusal.Rejected;
        }

        if (!stages.Contains(to))
        {
            return MoveRefusal.StageNotEnabled;
        }

        int from = IndexOf(stages, candidate.Stage);
        int target = IndexOf(stages, to);

        // Checked before the gates below so the message names the real problem.
        // Somebody dragged from Applicant to Interviewed is being told to go via
        // the interview, not to score a meeting that was never arranged.
        //
        // Moving backwards is allowed. A candidate sent back a stage is a
        // correction, and refusing it would leave the only fix as a rejection.
        if (from >= 0 && target > from + 1)
        {
            return MoveRefusal.SkipsAStage;
        }

        if (to == CandidateStage.Interviewed
            && Scorecards.AverageFor(scorecards) is null)
        {
            return MoveRefusal.NotScored;
        }

        if (to == CandidateStage.Hired)
        {
            // Held here as well as in the stage order, because the offer stage
            // can be switched off for a role — and a role that makes no formal
            // offers still must not hire somebody who declined one.
            if (!Offers.Accepted(offers))
            {
                return offers.Count == 0
                    ? MoveRefusal.NoOffer
                    : Offers.Declined(offers)
                        ? MoveRefusal.OfferDeclined
                        : MoveRefusal.OfferNotAccepted;
            }

            if (hired >= Math.Max(1, openings))
            {
                return MoveRefusal.NoOpeningsLeft;
            }
        }

        return MoveRefusal.None;
    }
}
