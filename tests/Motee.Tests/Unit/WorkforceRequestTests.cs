using Motee.Domain.Approvals;
using Motee.Domain.Organisation;
using Motee.Domain.Workforce;

namespace Motee.Tests.Unit;

// A manager asking for headcount, and the gates between asking and a vacancy
// existing.
//
// The status is the interesting part. Pending, approved and rejected are
// facts about the approval chain rather than columns on the request, so the
// tests below are mostly about one question: does the request agree with the
// chain underneath it. A stored copy that drifts is a badge saying "pending"
// over a chain that was approved last week, and nothing in the product would
// ever notice.
public class WorkforceRequestTests
{
    private static WorkforceRequest Asking(
        string position = "Backend engineer",
        string justification = "Two leavers and a roadmap we have committed to.",
        int hires = 1,
        string? costCentre = "ENG-01",
        Guid? departmentId = null,
        Guid? requisitionId = null,
        DateTimeOffset? cancelledAt = null) => new()
        {
            Id = Guid.NewGuid(),
            Position = position,
            Justification = justification,
            NumberOfHires = hires,
            CostCentreCode = costCentre,
            DepartmentId = departmentId ?? Guid.NewGuid(),
            RequisitionId = requisitionId,
            CancelledAt = cancelledAt,
        };

    // ── what the request is ─────────────────────────────────────────────────

    [Fact]
    public void ARequestWithNoChainYetIsADraft() =>
        Assert.Equal(WorkforceRequestStatus.Draft, WorkforceRequests.Derive(Asking(), null));

    [Theory]
    [InlineData(ApprovalStatus.Draft, WorkforceRequestStatus.Draft)]
    [InlineData(ApprovalStatus.InProgress, WorkforceRequestStatus.PendingApproval)]
    [InlineData(ApprovalStatus.Approved, WorkforceRequestStatus.Approved)]
    [InlineData(ApprovalStatus.Rejected, WorkforceRequestStatus.Rejected)]
    [InlineData(ApprovalStatus.Returned, WorkforceRequestStatus.Returned)]
    public void TheChainDecidesTheStatus(ApprovalStatus approval, WorkforceRequestStatus expected) =>
        Assert.Equal(expected, WorkforceRequests.Derive(Asking(), approval));

    // Somebody withdrew the approval, not the request, so it goes back to
    // being the requester's to edit rather than being stranded.
    [Fact]
    public void ACancelledChainLeavesTheRequestEditableAgain() =>
        Assert.Equal(
            WorkforceRequestStatus.Draft,
            WorkforceRequests.Derive(Asking(), ApprovalStatus.Cancelled));

    // A vacancy existing outranks everything. From then on the vacancy is the
    // record, and this row is the authorisation behind it.
    [Fact]
    public void AVacancyExistingMakesItConvertedWhateverTheChainSays() =>
        Assert.Equal(
            WorkforceRequestStatus.Converted,
            WorkforceRequests.Derive(
                Asking(requisitionId: Guid.NewGuid()), ApprovalStatus.InProgress));

    [Fact]
    public void ACancelledRequestIsCancelled() =>
        Assert.Equal(
            WorkforceRequestStatus.Cancelled,
            WorkforceRequests.Derive(Asking(cancelledAt: DateTimeOffset.UtcNow), null));

    // ── submitting ──────────────────────────────────────────────────────────

    [Fact]
    public void ACompleteDraftCanBeSubmitted() =>
        Assert.Equal(WorkforceRefusal.None, WorkforceRequests.CanSubmit(Asking(), null));

    [Fact]
    public void ARequestSentBackCanBeSubmittedAgain() =>
        Assert.Equal(
            WorkforceRefusal.None,
            WorkforceRequests.CanSubmit(Asking(), ApprovalStatus.Returned));

    [Fact]
    public void ARejectedRequestCanBeSubmittedAgain() =>
        Assert.Equal(
            WorkforceRefusal.None,
            WorkforceRequests.CanSubmit(Asking(), ApprovalStatus.Rejected));

    [Fact]
    public void ARequestAlreadyWithApproversIsNotSentTwice() =>
        Assert.Equal(
            WorkforceRefusal.AlreadySubmitted,
            WorkforceRequests.CanSubmit(Asking(), ApprovalStatus.InProgress));

    [Fact]
    public void AnApprovedRequestHasNothingLeftToSubmit() =>
        Assert.Equal(
            WorkforceRefusal.NotSubmittable,
            WorkforceRequests.CanSubmit(Asking(), ApprovalStatus.Approved));

    [Fact]
    public void AConvertedRequestIsClosed() =>
        Assert.Equal(
            WorkforceRefusal.AlreadyClosed,
            WorkforceRequests.CanSubmit(Asking(requisitionId: Guid.NewGuid()), null));

    // ── what an approver has to be given ────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ARequestWithoutAPositionCannotBeSubmitted(string position) =>
        Assert.Equal(
            WorkforceRefusal.PositionRequired,
            WorkforceRequests.CanSubmit(Asking(position: position), null));

