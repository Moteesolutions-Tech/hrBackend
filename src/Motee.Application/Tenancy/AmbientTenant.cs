namespace Motee.Application.Tenancy;

// A background job has no request, so nothing resolves a tenant from a token — and
// the query filter would then hide the very rows the job exists to read. A job sets
// the tenant it is working for here, and ICurrentTenant falls back to it.
//
// AsyncLocal, so concurrent jobs on the same worker never see each other's tenant.
public static class AmbientTenant
{
    private static readonly AsyncLocal<Guid?> Current = new();

    public static Guid? TenantId => Current.Value;

    public static IDisposable Use(Guid tenantId)
    {
        Guid? previous = Current.Value;
        Current.Value = tenantId;

        return new Restore(previous);
    }

    private sealed class Restore(Guid? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
