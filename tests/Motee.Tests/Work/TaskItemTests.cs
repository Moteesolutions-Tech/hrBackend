using System.Text.Json;
using System.Text.Json.Serialization;
using Motee.Domain.Work;

namespace Motee.Tests.Work;

// Tasks — the assigned and personal to-do list behind My Tasks, the HR Tasks
// page and both dashboard widgets.
//
// The frontend decides whether something is overdue by comparing its due date
// to a hardcoded TODAY = "2026-04-23" in task-utils.ts, so every badge is
// frozen at that date. These tests take the day as an argument, which is what
// lets the company's own clock answer it.
public class TaskItemTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly Guid Owner = Guid.NewGuid();

    private static readonly Guid Manager = Guid.NewGuid();

    private static readonly DateOnly Today = new(2026, 4, 23);

    private static TaskItem Item(
        string title = "Review the Q1 headcount plan",
        TaskPriority priority = TaskPriority.Medium,
        DateOnly? dueOn = null,
        DateOnly? completedOn = null,
        Guid? assignee = null,
        Guid? createdBy = null,
        string? category = "HR") =>
        new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            AssigneeEmployeeId = assignee ?? Owner,
            CreatedByEmployeeId = createdBy ?? Owner,
            Priority = priority,
            Category = category,
            DueOn = dueOn,
            CompletedOn = completedOn,
        };

    // Raised by a background job rather than a person. Its own factory because
    // the optional parameter above cannot express it: null is how a caller
    // asks for the default author.
    private static TaskItem SystemRaised(Guid? assignee = null)
    {
        TaskItem item = Item(assignee: assignee);

        item.CreatedByEmployeeId = null;

        return item;
    }

    // ── the wire shape ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(TaskPriority.High, "\"high\"")]
    [InlineData(TaskPriority.Low, "\"low\"")]
    [InlineData(TaskDueState.Overdue, "\"overdue\"")]
    [InlineData(TaskDueState.DueSoon, "\"dueSoon\"")]
    [InlineData(TaskSource.Assigned, "\"assigned\"")]
    public void SerialisesAsCamelCase(object value, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(value, Web));
    }

    // ── what the form will not accept ───────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    public void RefusesATaskWithNothingToDo(string title)
    {
        Assert.Equal(TaskRefusal.TitleRequired, TaskItems.Refusal(Item(title: title)));
    }

    [Fact]
    public void RefusesATitleLongerThanTheFieldAllows()
    {
        Assert.Equal(
            TaskRefusal.TitleTooLong,
            TaskItems.Refusal(Item(title: new string('a', TaskItems.LongestTitle + 1))));
    }

    // The HR form explicitly allows "No due date"; the self-service form
    // requires one. The looser of the two wins, because a task without a
    // deadline is an ordinary thing to write down.
    [Fact]
    public void AcceptsATaskWithNoDueDate()
    {
        Assert.Equal(TaskRefusal.None, TaskItems.Refusal(Item(dueOn: null)));
    }

    [Fact]
    public void RefusesATaskWithNobodyToDoIt()
    {
        TaskItem item = Item();

        item.AssigneeEmployeeId = Guid.Empty;

        Assert.Equal(TaskRefusal.AssigneeRequired, TaskItems.Refusal(item));
    }

    // ── done, and when ──────────────────────────────────────────────────────

    // The frontend carries a boolean. A date answers the same question and
    // also answers "how much did this person clear this week", which the
    // dashboard asks and a boolean cannot.
    [Fact]
    public void ATaskIsDoneOnceItHasACompletionDate()
    {
        Assert.True(TaskItems.IsDone(Item(completedOn: Today)));
        Assert.False(TaskItems.IsDone(Item(completedOn: null)));
    }

    // ── whose task it is ────────────────────────────────────────────────────

    [Fact]
    public void ATaskSomebodyWroteForThemselvesIsPersonal()
    {
        Assert.Equal(
            TaskSource.Personal,
            TaskItems.SourceOf(Item(assignee: Owner, createdBy: Owner), Owner));
    }

    [Fact]
    public void ATaskSomebodyElseWroteIsAssigned()
    {
        Assert.Equal(
            TaskSource.Assigned,
            TaskItems.SourceOf(Item(assignee: Owner, createdBy: Manager), Owner));
    }

    // A task raised by a background job rather than a person — nobody wrote it
    // for themselves, so it is work that arrived.
    [Fact]
    public void ATaskWithNoAuthorIsAssigned()
    {
        Assert.Equal(
            TaskSource.Assigned,
            TaskItems.SourceOf(SystemRaised(assignee: Owner), Owner));
    }

    // Looked at by the manager who raised it, their own task is one they gave
    // out rather than one of theirs to do.
    [Fact]
    public void ATaskGivenToSomebodyElseIsAssignedFromTheAuthorsSide()
    {
        Assert.Equal(
            TaskSource.Assigned,
            TaskItems.SourceOf(Item(assignee: Owner, createdBy: Manager), Manager));
    }

    // ── when it is due ──────────────────────────────────────────────────────

    [Fact]
    public void ADoneTaskIsNeverOverdueHoweverLateItWas()
    {
        Assert.Equal(
            TaskDueState.NotDue,
            TaskItems.DueStateOf(
                Item(dueOn: new DateOnly(2026, 1, 1), completedOn: new DateOnly(2026, 4, 1)),
                Today));
    }

    [Fact]
    public void ATaskPastItsDateIsOverdue()
    {
        Assert.Equal(
            TaskDueState.Overdue,
            TaskItems.DueStateOf(Item(dueOn: new DateOnly(2026, 4, 22)), Today));
    }

    // Their rule: three days or fewer left counts as soon, and the day itself
    // is included.
    [Theory]
    [InlineData(2026, 4, 23)]
    [InlineData(2026, 4, 26)]
    public void ATaskDueWithinThreeDaysIsDueSoon(int year, int month, int day)
    {
        Assert.Equal(
            TaskDueState.DueSoon,
            TaskItems.DueStateOf(Item(dueOn: new DateOnly(year, month, day)), Today));
    }

    [Fact]
    public void ATaskFurtherOutIsSimplyNotDue()
    {
        Assert.Equal(
            TaskDueState.NotDue,
            TaskItems.DueStateOf(Item(dueOn: new DateOnly(2026, 4, 27)), Today));
    }

    // Nothing to be late against.
    [Fact]
    public void ATaskWithNoDueDateIsNeverOverdue()
    {
        Assert.Equal(TaskDueState.NotDue, TaskItems.DueStateOf(Item(dueOn: null), Today));
    }

    // ── who may be given a task ─────────────────────────────────────────────

    // Writing a task for yourself is nobody else's business.
    [Fact]
    public void AnybodyMayGiveThemselvesATask()
    {
        Assert.True(TaskItems.CanAssign(Owner, Owner, managerOfAssignee: Manager));
    }

    [Fact]
    public void AManagerMayGiveTheirOwnReportATask()
    {
        Assert.True(TaskItems.CanAssign(Manager, Owner, managerOfAssignee: Manager));
    }

    // Handing work to somebody who does not report to you is not a task, it is
    // a request — and nothing in the org chart says it can be made to stick.
    [Fact]
    public void NobodyElseMayPutWorkOnSomebodysList()
    {
        Assert.False(TaskItems.CanAssign(Guid.NewGuid(), Owner, managerOfAssignee: Manager));
    }

    [Fact]
    public void SomebodyWithNoManagerRecordedCanStillOnlyBeGivenWorkByThemselves()
    {
        Assert.False(TaskItems.CanAssign(Manager, Owner, managerOfAssignee: null));
        Assert.True(TaskItems.CanAssign(Owner, Owner, managerOfAssignee: null));
    }

    // ── the counts both dashboards show ────────────────────────────────────

    private static TaskFacts Facts(
        bool done,
        TaskDueState dueState = TaskDueState.NotDue,
        TaskSource source = TaskSource.Personal) =>
        new() { IsDone = done, DueState = dueState, Source = source };

    [Fact]
    public void CountsWhatIsDoneAndWhatIsLate()
    {
        TaskTally tally = TaskBoard.Of(
        [
            Facts(done: true),
            Facts(done: false, TaskDueState.Overdue),
            Facts(done: false, TaskDueState.Overdue),
            Facts(done: false, TaskDueState.DueSoon),
            Facts(done: false),
        ]);

        Assert.Equal(5, tally.Total);
        Assert.Equal(1, tally.Done);
        Assert.Equal(4, tally.Outstanding);
        Assert.Equal(2, tally.Overdue);
        Assert.Equal(1, tally.DueSoon);
    }

    // The two tabs are counted separately, because the self-service page shows
    // a total per tab and the admin shell shows only the personal one.
    [Fact]
    public void CountsTheTwoTabsSeparately()
    {
        TaskTally tally = TaskBoard.Of(
        [
            Facts(done: true, source: TaskSource.Assigned),
            Facts(done: false, source: TaskSource.Assigned),
            Facts(done: true, source: TaskSource.Personal),
        ]);

        Assert.Equal(2, tally.Assigned);
        Assert.Equal(1, tally.Personal);
        Assert.Equal(2, tally.Done);
    }

    [Fact]
    public void AnEmptyListCountsNothing()
    {
        TaskTally tally = TaskBoard.Of([]);

        Assert.Equal(0, tally.Total);
        Assert.Equal(0, tally.Overdue);
    }

    // A done task that was overdue is not still overdue, so the two figures
    // can never add up to more than the total.
    [Fact]
    public void NothingIsCountedAsBothDoneAndLate()
    {
        TaskTally tally = TaskBoard.Of([Facts(done: true, TaskDueState.NotDue)]);

        Assert.Equal(1, tally.Done);
        Assert.Equal(0, tally.Overdue);
        Assert.Equal(0, tally.Outstanding);
    }

    // ── reordering ──────────────────────────────────────────────────────────

    // A drag produces a whole new order, not a swap, so the ordering is taken
    // as the list the client ended up with.
    [Fact]
    public void ReorderingNumbersTheListInTheOrderGiven()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        Guid third = Guid.NewGuid();

        IReadOnlyDictionary<Guid, int> order =
            TaskItems.SequenceFor([third, first, second]);

        Assert.Equal(0, order[third]);
        Assert.Equal(1, order[first]);
        Assert.Equal(2, order[second]);
    }

    // A client that sends the same id twice, or an id that is not on the list,
    // must not be able to leave two tasks sharing a position.
    [Fact]
    public void ReorderingIgnoresARepeatedId()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        IReadOnlyDictionary<Guid, int> order =
            TaskItems.SequenceFor([first, second, first]);

        Assert.Equal(2, order.Count);
        Assert.Equal(0, order[first]);
        Assert.Equal(1, order[second]);
    }
}
