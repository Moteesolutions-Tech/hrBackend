using System.Text.Json;
using System.Text.Json.Serialization;
using Motee.Domain.Checklists;

namespace Motee.Tests.Checklists;

// The onboarding checklist: the company's task template, and how far each new
// starter has got through it.
//
// The frontend carries two incompatible vocabularies for when a task is due —
// the type declares on_start | same_day | day_1 … day_90, while the form that
// writes it offers before_start | on_start | after_start with a separate
// number. Both are reaching for one signed offset from the start date, and
// that is what these test: negative is before somebody arrives, zero is their
// first day, positive is after.
public class ChecklistTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly DateOnly Started = new(2026, 1, 15);

    private static readonly DateOnly Today = new(2026, 2, 1);

    private static ChecklistItem Item(
        string title = "Set up workstation and accounts",
        int offset = 1,
        bool required = true,
        bool active = true,
        ResponsibleParty party = ResponsibleParty.It,
        string? category = "IT Setup") =>
        new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = "Configure laptop, email and the access they need.",
            Category = category,
            ResponsibleParty = party,
            DueDayOffset = offset,
            IsRequired = required,
            IsActive = active,
        };

    // ── the wire shape ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(ResponsibleParty.Hr, "\"hr\"")]
    [InlineData(ResponsibleParty.It, "\"it\"")]
    [InlineData(ResponsibleParty.Finance, "\"finance\"")]
    [InlineData(ChecklistTaskState.Overdue, "\"overdue\"")]
    [InlineData(ChecklistProgressState.InProgress, "\"inProgress\"")]
    public void SerialisesAsCamelCase(object value, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(value, Web));
    }

    // The form's own select offers only four. The type, the labels and the
    // seeded template all carry Finance as well — "submit bank details to
    // Finance" is one of the eight tasks the client shipped — so the API keeps
    // all five and the form is the thing that is short.
    [Fact]
    public void KeepsEveryResponsiblePartyTheDataUses()
    {
        Assert.Equal(5, Enum.GetValues<ResponsibleParty>().Length);
    }

    // ── when a task is due ──────────────────────────────────────────────────

    [Fact]
    public void ATaskOnTheFirstDayIsDueOnTheStartDate()
    {
        Assert.Equal(Started, ChecklistRules.DueOn(Item(offset: 0), Started));
    }

    [Fact]
    public void APositiveOffsetCountsForwardFromTheStartDate()
    {
        Assert.Equal(new DateOnly(2026, 4, 15), ChecklistRules.DueOn(Item(offset: 90), Started));
    }

    // The form's "before start" case: an offer letter signed, a laptop ordered.
    [Fact]
    public void ANegativeOffsetFallsBeforeSomebodyArrives()
    {
        Assert.Equal(new DateOnly(2026, 1, 8), ChecklistRules.DueOn(Item(offset: -7), Started));
    }

    // Nothing to count from. Reporting a due date here would invent one, and
    // reporting the task as overdue would blame somebody for a date nobody set.
    [Fact]
    public void ATaskHasNoDueDateUntilTheStartDateIsKnown()
    {
        Assert.Null(ChecklistRules.DueOn(Item(), null));
    }

    [Theory]
    [InlineData(-400)]
    [InlineData(400)]
    public void RefusesAnOffsetBeyondAnyRealOnboarding(int offset)
    {
        Assert.Equal(
            ChecklistRefusal.OffsetOutOfRange,
            ChecklistRules.Refusal(Item(offset: offset)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a")]
    public void RefusesATaskNameTooShortToActOn(string title)
    {
        Assert.Equal(ChecklistRefusal.TitleRequired, ChecklistRules.Refusal(Item(title: title)));
    }

    [Fact]
    public void RefusesATaskNameLongerThanTheFormAllows()
    {
        Assert.Equal(
            ChecklistRefusal.TitleTooLong,
            ChecklistRules.Refusal(Item(title: new string('a', ChecklistRules.LongestTitle + 1))));
    }

    [Fact]
    public void AcceptsATaskTheFormWouldAccept()
    {
        Assert.Equal(ChecklistRefusal.None, ChecklistRules.Refusal(Item()));
    }

    // ── one task's state ────────────────────────────────────────────────────

    [Fact]
    public void ADoneTaskIsDone()
    {
        Assert.Equal(
            ChecklistTaskState.Done,
            ChecklistRules.StateOf(Item(), Started, new DateOnly(2026, 1, 16), Today));
    }

    [Fact]
    public void AnUndoneTaskPastItsDateIsOverdue()
    {
        Assert.Equal(
            ChecklistTaskState.Overdue,
            ChecklistRules.StateOf(Item(offset: 1), Started, null, Today));
    }

    [Fact]
    public void AnUndoneTaskStillAheadIsOutstanding()
    {
        Assert.Equal(
            ChecklistTaskState.Outstanding,
            ChecklistRules.StateOf(Item(offset: 90), Started, null, Today));
    }

    [Fact]
    public void ATaskDueTodayIsNotYetOverdue()
    {
        Assert.Equal(
            ChecklistTaskState.Outstanding,
            ChecklistRules.StateOf(Item(offset: 17), Started, null, Today));
    }

    // Overdue needs a date to be late against, and there is none.
    [Fact]
    public void AnUndoneTaskForSomebodyWithNoStartDateIsOutstandingNotOverdue()
    {
        Assert.Equal(
            ChecklistTaskState.Outstanding,
            ChecklistRules.StateOf(Item(), null, null, Today));
    }

    // Lateness is about the deadline, not about when it was eventually done.
    [Fact]
    public void ATaskDoneLateIsStillDone()
    {
        Assert.Equal(
            ChecklistTaskState.Done,
            ChecklistRules.StateOf(Item(offset: 1), Started, new DateOnly(2026, 1, 31), Today));
    }

    // ── a starter's progress ────────────────────────────────────────────────

    private static ChecklistProgress ProgressOf(
        IReadOnlyList<ChecklistTaskFacts> tasks) =>
        ChecklistRules.Progress(tasks);

    private static ChecklistTaskFacts Task(
        ChecklistTaskState state,
        bool required = true) =>
        new() { State = state, IsRequired = required };

    [Fact]
    public void NothingDoneIsNotStarted()
    {
        ChecklistProgress progress = ProgressOf(
            [Task(ChecklistTaskState.Outstanding), Task(ChecklistTaskState.Outstanding)]);

        Assert.Equal(ChecklistProgressState.NotStarted, progress.State);
        Assert.Equal(0, progress.Completed);
        Assert.Equal(2, progress.Total);
    }

    [Fact]
    public void SomethingDoneAndNothingLateIsInProgress()
    {
        ChecklistProgress progress = ProgressOf(
            [Task(ChecklistTaskState.Done), Task(ChecklistTaskState.Outstanding)]);

        Assert.Equal(ChecklistProgressState.InProgress, progress.State);
        Assert.Equal(1, progress.Completed);
    }

    // A missed required task is the thing the screen needs to shout about, so
    // it beats "in progress" however much else has been done.
    [Fact]
    public void AMissedRequiredTaskMakesTheWholeChecklistOverdue()
    {
        ChecklistProgress progress = ProgressOf(
            [Task(ChecklistTaskState.Done), Task(ChecklistTaskState.Overdue)]);

        Assert.Equal(ChecklistProgressState.Overdue, progress.State);
    }

    // An optional 30-day check-in nobody booked is not a compliance problem.
    // With every required task done the checklist is finished, and the missed
    // optional one is reported in the counts rather than in the badge.
    [Fact]
    public void AMissedOptionalTaskDoesNotMakeTheChecklistOverdue()
    {
        ChecklistProgress progress = ProgressOf(
        [
            Task(ChecklistTaskState.Done),
            Task(ChecklistTaskState.Overdue, required: false),
        ]);

        Assert.Equal(ChecklistProgressState.Completed, progress.State);
        Assert.Equal(1, progress.Overdue);
    }

    // With a required task still to do, a missed optional one leaves the
    // checklist in progress rather than overdue.
    [Fact]
    public void AMissedOptionalTaskLeavesAnUnfinishedChecklistInProgress()
    {
        ChecklistProgress progress = ProgressOf(
        [
            Task(ChecklistTaskState.Done),
            Task(ChecklistTaskState.Outstanding),
            Task(ChecklistTaskState.Overdue, required: false),
        ]);

        Assert.Equal(ChecklistProgressState.InProgress, progress.State);
    }

    [Fact]
    public void EveryRequiredTaskDoneIsComplete()
    {
        ChecklistProgress progress = ProgressOf(
            [Task(ChecklistTaskState.Done), Task(ChecklistTaskState.Done)]);

        Assert.Equal(ChecklistProgressState.Completed, progress.State);
        Assert.Equal(0, progress.RequiredOutstanding);
    }

    // The badge answers "is onboarding finished", which is about the required
    // tasks; the counts drive the progress bar, which the client draws over
    // every task. Both are reported so the two never have to be reconciled by
    // guesswork.
    [Fact]
    public void CompleteOnTheRequiredTasksStillReportsTheOptionalOnesOutstanding()
    {
        ChecklistProgress progress = ProgressOf(
        [
            Task(ChecklistTaskState.Done),
            Task(ChecklistTaskState.Outstanding, required: false),
        ]);

        Assert.Equal(ChecklistProgressState.Completed, progress.State);
        Assert.Equal(1, progress.Completed);
        Assert.Equal(2, progress.Total);
        Assert.Equal(0, progress.RequiredOutstanding);
    }

    [Fact]
    public void AnEmptyTemplateIsNotAnUnfinishedChecklist()
    {
        ChecklistProgress progress = ProgressOf([]);

        Assert.Equal(ChecklistProgressState.Completed, progress.State);
        Assert.Equal(0, progress.Total);
    }

    [Fact]
    public void CountsHowManyAreOverdueSoTheListCanBeRanked()
    {
        ChecklistProgress progress = ProgressOf(
        [
            Task(ChecklistTaskState.Overdue),
            Task(ChecklistTaskState.Overdue),
            Task(ChecklistTaskState.Done),
        ]);

        Assert.Equal(2, progress.Overdue);
        Assert.Equal(2, progress.RequiredOutstanding);
    }

    // ── the template a new tenant starts with ───────────────────────────────

    [Fact]
    public void SeedsTheEightTasksTheClientShipped()
    {
        Assert.Equal(8, ChecklistSeeds.All.Count);
    }

    [Fact]
    public void EverySeededTaskPassesItsOwnValidation()
    {
        Assert.All(ChecklistSeeds.All, seed =>
        {
            ChecklistItem item = Item(
                title: seed.Title,
                offset: seed.DueDayOffset,
                required: seed.IsRequired,
                party: seed.ResponsibleParty,
                category: seed.Category);

            Assert.Equal(ChecklistRefusal.None, ChecklistRules.Refusal(item));
        });
    }

    // Process steps every company does, asserting no contract and no figure —
    // which is why these are seeded live rather than as drafts the way the
    // benefits catalogue is.
    [Fact]
    public void TheSeededTemplateRunsFromBeforeTheStartDateToProbation()
    {
        Assert.Equal(-7, ChecklistSeeds.All.Min(seed => seed.DueDayOffset));
        Assert.Equal(90, ChecklistSeeds.All.Max(seed => seed.DueDayOffset));
    }

    [Fact]
    public void TheSeededTemplateSpreadsTheWorkAcrossTheTeamsThatDoIt()
    {
        Assert.Equal(
            [
                ResponsibleParty.Hr,
                ResponsibleParty.Manager,
                ResponsibleParty.It,
                ResponsibleParty.Employee,
                ResponsibleParty.Finance,
            ],
            ChecklistSeeds.All
                .Select(seed => seed.ResponsibleParty)
                .Distinct()
                .Order());
    }
}
