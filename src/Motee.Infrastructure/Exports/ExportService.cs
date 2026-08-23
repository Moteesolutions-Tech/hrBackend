using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Exports;
using Motee.Domain.Exports;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Exports;

internal sealed class ExportService(
    MoteeDbContext dbContext,
    IRequestContext requestContext,
    IExportQueue queue,
    IFileStorage storage,
    TimeProvider timeProvider) : IExportService
{
    public const string EmployeesKind = "employees";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<ExportJobDto> RequestEmployeeExportAsync(
        EmployeeExportFilters filters,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        ExportJob job = new()
        {
            Id = Guid.NewGuid(),
            Kind = EmployeesKind,
            RequestedByUserId = Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null,
            RequestedByEmail = requestContext.UserEmail ?? string.Empty,
            Filters = JsonSerializer.Serialize(filters, SerializerOptions),
            Status = ExportStatus.Queued,
            CreatedAt = now,
        };

        dbContext.ExportJobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Enqueued after the row is committed, or the worker could pick up an id that
        // is not there yet.
        queue.Enqueue(job.Id);

        return ToDto(job);
    }

    public async Task<ExportJobDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ExportJob? job = await MineAsync(id, cancellationToken);

        return job is null ? null : ToDto(job);
    }

    public async Task<ExportLink?> CreateLinkAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        ExportJob? job = await MineAsync(id, cancellationToken);

        if (job is null || !job.IsDownloadable(now))
        {
            return null;
        }

        string fileName = job.FileName ?? "export.csv";

        return new ExportLink
        {
            Url = await storage.GetDownloadUrlAsync(
                job.FileKey!, ExportPolicy.LinkLifetime, fileName, cancellationToken),
            FileName = fileName,
            LinkExpiresAt = now.Add(ExportPolicy.LinkLifetime),
        };
    }

    // An export is a personal delivery, not a shared company file. The rows in it were
    // chosen by one person's permission scope, so a colleague with a narrower scope
    // must not be able to reach it by knowing its id — the tenant filter alone would
    // let them. No user, no export: a background caller has nothing to own.
    private async Task<ExportJob?> MineAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(requestContext.UserId, out Guid userId))
        {
            return null;
        }

        return await dbContext.ExportJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.Id == id && candidate.RequestedByUserId == userId,
                cancellationToken);
    }

    private static ExportJobDto ToDto(ExportJob job) => new()
    {
        Id = job.Id,
        Status = job.Status,
        Kind = job.Kind,
        RowCount = job.RowCount,
        FileName = job.FileName,
        Error = job.Error,
        CreatedAt = job.CreatedAt,
        CompletedAt = job.CompletedAt,
        ExpiresAt = job.ExpiresAt,
    };
}
