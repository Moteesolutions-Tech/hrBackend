namespace Motee.Application.Common;

public sealed record PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }

    public required int TotalItems { get; init; }

    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalItems / (double)PageSize);

    public bool HasNextPage => Page < TotalPages;

    public static PagedResult<T> Empty(int page, int pageSize) =>
        new() { Items = [], Page = page, PageSize = pageSize, TotalItems = 0 };
}

public record PagedQuery
{
    public const int DefaultPageSize = 25;

    // A ceiling rather than a business rule: an unbounded page size lets one request
    // pull an entire tenant's data in a single response.
    public const int MaxPageSize = 200;

    private readonly int _page = 1;
    private readonly int _pageSize = DefaultPageSize;

    public int Page
    {
        get => _page;
        init => _page = value < 1 ? 1 : value;
    }

    public int PageSize
    {
        get => _pageSize;
        init => _pageSize = value switch
        {
            < 1 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => value,
        };
    }

    public int Skip => (Page - 1) * PageSize;
}
