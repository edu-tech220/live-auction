using LiveAuction.Application.Abstractions;
using LiveAuction.Domain;
using LiveAuction.Domain.Common;
using LiveAuction.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace LiveAuction.Application.Auth;

public interface IAuthService
{
    Task<Result<AuthResponse>> RegisterAsync(RegisterRequest request, CancellationToken ct);
    Task<Result<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken ct);
    Task<Result<UserDto>> GetUserAsync(int userId, CancellationToken ct);
}

public sealed class AuthService(
    IApplicationDbContext db,
    IPasswordHasher hasher,
    ITokenService tokens,
    TimeProvider time) : IAuthService
{
    // Mismo error para "no existe" y "contraseña incorrecta": no se revela qué correos existen.
    private static readonly Error InvalidCredentials =
        Error.Unauthorized("InvalidCredentials", "Correo o contraseña incorrectos.");

    public async Task<Result<AuthResponse>> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var email = User.NormalizeEmail(request.Email);
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return Error.Conflict("EmailAlreadyRegistered", "Ya existe una cuenta con ese correo.");

        // Todo registro público es Bidder. Los administradores se crean por seed/configuración,
        // nunca por un campo que el cliente pueda mandar.
        var user = User.Create(email, request.DisplayName, passwordHash: string.Empty, UserRole.Bidder, time.GetUtcNow());
        user.ChangePasswordHash(hasher.Hash(user, request.Password));

        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueConstraintViolation(ex))
        {
            // Dos registros simultáneos con el mismo correo: el índice único decide.
            return Error.Conflict("EmailAlreadyRegistered", "Ya existe una cuenta con ese correo.");
        }

        return Issue(user);
    }

    public async Task<Result<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var email = User.NormalizeEmail(request.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
            return InvalidCredentials;

        var check = hasher.Verify(user, user.PasswordHash, request.Password);
        if (check == PasswordCheck.Failed)
            return InvalidCredentials;

        if (check == PasswordCheck.SuccessRehashNeeded)
        {
            user.ChangePasswordHash(hasher.Hash(user, request.Password));
            await db.SaveChangesAsync(ct);
        }

        return Issue(user);
    }

    public async Task<Result<UserDto>> GetUserAsync(int userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserDto(u.Id, u.Email, u.DisplayName, u.Role))
            .FirstOrDefaultAsync(ct);

        return user is null ? Error.NotFound("UserNotFound", "El usuario no existe.") : user;
    }

    private AuthResponse Issue(User user)
    {
        var token = tokens.CreateToken(user);
        return new AuthResponse(token.Token, token.ExpiresAt, new UserDto(user.Id, user.Email, user.DisplayName, user.Role));
    }
}
