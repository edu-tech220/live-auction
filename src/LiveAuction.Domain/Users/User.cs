namespace LiveAuction.Domain.Users;

// Usuario de la plataforma. Setters privados: el estado solo cambia por métodos con reglas.
public sealed class User
{
    public const int EmailMaxLength = 256;
    public const int DisplayNameMaxLength = 60;

    // Constructor sin parámetros para EF Core (materializa desde la BD).
    private User() { }

    public int Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;

    // Hash generado fuera del dominio (Infrastructure). El dominio nunca ve contraseñas.
    public string PasswordHash { get; private set; } = string.Empty;

    public UserRole Role { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsAdmin => Role == UserRole.Admin;

    public static User Create(string email, string displayName, string passwordHash, UserRole role, DateTimeOffset now) =>
        new()
        {
            Email = NormalizeEmail(email),
            DisplayName = displayName.Trim(),
            PasswordHash = passwordHash,
            Role = role,
            CreatedAt = now
        };

    public void ChangePasswordHash(string passwordHash) => PasswordHash = passwordHash;

    // Un solo lugar define cómo se comparan correos: sin espacios y en minúsculas.
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