    // "How many people" on its own tells an approver nothing about what the
    // money is for.
    [Fact]
    public void ARequestWithoutACaseCannotBeSubmitted() =>
        Assert.Equal(
            WorkforceRefusal.JustificationRequired,
            WorkforceRequests.CanSubmit(Asking(justification: " "), null));

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void ARequestForNobodyCannotBeSubmitted(int hires) =>
        Assert.Equal(
            WorkforceRefusal.HiresNotPositive,
            WorkforceRequests.CanSubmit(Asking(hires: hires), null));

    // Finance books the spend against a cost centre. Without one an approver
    // would be authorising an amount against nothing.
    [Fact]
    public void ARequestWithNothingToChargeCannotBeSubmitted() =>
        Assert.Equal(
            WorkforceRefusal.CostCentreRequired,
            WorkforceRequests.CanSubmit(Asking(costCentre: null), null));

    // ── becoming a vacancy ──────────────────────────────────────────────────

    [Fact]
    public void AnApprovedRequestBecomesAVacancy() =>
        Assert.Equal(
            WorkforceRefusal.None,
            WorkforceRequests.CanConvert(Asking(), ApprovalStatus.Approved));

    [Theory]
    [InlineData(ApprovalStatus.InProgress)]
    [InlineData(ApprovalStatus.Rejected)]
    [InlineData(ApprovalStatus.Returned)]
    [InlineData(null)]
    public void AnythingNobodyApprovedDoesNotBecomeAVacancy(ApprovalStatus? approval) =>
        Assert.Equal(
            WorkforceRefusal.NotApproved, WorkforceRequests.CanConvert(Asking(), approval));

    // Twice would be two vacancies for one authorisation, and the second
    // would be hiring against a budget nobody signed for.
    [Fact]
    public void ARequestOnlyBecomesAVacancyOnce() =>
        Assert.Equal(
            WorkforceRefusal.AlreadyConverted,
            WorkforceRequests.CanConvert(
                Asking(requisitionId: Guid.NewGuid()), ApprovalStatus.Approved));

    [Fact]
    public void AVacancyNeedsToKnowItsDepartment()
    {
        WorkforceRequest noDepartment = Asking();
        noDepartment.DepartmentId = null;

        Assert.Equal(
            WorkforceRefusal.DepartmentRequired,
            WorkforceRequests.CanConvert(noDepartment, ApprovalStatus.Approved));
    }

    // ── editing and withdrawing ─────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData(ApprovalStatus.Draft)]
    [InlineData(ApprovalStatus.Returned)]
    [InlineData(ApprovalStatus.Rejected)]
    public void ARequestNobodyIsLookingAtCanBeEdited(ApprovalStatus? approval) =>
        Assert.Equal(WorkforceRefusal.None, WorkforceRequests.CanEdit(Asking(), approval));

    // An approver reading a request that changes under them is approving
    // something nobody saw.
    [Fact]
    public void ARequestWithApproversCannotBeEdited() =>
        Assert.Equal(
            WorkforceRefusal.AlreadySubmitted,
            WorkforceRequests.CanEdit(Asking(), ApprovalStatus.InProgress));

    [Fact]
    public void AnApprovedRequestCannotBeEdited() =>
        Assert.Equal(
            WorkforceRefusal.AlreadyClosed,
            WorkforceRequests.CanEdit(Asking(), ApprovalStatus.Approved));

    [Theory]
    [InlineData(null)]
    [InlineData(ApprovalStatus.InProgress)]
    [InlineData(ApprovalStatus.Approved)]
    public void ARequestCanBeWithdrawnRightUpToConversion(ApprovalStatus? approval) =>
        Assert.Equal(WorkforceRefusal.None, WorkforceRequests.CanCancel(Asking(), approval));

    [Fact]
    public void AConvertedRequestCannotBeWithdrawn() =>
        Assert.Equal(
            WorkforceRefusal.AlreadyClosed,
            WorkforceRequests.CanCancel(
                Asking(requisitionId: Guid.NewGuid()), ApprovalStatus.Approved));

    // ── what the vacancy inherits ───────────────────────────────────────────

    [Theory]
    [InlineData(VacancyType.Permanent, EmploymentType.FullTime)]
    [InlineData(VacancyType.Contract, EmploymentType.Contract)]
    [InlineData(VacancyType.FixedTerm, EmploymentType.Contract)]
    [InlineData(VacancyType.Temporary, EmploymentType.Temporary)]
    [InlineData(VacancyType.Apprentice, EmploymentType.FullTime)]
    [InlineData(VacancyType.Graduate, EmploymentType.FullTime)]
    public void TheVacancyInheritsTheContractShape(
        VacancyType vacancy, EmploymentType expected) =>
        Assert.Equal(expected, WorkforceRequests.EmploymentTypeOf(vacancy));

    [Fact]
    public void AReferenceIsQuotableAndPadded() =>
        Assert.Equal("WFR-2026-00124", WorkforceRequests.Reference(2026, 124));
}
