using LiveAuction.Application.Abstractions;
using LiveAuction.Domain.Users;
using Microsoft.AspNetCore.Identity;
using IdentityHasher = Microsoft.AspNetCore.Identity.PasswordHasher<LiveAuction.Domain.Users.User>;

namespace LiveAuction.Infrastructure.Security;

// Adaptador sobre PasswordHasher<T> de ASP.NET Core Identity: PBKDF2 + HMAC-SHA512,
// 100,000 iteraciones y salt aleatorio por contraseña. El hash incluye versión y parámetros,
// así que se puede endurecer en el futuro sin invalidar contraseñas existentes.
public sealed class PasswordHasher : IPasswordHasher
{
    private readonly IdentityHasher _inner = new();

    public string Hash(User user, string password) => _inner.HashPassword(user, password);

    public PasswordCheck Verify(User user, string hashedPassword, string providedPassword) =>
        _inner.VerifyHashedPassword(user, hashedPassword, providedPassword) switch
        {
            PasswordVerificationResult.Success => PasswordCheck.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
            _ => PasswordCheck.Failed
        };
}
