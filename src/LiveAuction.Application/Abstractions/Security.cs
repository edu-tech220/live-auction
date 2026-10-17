using LiveAuction.Domain.Users;

namespace LiveAuction.Application.Abstractions;

// Hash de contraseñas. La implementación (PBKDF2 de ASP.NET Identity) vive en Infrastructure.
public interface IPasswordHasher
{
    string Hash(User user, string password);
    PasswordCheck Verify(User user, string hashedPassword, string providedPassword);
}

public enum PasswordCheck
{
    Failed,
    Success,
    SuccessRehashNeeded // correcto, pero el hash usa parámetros viejos: conviene regenerarlo
}

// Emisión de JWT.
public interface ITokenService
{
    AccessToken CreateToken(User user);
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);
