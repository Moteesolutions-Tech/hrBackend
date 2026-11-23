using Motee.Domain.Relations;

namespace Motee.Tests.Relations;

// How an employee-relations case moves.
//
// One case model covers grievance, disciplinary and everything adjacent, but
// not every kind walks the same stages: a performance case has no formal
// hearing and an appeal starts at appeal. And a stage cannot be left until what
// it exists to record has been recorded — without that the stage tracker is
// decoration, and a case can go from raised to closed with no investigation in
// it.
//
// Written before the flow.
public class CaseFlowTests
{
    private static ErCase Of(
        CaseComplaintType type = CaseComplaintType.Disciplinary,
        CaseStage stage = CaseStage.Raised,
        string description = "Repeated lateness over six weeks.",
        CasePriority priority = CasePriority.Medium,
        DateOnly? targetResolutionOn = null,
        Guid? investigatorId = null,
        Guid? ownerId = null,
        DateOnly? hearingOn = null,
        CaseOutcome? outcome = null,
        DateOnly? outcomeOn = null,
        bool hasAppeal = false,
        string? appealGrounds = null,
        int witnesses = 0,
        int evidence = 0) => new()
        {
            CaseNumber = "ERC-001",
            ComplaintType = type,
            Stage = stage,
            Description = description,
            Priority = priority,
            TargetResolutionOn = targetResolutionOn,
            InvestigatorEmployeeId = investigatorId,
            CaseOwnerEmployeeId = ownerId,
            HearingOn = hearingOn,
            Outcome = outcome,
            OutcomeOn = outcomeOn,
            HasAppeal = hasAppeal,
            AppealGrounds = appealGrounds,
            Witnesses = [.. Enumerable.Range(0, witnesses)
                .Select(index => new CaseWitness
                {
                    Name = $"Witness {index + 1}",
                    Statement = "Saw it happen.",
                })],
            EvidenceCount = evidence,
        };

    // ── which stages a case uses ────────────────────────────────────────────

    [Fact]
    public void ADisciplinaryCaseWalksEveryStage()
    {
        Assert.Equal(
            [
                CaseStage.Raised,
                CaseStage.Triage,
                CaseStage.Assigned,
                CaseStage.Investigation,
                CaseStage.Hearing,
                CaseStage.OutcomeIssued,
                CaseStage.Appeal,
                CaseStage.Closed,
            ],
            CaseFlows.For(CaseComplaintType.Disciplinary));
    }

    // A performance case has no formal hearing. Forcing it through the
    // disciplinary flow is what made the tracker meaningless.
    [Fact]
    public void APerformanceCaseHasNoHearing()
    {
        Assert.DoesNotContain(
            CaseStage.Hearing,
            CaseFlows.For(CaseComplaintType.PerformanceImprovement));
    }

    [Fact]
    public void AnAppealStartsAtAppeal()
    {
        IReadOnlyList<CaseStage> flow = CaseFlows.For(CaseComplaintType.Appeal);

        Assert.Contains(CaseStage.Appeal, flow);
        Assert.DoesNotContain(CaseStage.Triage, flow);
        Assert.DoesNotContain(CaseStage.Hearing, flow);
    }

    [Fact]
    public void EveryFlowStartsRaisedAndEndsClosed()
    {
        foreach (CaseComplaintType type in Enum.GetValues<CaseComplaintType>())
        {
            IReadOnlyList<CaseStage> flow = CaseFlows.For(type);

            Assert.Equal(CaseStage.Raised, flow[0]);
            Assert.Equal(CaseStage.Closed, flow[^1]);
        }
    }

    [Fact]
    public void NoFlowVisitsAStageTwice()
    {
        foreach (CaseComplaintType type in Enum.GetValues<CaseComplaintType>())
        {
            IReadOnlyList<CaseStage> flow = CaseFlows.For(type);

            Assert.Equal(flow.Count, flow.Distinct().Count());
        }
    }

