using System.Text.Json;
using System.Text.Json.Serialization;
using Motee.Domain.Recognition;

namespace Motee.Tests.Kudos;

// Kudos — peer recognition, the wall it appears on and the leaderboard over it.
//
// The client's type carries both `isPublic` and `isPrivate` and sets them from
// the same bit, plus a separate `isBroadcast` that can contradict either. These
// test one ordered visibility instead, which makes "a private kudos announced
// to the company" unrepresentable rather than merely unlikely.
public class KudosTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly Guid Sender = Guid.NewGuid();

    private static readonly Guid Recipient = Guid.NewGuid();

    private static readonly Guid Engineering = Guid.NewGuid();

    private const string Message = "Carried the release on her own over a bank holiday.";

    private static KudosPost Post(
        string message = Message,
        KudosType type = KudosType.Teamwork,
        CompanyValue value = CompanyValue.Collaboration,
        KudosVisibility visibility = KudosVisibility.Public,
        Guid? sender = null,
        Guid? recipient = null,
        Guid? department = null,
        string? customTypeName = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            SenderEmployeeId = sender ?? Sender,
            RecipientEmployeeId = recipient ?? (department is null ? Recipient : null),
            RecipientDepartmentId = department,
            KudosType = type,
            CustomTypeName = customTypeName,
            CompanyValue = value,
            Message = message,
            Visibility = visibility,
        };

    // ── the wire shape ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(KudosType.CustomerFocus, "\"customerFocus\"")]
    [InlineData(CompanyValue.CustomerFirst, "\"customerFirst\"")]
    [InlineData(ReactionType.Celebrate, "\"celebrate\"")]
    [InlineData(KudosVisibility.Broadcast, "\"broadcast\"")]
    [InlineData(KudosRecipientKind.Department, "\"department\"")]
    public void SerialisesAsCamelCase(object value, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(value, Web));
    }

    [Fact]
    public void CarriesEveryKudosTypeAndValueTheClientOffers()
    {
        Assert.Equal(7, Enum.GetValues<KudosType>().Length);
        Assert.Equal(5, Enum.GetValues<CompanyValue>().Length);
        Assert.Equal(6, Enum.GetValues<ReactionType>().Length);
    }

    // The client's union is individual | department | team, and its own comment
    // says team "reuses the department roster as its source list — there is no
    // separate team entity modelled anywhere else in the app". Two values for
    // one thing, so there are two here and `team` is accepted as a spelling of
    // department.
    [Fact]
    public void TeamIsADepartmentUnderAnotherName()
    {
        Assert.Equal(2, Enum.GetValues<KudosRecipientKind>().Length);

        Assert.True(KudosRecipients.TryParse("team", out KudosRecipientKind kind));
        Assert.Equal(KudosRecipientKind.Department, kind);
    }

    [Theory]
    [InlineData("individual", KudosRecipientKind.Individual)]
    [InlineData("department", KudosRecipientKind.Department)]
    [InlineData("Department", KudosRecipientKind.Department)]
    public void AcceptsTheRecipientKindsTheFormSends(string input, KudosRecipientKind expected)
    {
        Assert.True(KudosRecipients.TryParse(input, out KudosRecipientKind kind));
        Assert.Equal(expected, kind);
    }

    // ── what the form will not accept ───────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Well done")]
    public void RefusesAMessageTooShortToBeRecognition(string message)
    {
        Assert.Equal(KudosRefusal.MessageRequired, KudosPosts.Refusal(Post(message: message)));
    }

    [Fact]
    public void RefusesAMessageLongerThanTheFormAllows()
    {
        Assert.Equal(
            KudosRefusal.MessageTooLong,
            KudosPosts.Refusal(Post(message: new string('a', KudosPosts.LongestMessage + 1))));
    }

    [Fact]
    public void RefusesAKudosWithNobodyToReceiveIt()
    {
        KudosPost post = Post();

        post.RecipientEmployeeId = null;
        post.RecipientDepartmentId = null;

        Assert.Equal(KudosRefusal.RecipientRequired, KudosPosts.Refusal(post));
    }

    // One or the other, never both — a kudos addressed to a person and a
    // department at once has no answer to "whose wall is this on".
    [Fact]
    public void RefusesAKudosAddressedToBothAPersonAndADepartment()
    {
        KudosPost post = Post();

        post.RecipientDepartmentId = Engineering;

        Assert.Equal(KudosRefusal.RecipientAmbiguous, KudosPosts.Refusal(post));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void RefusesACustomTypeWithNoName(string? name)
    {
        Assert.Equal(
            KudosRefusal.CustomTypeNameRequired,
            KudosPosts.Refusal(Post(type: KudosType.Custom, customTypeName: name)));
    }

    [Fact]
    public void AcceptsACustomTypeThatIsNamed()
    {
        Assert.Equal(
            KudosRefusal.None,
            KudosPosts.Refusal(Post(type: KudosType.Custom, customTypeName: "Above and beyond")));
    }

    // Nothing in the client stops this, and a recognition wall where people
    // congratulate themselves is a wall nobody reads.
    [Fact]
    public void RefusesCongratulatingYourself()
    {
        Assert.Equal(
            KudosRefusal.CannotSendToSelf,
            KudosPosts.Refusal(Post(sender: Sender, recipient: Sender)));
    }

    [Fact]
    public void AcceptsAKudosToSomebodysOwnDepartment()
    {
        // Thanking your own team is ordinary, and the sender is not the
        // recipient of a department kudos in any meaningful sense.
        Assert.Equal(
            KudosRefusal.None,
            KudosPosts.Refusal(Post(sender: Sender, department: Engineering)));
    }

    // ── who it is addressed to ──────────────────────────────────────────────

    [Fact]
    public void AKudosToAPersonIsIndividual()
    {
        Assert.Equal(KudosRecipientKind.Individual, KudosPosts.RecipientKindOf(Post()));
    }

    [Fact]
    public void AKudosToADepartmentIsADepartmentKudos()
    {
        Assert.Equal(
            KudosRecipientKind.Department,
            KudosPosts.RecipientKindOf(Post(department: Engineering)));
    }

    // ── who can see it ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(KudosVisibility.Public)]
    [InlineData(KudosVisibility.Broadcast)]
    public void AnybodyCanSeeAKudosOnTheWall(KudosVisibility visibility)
    {
        Assert.True(KudosPosts.CanSee(
            Post(visibility: visibility), Guid.NewGuid(), viewerDepartmentId: null));
    }

    [Fact]
    public void OnlyTheTwoPeopleInvolvedCanSeeAPrivateKudos()
    {
        KudosPost post = Post(visibility: KudosVisibility.Private);

        Assert.True(KudosPosts.CanSee(post, Sender, viewerDepartmentId: null));
        Assert.True(KudosPosts.CanSee(post, Recipient, viewerDepartmentId: null));
        Assert.False(KudosPosts.CanSee(post, Guid.NewGuid(), viewerDepartmentId: null));
    }

    // A private kudos to a department is seen by that department, which is who
    // it was addressed to.
    [Fact]
    public void APrivateDepartmentKudosIsVisibleToThatDepartment()
    {
        KudosPost post = Post(visibility: KudosVisibility.Private, department: Engineering);

        Assert.True(KudosPosts.CanSee(post, Guid.NewGuid(), viewerDepartmentId: Engineering));
        Assert.False(KudosPosts.CanSee(post, Guid.NewGuid(), viewerDepartmentId: Guid.NewGuid()));
        Assert.True(KudosPosts.CanSee(post, Sender, viewerDepartmentId: null));
    }

    // Only the wall is announced. Broadcast beats public, public beats private,
    // and the three cannot contradict each other the way the client's two
    // booleans and a flag can.
    [Fact]
    public void OnlyABroadcastIsAnnounced()
    {
        Assert.True(KudosPosts.IsAnnounced(Post(visibility: KudosVisibility.Broadcast)));
        Assert.False(KudosPosts.IsAnnounced(Post(visibility: KudosVisibility.Public)));
        Assert.False(KudosPosts.IsAnnounced(Post(visibility: KudosVisibility.Private)));
    }

    [Theory]
    [InlineData("private", KudosVisibility.Private)]
    [InlineData("public", KudosVisibility.Public)]
    [InlineData("broadcast", KudosVisibility.Broadcast)]
    public void AcceptsTheVisibilityTheFormSends(string input, KudosVisibility expected)
    {
        Assert.True(KudosVisibilities.TryParse(input, out KudosVisibility visibility));
        Assert.Equal(expected, visibility);
    }

    // ── the wall's figures ──────────────────────────────────────────────────

    private static KudosFacts Facts(
        Guid senderId,
        Guid? recipientId,
        KudosType type = KudosType.Teamwork,
        CompanyValue value = CompanyValue.Collaboration,
        DateOnly? sentOn = null) =>
        new()
        {
            SenderEmployeeId = senderId,
            RecipientEmployeeId = recipientId,
            KudosType = type,
            CompanyValue = value,
            SentOn = sentOn ?? new DateOnly(2026, 4, 20),
        };

    private static readonly DateOnly Today = new(2026, 4, 23);

    [Fact]
    public void CountsThisMonthAgainstTheCompanysClock()
    {
        KudosTally tally = KudosWall.Of(
            [
                Facts(Sender, Recipient, sentOn: new DateOnly(2026, 4, 2)),
                Facts(Sender, Recipient, sentOn: new DateOnly(2026, 3, 30)),
            ],
            headcount: 10,
            asAt: Today);

        Assert.Equal(2, tally.Total);
        Assert.Equal(1, tally.ThisMonth);
    }

    // The card is labelled "Most Used Value" and the code behind it counts the
    // kudos type instead. Both are reported, so the screen can use whichever
    // it meant without the API having to guess.
    [Fact]
    public void ReportsTheCommonestTypeAndTheCommonestValueSeparately()
    {
        KudosTally tally = KudosWall.Of(
            [
                Facts(Sender, Recipient, KudosType.Teamwork, CompanyValue.Excellence),
                Facts(Sender, Recipient, KudosType.Teamwork, CompanyValue.Integrity),
                Facts(Sender, Recipient, KudosType.Growth, CompanyValue.Integrity),
            ],
            headcount: 10,
            asAt: Today);

        Assert.Equal(KudosType.Teamwork, tally.CommonestType);
        Assert.Equal(2, tally.CommonestTypeCount);
        Assert.Equal(CompanyValue.Integrity, tally.CommonestValue);
        Assert.Equal(2, tally.CommonestValueCount);
    }

    // The client divides by a hardcoded 156.
    [Fact]
    public void ParticipationIsOverTheRealHeadcount()
    {
        Guid other = Guid.NewGuid();

        KudosTally tally = KudosWall.Of(
            [Facts(Sender, Recipient), Facts(other, Recipient), Facts(Sender, Recipient)],
            headcount: 8,
            asAt: Today);

        Assert.Equal(2, tally.Givers);
        Assert.Equal(25, tally.ParticipationPercent);
    }

    [Fact]
    public void ParticipationIsZeroRatherThanInfiniteWithNoEmployees()
    {
        KudosTally tally = KudosWall.Of([], headcount: 0, asAt: Today);

        Assert.Equal(0, tally.ParticipationPercent);
        Assert.Null(tally.CommonestType);
    }

    // ── the leaderboard ─────────────────────────────────────────────────────

    [Fact]
    public void RanksByKudosReceived()
    {
        Guid ada = Guid.NewGuid();
        Guid bola = Guid.NewGuid();

        IReadOnlyList<KudosStanding> board = KudosWall.Leaderboard(
            [
                Facts(Sender, ada),
                Facts(Sender, ada),
                Facts(Sender, bola),
            ],
            asAt: Today,
            take: 10);

        // The sender is on the board too, on nil received — the client's own
        // filter keeps anybody who has either given or received.
        Assert.Equal([ada, bola], board.Take(2).Select(entry => entry.EmployeeId));
        Assert.Equal([1, 2], board.Take(2).Select(entry => entry.Rank));
        Assert.Equal(2, board[0].Received);
        Assert.Contains(board, entry => entry.EmployeeId == Sender && entry.Received == 0);
    }

    [Fact]
    public void CountsWhatSomebodyHasGivenAsWellAsReceived()
    {
        Guid ada = Guid.NewGuid();

        IReadOnlyList<KudosStanding> board = KudosWall.Leaderboard(
            [Facts(ada, Recipient), Facts(Sender, ada)],
            asAt: Today,
            take: 10);

        KudosStanding standing = board.First(entry => entry.EmployeeId == ada);

        Assert.Equal(1, standing.Received);
        Assert.Equal(1, standing.Sent);
    }

    // A department kudos has no individual recipient, so it counts towards the
    // sender's giving and nobody's receiving — otherwise one post would credit
    // every member and swamp the board.
    [Fact]
    public void ADepartmentKudosCreditsTheSenderAndNobodyElse()
    {
        IReadOnlyList<KudosStanding> board = KudosWall.Leaderboard(
            [Facts(Sender, recipientId: null)],
            asAt: Today,
            take: 10);

        KudosStanding standing = Assert.Single(board);

        Assert.Equal(Sender, standing.EmployeeId);
        Assert.Equal(1, standing.Sent);
        Assert.Equal(0, standing.Received);
    }

    [Fact]
    public void LeavesOutAnybodyWhoHasNeitherGivenNorReceived()
    {
        Assert.Empty(KudosWall.Leaderboard([], asAt: Today, take: 10));
    }

    [Fact]
    public void TakesOnlyTheTopFew()
    {
        List<KudosFacts> given =
        [
            .. Enumerable.Range(0, 5).Select(_ => Facts(Sender, Guid.NewGuid())),
        ];

        Assert.Equal(3, KudosWall.Leaderboard(given, asAt: Today, take: 3).Count);
    }

    // The client's leaderboard carries a streak and hardcodes it to zero, with
    // no definition anywhere. This is the definition: consecutive weeks, back
    // from the current one, in which the person gave at least one kudos. Stated
    // here so it can be corrected rather than guessed at.
    [Fact]
    public void AStreakIsConsecutiveWeeksOfGiving()
    {
        // Monday-anchored weeks. 23 April 2026 is a Thursday, so this week
        // starts on the 20th.
        List<KudosFacts> given =
        [
            Facts(Sender, Recipient, sentOn: new DateOnly(2026, 4, 21)),
            Facts(Sender, Recipient, sentOn: new DateOnly(2026, 4, 15)),
            Facts(Sender, Recipient, sentOn: new DateOnly(2026, 4, 8)),
        ];

        Assert.Equal(
            3,
            KudosWall.Leaderboard(given, asAt: Today, take: 10)
                .First(entry => entry.EmployeeId == Sender)
                .Streak);
    }

    [Fact]
    public void AStreakBreaksOnAMissedWeek()
    {
        List<KudosFacts> given =
        [
            Facts(Sender, Recipient, sentOn: new DateOnly(2026, 4, 21)),

            // Nothing in the week of the 13th.
            Facts(Sender, Recipient, sentOn: new DateOnly(2026, 4, 8)),
        ];

        Assert.Equal(
            1,
            KudosWall.Leaderboard(given, asAt: Today, take: 10)
                .First(entry => entry.EmployeeId == Sender)
                .Streak);
    }

    // Somebody who gave last week but not yet this week has not broken their
    // run — the week is not over.
    [Fact]
    public void AStreakSurvivesAWeekThatIsStillRunning()
    {
        List<KudosFacts> given =
        [
            Facts(Sender, Recipient, sentOn: new DateOnly(2026, 4, 15)),
            Facts(Sender, Recipient, sentOn: new DateOnly(2026, 4, 8)),
        ];

        Assert.Equal(
            2,
            KudosWall.Leaderboard(given, asAt: Today, take: 10)
                .First(entry => entry.EmployeeId == Sender)
                .Streak);
    }

    [Fact]
    public void SomebodyWhoHasOnlyReceivedHasNoStreak()
    {
        Assert.Equal(
            0,
            KudosWall.Leaderboard([Facts(Sender, Recipient)], asAt: Today, take: 10)
                .First(entry => entry.EmployeeId == Recipient)
                .Streak);
    }
}
