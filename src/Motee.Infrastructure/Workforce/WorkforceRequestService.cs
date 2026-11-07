using Microsoft.EntityFrameworkCore;
using Motee.Application.Approvals;
using Motee.Application.Common;
using Motee.Application.Recruitment;
using Motee.Application.Tenancy;
using Motee.Application.Workforce;
using Motee.Domain.Approvals;
using Motee.Domain.Organisation;
using Motee.Domain.Recruitment;
using Motee.Domain.Workforce;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Workforce;

// This file's own namespace shadows the domain's helper, so the rules it
// holds are aliased rather than written out in full at each call.
using Rules = Motee.Domain.Workforce.WorkforceRequests;

// Workforce requests.
//
// Two integrations and almost no logic of its own, which is the point. The
// approval route comes from the existing engine, so headcount follows
// whatever chain the tenant configured rather than a second set of rules; and
// the vacancy comes from the existing requisition service, so there is one
// definition of what a vacancy is.
//
// What this adds is the authorisation trail the chain was missing: who asked
// for the headcount, on what grounds, against which cost centre, and what
// came of it.
internal sealed class WorkforceRequestService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICurrentEmployee currentEmployee,
    IApprovalService approvals,
    IRequisitionService requisitions,
    TimeProvider timeProvider) : IWorkforceRequestService
{
    private const string SubjectType = "workforce_request";

    private Dictionary<Guid, string>? _departments;

    public async Task<PagedResult<WorkforceRequestDto>> ListAsync(
        WorkforceRequestQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<WorkforceRequest> rows = dbContext.WorkforceRequests.AsNoTracking();

        if (query.DepartmentId is Guid departmentId)
        {
            rows = rows.Where(request => request.DepartmentId == departmentId);
        }

        if (query.RequestedByEmployeeId is Guid requester)
        {
            rows = rows.Where(request => request.RequestedByEmployeeId == requester);
        }

        if (query.Urgency is WorkforceUrgency urgency)
        {
            rows = rows.Where(request => request.Urgency == urgency);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string needle = $"%{query.Search.Trim()}%";

            rows = rows.Where(request =>
                EF.Functions.ILike(request.Position, needle)
                || (request.Reference != null && EF.Functions.ILike(request.Reference, needle)));
        }

        List<WorkforceRequest> found = await rows
            .OrderByDescending(request => request.CreatedAt)
            .ToListAsync(cancellationToken);

        List<WorkforceRequestDto> described = await DescribeAsync(found, cancellationToken);

        // Status is read from the approval chain, so it is filtered after the
        // describe. Storing it to make this a SQL predicate is exactly the
        // drift the derivation exists to prevent.
        if (query.Status is WorkforceRequestStatus status)
        {
            described = [.. described.Where(request => request.Status == status)];
        }

        return new PagedResult<WorkforceRequestDto>
        {
            Items = [.. described.Skip(query.Skip).Take(query.PageSize)],
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = described.Count,
        };
    }

    public async Task<WorkforceRequestDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        WorkforceRequest? request = await dbContext.WorkforceRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        return request is null ? null : (await DescribeAsync([request], cancellationToken))[0];
    }

    public async Task<WorkforceRequestResult> CreateAsync(
        WorkforceRequestRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await MissingDepartmentAsync(request.DepartmentId, cancellationToken))
        {
            return WorkforceRequestResult.Failed(WorkforceRequestOutcome.DepartmentNotFound);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        Guid? actor = await currentEmployee.IdAsync(cancellationToken);

        WorkforceRequest row = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            Position = string.Empty,
            Justification = string.Empty,
            RequestedByEmployeeId = actor,

            // Captured now rather than joined on read, so the request still
            // says who raised it after that person has left.
            RequestedByName = actor is Guid who
                ? await NameOfAsync(who, cancellationToken)
                : null,
            CreatedAt = now,
            UpdatedAt = now,
        };

        Apply(row, request, now);

        dbContext.WorkforceRequests.Add(row);

        await dbContext.SaveChangesAsync(cancellationToken);

        return WorkforceRequestResult.Ok((await DescribeAsync([row], cancellationToken))[0]);
    }

    public async Task<WorkforceRequestResult> UpdateAsync(
        Guid id,
        WorkforceRequestRequest request,
        CancellationToken cancellationToken = default)
    {
        WorkforceRequest? row = await FindAsync(id, cancellationToken);

        if (row is null)
        {
            return WorkforceRequestResult.Failed(WorkforceRequestOutcome.NotFound);
        }

        if (await MissingDepartmentAsync(request.DepartmentId, cancellationToken))
        {
            return WorkforceRequestResult.Failed(WorkforceRequestOutcome.DepartmentNotFound);
        }

        ApprovalStatus? approval = await ApprovalOf(row, cancellationToken);
        WorkforceRefusal refusal = Rules.CanEdit(row, approval);

        if (refusal != WorkforceRefusal.None)
        {
            return WorkforceRequestResult.Refused(refusal);
        }

        Apply(row, request, timeProvider.GetUtcNow());

        await dbContext.SaveChangesAsync(cancellationToken);

        return WorkforceRequestResult.Ok((await DescribeAsync([row], cancellationToken))[0]);
    }

    public async Task<WorkforceRequestResult> SubmitAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        WorkforceRequest? row = await FindAsync(id, cancellationToken);

        if (row is null)
        {
            return WorkforceRequestResult.Failed(WorkforceRequestOutcome.NotFound);
        }

        ApprovalStatus? approval = await ApprovalOf(row, cancellationToken);
        WorkforceRefusal refusal = Rules.CanSubmit(row, approval);

        if (refusal != WorkforceRefusal.None)
        {
            return WorkforceRequestResult.Refused(refusal);
        }

        // A returned chain is reopened; a rejected one is not.
        //
        // The engine draws that line deliberately: Returned is the
        // submitter's again and resubmitting replays the same instance with
        // its round counter, while Approved, Rejected and Cancelled are
        // terminal because a decided approval is the evidence that it was
        // decided. So a rejection stays on the record and a reworked request
        // goes round on a fresh chain.
        ApprovalResult started = approval == ApprovalStatus.Returned
                && row.ApprovalInstanceId is Guid existing
            ? await approvals.ResubmitAsync(existing, cancellationToken)
            : await approvals.StartAsync(
                new StartApprovalRequest
                {
                    DocumentType = ApprovalDocumentTypes.WorkforceRequest,
                    SubjectType = SubjectType,
                    SubjectId = row.Id,

                    // Who the chain is about: the requester, so "their line
                    // manager" and "the head of their department" resolve to
                    // the right people.
                    SubjectEmployeeId = row.RequestedByEmployeeId,
                },
                cancellationToken);

        if (!started.Succeeded || started.Approval is null)
        {
            return WorkforceRequestResult.Failed(
                WorkforceRequestOutcome.CouldNotStartApproval);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        row.ApprovalInstanceId = started.Approval.Id;
        row.SubmittedAt = now;
        row.UpdatedAt = now;

        // Issued on submission and never reissued, so a resubmitted request
        // keeps the reference Finance already has.
        row.Reference ??= await NextReferenceAsync(now, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        return WorkforceRequestResult.Ok((await DescribeAsync([row], cancellationToken))[0]);
    }

    public async Task<WorkforceRequestResult> CancelAsync(
        Guid id,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return WorkforceRequestResult.Failed(WorkforceRequestOutcome.ReasonRequired);
        }

        WorkforceRequest? row = await FindAsync(id, cancellationToken);

        if (row is null)
        {
            return WorkforceRequestResult.Failed(WorkforceRequestOutcome.NotFound);
        }

        ApprovalStatus? approval = await ApprovalOf(row, cancellationToken);
        WorkforceRefusal refusal = Rules.CanCancel(row, approval);

        if (refusal != WorkforceRefusal.None)
        {
            return WorkforceRequestResult.Refused(refusal);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        row.CancelledAt = now;
        row.CancellationReason = reason.Trim();
        row.UpdatedAt = now;

        // The chain goes with it. Leaving it open would keep the request in
        // somebody's approval queue after it had been withdrawn.
        if (row.ApprovalInstanceId is Guid instanceId
            && approval is ApprovalStatus.InProgress or ApprovalStatus.Returned)
        {
            await approvals.CancelAsync(instanceId, reason.Trim(), cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return WorkforceRequestResult.Ok((await DescribeAsync([row], cancellationToken))[0]);
    }

    public async Task<WorkforceRequestResult> ConvertToRequisitionAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        WorkforceRequest? row = await FindAsync(id, cancellationToken);

        if (row is null)
        {
            return WorkforceRequestResult.Failed(WorkforceRequestOutcome.NotFound);
        }

        ApprovalStatus? approval = await ApprovalOf(row, cancellationToken);
        WorkforceRefusal refusal = Rules.CanConvert(row, approval);

        if (refusal != WorkforceRefusal.None)
        {
            return WorkforceRequestResult.Refused(refusal);
        }

        // Created through the requisition service rather than written
        // directly, so a vacancy raised this way is the same kind of thing as
        // one raised by hand — same validation, same reference scheme, same
        // default stages.
        RequisitionResult created = await requisitions.CreateAsync(
            new RequisitionRequest
            {
                PositionTitle = row.Position,
                DepartmentId = row.DepartmentId,
                HiringManagerId = row.RequestedByEmployeeId,
                EmploymentType = Rules.EmploymentTypeOf(row.VacancyType),
                HiringPriority = PriorityOf(row.Urgency),
                Openings = row.NumberOfHires,
                JobDescription = row.Justification,
                TargetStartDate = row.ExpectedStartDate,
            },
            cancellationToken);

        if (!created.Succeeded || created.Requisition is null)
        {
            return WorkforceRequestResult.Failed(
                WorkforceRequestOutcome.CouldNotCreateRequisition);
        }

        row.RequisitionId = created.Requisition.Id;
        row.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return WorkforceRequestResult.Ok((await DescribeAsync([row], cancellationToken))[0]);
    }

    public async Task<WorkforceRequestOutcome> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        WorkforceRequest? row = await FindAsync(id, cancellationToken);

        if (row is null)
        {
            return WorkforceRequestOutcome.NotFound;
        }

        // Only a draft is deletable. Once it has been in front of an approver
        // it is part of the authorisation record, and withdrawing is what
        // cancelling is for.
        if (row.SubmittedAt is not null || row.RequisitionId is not null)
        {
            return WorkforceRequestOutcome.Refused;
        }

        dbContext.WorkforceRequests.Remove(row);

        await dbContext.SaveChangesAsync(cancellationToken);

        return WorkforceRequestOutcome.Succeeded;
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private static HiringPriority PriorityOf(WorkforceUrgency urgency) => urgency switch
    {
        WorkforceUrgency.Critical => HiringPriority.Urgent,
        WorkforceUrgency.High => HiringPriority.High,
        WorkforceUrgency.Low => HiringPriority.Low,
        _ => HiringPriority.Medium,
    };

    private static void Apply(
        WorkforceRequest row,
        WorkforceRequestRequest request,
        DateTimeOffset now)
    {
        row.DepartmentId = request.DepartmentId;
        row.BusinessUnitId = request.BusinessUnitId;
        row.Position = request.Position.Trim();
        row.Grade = Blank(request.Grade);
        row.NumberOfHires = request.NumberOfHires;
        row.HiringReason = request.HiringReason;
        row.VacancyType = request.VacancyType;
        row.Urgency = request.Urgency;
        row.Justification = request.Justification.Trim();
        row.BudgetEstimate = request.BudgetEstimate;
        row.Currency = Blank(request.Currency);
        row.CostCentreCode = Blank(request.CostCentreCode);
        row.ExpectedStartDate = request.ExpectedStartDate;
        row.UpdatedAt = now;
    }

    private Task<WorkforceRequest?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.WorkforceRequests.FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

    private async Task<ApprovalStatus?> ApprovalOf(
        WorkforceRequest row,
        CancellationToken cancellationToken)
    {
        if (row.ApprovalInstanceId is not Guid instanceId)
        {
            return null;
        }

        return (await approvals.GetAsync(instanceId, cancellationToken))?.Status;
    }

    private async Task<bool> MissingDepartmentAsync(
        Guid? departmentId,
        CancellationToken cancellationToken) =>
        departmentId is Guid id
        && !await dbContext.Departments.AnyAsync(
            department => department.Id == id, cancellationToken);

    private async Task<List<WorkforceRequestDto>> DescribeAsync(
        IReadOnlyList<WorkforceRequest> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        IReadOnlyDictionary<Guid, string> departments = await DepartmentsAsync(cancellationToken);

        // One read for every chain rather than one per row: a list of fifty
        // requests would otherwise be fifty round trips to answer "what is
        // this waiting on".
        Dictionary<Guid, ApprovalDto> chains = new(
            await approvals.LatestForSubjectsAsync(
                SubjectType,
                [.. rows.Select(row => row.Id)],
                cancellationToken));

        // The request's own instance id is the authority, not whichever chain
        // is newest. A request rejected and reworked has two chains against
        // it, and "latest" is decided on a timestamp the two can share — so
        // where they disagree, the one this request points at wins. Costs an
        // extra read only for the rows that have been round twice.
        foreach (WorkforceRequest row in rows)
        {
            if (row.ApprovalInstanceId is not Guid instanceId
                || chains.GetValueOrDefault(row.Id)?.Id == instanceId)
            {
                continue;
            }

            if (await approvals.GetAsync(instanceId, cancellationToken) is ApprovalDto theirs)
            {
                chains[row.Id] = theirs;
            }
        }

        List<Guid> requisitionIds =
            [.. rows.Where(row => row.RequisitionId is not null).Select(row => row.RequisitionId!.Value)];

        Dictionary<Guid, string?> references = requisitionIds.Count == 0
            ? []
            : await dbContext.JobRequisitions
                .AsNoTracking()
                .Where(requisition => requisitionIds.Contains(requisition.Id))
                .ToDictionaryAsync(
                    requisition => requisition.Id,
                    requisition => requisition.Reference,
                    cancellationToken);

        return
        [
            .. rows.Select(row =>
            {
                ApprovalDto? chain = chains.GetValueOrDefault(row.Id);

                return new WorkforceRequestDto
                {
                    Id = row.Id,
                    Reference = row.Reference,
                    DepartmentId = row.DepartmentId,
                    DepartmentName = row.DepartmentId is Guid id
                        ? departments.GetValueOrDefault(id)
                        : null,
                    BusinessUnitId = row.BusinessUnitId,
                    Position = row.Position,
                    Grade = row.Grade,
                    NumberOfHires = row.NumberOfHires,
                    HiringReason = row.HiringReason,
                    VacancyType = row.VacancyType,
                    Urgency = row.Urgency,
                    Justification = row.Justification,
                    BudgetEstimate = row.BudgetEstimate,
                    Currency = row.Currency,
                    CostCentreCode = row.CostCentreCode,
                    ExpectedStartDate = row.ExpectedStartDate,
                    RequestedByEmployeeId = row.RequestedByEmployeeId,
                    RequestedByName = row.RequestedByName,
                    Status = Rules.Derive(row, chain?.Status),
                    ApprovalInstanceId = row.ApprovalInstanceId,

                    // The step it is sitting on, named. Null once the chain
                    // has finished, because nothing is waiting any more.
                    WaitingOn = chain?.Status == ApprovalStatus.InProgress
                        ? chain.CurrentStep?.ResolvedName ?? chain.CurrentStep?.Label
                        : null,

                    RequisitionId = row.RequisitionId,
                    RequisitionReference = row.RequisitionId is Guid requisitionId
                        ? references.GetValueOrDefault(requisitionId)
                        : null,
                    SubmittedAt = row.SubmittedAt,
                    CancellationReason = row.CancellationReason,
                    CreatedAt = row.CreatedAt,
                    UpdatedAt = row.UpdatedAt,
                };
            }),
        ];
    }

    // WFR-2026-00124. Counted off what this tenant has already issued this
    // year, which is why it is read inside the same save as the write that
    // claims it — the unique index is what makes two tabs safe.
    private async Task<string> NextReferenceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string prefix = $"WFR-{now.Year}-";

        List<string> issued = await dbContext.WorkforceRequests
            .AsNoTracking()
            .Where(row => row.Reference != null && row.Reference.StartsWith(prefix))
            .Select(row => row.Reference!)
            .ToListAsync(cancellationToken);

        int highest = issued
            .Select(reference =>
                int.TryParse(reference[prefix.Length..], out int number) ? number : 0)
            .DefaultIfEmpty(0)
            .Max();

        return Rules.Reference(now.Year, highest + 1);
    }

    private async Task<string?> NameOfAsync(Guid employeeId, CancellationToken cancellationToken) =>
        await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == employeeId)
            .Select(employee => employee.FirstName + " " + employee.LastName)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<IReadOnlyDictionary<Guid, string>> DepartmentsAsync(
        CancellationToken cancellationToken)
    {
        _departments ??= await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id, department => department.Name, cancellationToken);

        return _departments;
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
