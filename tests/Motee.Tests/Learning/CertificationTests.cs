using Motee.Domain.Learning;

namespace Motee.Tests.Learning;

// Certificates, and whether a company is where it should be on the training it
// has made compulsory.
//
// A completion certificate's expiry is worked out from the course's own
// validity rather than typed: a certificate dated by hand is one somebody can
// get wrong, and on compliance training the expiry is the whole record.
//
// Written before the rules.
public class CertificationTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    // ── where a certificate stands ──────────────────────────────────────────

    [Fact]
    public void ACertificateWithNoExpiryNeverExpires()
    {
        Assert.Equal(CertStatus.NoExpiry, Certifications.StatusOf(null, Today));
    }

    [Fact]
    public void ACertificateWellInDateIsActive()
    {
        Assert.Equal(
            CertStatus.Active, Certifications.StatusOf(Today.AddDays(90), Today));
    }

    [Fact]
    public void ACertificateInsideSixtyDaysIsComingUp()
    {
        Assert.Equal(
            CertStatus.ExpiringInSixty, Certifications.StatusOf(Today.AddDays(45), Today));
    }

    [Fact]
    public void ACertificateInsideThirtyDaysIsUrgent()
    {
        Assert.Equal(
            CertStatus.ExpiringInThirty, Certifications.StatusOf(Today.AddDays(20), Today));
    }

    [Fact]
    public void ACertificatePastItsDateHasExpired()
    {
        Assert.Equal(
            CertStatus.Expired, Certifications.StatusOf(Today.AddDays(-1), Today));
    }

    // Expiring today has not expired: somebody can still renew it this
    // afternoon.
    [Fact]
    public void ACertificateExpiringTodayHasNotExpiredYet()
    {
        Assert.Equal(CertStatus.ExpiringInThirty, Certifications.StatusOf(Today, Today));
    }

    [Theory]
    [InlineData(30, CertStatus.ExpiringInThirty)]
    [InlineData(31, CertStatus.ExpiringInSixty)]
    [InlineData(60, CertStatus.ExpiringInSixty)]
    [InlineData(61, CertStatus.Active)]
    public void TheWindowsMeetWithoutOverlapping(int days, CertStatus expected)
    {
        Assert.Equal(expected, Certifications.StatusOf(Today.AddDays(days), Today));
    }

    [Fact]
    public void OnlyAnExpiredCertificateIsOutOfCompliance()
    {
        Assert.True(Certifications.IsCompliant(CertStatus.Active));
        Assert.True(Certifications.IsCompliant(CertStatus.ExpiringInThirty));
        Assert.True(Certifications.IsCompliant(CertStatus.NoExpiry));
        Assert.False(Certifications.IsCompliant(CertStatus.Expired));
    }

    // ── when a completion runs out ──────────────────────────────────────────

    // Derived from the course's validity rather than typed: on compliance
    // training the expiry is the record, and a hand-entered one is a record
    // somebody can get wrong.
    [Fact]
    public void ACompletionExpiresAfterTheCoursesOwnValidity()
    {
        Assert.Equal(
            new DateOnly(2027, 6, 15),
            Certifications.ExpiryFor(completedOn: Today, validityMonths: 12));
    }

    [Fact]
    public void ACourseWithNoValidityIssuesACertificateThatNeverExpires()
    {
        Assert.Null(Certifications.ExpiryFor(completedOn: Today, validityMonths: null));
        Assert.Null(Certifications.ExpiryFor(completedOn: Today, validityMonths: 0));
    }

    [Fact]
    public void ValidityLandsOnTheSameDayOfTheMonth()
    {
        Assert.Equal(
            new DateOnly(2026, 12, 15),
            Certifications.ExpiryFor(completedOn: Today, validityMonths: 6));
    }

    // A renewal dated from the end of a long month has to land somewhere real.
    [Fact]
    public void ValidityFromTheThirtyFirstLandsOnARealDate()
    {
        Assert.Equal(
            new DateOnly(2026, 2, 28),
            Certifications.ExpiryFor(new DateOnly(2025, 8, 31), validityMonths: 6));
    }

    // ── the register's figures ──────────────────────────────────────────────

    [Fact]
    public void AnEmptyRegisterIsFullyCompliant()
    {
        CertTally tally = Certifications.Tally([], Today);

        Assert.Equal(0, tally.Total);
        Assert.Equal(100, tally.CompliancePercent);
    }

    [Fact]
    public void CountsWhatIsInDateAndWhatIsNot()
    {
        CertTally tally = Certifications.Tally(
            [
                Today.AddDays(200),
                Today.AddDays(45),
                Today.AddDays(10),
                Today.AddDays(-5),
                null,
            ],
            Today);

        Assert.Equal(5, tally.Total);
        Assert.Equal(4, tally.Active);
        Assert.Equal(1, tally.Expired);
        Assert.Equal(1, tally.ExpiringInThirty);
        Assert.Equal(2, tally.ExpiringInSixty);
        Assert.Equal(80, tally.CompliancePercent);
    }

    // The client reports a status on a 30/60-day window and then counts the
    // register on a 30/90-day one, so a certificate reads "Active" in one place
    // and sits in the expiring column in another — the exact thing its own
    // comment says the shared rule exists to prevent.
    [Fact]
    public void TheCountsUseTheSameWindowsAsTheStatus()
    {
        CertTally tally = Certifications.Tally([Today.AddDays(75)], Today);

        Assert.Equal(CertStatus.Active, Certifications.StatusOf(Today.AddDays(75), Today));

        Assert.Equal(0, tally.ExpiringInThirty);
        Assert.Equal(0, tally.ExpiringInSixty);
        Assert.Equal(1, tally.Active);
    }

    [Fact]
    public void ACertificateWithNoExpiryIsNeverCountedAsExpiring()
    {
        CertTally tally = Certifications.Tally([null, null], Today);

        Assert.Equal(2, tally.Active);
        Assert.Equal(0, tally.ExpiringInSixty);
        Assert.Equal(100, tally.CompliancePercent);
    }

    // ── compulsory training ─────────────────────────────────────────────────

    private static readonly Guid Ada = Guid.NewGuid();
    private static readonly Guid Bola = Guid.NewGuid();
    private static readonly Guid Dpia = Guid.NewGuid();
    private static readonly Guid Safety = Guid.NewGuid();

    private static Learner Person(Guid id, string name, string department = "Engineering") =>
        new()
        {
            EmployeeId = id,
            Name = name,
            DepartmentName = department,
        };

    private static Assignment Done(Guid employeeId, Guid courseId) => new()
    {
        EmployeeId = employeeId,
        CourseId = courseId,
        Status = EnrollmentStatus.Completed,
    };

    private static Assignment Outstanding(
        Guid employeeId,
        Guid courseId,
        DateOnly? dueOn = null) => new()
        {
            EmployeeId = employeeId,
            CourseId = courseId,
            Status = EnrollmentStatus.InProgress,
            DueOn = dueOn,
        };

    [Fact]
    public void WithNothingCompulsoryEverybodyIsCompliant()
    {
        MandatoryTally tally = MandatoryTraining.Of(
            [Person(Ada, "Ada Okafor")], [], [], Today);

        Assert.Equal(0, tally.Required);
        Assert.Equal(100, tally.CompletionPercent);
        Assert.Empty(tally.Overdue);
    }

    // Every current employee has to complete every compulsory course, so the
    // requirement is the product of the two.
    [Fact]
    public void EverybodyNeedsEveryCompulsoryCourse()
    {
        MandatoryTally tally = MandatoryTraining.Of(
            [Person(Ada, "Ada Okafor"), Person(Bola, "Bola Okafor")],
            [new MandatoryCourse { CourseId = Dpia, Title = "Data protection" },
                new MandatoryCourse { CourseId = Safety, Title = "Health & safety" }],
            [],
            Today);

        Assert.Equal(4, tally.Required);
        Assert.Equal(0, tally.Completed);
        Assert.Equal(0, tally.CompletionPercent);
    }

    [Fact]
    public void CountsWhatHasActuallyBeenCompleted()
    {
        MandatoryTally tally = MandatoryTraining.Of(
            [Person(Ada, "Ada Okafor"), Person(Bola, "Bola Okafor")],
            [new MandatoryCourse { CourseId = Dpia, Title = "Data protection" }],
            [Done(Ada, Dpia)],
            Today);

        Assert.Equal(2, tally.Required);
        Assert.Equal(1, tally.Completed);
        Assert.Equal(50, tally.CompletionPercent);
    }

    // Somebody never enrolled at all is overdue, not merely missing: the
    // requirement does not wait for an administrator to create a row.
    [Fact]
    public void SomebodyNeverEnrolledIsAlreadyOverdue()
    {
        MandatoryTally tally = MandatoryTraining.Of(
            [Person(Ada, "Ada Okafor")],
            [new MandatoryCourse { CourseId = Dpia, Title = "Data protection" }],
            [],
            Today);

        Overdue overdue = Assert.Single(tally.Overdue);

        Assert.Equal(Ada, overdue.EmployeeId);
        Assert.Equal("Data protection", overdue.CourseTitle);
        Assert.Null(overdue.DueOn);
        Assert.Equal(1, tally.PeopleOverdue);
    }

    [Fact]
    public void AnAssignmentPastItsDateIsOverdue()
    {
        MandatoryTally tally = MandatoryTraining.Of(
            [Person(Ada, "Ada Okafor")],
            [new MandatoryCourse { CourseId = Dpia, Title = "Data protection" }],
            [Outstanding(Ada, Dpia, Today.AddDays(-1))],
            Today);

        Assert.Equal(Today.AddDays(-1), Assert.Single(tally.Overdue).DueOn);
    }

    [Fact]
    public void AnAssignmentStillInDateIsNotOverdue()
    {
        MandatoryTally tally = MandatoryTraining.Of(
            [Person(Ada, "Ada Okafor")],
            [new MandatoryCourse { CourseId = Dpia, Title = "Data protection" }],
            [Outstanding(Ada, Dpia, Today.AddDays(7))],
            Today);

        Assert.Empty(tally.Overdue);
    }

    [Fact]
    public void ACompletionIsNeverOverdueWhateverItsDate()
    {
        MandatoryTally tally = MandatoryTraining.Of(
            [Person(Ada, "Ada Okafor")],
            [new MandatoryCourse { CourseId = Dpia, Title = "Data protection" }],
            [Done(Ada, Dpia) with { DueOn = Today.AddDays(-100) }],
            Today);

        Assert.Empty(tally.Overdue);
        Assert.Equal(100, tally.CompletionPercent);
    }

    // Somebody enrolled twice, having failed once, is compliant if either
    // attempt completed.
    [Fact]
    public void ACompletionWinsOverAnEarlierFailure()
    {
        MandatoryTally tally = MandatoryTraining.Of(
            [Person(Ada, "Ada Okafor")],
            [new MandatoryCourse { CourseId = Dpia, Title = "Data protection" }],
            [
                new Assignment
                {
                    EmployeeId = Ada,
                    CourseId = Dpia,
                    Status = EnrollmentStatus.Failed,
                },
                Done(Ada, Dpia),
            ],
            Today);

        Assert.Equal(1, tally.Completed);
        Assert.Empty(tally.Overdue);
    }

    [Fact]
    public void ReportsEachDepartmentAndFlagsTheOnesBehind()
    {
        MandatoryTally tally = MandatoryTraining.Of(
            [
                Person(Ada, "Ada Okafor", "Engineering"),
                Person(Bola, "Bola Okafor", "Finance"),
            ],
            [new MandatoryCourse { CourseId = Dpia, Title = "Data protection" }],
            [Done(Ada, Dpia)],
            Today);

        DepartmentTally engineering =
            tally.Departments.First(row => row.DepartmentName == "Engineering");

        Assert.Equal(100, engineering.CompletionPercent);
        Assert.False(engineering.AtRisk);

        DepartmentTally finance =
            tally.Departments.First(row => row.DepartmentName == "Finance");

        Assert.Equal(0, finance.CompletionPercent);
        Assert.True(finance.AtRisk);
        Assert.Equal(1, tally.DepartmentsAtRisk);
    }

    [Fact]
    public void ADepartmentExactlyOnTheThresholdIsNotAtRisk()
    {
        IReadOnlyList<Learner> five =
        [
            .. Enumerable.Range(0, 5)
                .Select(index => Person(Guid.NewGuid(), $"Person {index}")),
        ];

        MandatoryTally tally = MandatoryTraining.Of(
            five,
            [new MandatoryCourse { CourseId = Dpia, Title = "Data protection" }],
            [.. five.Take(4).Select(person => Done(person.EmployeeId, Dpia))],
            Today);

        Assert.Equal(
            MandatoryTraining.AtRiskBelowPercent,
            tally.Departments[0].CompletionPercent);

        Assert.False(tally.Departments[0].AtRisk);
    }
}
