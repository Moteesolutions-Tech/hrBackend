namespace Motee.Application.Auth;

public interface IPasswordResetService
{
    // Silent by design: the caller learns nothing about whether the address exists.
    Task RequestAsync(string email, CancellationToken cancellationToken = default);

    Task<PasswordResetOutcome> ResetAsync(
        string email,
        string code,
        string newPassword,
        CancellationToken cancellationToken = default);
}

public enum PasswordResetOutcome
{
    Succeeded,
    InvalidCode,
    Expired,
    LockedOut,
    AlreadyUsed,
    WeakPassword,
}
