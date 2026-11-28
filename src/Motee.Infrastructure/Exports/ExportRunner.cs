using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Exports;
using Motee.Application.Notifications;
using Motee.Application.Reports;
using Motee.Application.Tenancy;
using Motee.Domain.Exports;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Exports;

// Runs on the background worker. Pages the query rather than materialising every row,
// so exporting 5,000 people does not hold the whole set in memory at once.
internal sealed class ExportRunner(
    MoteeDbContext dbContext,
    IEmployeeService employees,
    Reports.ReportRowProviders reports,
    IFileStorage storage,
    IEmailDispatcher email,
    TimeProvider timeProvider,
    ILogger<ExportRunner> logger) : IExportRunner
{
    private const int PageSize = 5_000;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(Guid exportId, CancellationToken cancellationToken = default)
    {
        // No request, so no tenant — the filter would hide the job from its own
        // worker. The tenant is read from the row, then made ambient for everything
        // after, which is what lets the employee query see anything at all.
        ExportJob? job = await dbContext.ExportJobs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(candidate => candidate.Id == exportId, cancellationToken);

        if (job is null)
        {
            logger.LogWarning("Export {ExportId} no longer exists; nothing to run.", exportId);
            return;
        }

        using IDisposable tenant = AmbientTenant.Use(job.TenantId);

        try
        {
            job.Status = ExportStatus.Running;
            await dbContext.SaveChangesAsync(cancellationToken);

            Built built = await BuildAsync(job, cancellationToken);

            DateTimeOffset now = timeProvider.GetUtcNow();

            // The extension and the content type come from what was actually written,
            // not from a constant: a .csv holding JSON opens as one long broken row.
            string fileName =
                $"{built.Subject}-{now:yyyy-MM-dd-HHmm}.{Extension(built.Format)}";

            string key = $"exports/{job.TenantId:N}/{job.Id:N}/{fileName}";

            await using (MemoryStream content = new(Encoding.UTF8.GetBytes(built.Body)))
            {
                await storage.SaveAsync(
                    key, content, ContentType(built.Format), cancellationToken);
            }

            int rows = built.Rows;

            job.Status = ExportStatus.Completed;
            job.RowCount = rows;
            job.FileKey = key;
            job.FileName = fileName;
            job.CompletedAt = now;
            job.ExpiresAt = ExportPolicy.ExpiresAt(now);

            await dbContext.SaveChangesAsync(cancellationToken);

            await NotifyAsync(job, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Export {ExportId} failed.", exportId);

            job.Status = ExportStatus.Failed;

            // The message only. A stack trace is noise to the person who asked for a
            // spreadsheet, and detail to anyone else.
            job.Error = exception.Message;
            job.CompletedAt = timeProvider.GetUtcNow();

            await dbContext.SaveChangesAsync(CancellationToken.None);

            // Rethrown so Hangfire records the failure and applies its retry policy.
            throw;
        }
    }

    // Which kind of export this is. The kind was decided when the job was queued and
    // the two filter shapes are not interchangeable: deserialising a report job as an
    // employee one would leave Scope at its default rather than failing, and an export
    // with the wrong scope is the one failure that must not be quiet.
    private async Task<Built> BuildAsync(
        ExportJob job,
        CancellationToken cancellationToken) =>
        job.Kind.StartsWith(ExportService.ReportKindPrefix, StringComparison.Ordinal)
            ? await BuildReportAsync(job, cancellationToken)
            : await BuildEmployeesAsync(job, cancellationToken);

    // One finished export: the bytes, how many rows went into them, what to call the
    // file and which format it actually is.
    private sealed record Built
    {
        public required string Body { get; init; }

        public required int Rows { get; init; }

        public required string Subject { get; init; }

        public ReportExportFormat Format { get; init; } = ReportExportFormat.Csv;
    }

    private static string Extension(ReportExportFormat format) =>
        format == ReportExportFormat.Json ? "json" : "csv";

    private static string ContentType(ReportExportFormat format) =>
        format == ReportExportFormat.Json ? "application/json" : "text/csv";

    private async Task<Built> BuildEmployeesAsync(
        ExportJob job,
        CancellationToken cancellationToken)
    {
        EmployeeExportFilters filters =
            JsonSerializer.Deserialize<EmployeeExportFilters>(job.Filters, SerializerOptions)!;

        (string csv, int rows) = await BuildCsvAsync(filters, cancellationToken);

        // The employee export predates the format option and has no column catalogue
        // behind it, so it stays CSV.
        return new Built { Body = csv, Rows = rows, Subject = ExportService.EmployeesKind };
    }

    // One dataset at a time through the same provider the on-screen report reads, so a
    // spreadsheet and the grid it was exported from cannot disagree.
    //
    // The scope comes off the job rather than from a permission check: there is no user
    // here to check. See ReportExportFilters.
    private async Task<Built> BuildReportAsync(
        ExportJob job,
        CancellationToken cancellationToken)
    {
        ReportExportFilters filters =
            JsonSerializer.Deserialize<ReportExportFilters>(job.Filters, SerializerOptions)!;

        // Thrown rather than written empty. A dataset can only vanish between queueing
        // and running if it was removed from the catalogue in a deploy, and a file of
        // nothing but a header would look like an answer.
        ReportDataset dataset = ReportCatalogue.Find(filters.Dataset)
            ?? throw new InvalidOperationException(
                $"Report dataset '{filters.Dataset}' no longer exists.");

        // Over the dataset's columns, because the stored keys are already the gated
        // subset — resolving cannot widen them, and anything no longer in the catalogue
        // is dropped.
        IReadOnlyList<ReportColumn> columns =
            ReportCatalogue.Resolve(dataset.Columns, filters.Columns);

        // The tenant's own today, for the columns derived against it — age and tenure.
        // Taken from the job's tenant rather than UTC, because a company in Lagos
        // exporting at 23:30 UTC is already on tomorrow.
        DateOnly asOf = await TodayAsync(job.TenantId, cancellationToken);

        // Collected once, then written in whichever shape was asked for. The paging is
        // the expensive part and it is the same either way, so the format is a choice
        // about the last step rather than two readers.
        List<IReadOnlyDictionary<string, string?>> all = [];

        int page = 1;

        while (true)
        {
            PagedResult<IReadOnlyDictionary<string, string?>> result = await reports.RowsAsync(
                dataset,
                filters.Scope,
                filters.ViewerEmployeeId,
                filters.Filters,
                new PagedQuery { Page = page, PageSize = PageSize },
                asOf,
                cancellationToken);

            all.AddRange(result.Items);

            if (!result.HasNextPage)
            {
                break;
            }

            page++;
        }

        return new Built
        {
            Body = filters.Format == ReportExportFormat.Json
                ? Json(all, columns)
                : Csv(all, columns),
            Rows = all.Count,
            Subject = dataset.Key,
            Format = filters.Format,
        };
    }

    private static string Csv(
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
        IReadOnlyList<ReportColumn> columns)
    {
        StringBuilder csv = new();

        csv.Append(ReportCsvWriter.Header(columns));

        foreach (IReadOnlyDictionary<string, string?> row in rows)
        {
            csv.Append(ReportCsvWriter.Row(row, columns));
        }

        return csv.ToString();
    }

    // One array, a record per row, keyed by the column key rather than its header: a
    // JSON export is read by code, and a key is stable where a header is wording.
    //
    // Cells stay strings, as they are everywhere else in reporting. Guessing types here
    // would make the same column a number in one export and a string in the next,
    // depending on whether a row happened to be empty.
    private static string Json(
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
        IReadOnlyList<ReportColumn> columns) =>
        JsonSerializer.Serialize(
            rows.Select(row => columns.ToDictionary(
                column => column.Key,
                column => row.GetValueOrDefault(column.Key))),
            SerializerOptions);

    // By id, because tenants are not themselves tenant-scoped and there is no request
    // here to make one ambient for this read.
    //
    // Settings is fetched whole rather than projected to its time zone: it is jsonb
    // behind a value converter, and selecting into a field of it does not reach the
    // column. That has been a bug elsewhere in this codebase.
    private async Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        TenantSettings? settings = await dbContext.Tenants
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Settings)
            .FirstOrDefaultAsync(cancellationToken);

        return TenantClock.DayOf(timeProvider.GetUtcNow(), settings?.TimeZoneId);
    }

    private async Task<(string Csv, int Rows)> BuildCsvAsync(
        EmployeeExportFilters filters,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ExportColumn> columns = EmployeeExportColumns.Resolve(filters.Columns);

        StringBuilder csv = new();
        csv.Append(EmployeeCsvWriter.Header(columns));

        int page = 1;
        int rows = 0;

        while (true)
        {
            PagedResult<EmployeeListItemDto> result = await employees.ListAsync(
                new EmployeeQuery
                {
                    Scope = filters.Scope,
                    ViewerEmployeeId = filters.ViewerEmployeeId,
                    Search = filters.Search,
                    DepartmentId = filters.DepartmentId,
                    Status = filters.Status,
                    EmploymentType = filters.EmploymentType,
                    WorkMode = filters.WorkMode,
                    StartedFrom = filters.StartedFrom,
                    StartedTo = filters.StartedTo,
                    Page = page,
                    PageSize = PageSize,
                },
                cancellationToken);

            foreach (EmployeeListItemDto employee in result.Items)
            {
                csv.Append(EmployeeCsvWriter.Row(employee, columns));
                rows++;
            }

            if (!result.HasNextPage)
            {
                break;
            }

            page++;
        }

        return (csv.ToString(), rows);
    }

    private async Task NotifyAsync(ExportJob job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.RequestedByEmail))
        {
            return;
        }

        // The link goes straight to storage, so the recipient downloads without
        // coming back through the app. It carries its own signature and expires;
        // anyone the mail is forwarded to can use it until then.
        string url = await storage.GetDownloadUrlAsync(
            job.FileKey!, ExportPolicy.LinkLifetime, job.FileName, cancellationToken);

        email.Send(job.RequestedByEmail, new ExportReadyEmail
        {
            RowCount = job.RowCount,
            DownloadUrl = url,
            LinkLifetime = ExportPolicy.LinkLifetime,

            // Set alongside Status = Completed, and this only runs for a completed job.
            AvailableUntil = job.ExpiresAt!.Value,
        });
    }
}
