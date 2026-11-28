using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Exports;
using Motee.Application.Reports;
using Motee.Application.Tenancy;
using Motee.Domain.Authorization;
using Motee.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Reports;

// Running a report over one of the declared datasets.
//
// The permission work is the substance here, not the row reading. A report is the same
// data in a different shape, so it has to carry the same narrowing the module's own
// screen carries — and the scope cannot come from the endpoint. A controller's
// GrantedScope() reads what the permission handler resolved for the module named on
// that endpoint; for a reports controller that is the reports module, which says
// nothing about how much of the employee list the caller may read.
//
// So the scope is resolved here, per dataset, against the dataset's own module, and
// handed to that module's typed query.
internal sealed class ReportService(
    MoteeDbContext dbContext,
    ReportRowProviders providers,
    IUserPermissions permissions,
    ICurrentEmployee currentEmployee,
    IRequestContext requestContext,
    ICurrentTenant currentTenant,
    IExportService exports,
    TimeProvider timeProvider) : IReportService
{
    public async Task<IReadOnlyList<ReportDatasetDto>> DatasetsAsync(
        CancellationToken cancellationToken = default)
    {
        (AccessLevelPermissions? held, bool isOwner) = await HeldAsync(cancellationToken);

        DataScope scope;
        Guid? viewer = await currentEmployee.IdAsync(cancellationToken);
        DateOnly asOf = await TodayAsync(cancellationToken);

        List<ReportDatasetDto> offered = [];

        foreach (ReportDataset dataset in ReportCatalogue.All)
        {
            if (!ReportCatalogue.CanRead(held, isOwner, dataset))
            {
                continue;
            }

            scope = ReportCatalogue.ScopeFor(held, isOwner, dataset);

            // Counted through the same provider the report runs through, so the figure
            // beside a dataset is the number of rows it would actually return. A count
            // straight off the table would promise rows the caller's scope excludes.
            PagedResult<IReadOnlyDictionary<string, string?>> probe = await providers.RowsAsync(
                dataset,
                scope,
                viewer,
                filters: null,

                // One row, for the total. The page itself is thrown away.
                new PagedQuery { Page = 1, PageSize = 1 },
                asOf,
                cancellationToken);

            offered.Add(new ReportDatasetDto
            {
                Key = dataset.Key,
                Label = dataset.Label,
                Group = dataset.Group,
                Columns = dataset.Columns,
                RecordCount = probe.TotalItems,
            });
        }

        return offered;
    }

    public async Task<ReportRunResult> RunAsync(
        ReportRunRequest request,
        CancellationToken cancellationToken = default)
    {
        Resolution resolved = await ResolveAsync(
            request.Dataset, request.Filters, cancellationToken);

        if (resolved.Dataset is not ReportDataset dataset)
        {
            return ReportRunResult.Failed(resolved.Outcome, resolved.Column);
        }

        // Read once and used for both the derived columns and the response's AsOf, so
        // the ages in the table and the date printed above it cannot be a day apart
        // across midnight in the tenant's zone.
        DateOnly asOf = await TodayAsync(cancellationToken);

        PagedResult<IReadOnlyDictionary<string, string?>> rows = await providers.RowsAsync(
            dataset,
            resolved.Scope,
            await currentEmployee.IdAsync(cancellationToken),
            request.Filters,
            request,
            asOf,
            cancellationToken);

        IReadOnlyList<ReportColumn> columns =
            ReportCatalogue.Resolve(resolved.Visible, request.Columns);

        return ReportRunResult.Ok(new ReportTableDto
        {
            Dataset = dataset.Key,
            Label = dataset.Label,
            Columns = columns,

            // Narrowed to the columns asked for, after the provider built the full row.
            // The provider fills every column the dataset declares; trimming here keeps
            // the projection in one place rather than threading a column list through
            // every dataset's reader.
            Rows = new PagedResult<IReadOnlyDictionary<string, string?>>
            {
                Items = [.. rows.Items.Select(row => Only(row, columns))],
                Page = rows.Page,
                PageSize = rows.PageSize,
                TotalItems = rows.TotalItems,
            },
            AsOf = asOf,
        });
    }

    public async Task<ReportExportResult> ExportAsync(
        ReportExportRequest request,
        CancellationToken cancellationToken = default)
    {
        Resolution resolved = await ResolveAsync(
            request.Dataset, request.Filters, cancellationToken);

        if (resolved.Dataset is not ReportDataset dataset)
        {
            // Nothing queued on a refusal. A failed job would sit in the caller's
            // export list claiming a file they could never open.
            return ReportExportResult.Failed(resolved.Outcome, resolved.Column);
        }

        // The scope and the column list both go onto the job here, while there is still
        // a signed-in user to resolve them from. The worker that writes the file has no
        // request behind it and cannot ask again — ReportExportFilters says what that
        // would cost.
        //
        // Columns are stored already resolved and already gated, rather than as the
        // caller asked for them, so a worker reading the row cannot widen them back to
        // the dataset's full set and put pay in the file.
        return ReportExportResult.Ok(await exports.RequestReportExportAsync(
            new ReportExportFilters
            {
                Dataset = dataset.Key,
                Columns =
                [
                    .. ReportCatalogue.Resolve(resolved.Visible, request.Columns)
                        .Select(column => column.Key),
                ],
                Filters = request.Filters,
                Format = request.Format,
                Scope = resolved.Scope,
                ViewerEmployeeId = await currentEmployee.IdAsync(cancellationToken),
            },
            cancellationToken));
    }

    // What a run and an export both have to establish: that the dataset exists, that
    // this caller may read it, that its filters can be honoured, and how much of it
    // they reach. Shared so the two paths cannot drift — an export that checked less
    // than the screen behind it is a leak, and one that checked more is a button that
    // does nothing.
    private async Task<Resolution> ResolveAsync(
        string datasetKey,
        IReadOnlyDictionary<string, string?>? filters,
        CancellationToken cancellationToken)
    {
        ReportDataset? dataset = ReportCatalogue.Find(datasetKey);

        if (dataset is null)
        {
            return new Resolution { Outcome = ReportOutcome.UnknownDataset };
        }

        (AccessLevelPermissions? held, bool isOwner) = await HeldAsync(cancellationToken);

        // The same answer for "no such dataset" and "not yours". The catalogue already
        // says what exists, so a sharper message would only confirm that something the
        // caller cannot read is there.
        if (!ReportCatalogue.CanRead(held, isOwner, dataset))
        {
            return new Resolution { Outcome = ReportOutcome.UnknownDataset };
        }

        // Established before the filter check, because a column this caller cannot see
        // is one they cannot filter on either.
        IReadOnlyList<ReportColumn> visible = ReportCatalogue.Visible(dataset, held, isOwner);

        // Refused, not ignored. A report that quietly drops "department = Engineering"
        // answers a different question from the one asked, and looks right doing it.
        if (ReportCatalogue.UnfilterableColumn(visible, filters) is string offending)
        {
            return new Resolution
            {
                Outcome = ReportOutcome.NotFilterable,
                Column = offending,
            };
        }

        return new Resolution
        {
            Outcome = ReportOutcome.Succeeded,
            Dataset = dataset,
            Scope = ReportCatalogue.ScopeFor(held, isOwner, dataset),
            Visible = visible,
        };
    }

    // Dataset is set only when every check passed, so one null test covers all three
    // refusals and a caller cannot read a dataset off a failed resolution.
    private sealed record Resolution
    {
        public required ReportOutcome Outcome { get; init; }

        public ReportDataset? Dataset { get; init; }

        public string? Column { get; init; }

        public DataScope Scope { get; init; } = DataScope.Nothing;

        // The dataset's columns minus any this caller may not see. Resolved here
        // because the gate needs permissions — and an export's worker will not have
        // any, so the result has to travel with the job.
        public IReadOnlyList<ReportColumn> Visible { get; init; } = [];
    }

    private static IReadOnlyDictionary<string, string?> Only(
        IReadOnlyDictionary<string, string?> row,
        IReadOnlyList<ReportColumn> columns)
    {
        Dictionary<string, string?> trimmed = new(StringComparer.Ordinal);

        foreach (ReportColumn column in columns)
        {
            trimmed[column.Key] = row.GetValueOrDefault(column.Key);
        }

        return trimmed;
    }

    private async Task<(AccessLevelPermissions? Held, bool IsOwner)> HeldAsync(
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(requestContext.UserId, out Guid userId))
        {
            return (AccessLevelPermissions.Nothing, false);
        }

        ResolvedPermissions resolved = await permissions.ForAsync(userId, cancellationToken);

        return (resolved.Permissions, resolved.IsOwner);
    }

    // The tenant's own day, so "started this month" means what the company means.
    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        }

        // The whole Settings object rather than a field of it: Settings is jsonb behind
        // a value converter and selecting into it does not reach the column. That has
        // been a bug here before.
        TenantSettings? settings = await dbContext.Tenants
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Settings)
            .FirstOrDefaultAsync(cancellationToken);

        return TenantClock.DayOf(timeProvider.GetUtcNow(), settings?.TimeZoneId);
    }
}
