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

            (string csv, int rows, string subject) = await BuildAsync(job, cancellationToken);

            DateTimeOffset now = timeProvider.GetUtcNow();
            string fileName = $"{subject}-{now:yyyy-MM-dd-HHmm}.csv";
            string key = $"exports/{job.TenantId:N}/{job.Id:N}/{fileName}";

            await using (MemoryStream content = new(Encoding.UTF8.GetBytes(csv)))
            {
                await storage.SaveAsync(key, content, "text/csv", cancellationToken);
            }

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
    private async Task<(string Csv, int Rows, string Subject)> BuildAsync(
        ExportJob job,
        CancellationToken cancellationToken) =>
        job.Kind.StartsWith(ExportService.ReportKindPrefix, StringComparison.Ordinal)
            ? await BuildReportAsync(job, cancellationToken)
            : await BuildEmployeesAsync(job, cancellationToken);

    private async Task<(string Csv, int Rows, string Subject)> BuildEmployeesAsync(
        ExportJob job,
        CancellationToken cancellationToken)
    {
        EmployeeExportFilters filters =
            JsonSerializer.Deserialize<EmployeeExportFilters>(job.Filters, SerializerOptions)!;

        (string csv, int rows) = await BuildCsvAsync(filters, cancellationToken);

        return (csv, rows, ExportService.EmployeesKind);
    }

    // One dataset at a time through the same provider the on-screen report reads, so a
    // spreadsheet and the grid it was exported from cannot disagree.
    //
    // The scope comes off the job rather than from a permission check: there is no user
    // here to check. See ReportExportFilters.
    private async Task<(string Csv, int Rows, string Subject)> BuildReportAsync(
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

        IReadOnlyList<ReportColumn> columns = ReportCatalogue.Resolve(dataset, filters.Columns);

        StringBuilder csv = new();
        csv.Append(ReportCsvWriter.Header(columns));

        int page = 1;
        int rows = 0;

        while (true)
        {
            PagedResult<IReadOnlyDictionary<string, string?>> result = await reports.RowsAsync(
                dataset,
                filters.Scope,
                filters.ViewerEmployeeId,
                filters.Filters,
                new PagedQuery { Page = page, PageSize = PageSize },
                cancellationToken);

            foreach (IReadOnlyDictionary<string, string?> row in result.Items)
            {
                csv.Append(ReportCsvWriter.Row(row, columns));
                rows++;
            }

            if (!result.HasNextPage)
            {
                break;
            }

            page++;
        }

        return (csv.ToString(), rows, dataset.Key);
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