    // An appeal case hears the appeal and *then* issues its outcome, which is
    // the reverse of where those two sit in the full order. So a flow is not a
    // straight subset of that order, and whether a case may move is measured by
    // position within its own flow rather than by the enum.
    [Fact]
    public void AnAppealIssuesItsOutcomeAfterTheAppealIsHeard()
    {
        IReadOnlyList<CaseStage> flow = CaseFlows.For(CaseComplaintType.Appeal);

        Assert.True(
            flow.ToList().IndexOf(CaseStage.Appeal)
                < flow.ToList().IndexOf(CaseStage.OutcomeIssued));

        // And moving through it works in that order, not the enum's.
        ErCase appeal = Of(
            type: CaseComplaintType.Appeal,
            stage: CaseStage.Appeal,
            investigatorId: Guid.NewGuid(),
            ownerId: Guid.NewGuid(),
            outcome: CaseOutcome.NotUpheld,
            outcomeOn: new DateOnly(2026, 6, 20));

        Assert.True(CaseFlows.CanAdvanceTo(appeal, CaseStage.OutcomeIssued).Allowed);
    }

    // ── what each stage has to record ───────────────────────────────────────

    [Fact]
    public void ACaseWithNoDescriptionCannotLeaveRaised()
    {
        Assert.Equal(
            ["Add a case description"],
            CaseGates.Blockers(Of(description: "  "), CaseStage.Raised));
    }

    [Fact]
    public void TriageNeedsATargetDate()
    {
        Assert.Equal(
            ["Set a target resolution date"],
            CaseGates.Blockers(Of(stage: CaseStage.Triage), CaseStage.Triage));
    }

    [Fact]
    public void AssignmentNeedsAnInvestigatorAndAnOwner()
    {
        Assert.Equal(
            ["Assign an investigator", "Name a case owner"],
            CaseGates.Blockers(Of(stage: CaseStage.Assigned), CaseStage.Assigned));
    }

    // The two are deliberately different people: one does the digging, one is
    // accountable for the case.
    [Fact]
    public void AnOwnerAloneIsNotEnoughToLeaveAssignment()
    {
        Assert.Equal(
            ["Assign an investigator"],
            CaseGates.Blockers(
                Of(stage: CaseStage.Assigned, ownerId: Guid.NewGuid()),
                CaseStage.Assigned));
    }

    [Fact]
    public void AnInvestigationNeedsSomethingInIt()
    {
        Assert.Equal(
            ["Record at least one witness statement or piece of evidence"],
            CaseGates.Blockers(Of(stage: CaseStage.Investigation), CaseStage.Investigation));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(2, 3)]
    public void EitherAWitnessOrAPieceOfEvidenceWillDo(int witnesses, int evidence)
    {
        Assert.Empty(
            CaseGates.Blockers(
                Of(stage: CaseStage.Investigation, witnesses: witnesses, evidence: evidence),
                CaseStage.Investigation));
    }

    [Fact]
    public void AHearingNeedsADateAndAPanel()
    {
        Assert.Equal(
            ["Record the hearing date", "Record who sat on the hearing panel"],
            CaseGates.Blockers(Of(stage: CaseStage.Hearing), CaseStage.Hearing));
    }

    [Fact]
    public void AnOutcomeNeedsToBeRecordedWithItsDate()
    {
        Assert.Equal(
            ["Record the outcome", "Record the outcome date"],
            CaseGates.Blockers(Of(stage: CaseStage.OutcomeIssued), CaseStage.OutcomeIssued));
    }

    [Fact]
    public void AnAppealNeedsItsGroundsOnlyWhenThereIsOne()
    {
        Assert.Empty(CaseGates.Blockers(Of(stage: CaseStage.Appeal), CaseStage.Appeal));

        Assert.Equal(
            ["Record the grounds of appeal"],
            CaseGates.Blockers(
                Of(stage: CaseStage.Appeal, hasAppeal: true), CaseStage.Appeal));
    }

    // ── moving between stages ───────────────────────────────────────────────

