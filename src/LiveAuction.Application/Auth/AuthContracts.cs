using System.ComponentModel.DataAnnotations;
using LiveAuction.Domain;
using LiveAuction.Domain.Users;

namespace LiveAuction.Application.Auth;

public sealed record RegisterRequest
{
    [Required, EmailAddress, StringLength(User.EmailMaxLength)]
    public string Email { get; init; } = string.Empty;

    [Required, StringLength(User.DisplayNameMaxLength, MinimumLength = 2)]
    public string DisplayName { get; init; } = string.Empty;

    // 8+ caracteres con al menos una letra y un número.
    [Required, StringLength(128, MinimumLength = 8)]
    [RegularExpression(@"^(?=.*[A-Za-z])(?=.*\d).+$", ErrorMessage = "La contraseña debe incluir al menos una letra y un número.")]
    public string Password { get; init; } = string.Empty;
}

public sealed record LoginRequest
{
    [Required, EmailAddress]
    public string Email { get; init; } = string.Empty;

    [Required]
    public string Password { get; init; } = string.Empty;
}

public sealed record UserDto(int Id, string Email, string DisplayName, UserRole Role);

public sealed record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt, UserDto User);
