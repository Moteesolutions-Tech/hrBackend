namespace Motee.Application.Tenancy;

// Resolved from the access token, never from the caller's IP or a client-supplied
// header. Payroll and tax must resolve identically for a background run with no
// HTTP request at all.
public interface ICurrentTenant
{
    Guid? TenantId { get; }
}