    [Fact]
    public void ACaseMovesOnOnceTheStageIsSatisfied()
    {
        ErCase ready = Of(targetResolutionOn: new DateOnly(2026, 7, 1));

        CaseAdvance advance = CaseFlows.CanAdvanceTo(ready, CaseStage.Triage);

        Assert.True(advance.Allowed);
        Assert.Empty(advance.Blockers);
    }

    // The whole point of gating: no jumping from raised to closed with nothing
    // recorded in between.
    [Fact]
    public void ACaseCannotSkipToTheEnd()
    {
        CaseAdvance advance = CaseFlows.CanAdvanceTo(Of(), CaseStage.Closed);

        Assert.False(advance.Allowed);

        Assert.Contains("Set a target resolution date", advance.Blockers);
        Assert.Contains("Assign an investigator", advance.Blockers);
        Assert.Contains("Record the outcome", advance.Blockers);
    }

    [Fact]
    public void EveryStageInBetweenHasToBeSatisfiedNotJustTheOneBeingLeft()
    {
        // Triage is satisfied, assignment is not, so the case cannot reach
        // investigation.
        ErCase triaged = Of(
            stage: CaseStage.Triage,
            targetResolutionOn: new DateOnly(2026, 7, 1));

        CaseAdvance advance = CaseFlows.CanAdvanceTo(triaged, CaseStage.Investigation);

        Assert.False(advance.Allowed);
        Assert.Contains("Assign an investigator", advance.Blockers);
    }

    // Correcting a mis-click should not mean satisfying gates already passed.
    [Fact]
    public void MovingBackIsAlwaysAllowed()
    {
        ErCase late = Of(stage: CaseStage.Hearing);

        CaseAdvance advance = CaseFlows.CanAdvanceTo(late, CaseStage.Triage);

        Assert.True(advance.Allowed);
        Assert.Empty(advance.Blockers);
    }

    [Fact]
    public void StayingPutIsAllowed()
    {
        Assert.True(CaseFlows.CanAdvanceTo(Of(), CaseStage.Raised).Allowed);
    }

    [Fact]
    public void ACaseCannotMoveToAStageItsKindDoesNotHave()
    {
        CaseAdvance advance = CaseFlows.CanAdvanceTo(
            Of(type: CaseComplaintType.PerformanceImprovement), CaseStage.Hearing);

        Assert.False(advance.Allowed);
        Assert.Equal(["This kind of case has no hearing stage"], advance.Blockers);
    }

    // A case whose stage is not in its own flow — the kind was changed after it
    // was raised. It can still be moved somewhere valid rather than being
    // stuck.
    [Fact]
    public void ACaseStrandedOffItsFlowCanStillBeMoved()
    {
        ErCase stranded = Of(
            type: CaseComplaintType.PerformanceImprovement,
            stage: CaseStage.Hearing,
            targetResolutionOn: new DateOnly(2026, 7, 1),
            investigatorId: Guid.NewGuid(),
            ownerId: Guid.NewGuid(),
            outcome: CaseOutcome.WrittenWarning,
            outcomeOn: new DateOnly(2026, 7, 2));

        Assert.True(CaseFlows.CanAdvanceTo(stranded, CaseStage.OutcomeIssued).Allowed);
    }

    [Fact]
    public void WalksADisciplinaryCaseAllTheWayThrough()
    {
        ErCase live = Of(
            targetResolutionOn: new DateOnly(2026, 7, 1),
            investigatorId: Guid.NewGuid(),
            ownerId: Guid.NewGuid(),
            witnesses: 1,
            hearingOn: new DateOnly(2026, 6, 20),
            outcome: CaseOutcome.FinalWrittenWarning,
            outcomeOn: new DateOnly(2026, 6, 25));

        live.HearingPanelEmployeeIds = [Guid.NewGuid(), Guid.NewGuid()];

        foreach (CaseStage stage in CaseFlows.For(live.ComplaintType))
        {
            CaseAdvance advance = CaseFlows.CanAdvanceTo(live, stage);

            Assert.True(
                advance.Allowed,
                $"could not reach {stage}: {string.Join("; ", advance.Blockers)}");

            live.Stage = stage;
        }

        Assert.Equal(CaseStage.Closed, live.Stage);
    }
}
