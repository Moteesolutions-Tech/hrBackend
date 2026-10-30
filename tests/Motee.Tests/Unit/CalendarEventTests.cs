using Motee.Domain.Calendar;

namespace Motee.Tests.Unit;

// What a calendar event has to be before it is worth storing.
//
// The frontend's dialog already refuses most of these, which is the reason to
// hold them here as well rather than instead: it is one of two clients, and the
// API is reachable without either.
public class CalendarEventTests
{
    private static readonly DateOnly Monday = new(2026, 6, 15);

    private static CalendarEvent Draft(
        string? title = "All-hands",
        DateOnly? date = null,
        DateOnly? endDate = null,
        bool allDay = true,
        TimeOnly? startTime = null,
        TimeOnly? endTime = null,
        CalendarCategory category = CalendarCategory.Meeting,
        CalendarAudience audience = CalendarAudience.Company) => new()
        {
            Id = Guid.NewGuid(),
            Title = title!,
            Date = date ?? Monday,
            EndDate = endDate,
            AllDay = allDay,
            StartTime = startTime,
            EndTime = endTime,
            Category = category,
            Audience = audience,
        };

    private static TimeOnly At(int hour, int minute = 0) => new(hour, minute);

    [Fact]
    public void AnAllDayCompanyEventIsAccepted() =>
        Assert.Equal(CalendarRefusal.None, CalendarEvents.Refusal(Draft(), []));

    [Fact]
    public void ATimedEventIsAccepted() =>
        Assert.Equal(
            CalendarRefusal.None,
            CalendarEvents.Refusal(
                Draft(allDay: false, startTime: At(9), endTime: At(10)), []));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnEventWithoutATitleIsRefused(string? title) =>
        Assert.Equal(
            CalendarRefusal.TitleRequired, CalendarEvents.Refusal(Draft(title: title), []));

    [Fact]
    public void AnEventEndingBeforeItStartsIsRefused() =>
        Assert.Equal(
            CalendarRefusal.EndsBeforeItStarts,
            CalendarEvents.Refusal(Draft(endDate: Monday.AddDays(-1)), []));

    [Fact]
    public void AnEventEndingOnItsStartDayIsFine() =>
        Assert.Equal(CalendarRefusal.None, CalendarEvents.Refusal(Draft(endDate: Monday), []));

    [Fact]
    public void ATimedEventEndingBeforeItStartsIsRefused() =>
        Assert.Equal(
            CalendarRefusal.EndTimeNotAfterStart,
            CalendarEvents.Refusal(
                Draft(allDay: false, startTime: At(14), endTime: At(13)), []));

    [Fact]
    public void ATimedEventWithNoLengthIsRefused() =>
        Assert.Equal(
            CalendarRefusal.EndTimeNotAfterStart,
            CalendarEvents.Refusal(
                Draft(allDay: false, startTime: At(14), endTime: At(14)), []));

    // A meeting that starts on Monday afternoon and ends Tuesday morning is a
    // real thing. Only the single-day case can compare clock times.
    [Fact]
    public void AnOvernightEventIsNotJudgedOnItsClockTimes() =>
        Assert.Equal(
            CalendarRefusal.None,
            CalendarEvents.Refusal(
                Draft(
                    endDate: Monday.AddDays(1),
                    allDay: false,
                    startTime: At(18),
                    endTime: At(9)),
                []));

    [Fact]
    public void ATimedEventWithoutAStartIsRefused() =>
        Assert.Equal(
            CalendarRefusal.StartTimeRequired,
            CalendarEvents.Refusal(Draft(allDay: false, endTime: At(10)), []));

    [Fact]
    public void AnAllDayEventIgnoresAnyTimesSentWithIt() =>
        Assert.Equal(
            CalendarRefusal.None,
            CalendarEvents.Refusal(
                Draft(allDay: true, startTime: At(14), endTime: At(13)), []));

    // ── who it is for ───────────────────────────────────────────────────────

    [Fact]
    public void APeopleEventNeedsSomebodyToPutItOn() =>
        Assert.Equal(
            CalendarRefusal.NobodyToAddItTo,
            CalendarEvents.Refusal(Draft(audience: CalendarAudience.People), []));

    [Fact]
    public void APeopleEventWithAttendeesIsAccepted() =>
        Assert.Equal(
            CalendarRefusal.None,
            CalendarEvents.Refusal(
                Draft(audience: CalendarAudience.People), [Guid.NewGuid()]));

    // Dropping the list silently would leave somebody certain they had invited
    // five people to a thing that went out to the whole company.
    [Fact]
    public void ACompanyEventWithAGuestListIsRefused() =>
        Assert.Equal(
            CalendarRefusal.CompanyEventTakesNoAttendees,
            CalendarEvents.Refusal(Draft(), [Guid.NewGuid()]));

