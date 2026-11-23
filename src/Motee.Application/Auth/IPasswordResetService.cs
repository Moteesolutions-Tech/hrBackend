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

    // Refused by the company's reuse policy. Distinct from WeakPassword
    // because the password may be perfectly strong, and "choose a stronger
    // one" is advice somebody cannot act on when the real problem is that they
    // have used it before.
    PreviouslyUsed,
}
