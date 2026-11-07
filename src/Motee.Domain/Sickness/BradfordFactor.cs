namespace Motee.Domain.Sickness;

// What the score suggests HR does next. Suggests: the Bradford Factor is a
// monitoring indicator and nothing here may act on it unprompted. An automatic
// sanction driven off a number that counts disability-related absence is a
// discrimination claim with an audit trail attached.
public enum BradfordAction
{
    Monitor,
    ManagerReview,
    FormalReview,
    SeniorHrReview,
}

public sealed record BradfordThreshold
{
    public required int Min { get; init; }

    public required string Label { get; init; }

    public required BradfordAction Action { get; init; }
}

public sealed record BradfordSettings
{
    public static readonly BradfordSettings Default = new();

    // 52 weeks is the convention. Held as a setting because some employers run
    // a shorter window, and a hardcoded year would quietly disagree with their
    // written policy.
    public int WindowWeeks { get; init; } = 52;

    // Off on the Friday and again on the Monday is one spell. Squaring the
    // spell count means getting this wrong moves a 10 to a 40.
    public bool MergeAcrossNonWorkingDays { get; init; } = true;

    public bool HalfDaysAsHalf { get; init; } = true;

    public bool HonourExclusions { get; init; } = true;

    public IReadOnlyList<BradfordThreshold> Thresholds { get; init; } =
    [
        new() { Min = 0, Label = "0–49", Action = BradfordAction.Monitor },
        new() { Min = 50, Label = "50–199", Action = BradfordAction.ManagerReview },
        new() { Min = 200, Label = "200–399", Action = BradfordAction.FormalReview },
        new() { Min = 400, Label = "400+", Action = BradfordAction.SeniorHrReview },
    ];

    public BradfordThreshold Band(decimal score)
    {
        BradfordThreshold? matched = Thresholds
            .OrderByDescending(threshold => threshold.Min)
            .FirstOrDefault(threshold => score >= threshold.Min);

        return matched ?? Default.Thresholds[0];
    }
}

public sealed record BradfordEpisode
{
    public required DateOnly Start { get; init; }

    public required DateOnly End { get; init; }

    // Qualifying days inside the window, which is not the whole spell when it
    // began before the window did.
    public required decimal Days { get; init; }

    public required IReadOnlyList<Guid> RecordIds { get; init; }

    public required bool Clipped { get; init; }
}

public sealed record BradfordExclusion
{
    public required Guid RecordId { get; init; }

    public required string Reason { get; init; }
}

public sealed record BradfordResult
{
    public required Guid EmployeeId { get; init; }

    public required DateOnly WindowStart { get; init; }

    public required DateOnly WindowEnd { get; init; }

    public required IReadOnlyList<BradfordEpisode> Episodes { get; init; }

    // S in B = S² × D.
    public required int Spells { get; init; }

    // D. Half-days make this fractional, so it is not an int.
    public required decimal Days { get; init; }

    public required decimal Score { get; init; }

    public required BradfordAction Action { get; init; }

    public required string Band { get; init; }

    // What was left out and why, so somebody querying a score can be shown the
    // answer rather than told to trust it.
    public required IReadOnlyList<BradfordExclusion> Excluded { get; init; }
}

// B = S² × D.
//
// Squaring the spell count is the point of the measure: five one-day absences
// disrupt a team more than one five-day absence. It also means a miscounted
// spell moves the result a long way, which is why the merging rules below are
// written out rather than left to a date library.
public static class BradfordFactor
{
    public static BradfordResult Calculate(
        Guid employeeId,
        IReadOnlyCollection<SicknessRecord> records,
        DateOnly asOf,
        BradfordSettings settings,
        IReadOnlySet<DayOfWeek> workingDays,
        IReadOnlySet<DateOnly> holidays)
    {
        DateOnly windowStart = asOf.AddDays(1 - (settings.WindowWeeks * 7));

        List<BradfordExclusion> excluded = [];
        Dictionary<DateOnly, Day> absent = [];

        foreach (SicknessRecord record in records)
        {
            if (record.EmployeeId != employeeId)
            {
                continue;
            }

            (DateOnly start, DateOnly end) = Absences.Span(record, asOf);

            // Outside the window entirely — not excluded, just not this year's
            // business, so it does not appear in the list of what was left out.
            if (end < windowStart || start > asOf)
            {
                continue;
            }

            if (Disqualify(record, settings) is string reason)
            {
                excluded.Add(new BradfordExclusion { RecordId = record.Id, Reason = reason });
                continue;
            }

            for (DateOnly day = start; day <= end; day = day.AddDays(1))
            {
                if (!Absences.IsWorked(day, workingDays, holidays))
                {
                    continue;
                }

                decimal weight = Absences.Weight(
                    record, day, start, end, settings.HalfDaysAsHalf);

                // Two records over the same day are one day off. Taking the
                // larger weight means a full-day record wins over a half-day
                // correction rather than the pair summing to 1.5.
                if (absent.TryGetValue(day, out Day? already))
                {
                    already.Weight = Math.Max(already.Weight, weight);
                    already.RecordIds.Add(record.Id);
                }
                else
                {
                    absent[day] = new Day(weight, [record.Id]);
                }
            }
        }

        List<BradfordEpisode> episodes = Episodes(
            absent, windowStart, asOf, settings, workingDays, holidays);

        int spells = episodes.Count;

        // To the nearest half day, because that is the smallest unit anything
        // here records, and a float's trailing digits in a published score
        // invite an argument nobody can settle.
        decimal days = Math.Round(
            episodes.Sum(episode => episode.Days) * 2m, MidpointRounding.AwayFromZero) / 2m;

        decimal score = Math.Round(spells * spells * days, 1, MidpointRounding.AwayFromZero);
        BradfordThreshold band = settings.Band(score);

        return new BradfordResult
        {
            EmployeeId = employeeId,
            WindowStart = windowStart,
            WindowEnd = asOf,
            Episodes = episodes,
            Spells = spells,
            Days = days,
            Score = score,
            Action = band.Action,
            Band = band.Label,
            Excluded = excluded,
        };
    }