    // ── which categories a person may pick ──────────────────────────────────

    [Theory]
    [InlineData(CalendarCategory.Meeting)]
    [InlineData(CalendarCategory.OneToOne)]
    [InlineData(CalendarCategory.Company)]
    [InlineData(CalendarCategory.Deadline)]
    [InlineData(CalendarCategory.Reminder)]
    [InlineData(CalendarCategory.Training)]
    [InlineData(CalendarCategory.Performance)]
    [InlineData(CalendarCategory.Personal)]
    public void TheChoosableCategoriesAreAccepted(CalendarCategory category) =>
        Assert.True(CalendarCategories.Choosable(category));

    // These are facts about other records — a holiday, a birth date, an approved
    // leave request. Writing one by hand would put a leave day on the calendar
    // with no leave behind it, and the balance would never know.
    [Theory]
    [InlineData(CalendarCategory.Holiday)]
    [InlineData(CalendarCategory.Birthday)]
    [InlineData(CalendarCategory.Anniversary)]
    [InlineData(CalendarCategory.Leave)]
    [InlineData(CalendarCategory.LeaveRequest)]
    [InlineData(CalendarCategory.Cover)]
    [InlineData(CalendarCategory.Interview)]
    [InlineData(CalendarCategory.Review)]
    public void ADerivedCategoryCannotBeChosen(CalendarCategory category)
    {
        Assert.False(CalendarCategories.Choosable(category));
        Assert.Equal(
            CalendarRefusal.CategoryNotChoosable,
            CalendarEvents.Refusal(Draft(category: category), []));
    }

    [Fact]
    public void AnEventRunningLongerThanAYearIsRefused() =>
        Assert.Equal(
            CalendarRefusal.LongerThanAYear,
            CalendarEvents.Refusal(Draft(endDate: Monday.AddYears(2)), []));

    // ── the window a view asks for ──────────────────────────────────────────

    [Fact]
    public void ASingleDayEventSpansOnlyItsOwnDay()
    {
        Assert.True(CalendarEvents.Spans(Monday, null, Monday));
        Assert.False(CalendarEvents.Spans(Monday, null, Monday.AddDays(1)));
    }

    [Fact]
    public void AMultiDayEventSpansEveryDayInclusive()
    {
        DateOnly friday = Monday.AddDays(4);

        Assert.True(CalendarEvents.Spans(Monday, friday, Monday));
        Assert.True(CalendarEvents.Spans(Monday, friday, Monday.AddDays(2)));
        Assert.True(CalendarEvents.Spans(Monday, friday, friday));
        Assert.False(CalendarEvents.Spans(Monday, friday, friday.AddDays(1)));
    }

    // A fortnight of leave straddling the edge of the month shows in that month,
    // which is the whole reason the week view is not just a day filter.
    [Fact]
    public void AnEventOverlappingTheWindowAtEitherEdgeIsInIt()
    {
        DateOnly from = Monday;
        DateOnly to = Monday.AddDays(6);

        Assert.True(CalendarEvents.Overlaps(Monday.AddDays(-3), Monday, from, to));
        Assert.True(CalendarEvents.Overlaps(to, to.AddDays(5), from, to));
        Assert.True(CalendarEvents.Overlaps(Monday.AddDays(-9), to.AddDays(9), from, to));
        Assert.False(CalendarEvents.Overlaps(Monday.AddDays(-9), Monday.AddDays(-1), from, to));
        Assert.False(CalendarEvents.Overlaps(to.AddDays(1), null, from, to));
    }

    // ── which pane it belongs in ────────────────────────────────────────────

    [Fact]
    public void AHolidayBelongsToTheHolidaysLayer() =>
        Assert.Equal(
            CalendarLayer.Holidays,
            CalendarEvents.LayerOf(CalendarCategory.Holiday, CalendarAudience.Company));

    [Fact]
    public void ACompanyEventBelongsToTheCompanyLayer() =>
        Assert.Equal(
            CalendarLayer.Company,
            CalendarEvents.LayerOf(CalendarCategory.Meeting, CalendarAudience.Company));

    // Birthdays and anniversaries are about a person but belong to everyone, so
    // they sit with the company rather than on one person's schedule — hiding
    // your own calendar should not hide the office birthdays.
    [Theory]
    [InlineData(CalendarCategory.Birthday)]
    [InlineData(CalendarCategory.Anniversary)]
    public void CelebrationsBelongToTheCompanyLayer(CalendarCategory category) =>
        Assert.Equal(
            CalendarLayer.Company,
            CalendarEvents.LayerOf(category, CalendarAudience.Company));

    [Fact]
    public void AnythingOnAPersonBelongsToTheirSchedule() =>
        Assert.Equal(
            CalendarLayer.Schedule,
            CalendarEvents.LayerOf(CalendarCategory.Leave, CalendarAudience.People));
}
