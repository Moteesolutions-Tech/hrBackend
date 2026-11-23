using Motee.Domain.Settings;

namespace Motee.Tests.Settings;

// What a company requires of a password.
//
// The screen had six controls and the backend had one fixed rule — eight
// characters, nothing else — applied to every tenant alike. A company that set
// a sixteen-character minimum with symbols still had "password" accepted.
//
// Written before the rules.
public class PasswordPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);

    // ── length ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheDefaultPolicyAsksForEightCharacters()
    {
        Assert.Equal(PasswordRefusal.TooShort, PasswordRules.Check(PasswordPolicy.Default, "short12"));
        Assert.Equal(PasswordRefusal.None, PasswordRules.Check(PasswordPolicy.Default, "longer12"));
    }

    [Fact]
    public void ALongerMinimumIsEnforced()
    {
        PasswordPolicy policy = PasswordPolicy.Default with { MinLength = 16 };

        Assert.Equal(PasswordRefusal.TooShort, PasswordRules.Check(policy, "twelvechars1"));
        Assert.Equal(PasswordRefusal.None, PasswordRules.Check(policy, "sixteencharacter"));
    }

    [Fact]
    public void NothingIsNotAPassword()
    {
        Assert.Equal(PasswordRefusal.TooShort, PasswordRules.Check(PasswordPolicy.Default, null));
    }

    // ── composition ────────────────────────────────────────────────────────

    [Fact]
    public void UppercaseIsAskedForWhenRequired()
    {
        PasswordPolicy policy = PasswordPolicy.Default with { RequireUppercase = true };

        Assert.Equal(PasswordRefusal.NeedsUppercase, PasswordRules.Check(policy, "lowercase1"));
        Assert.Equal(PasswordRefusal.None, PasswordRules.Check(policy, "Lowercase1"));
    }

    [Fact]
    public void ANumberIsAskedForWhenRequired()
    {
        PasswordPolicy policy = PasswordPolicy.Default with { RequireNumbers = true };

        Assert.Equal(PasswordRefusal.NeedsNumber, PasswordRules.Check(policy, "noDigitsHere"));
        Assert.Equal(PasswordRefusal.None, PasswordRules.Check(policy, "oneDigit1"));
    }

    [Fact]
    public void ASymbolIsAskedForWhenRequired()
    {
        PasswordPolicy policy = PasswordPolicy.Default with { RequireSymbols = true };

        Assert.Equal(PasswordRefusal.NeedsSymbol, PasswordRules.Check(policy, "lettersAnd1"));
        Assert.Equal(PasswordRefusal.None, PasswordRules.Check(policy, "lettersAnd!"));
    }

    // The screen says "e.g. @, #, !, $" — an example, not a list. A policy that
    // rejects £ because somebody wrote out their favourite punctuation is a
    // policy that argues with its own users.
    [Theory]
    [InlineData("password£1")]
    [InlineData("password 1")]
    [InlineData("password–1")]
    [InlineData("password€1")]
    public void AnythingThatIsNotALetterOrDigitCounts(string candidate)
    {
        PasswordPolicy policy = PasswordPolicy.Default with { RequireSymbols = true };

        Assert.Equal(PasswordRefusal.None, PasswordRules.Check(policy, candidate));
    }

    // Worked down in the order the form lists them, so somebody fixing one
    // complaint at a time works through the form rather than around it.
    [Fact]
    public void LengthIsComplainedAboutBeforeComposition()
    {
        PasswordPolicy policy = PasswordPolicy.Default with
        {
            MinLength = 12,
            RequireUppercase = true,
            RequireNumbers = true,
            RequireSymbols = true,
        };

        Assert.Equal(PasswordRefusal.TooShort, PasswordRules.Check(policy, "short"));
    }

    [Fact]
    public void AllTheRequirementsTogetherStillPass()
    {
        PasswordPolicy policy = PasswordPolicy.Default with
        {
            MinLength = 12,
            RequireUppercase = true,
            RequireNumbers = true,
            RequireSymbols = true,
        };

        Assert.Equal(PasswordRefusal.None, PasswordRules.Check(policy, "Correct-Horse1"));
    }

    // ── bounds ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2, PasswordPolicy.MinimumLength)]
    [InlineData(99, PasswordPolicy.MaximumLength)]
    [InlineData(16, 16)]
    public void AMinimumLengthIsBroughtInsideTheSlider(int given, int expected)
    {
        Assert.Equal(
            expected,
            PasswordRules.Clamp(PasswordPolicy.Default with { MinLength = given }).MinLength);
    }

    [Theory]
    [InlineData(1, PasswordPolicy.MinimumSessionMinutes)]
    [InlineData(100_000, PasswordPolicy.MaximumSessionMinutes)]
    [InlineData(60, 60)]
    public void ASessionTimeoutIsBroughtInsideTheOptions(int given, int expected)
    {
        Assert.Equal(
            expected,
            PasswordRules.Clamp(PasswordPolicy.Default with { SessionTimeoutMinutes = given })
                .SessionTimeoutMinutes);
    }

    [Fact]
    public void NegativeExpiryAndReuseBecomeOff()
    {
        PasswordPolicy clamped = PasswordRules.Clamp(
            PasswordPolicy.Default with { ExpiryDays = -30, PreventReuse = -5 });

        Assert.Equal(0, clamped.ExpiryDays);
        Assert.Equal(0, clamped.PreventReuse);
    }

    // ── expiry ─────────────────────────────────────────────────────────────

    [Fact]
    public void WithNoExpiryAPasswordNeverLapses()
    {
        Assert.False(PasswordRules.HasExpired(
            PasswordPolicy.Default, Now.AddYears(-10), Now));
    }

    [Fact]
    public void APasswordOlderThanTheExpiryHasLapsed()
    {
        PasswordPolicy policy = PasswordPolicy.Default with { ExpiryDays = 90 };

        Assert.True(PasswordRules.HasExpired(policy, Now.AddDays(-91), Now));
    }

    [Fact]
    public void APasswordInsideTheExpiryHasNot()
    {
        PasswordPolicy policy = PasswordPolicy.Default with { ExpiryDays = 90 };

        Assert.False(PasswordRules.HasExpired(policy, Now.AddDays(-89), Now));
    }

    [Fact]
    public void APasswordExactlyAtTheExpiryHasLapsed()
    {
        PasswordPolicy policy = PasswordPolicy.Default with { ExpiryDays = 90 };

        Assert.True(PasswordRules.HasExpired(policy, Now.AddDays(-90), Now));
    }

    // The day a company first saves a 90-day expiry, nobody's password has a
    // recorded change date. Treating that as lapsed would lock the whole company
    // out at once, which is not what they asked for.
    [Fact]
    public void APasswordWithNoRecordedChangeDateHasNotLapsed()
    {
        PasswordPolicy policy = PasswordPolicy.Default with { ExpiryDays = 90 };

        Assert.False(PasswordRules.HasExpired(policy, null, Now));
    }

    // ── reuse ──────────────────────────────────────────────────────────────

    [Fact]
    public void NoReusePolicyKeepsNoHistory()
    {
        Assert.Equal(0, PasswordRules.HistoryDepth(PasswordPolicy.Default));
    }

    [Fact]
    public void TheHistoryKeptIsExactlyWhatThePolicyForbids()
    {
        Assert.Equal(
            5, PasswordRules.HistoryDepth(PasswordPolicy.Default with { PreventReuse = 5 }));
    }

    [Fact]
    public void TheHistoryKeptHasACeiling()
    {
        Assert.Equal(
            PasswordPolicy.MaximumReuseHistory,
            PasswordRules.HistoryDepth(PasswordPolicy.Default with { PreventReuse = 500 }));
    }
}
