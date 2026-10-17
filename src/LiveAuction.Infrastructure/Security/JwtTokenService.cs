using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using LiveAuction.Application.Abstractions;
using LiveAuction.Domain.Users;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LiveAuction.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required] public string Issuer { get; set; } = "LiveAuction";
    [Required] public string Audience { get; set; } = "LiveAuction";

    // Secreto HMAC-SHA256: User Secrets en desarrollo, variable de entorno en Docker/CI.
    // Nunca en appsettings del repositorio. 32+ caracteres (256 bits).
    [Required, MinLength(32, ErrorMessage = "Jwt:SigningKey debe tener al menos 32 caracteres.")]
    public string SigningKey { get; set; } = string.Empty;

    [Range(5, 1440)] public int ExpiresMinutes { get; set; } = 120;

    public SymmetricSecurityKey CreateKey() => new(Encoding.UTF8.GetBytes(SigningKey));
}

// Claims mínimos y públicos (un JWT está firmado, no cifrado: cualquiera puede leerlo).
public static class AppClaims
{
    public const string UserId = "sub";
    public const string Name = "name";
    public const string Role = "role";
}

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider time) : ITokenService
{
    private readonly JwtOptions _options = options.Value;

    public AccessToken CreateToken(User user)
    {
        var now = time.GetUtcNow();
        var expires = now.AddMinutes(_options.ExpiresMinutes);

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(AppClaims.UserId, user.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim(AppClaims.Name, user.DisplayName),
                new Claim(AppClaims.Role, user.Role.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
            ]),
            SigningCredentials = new SigningCredentials(_options.CreateKey(), SecurityAlgorithms.HmacSha256)
        });

        return new AccessToken(token, expires);
    }
}
