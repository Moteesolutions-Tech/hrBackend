using Motee.Domain.Common;

namespace Motee.Domain.Organisation;

// Where a post stands. Derived from who holds it and what is being recruited
// into it, never stored.
//
// The frontend's own list has four values — active, vacant, inactive, filled —
// where "active" and "filled" cannot be told apart by any rule over the data.
// These four can: a post is retired, fully staffed, being hired into, or
// sitting empty with nobody looking.
public enum PositionStatus
{
    Filled,
    Recruiting,
    Vacant,
    Inactive,
}

// A post the company has, as distinct from the person in it.
//
// The establishment is what finance and HR argue from — how many of each post
// exist, how many are filled, what is still to hire — so it has to agree with
// the roster underneath it. That is why the status is derived and the
// complement is stored: the first changes when people move, the second only
// when somebody decides.
public class Position : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Title { get; set; }

    public Guid? DepartmentId { get; set; }

    public string? Grade { get; set; }

    public string? Description { get; set; }

    // How many of this post exist. One unless somebody says otherwise — three
    // backend engineers is one post with a complement of three, not three
    // posts, and keeping it that way is what makes "two still to hire"
    // answerable.
    public int Headcount { get; set; } = 1;

    // Retired rather than deleted, because employees and vacancies point at
    // it and history has to keep reading correctly.
    public bool IsActive { get; set; } = true;

    public int Sequence { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum PositionRefusal
{
    None,
    TitleRequired,

    // A post for nobody is not a post. Retiring it is what "no longer
    // needed" means, and that is a different field.
    HeadcountNotPositive,

    // Almost always a mistyped figure.
    HeadcountTooLarge,

    // Two posts with one title in one department is two answers to "which is
    // Sarah in".
    TitleTakenInDepartment,

    // Retiring or deleting a post people are currently in.
    InUse,
}

public static class Positions
{
    private const int LargestComplement = 1_000;

    public static PositionStatus Status(
        bool isActive,
        int assigned,
        int recruiting,
        int headcount)
    {
        if (!isActive)
        {
            return PositionStatus.Inactive;
        }

        // Over establishment is still filled. A handover overlap or a
        // transfer in is a real situation, and reporting it as vacant would
        // be absurd.
        if (assigned >= Math.Max(1, headcount))
        {
            return PositionStatus.Filled;
        }

        return recruiting > 0 ? PositionStatus.Recruiting : PositionStatus.Vacant;
    }

    // Never negative: an overlapping handover does not create a debt of minus
    // one opening.
    public static int OpeningsLeft(int headcount, int assigned) =>
        Math.Max(0, Math.Max(1, headcount) - assigned);

    public static PositionRefusal Refusal(Position position)
    {
        if (string.IsNullOrWhiteSpace(position.Title))
        {
            return PositionRefusal.TitleRequired;
        }

        if (position.Headcount < 1)
        {
            return PositionRefusal.HeadcountNotPositive;
        }

        return position.Headcount > LargestComplement
            ? PositionRefusal.HeadcountTooLarge
            : PositionRefusal.None;
    }
}
