using Motee.Domain.Diversity;

namespace Motee.Application.Diversity;

// Self-declared diversity data.
//
// Special-category data under UK GDPR and sensitive personal data under
// Nigeria's NDPA. Four things hold, and the first two are the reason this
// interface looks the way it does:
//
//   There is no write that takes an employee id. DeclareAsync resolves the
//   caller and writes their own row. An employee's ethnicity is not an
//   administrative field, and an API that lets HR set it has already lost the
//   argument however carefully the UI is built.
//
//   There is no read that returns somebody else's answers. MineAsync is the
//   only route to an individual declaration, and it is the caller's own.
//   Reporting goes through ReportAsync, which returns counts.
//
//   Groups below the suppression threshold are folded away before anything
//   leaves the server, so a chart cannot name somebody by elimination.
//
//   An answer nobody was offered is refused. A Nigerian tenant has no
//   ethnicity question, so a client that sends one is turned down rather than
//   quietly stored.
public interface IDiversityService
{
    // The questions for this tenant's country, with the caller's own answers
    // against them. Both halves together, because a form needs the options
    // and the person needs to see what they said last time.
    Task<DiversityFormDto> MineAsync(CancellationToken cancellationToken = default);

    // The caller's own, and only ever the caller's own.
    Task<DiversityResult> DeclareAsync(
        DiversityRequest request,
        CancellationToken cancellationToken = default);

    // Counts, suppressed. Never ids, never names.
    Task<DiversityReportDto> ReportAsync(
        DiversityReportQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record DiversityFormDto
{
    public required IReadOnlyList<DiversityCategoryDto> Categories { get; init; }

    // Null until they have answered anything. Distinct from an empty set of
    // answers, which is somebody who opened the form and declined every
    // question — a real and different thing to have recorded.
    public DateTimeOffset? DeclaredAt { get; init; }
}

public sealed record DiversityCategoryDto
{
    public required DiversityField Field { get; init; }

    public required string Label { get; init; }

    // Shown to the person being asked. Somebody handing over their religion
    // is owed a sentence about why, not a link to a policy.
    public required string Purpose { get; init; }

    public required IReadOnlyList<string> Options { get; init; }

    public string? Answer { get; init; }
}

public sealed record DiversityRequest
{
    // Absent means "leave what I said before". Explicitly clearing an answer
    // is done by sending "Prefer not to say", which is an answer rather than
    // an absence.
    public string? Ethnicity { get; init; }

    public string? Disability { get; init; }

    public string? Religion { get; init; }

    public string? VeteranStatus { get; init; }

    public string? CaringResponsibilities { get; init; }
}

public sealed record DiversityReportQuery
{
    public Guid? DepartmentId { get; init; }

    public Guid? BranchId { get; init; }

    // Restrict the population to a named set of people — what the workforce
    // lens passes when its filters are on. Ids in, counts out: nothing comes
    // back that could be paired with one of them.
    public IReadOnlyList<Guid> EmployeeIds { get; init; } = [];
}

public sealed record DiversityReportDto
{
    public required int Eligible { get; init; }

    // How many of them have answered anything at all. Published with the
    // figures, because a breakdown built from a fifth of the staff is a
    // different claim from one built from all of them.
    public required int Declared { get; init; }

    public required int DeclarationRatePercent { get; init; }

    public required IReadOnlyList<DiversityBreakdownDto> Breakdowns { get; init; }
}

public sealed record DiversityBreakdownDto
{
    public required DiversityField Field { get; init; }

    public required string Label { get; init; }

    public required IReadOnlyList<DiversityTally> Rows { get; init; }

    public required int SuppressedCount { get; init; }

    public required int SuppressedGroups { get; init; }

    public required int Declared { get; init; }

    // Nothing here could be reported safely. Said explicitly so the screen
    // can explain itself rather than render an empty chart that reads as a
    // gap in collection.
    public required bool FullySuppressed { get; init; }
}

public enum DiversityOutcome
{
    Succeeded,

    // The caller has no employee record. The account owner is permanently in
    // this position: they run the company rather than work there, so they
    // have nothing to declare.
    NoEmployeeRecord,

    // An answer that was never offered for this country — including an answer
    // to a question this country does not ask at all.
    AnswerNotOffered,
}

public sealed record DiversityResult
{
    public required DiversityOutcome Outcome { get; init; }

    public DiversityFormDto? Form { get; init; }

    // Which question the refused answer was for, so the form can mark it.
    public DiversityField? Field { get; init; }

    public bool Succeeded => Outcome == DiversityOutcome.Succeeded;

    public static DiversityResult Failed(DiversityOutcome outcome) => new() { Outcome = outcome };

    public static DiversityResult Refused(DiversityField field) =>
        new() { Outcome = DiversityOutcome.AnswerNotOffered, Field = field };

    public static DiversityResult Ok(DiversityFormDto form) =>
        new() { Outcome = DiversityOutcome.Succeeded, Form = form };
}
