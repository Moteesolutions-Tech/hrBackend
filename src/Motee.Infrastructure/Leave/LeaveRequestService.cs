using Microsoft.EntityFrameworkCore;
using Motee.Application.Approvals;
using Motee.Application.Common;
using Motee.Application.Leave;
using Motee.Domain.Approvals;
using Motee.Domain.Leave;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Leave;

internal sealed class LeaveRequestService(
    MoteeDbContext dbContext,
    IApprovalService approvals,
    ILeaveBalanceService balances,
    LeaveYearResolver leaveYears,
    HolidayLookup holidays,
    IRequestContext requestContext,
    TimeProvider timeProvider) : ILeaveRequestService
{
    public async Task<PagedResult<LeaveRequestDto>> ListAsync(
        LeaveRequestQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<LeaveRequest> matching = Filter(query);

        int total = await matching.CountAsync(cancellationToken);

        List<LeaveRequestDto> items = await Project(matching)
            .OrderByDescending(request => request.StartDate)
            .ThenBy(request => request.EmployeeName)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<LeaveRequestDto>
        {
            Items = await WithApprovalsAsync(items, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<LeaveRequestDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        List<LeaveRequestDto> found = await Project(
                dbContext.LeaveRequests.Where(request => request.Id == id))
            .Take(1)
            .ToListAsync(cancellationToken);

        return (await WithApprovalsAsync(found, cancellationToken)).FirstOrDefault();
    }

    public async Task<LeaveRequestResult> SubmitAsync(
        LeaveRequestSubmission request,
        CancellationToken cancellationToken = default)
    {
        Validation check = await ValidateAsync(
            new LeaveQuoteRequest
            {
                EmployeeId = request.EmployeeId,
                LeaveTypeId = request.LeaveTypeId,
                StartDate = request.StartDate,
                EndDate = request.EndDate,
                IsHalfDay = request.IsHalfDay,
            },
            cancellationToken);

        if (check.Problems.Count > 0)
        {
            return LeaveRequestResult.Failed(check.Problems[0], check.Message);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        LeaveRequest booking = new()
        {
            Id = Guid.NewGuid(),
            EmployeeId = request.EmployeeId,
            LeaveTypeId = request.LeaveTypeId,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            IsHalfDay = request.IsHalfDay,
            HalfDayPeriod = request.IsHalfDay ? Trimmed(request.HalfDayPeriod) : null,

            // Snapshotted, not recomputed on read: a public holiday added later would
            // otherwise silently change what somebody's past leave cost them.
            TotalDays = check.TotalDays,
            LeaveYearStart = check.Year.Start,
            Status = LeaveRequestStatus.Pending,
            Reason = Trimmed(request.Reason),
            Notes = Trimmed(request.Notes),
            ReliefEmployeeId = request.ReliefEmployeeId,
            SubmittedByUserId = CurrentUser(),
            SubmittedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.LeaveRequests.Add(booking);
        await dbContext.SaveChangesAsync(cancellationToken);

        ApprovalResult chain = await approvals.StartAsync(
            new StartApprovalRequest
            {
                DocumentType = ApprovalDocumentTypes.LeaveRequest,
                SubjectType = LeaveSubject.Type,
                SubjectId = booking.Id,
                SubjectEmployeeId = request.EmployeeId,
                FileIds = request.FileIds,
            },
            cancellationToken);

        if (chain.Succeeded)
        {
            booking.ApprovalInstanceId = chain.Approval!.Id;
        }
        else if (chain.Outcome == ApprovalOutcome.TemplateNotFound)
        {
            // No workflow configured for leave. Approved on submission rather than left
            // pending for ever: a company that has not set up a chain is not a company
            // where leave should be impossible, and the alternative is a queue nobody
            // owns filling up silently.
            booking.Status = LeaveRequestStatus.Approved;
            booking.DecidedAt = now;
        }
        else
        {
            // The chain exists but could not run — nobody to ask, or evidence the policy
            // requires was not attached. The booking is withdrawn rather than left
            // half-made, and the reason says which.
            dbContext.LeaveRequests.Remove(booking);
            await dbContext.SaveChangesAsync(cancellationToken);

            return LeaveRequestResult.Failed(
                LeaveRequestOutcome.NoPolicy,
                chain.Reason ?? "The leave approval workflow could not be started.");
        }

        booking.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);

        return LeaveRequestResult.Ok((await GetAsync(booking.Id, cancellationToken))!);
    }

    public async Task<LeaveRequestResult> CancelAsync(
        Guid id,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        LeaveRequest? booking = await dbContext.LeaveRequests
            .FirstOrDefaultAsync(request => request.Id == id, cancellationToken);

        if (booking is null)
        {
            return LeaveRequestResult.Failed(LeaveRequestOutcome.NotFound);
        }

        if (!LeaveRequestLifecycle.CanCancel(booking.Status))
        {
            return LeaveRequestResult.Failed(LeaveRequestOutcome.NotCancellable);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        booking.Status = LeaveRequestStatus.Cancelled;
        booking.CancelledByUserId = CurrentUser();
        booking.CancelledAt = now;
        booking.CancellationReason = Trimmed(reason);
        booking.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        // Withdraw the chain too, so it leaves the approvers' queues. Somebody being
        // asked to decide leave that has already been withdrawn is how a queue stops
        // being worth reading.
        if (booking.ApprovalInstanceId is Guid instanceId)
        {
            await approvals.CancelAsync(instanceId, reason, cancellationToken);
        }

        return LeaveRequestResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    public async Task<LeaveQuoteDto> QuoteAsync(
        LeaveQuoteRequest request,
        CancellationToken cancellationToken = default)
    {
        Validation check = await ValidateAsync(request, cancellationToken);

        return new LeaveQuoteDto
        {
            TotalDays = check.TotalDays,
            AvailableBefore = check.Available,
            AvailableAfter = check.Available - check.TotalDays,
            NonWorkingDays = check.NonWorkingDays,
            Problems = check.Problems,
            Message = check.Message,
        };
    }

    // Everything that decides whether a request can be made, in one place — so the quote
    // the form shows and the answer the submission gives cannot disagree.
    // The first blackout standing in the way, or null.
    //
    // The date overlap is done in the database so a company with years of past blackouts
    // does not fetch them all; the leave-type and department tests are done in memory
    // because both are jsonb lists, and a company has a handful of blackouts live at once.
    private async Task<LeaveBlackout?> BlockingBlackoutAsync(
        LeaveQuoteRequest request,
        CancellationToken cancellationToken)
    {
        List<LeaveBlackout> candidates = await dbContext.LeaveBlackouts
            .AsNoTracking()
            .Where(blackout => blackout.IsActive
                && blackout.StartDate <= request.EndDate
                && blackout.EndDate >= request.StartDate)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return null;
        }

        Guid? departmentId = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == request.EmployeeId)
            .Select(employee => employee.DepartmentId)
            .FirstOrDefaultAsync(cancellationToken);

        return candidates.FirstOrDefault(blackout => blackout.Blocks(
            request.StartDate, request.EndDate, request.LeaveTypeId, departmentId));
    }

    private async Task<Validation> ValidateAsync(
        LeaveQuoteRequest request,
        CancellationToken cancellationToken)
    {
        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        LeaveYear year = await leaveYears.ForAsync(request.StartDate, cancellationToken);

        List<LeaveRequestOutcome> problems = [];

        if (request.EndDate < request.StartDate)
        {
            return Validation.Rejected(year, LeaveRequestOutcome.InvalidDates,
                "The end date is before the start date.");
        }

        if (request.IsHalfDay && request.StartDate != request.EndDate)
        {
            return Validation.Rejected(year, LeaveRequestOutcome.InvalidDates,
                "A half day can only be taken on a single date.");
        }

        LeavePolicy? policy = await dbContext.LeavePolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.LeaveTypeId == request.LeaveTypeId && candidate.IsActive,
                cancellationToken);

        bool typeExists = await dbContext.LeaveTypes
            .AnyAsync(type => type.Id == request.LeaveTypeId && type.IsActive, cancellationToken);

        if (!typeExists)
        {
            return Validation.Rejected(year, LeaveRequestOutcome.UnknownLeaveType,
                "That kind of leave is not available.");
        }

        if (policy is null)
        {
            return Validation.Rejected(year, LeaveRequestOutcome.NoPolicy,
                "No policy is configured for that kind of leave.");
        }

        IReadOnlyList<(DateOnly Date, string Name)> named =
            await holidays.NamedAsync(request.StartDate, request.EndDate, cancellationToken);

        HashSet<DateOnly> holidayDates = [.. named.Select(holiday => holiday.Date)];

        decimal totalDays = WorkingDays.ForRequest(
            request.StartDate,
            request.EndDate,
            request.IsHalfDay,
            holidayDates,
            policy.ExcludePublicHolidays);

        List<LeaveNonWorkingDayDto> nonWorking = NonWorkingDays(
            request.StartDate, request.EndDate, named, policy.ExcludePublicHolidays);

        if (totalDays <= 0m)
        {
            return Validation.Rejected(year, LeaveRequestOutcome.NoWorkingDays,
                "Every day in that range is a weekend or a company holiday.",
                nonWorking);
        }

        string? message = null;

        // Notice is measured from today to the first day off, and only for leave in the
        // future. Backdating sick leave is ordinary — somebody was ill last week and is
        // recording it now — and a notice rule would make that impossible.
        if (policy.MinNoticeDays > 0 && request.StartDate > today)
        {
            int notice = request.StartDate.DayNumber - today.DayNumber;

            if (notice < policy.MinNoticeDays)
            {
                problems.Add(LeaveRequestOutcome.InsufficientNotice);
                message = $"This leave needs {policy.MinNoticeDays} days' notice; "
                    + $"you have given {notice}.";
            }
        }

        if (policy.MaxConsecutiveDays > 0 && totalDays > policy.MaxConsecutiveDays)
        {
            problems.Add(LeaveRequestOutcome.TooLong);
            message ??= $"This leave allows at most {policy.MaxConsecutiveDays} days at a time.";
        }

        // Checked before the balance, because a blackout is about the dates rather than
        // the person: being told to pick different dates is more useful than being told
        // how many days are left over dates that were never available.
        LeaveBlackout? blackout = await BlockingBlackoutAsync(request, cancellationToken);

        if (blackout is not null)
        {
            problems.Add(LeaveRequestOutcome.Blackout);
            message ??= blackout.Reason
                ?? $"Leave cannot be booked over {blackout.Name} "
                + $"({blackout.StartDate:d MMM} to {blackout.EndDate:d MMM}).";
        }

        bool overlaps = await dbContext.LeaveRequests.AnyAsync(
            existing => existing.EmployeeId == request.EmployeeId
                && existing.StartDate <= request.EndDate
                && existing.EndDate >= request.StartDate
                && (existing.Status == LeaveRequestStatus.Pending
                    || existing.Status == LeaveRequestStatus.Approved),
            cancellationToken);

        if (overlaps)
        {
            problems.Add(LeaveRequestOutcome.Overlaps);
            message ??= "There is already leave booked over those dates.";
        }

        LeaveBalanceDto? balance = await balances.ForAsync(
            request.EmployeeId, request.LeaveTypeId, request.StartDate, cancellationToken);

        decimal available = balance?.Available ?? 0m;

        // Unpaid leave has no entitlement to run down, so a balance check would refuse
        // every request. The limit there is what a manager will agree to, which is what
        // the approval chain is for.
        if (policy.DaysPerYear > 0m && totalDays > available)
        {
            problems.Add(LeaveRequestOutcome.InsufficientBalance);
            message ??= $"You have {available} days left and asked for {totalDays}.";
        }

        return new Validation
        {
            Year = year,
            TotalDays = totalDays,
            Available = available,
            NonWorkingDays = nonWorking,
            Problems = problems,
            Message = message,
        };
    }

    // Why ten calendar days cost six. Listed rather than summarised, because "4 days
    // excluded" invites somebody to check and find nothing to check against.
    private static List<LeaveNonWorkingDayDto> NonWorkingDays(
        DateOnly start,
        DateOnly end,
        IReadOnlyList<(DateOnly Date, string Name)> holidays,
        bool excludeHolidays)
    {
        List<LeaveNonWorkingDayDto> days = [];

        for (DateOnly day = start; day <= end; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                days.Add(new LeaveNonWorkingDayDto { Date = day, Reason = "Weekend" });
                continue;
            }

            if (!excludeHolidays)
            {
                continue;
            }

            string? name = holidays
                .Where(holiday => holiday.Date == day)
                .Select(holiday => holiday.Name)
                .FirstOrDefault();

            if (name is not null)
            {
                days.Add(new LeaveNonWorkingDayDto { Date = day, Reason = name });
            }
        }

        return days;
    }

    private IQueryable<LeaveRequest> Filter(LeaveRequestQuery query)
    {
        IQueryable<LeaveRequest> requests = dbContext.LeaveRequests;

        if (query.EmployeeId is Guid employeeId)
        {
            requests = requests.Where(request => request.EmployeeId == employeeId);
        }

        if (query.LeaveTypeId is Guid leaveTypeId)
        {
            requests = requests.Where(request => request.LeaveTypeId == leaveTypeId);
        }

        if (query.Status is LeaveRequestStatus status)
        {
            requests = requests.Where(request => request.Status == status);
        }

        if (query.DepartmentId is Guid departmentId)
        {
            requests = requests.Where(request => dbContext.Employees
                .Any(employee => employee.Id == request.EmployeeId
                    && employee.DepartmentId == departmentId));
        }

        // Overlap, not containment: a fortnight beginning last Friday is still absence
        // during this week, and a calendar that hid it would show the office fully
        // staffed when it is not.
        if (query.From is DateOnly from)
        {
            requests = requests.Where(request => request.EndDate >= from);
        }

        if (query.To is DateOnly to)
        {
            requests = requests.Where(request => request.StartDate <= to);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string term = $"%{query.Search.Trim()}%";

            requests = requests.Where(request => dbContext.Employees
                .Any(employee => employee.Id == request.EmployeeId
                    && EF.Functions.ILike(employee.FirstName + " " + employee.LastName, term)));
        }

        return requests;
    }

    private IQueryable<LeaveRequestDto> Project(IQueryable<LeaveRequest> requests) =>
        requests.Select(request => new LeaveRequestDto
        {
            Id = request.Id,
            EmployeeId = request.EmployeeId,

            // Read through rather than copied, so somebody who married between booking
            // and going is not filed under the wrong name.
            EmployeeName = dbContext.Employees
                .Where(employee => employee.Id == request.EmployeeId)
                .Select(employee => employee.FirstName + " " + employee.LastName)
                .FirstOrDefault() ?? "Unknown",
            JobTitle = dbContext.Employees
                .Where(employee => employee.Id == request.EmployeeId)
                .Select(employee => employee.JobTitle)
                .FirstOrDefault(),
            DepartmentName = dbContext.Employees
                .Where(employee => employee.Id == request.EmployeeId)
                .SelectMany(employee => dbContext.Departments
                    .Where(department => department.Id == employee.DepartmentId)
                    .Select(department => department.Name))
                .FirstOrDefault(),
            LeaveTypeId = request.LeaveTypeId,
            LeaveTypeName = dbContext.LeaveTypes
                .Where(type => type.Id == request.LeaveTypeId)
                .Select(type => type.Name)
                .FirstOrDefault() ?? "Unknown",
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            TotalDays = request.TotalDays,
            IsHalfDay = request.IsHalfDay,
            HalfDayPeriod = request.HalfDayPeriod,
            Status = request.Status,
            Reason = request.Reason,
            Notes = request.Notes,
            ReliefEmployeeId = request.ReliefEmployeeId,
            ReliefEmployeeName = dbContext.Employees
                .Where(employee => employee.Id == request.ReliefEmployeeId)
                .Select(employee => employee.FirstName + " " + employee.LastName)
                .FirstOrDefault(),
            SubmittedAt = request.SubmittedAt,
            DecidedAt = request.DecidedAt,
            CancelledAt = request.CancelledAt,
            CancellationReason = request.CancellationReason,

            // Filled after materialising: the chain is a service call, not something a
            // projection can reach into.
            Approval = null,
        });

    private async Task<IReadOnlyList<LeaveRequestDto>> WithApprovalsAsync(
        List<LeaveRequestDto> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return requests;
        }

        IReadOnlyDictionary<Guid, ApprovalDto> chains = await approvals.LatestForSubjectsAsync(
            LeaveSubject.Type, [.. requests.Select(request => request.Id)], cancellationToken);

        return
        [
            .. requests.Select(request => request with
            {
                Approval = chains.GetValueOrDefault(request.Id),
            }),
        ];
    }

    private Guid? CurrentUser() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record Validation
    {
        public required LeaveYear Year { get; init; }

        public decimal TotalDays { get; init; }

        public decimal Available { get; init; }

        public IReadOnlyList<LeaveNonWorkingDayDto> NonWorkingDays { get; init; } = [];

        public IReadOnlyList<LeaveRequestOutcome> Problems { get; init; } = [];

        public string? Message { get; init; }

        public static Validation Rejected(
            LeaveYear year,
            LeaveRequestOutcome problem,
            string message,
            IReadOnlyList<LeaveNonWorkingDayDto>? nonWorking = null) =>
            new()
            {
                Year = year,
                Problems = [problem],
                Message = message,
                NonWorkingDays = nonWorking ?? [],
            };
    }
}