    private static string? Disqualify(SicknessRecord record, BradfordSettings settings)
    {
        if (!SicknessLifecycle.Counts(record.ApprovalStatus))
        {
            return $"Absence {record.ApprovalStatus.ToString().ToLowerInvariant()}";
        }

        if (record.Kind == SicknessKind.MedicalProcedure)
        {
            return "Planned medical procedure";
        }

        if (!settings.HonourExclusions || record.ExclusionCategory is null)
        {
            return null;
        }

        return "Excluded by HR — "
            + (record.ExclusionReason ?? record.ExclusionCategory.ToString());
    }

    // Consecutive absent working days, grouped. A gap of nothing but
    // non-working days does not break a spell unless the employer says it
    // does; any worked day in between does.
    private static List<BradfordEpisode> Episodes(
        Dictionary<DateOnly, Day> absent,
        DateOnly windowStart,
        DateOnly windowEnd,
        BradfordSettings settings,
        IReadOnlySet<DayOfWeek> workingDays,
        IReadOnlySet<DateOnly> holidays)
    {
        List<BradfordEpisode> episodes = [];
        Open? current = null;

        foreach (DateOnly day in absent.Keys.Order())
        {
            Day entry = absent[day];
            bool inside = day >= windowStart && day <= windowEnd;

            if (current is not null
                && Continues(current.End, day, settings, workingDays, holidays))
            {
                current.End = day;
                current.Days += entry.Weight;

                if (inside)
                {
                    current.InWindow += entry.Weight;
                }

                current.RecordIds.UnionWith(entry.RecordIds);
                continue;
            }

            if (current is not null)
            {
                episodes.Add(Close(current, windowStart));
            }

            current = new Open(day, entry.Weight, inside ? entry.Weight : 0m, entry.RecordIds);
        }

        if (current is not null)
        {
            episodes.Add(Close(current, windowStart));
        }

        // A spell made entirely of days before the window contributes nothing,
        // and counting it would square an S that represents no lost time.
        return [.. episodes.Where(episode => episode.Days > 0m)];
    }

    private static bool Continues(
        DateOnly previous,
        DateOnly next,
        BradfordSettings settings,
        IReadOnlySet<DayOfWeek> workingDays,
        IReadOnlySet<DateOnly> holidays)
    {
        if (next.DayNumber - previous.DayNumber == 1)
        {
            return true;
        }

        if (!settings.MergeAcrossNonWorkingDays)
        {
            return false;
        }

        for (DateOnly day = previous.AddDays(1); day < next; day = day.AddDays(1))
        {
            if (Absences.IsWorked(day, workingDays, holidays))
            {
                return false;
            }
        }

        return true;
    }

    private static BradfordEpisode Close(Open open, DateOnly windowStart) => new()
    {
        Start = open.Start,
        End = open.End,
        Days = open.InWindow,
        RecordIds = [.. open.RecordIds],
        Clipped = open.InWindow != open.Days || open.Start < windowStart,
    };

    private sealed record Day(decimal Weight, HashSet<Guid> RecordIds)
    {
        public decimal Weight { get; set; } = Weight;
    }

    private sealed class Open(DateOnly start, decimal days, decimal inWindow, HashSet<Guid> ids)
    {
        public DateOnly Start { get; } = start;

        public DateOnly End { get; set; } = start;

        public decimal Days { get; set; } = days;

        public decimal InWindow { get; set; } = inWindow;

        public HashSet<Guid> RecordIds { get; } = [.. ids];
    }
}
