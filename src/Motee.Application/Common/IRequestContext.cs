namespace Motee.Application.Common;

// Observed transport facts only. Jurisdiction lives in ICurrentTenant — an IP
// tells you where a request came from, never which rules apply to the employee.
public interface IRequestContext
{
    string? RequestId { get; }

    string? CorrelationId { get; }

    string? UserId { get; }

    // Where a finished background job sends its result — the person who asked for it,
    // not whoever the worker happens to be running as.
    string? UserEmail { get; }

    // Real caller only when the reverse proxy is trusted — see ForwardedHeaders setup.
    string? IpAddress { get; }

    string? UserAgent { get; }
}
