using Motee.Domain.Workflows;

namespace Motee.Tests.Workflows;

// A workflow that is actually running.
//
// The run owns assignment, due dates and dependency state; the workflow stays
// the template. Two statuses a screen shows are never stored: blocked is a
// question about other rows, and overdue is a question about today. The client
// writes both into the rows on every change, which is a figure that is wrong
// the next morning.
//
// Written before the run.
public class WorkflowRunTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    private static RunTask Task(
        string title = "Collect signed contract",
        RunTaskState state = RunTaskState.NotStarted,
        int dueIn = 7,
        bool hasReviewer = false,
        int sequence = 1,
        string? parallelGroup = null,
        params Guid[] dependsOn) => new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            Sequence = sequence,
            State = state,
            DueOn = Today.AddDays(dueIn),
            HasReviewer = hasReviewer,
            ParallelGroup = parallelGroup,
            DependsOn = [.. dependsOn],
            AssigneeName = "Ada Okafor",
        };

    private static WorkflowTask Template(
        string title = "Collect signed contract",
        int sequence = 1,
        WorkflowCondition? condition = null,
        string? parallelGroup = null,
        params Guid[] dependsOn) => new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            Sequence = sequence,
            AssigneeKind = AssigneeKind.AccessLevel,
            AssigneeAccessLevelId = Guid.NewGuid(),
            Condition = condition,
            ParallelGroup = parallelGroup,
            DependsOn = [.. dependsOn],
        };

    // ── planning a run ──────────────────────────────────────────────────────

    [Fact]
    public void WorkWithNoConditionAlwaysApplies()
    {
        Assert.True(WorkflowPlan.Applies(Template(), []));
    }

    [Fact]
    public void ConditionalWorkAppliesWhenTheConditionHolds()
    {
        Assert.True(
            WorkflowPlan.Applies(
                Template(condition: WorkflowCondition.RemoteWorker),
                [WorkflowCondition.RemoteWorker]));
    }

    // Shipping a laptop to a home address should not sit on everybody's list
    // looking unfinished.
    [Fact]
    public void ConditionalWorkIsSkippedWhenTheConditionDoesNot()
    {
        Assert.False(
            WorkflowPlan.Applies(
                Template(condition: WorkflowCondition.RemoteWorker),
                [WorkflowCondition.Contractor]));
    }

    [Fact]
    public void AnEmptyContextSkipsEveryConditionalTask()
    {
        Assert.False(
            WorkflowPlan.Applies(Template(condition: WorkflowCondition.VisaHolder), []));
    }

    // "Unless a contractor" is a rule the client cannot state, and its own
    // seeded benefits enrolment is the proof: tagged "contractor" with a
    // comment saying contractors are not enrolled.
    [Fact]
    public void WorkPhrasedAsUnlessAppliesWhenTheFactIsAbsent()
    {
        WorkflowTask benefits = Template(condition: WorkflowCondition.Contractor);
        benefits.ConditionHolds = false;

        Assert.True(WorkflowPlan.Applies(benefits, []));
        Assert.True(WorkflowPlan.Applies(benefits, [WorkflowCondition.RemoteWorker]));
        Assert.False(WorkflowPlan.Applies(benefits, [WorkflowCondition.Contractor]));
    }

    [Fact]
    public void APlanSeparatesWhatStartsFromWhatWaits()
    {
        WorkflowTask first = Template("Sign the contract");
        WorkflowTask second = Template("Activate the account", sequence: 2, dependsOn: first.Id);

        WorkflowRunPlan plan = WorkflowPlan.Of([first, second], []);

        Assert.Equal(["Sign the contract"], plan.Activated.Select(task => task.Title));
        Assert.Equal(["Activate the account"], plan.Blocked.Select(task => task.Title));
        Assert.Empty(plan.Skipped);
    }

    // If "ship laptop to home address" does not apply, everything waiting on it
    // would otherwise wait forever.
    [Fact]
    public void WorkWaitingOnSomethingThatDoesNotApplyStartsAnyway()
    {
        WorkflowTask shipping = Template(
            "Ship the laptop", condition: WorkflowCondition.RemoteWorker);

        WorkflowTask next = Template("Confirm it arrived", sequence: 2, dependsOn: shipping.Id);

        WorkflowRunPlan plan = WorkflowPlan.Of([shipping, next], []);

        Assert.Equal(["Ship the laptop"], plan.Skipped.Select(task => task.Title));
        Assert.Equal(["Confirm it arrived"], plan.Activated.Select(task => task.Title));
        Assert.Empty(plan.Blocked);
    }

    // Tasks sharing a group run at the same time instead of queueing.
    [Fact]
    public void WorkInOneGroupStartsTogether()
    {
        WorkflowTask contract = Template("Sign the contract");
        WorkflowTask payroll = Template(
            "Payroll setup", sequence: 2, parallelGroup: "setup", dependsOn: contract.Id);
        WorkflowTask it = Template(
            "IT setup", sequence: 3, parallelGroup: "setup", dependsOn: contract.Id);

        WorkflowRunPlan plan = WorkflowPlan.Of([contract, payroll, it], []);

        Assert.Equal(["Sign the contract"], plan.Activated.Select(task => task.Title));

        WorkflowRunPlan after = WorkflowPlan.Of(
            [contract, payroll, it], [], settled: [contract.Id]);

        Assert.Equal(
            ["Payroll setup", "IT setup"], after.Activated.Select(task => task.Title));

        Assert.Equal(["setup"], after.ParallelGroups.Keys);
        Assert.Equal(2, after.ParallelGroups["setup"].Count);
    }

    // ── what a screen shows ─────────────────────────────────────────────────

    [Fact]
    public void WorkWithEverythingDoneBeforeItIsReadyToStart()
    {
        RunTask only = Task();

        Assert.Equal(RunTaskStatus.NotStarted, RunProgress.StatusOf(only, [only], Today));
    }

    // Blocked is not a stored state. The client writes it into the row on every
    // change, which means a task unblocked by another route stays blocked until
    // something happens to rewrite it.
    [Fact]
    public void WorkWaitingOnSomethingUnfinishedReadsAsBlocked()
    {
        RunTask first = Task("Sign the contract");
        RunTask second = Task("Activate the account", sequence: 2, dependsOn: first.Id);

        Assert.Equal(
            RunTaskStatus.Blocked, RunProgress.StatusOf(second, [first, second], Today));
    }

    [Fact]
    public void WorkUnblocksTheMomentWhatItWaitedOnIsDone()
    {
        RunTask first = Task("Sign the contract", RunTaskState.Completed);
        RunTask second = Task("Activate the account", sequence: 2, dependsOn: first.Id);

        Assert.Equal(
            RunTaskStatus.NotStarted, RunProgress.StatusOf(second, [first, second], Today));
    }

    [Fact]
    public void WorkWaitingOnSomethingSkippedIsNotBlocked()
    {
        RunTask shipping = Task("Ship the laptop", RunTaskState.Skipped);
        RunTask next = Task("Confirm it arrived", sequence: 2, dependsOn: shipping.Id);

        Assert.Equal(
            RunTaskStatus.NotStarted, RunProgress.StatusOf(next, [shipping, next], Today));
    }

    [Fact]
    public void WorkPastItsDateReadsAsOverdue()
    {
        RunTask late = Task(dueIn: -1);

        Assert.Equal(RunTaskStatus.Overdue, RunProgress.StatusOf(late, [late], Today));
    }

    [Fact]
    public void WorkDueTodayIsNotYetOverdue()
    {
        RunTask due = Task(dueIn: 0);

        Assert.Equal(RunTaskStatus.NotStarted, RunProgress.StatusOf(due, [due], Today));
    }

    [Fact]
    public void WorkInProgressAndPastItsDateIsOverdue()
    {
        RunTask late = Task(state: RunTaskState.InProgress, dueIn: -3);

        Assert.Equal(RunTaskStatus.Overdue, RunProgress.StatusOf(late, [late], Today));
    }

    // Once it is with the reviewer it is no longer the doer's to chase, so a
    // late sign-off is not reported as the task being overdue.
    [Fact]
    public void WorkAwaitingSignOffIsNotReportedAsOverdue()
    {
        RunTask waiting = Task(state: RunTaskState.AwaitingApproval, dueIn: -3);

        Assert.Equal(
            RunTaskStatus.AwaitingApproval, RunProgress.StatusOf(waiting, [waiting], Today));
    }

    [Theory]
    [InlineData(RunTaskState.Completed, RunTaskStatus.Completed)]
    [InlineData(RunTaskState.Skipped, RunTaskStatus.Skipped)]
    public void FinishedWorkIsNeverOverdue(RunTaskState state, RunTaskStatus expected)
    {
        RunTask done = Task(state: state, dueIn: -30);

        Assert.Equal(expected, RunProgress.StatusOf(done, [done], Today));
    }

    // Blocked wins over overdue: the person it is assigned to cannot start it,
    // so chasing them is the wrong thing for a screen to do.
    [Fact]
    public void WorkThatIsBothBlockedAndLateReadsAsBlocked()
    {
        RunTask first = Task("Sign the contract");
        RunTask second = Task(
            "Activate the account", sequence: 2, dueIn: -5, dependsOn: first.Id);

        Assert.Equal(
            RunTaskStatus.Blocked, RunProgress.StatusOf(second, [first, second], Today));
    }

    [Fact]
    public void CountsTheDaysWorkIsLate()
    {
        Assert.Equal(4, RunProgress.DaysOverdue(Task(dueIn: -4), Today));
        Assert.Equal(0, RunProgress.DaysOverdue(Task(dueIn: 0), Today));
        Assert.Equal(0, RunProgress.DaysOverdue(Task(dueIn: 3), Today));
    }

    [Fact]
    public void NamesWhatWorkIsWaitingOn()
    {
        RunTask first = Task("Sign the contract");
        RunTask second = Task("Activate the account", sequence: 2, dependsOn: first.Id);

        Assert.Equal(
            ["Sign the contract"],
            RunProgress.Blockers(second, [first, second]).Select(task => task.Title));
    }

    // ── the run as a whole ──────────────────────────────────────────────────

    [Fact]
    public void ARunWithWorkOutstandingIsStillRunning()
    {
        Assert.Equal(
            RunState.Active,
            RunProgress.StateOf([Task(state: RunTaskState.Completed), Task()], null));
    }

    // Derived, not written by a cascade: every task settled is the definition
    // of a finished run.
    [Fact]
    public void ARunWithEverythingSettledIsFinished()
    {
        Assert.Equal(
            RunState.Completed,
            RunProgress.StateOf(
                [Task(state: RunTaskState.Completed), Task(state: RunTaskState.Skipped)],
                null));
    }

    [Fact]
    public void ARunNobodyCancelledWithNoWorkAtAllIsFinished()
    {
        Assert.Equal(RunState.Completed, RunProgress.StateOf([], null));
    }

    // Cancelling is a decision somebody took, so it is recorded rather than
    // worked out — and it outranks the tasks underneath.
    [Fact]
    public void ACancelledRunStaysCancelledWhateverTheWorkSays()
    {
        DateTimeOffset cancelled = new(2026, 6, 10, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            RunState.Cancelled,
            RunProgress.StateOf([Task(state: RunTaskState.Completed)], cancelled));

        Assert.Equal(RunState.Cancelled, RunProgress.StateOf([Task()], cancelled));
    }

    [Fact]
    public void TalliesWhereARunHasGot()
    {
        RunTask first = Task("Sign the contract", RunTaskState.Completed);
        RunTask second = Task("Activate", sequence: 2, dueIn: -2, dependsOn: first.Id);
        RunTask third = Task("Order a laptop", RunTaskState.Skipped, sequence: 3);
        RunTask fourth = Task("Book induction", RunTaskState.AwaitingApproval, sequence: 4);

        RunTally tally = RunProgress.Tally([first, second, third, fourth], Today);

        Assert.Equal(4, tally.Tasks);
        Assert.Equal(1, tally.Completed);
        Assert.Equal(1, tally.Skipped);
        Assert.Equal(1, tally.AwaitingApproval);
        Assert.Equal(1, tally.Overdue);

        // Settled over the whole list: two of four.
        Assert.Equal(50, tally.PercentComplete);
    }

    [Fact]
    public void ARunWithNoWorkIsNotDividedByNothing()
    {
        Assert.Equal(0, RunProgress.Tally([], Today).PercentComplete);
    }

    // ── what a task will accept next ────────────────────────────────────────

    [Theory]
    [InlineData(RunTaskState.NotStarted, RunTaskAction.Start, true)]
    [InlineData(RunTaskState.NotStarted, RunTaskAction.Submit, true)]
    [InlineData(RunTaskState.NotStarted, RunTaskAction.Skip, true)]
    [InlineData(RunTaskState.NotStarted, RunTaskAction.Approve, false)]
    [InlineData(RunTaskState.InProgress, RunTaskAction.Submit, true)]
    [InlineData(RunTaskState.InProgress, RunTaskAction.Start, false)]
    [InlineData(RunTaskState.AwaitingApproval, RunTaskAction.Approve, true)]
    [InlineData(RunTaskState.AwaitingApproval, RunTaskAction.Reject, true)]
    [InlineData(RunTaskState.AwaitingApproval, RunTaskAction.Submit, false)]
    [InlineData(RunTaskState.Completed, RunTaskAction.Approve, false)]
    [InlineData(RunTaskState.Completed, RunTaskAction.Skip, false)]
    [InlineData(RunTaskState.Skipped, RunTaskAction.Start, false)]
    public void KnowsWhatMoveWorkCanMakeNext(
        RunTaskState from, RunTaskAction action, bool allowed)
    {
        Assert.Equal(allowed, RunTaskLifecycle.Can(from, action));
    }

    // Signing off work nobody has handed in is how a checklist becomes
    // theatre. The client guards only against approving something already
    // completed, so a reviewer can approve a task that was never started.
    [Fact]
    public void WorkCannotBeSignedOffBeforeItIsHandedIn()
    {
        Assert.False(RunTaskLifecycle.Can(RunTaskState.NotStarted, RunTaskAction.Approve));

        Assert.Equal(
            "This has not been handed in yet, so there is nothing to sign off.",
            RunTaskLifecycle.Refusal(RunTaskState.NotStarted, RunTaskAction.Approve));
    }

    [Fact]
    public void FinishedWorkIsNotReopenedByAnotherSignOff()
    {
        Assert.Equal(
            "This has already been signed off.",
            RunTaskLifecycle.Refusal(RunTaskState.Completed, RunTaskAction.Approve));
    }

    [Fact]
    public void EveryRefusalSaysSomething()
    {
        foreach (RunTaskState state in Enum.GetValues<RunTaskState>())
        {
            foreach (RunTaskAction action in Enum.GetValues<RunTaskAction>())
            {
                if (RunTaskLifecycle.Can(state, action))
                {
                    continue;
                }

                Assert.False(
                    string.IsNullOrWhiteSpace(RunTaskLifecycle.Refusal(state, action)),
                    $"{state} + {action} has no refusal to show.");
            }
        }
    }

    // ── handing work in ─────────────────────────────────────────────────────

    // Making somebody approve their own work is how a checklist becomes
    // theatre, so a task with no reviewer completes outright.
    [Fact]
    public void HandingInWorkWithNoReviewerFinishesIt()
    {
        Assert.Equal(
            RunTaskState.Completed, RunTaskLifecycle.Submitted(hasReviewer: false));
    }

    [Fact]
    public void HandingInWorkWithAReviewerSendsItForSignOff()
    {
        Assert.Equal(
            RunTaskState.AwaitingApproval, RunTaskLifecycle.Submitted(hasReviewer: true));
    }

    // ── escalation ──────────────────────────────────────────────────────────

    [Fact]
    public void WorkEscalatesOnceItIsLateEnough()
    {
        RunTask late = Task(dueIn: -5);
        late.EscalateAfterDays = 3;

        Assert.True(RunProgress.HasEscalated(late, Today));
    }

    [Fact]
    public void WorkDoesNotEscalateBeforeItsGracePeriodIsUp()
    {
        RunTask late = Task(dueIn: -2);
        late.EscalateAfterDays = 3;

        Assert.False(RunProgress.HasEscalated(late, Today));
    }

    [Fact]
    public void WorkWithNoEscalationSetNeverEscalates()
    {
        Assert.False(RunProgress.HasEscalated(Task(dueIn: -40), Today));
    }

    [Fact]
    public void FinishedWorkDoesNotEscalate()
    {
        RunTask done = Task(state: RunTaskState.Completed, dueIn: -40);
        done.EscalateAfterDays = 1;

        Assert.False(RunProgress.HasEscalated(done, Today));
    }
}
