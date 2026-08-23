namespace Motee.Application.Localization;

public interface ITenantLocaleService
{
    Task<TenantLocaleDto?> GetCurrentAsync(CancellationToken cancellationToken = default);
}
