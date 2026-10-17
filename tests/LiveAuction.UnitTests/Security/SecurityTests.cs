using LiveAuction.Application.Abstractions;
using LiveAuction.Domain;
using LiveAuction.Domain.Users;
using LiveAuction.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LiveAuction.UnitTests.Security;

public class JwtTokenServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly JwtOptions Options = new()
    {
        Issuer = "LiveAuction",
        Audience = "LiveAuction",
        SigningKey = "clave-de-pruebas-unitarias-0123456789-abcdef",
        ExpiresMinutes = 60
    };

    private static User SampleUser()
    {
        var user = User.Create("ana@demo.local", "Ana", "hash", UserRole.Admin, Now);
        typeof(User).GetProperty(nameof(User.Id))!.SetValue(user, 42);
        return user;
    }

    [Fact]
    public async Task Token_contains_identity_claims_and_validates_with_same_key()
    {
        var service = new JwtTokenService(Microsoft.Extensions.Options.Options.Create(Options), new FakeTimeProvider(Now));

        var token = service.CreateToken(SampleUser());

        Assert.Equal(Now.AddMinutes(60), token.ExpiresAt);

        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(token.Token, new TokenValidationParameters
        {
            ValidIssuer = Options.Issuer,
            ValidAudience = Options.Audience,
            IssuerSigningKey = Options.CreateKey(),
            ValidateLifetime = false // el reloj real no coincide con el de la prueba
        });

        Assert.True(validation.IsValid);
        Assert.Equal("42", validation.Claims[AppClaims.UserId]);
        Assert.Equal("Ana", validation.Claims[AppClaims.Name]);
        Assert.Equal("Admin", validation.Claims[AppClaims.Role]);
        Assert.False(validation.Claims.ContainsKey("email")); // nada de datos privados en el token
    }

    [Fact]
    public async Task Token_signed_with_another_key_is_rejected()
    {
        var service = new JwtTokenService(Microsoft.Extensions.Options.Options.Create(Options), new FakeTimeProvider(Now));
        var token = service.CreateToken(SampleUser());

        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(token.Token, new TokenValidationParameters
        {
            ValidIssuer = Options.Issuer,
            ValidAudience = Options.Audience,
            IssuerSigningKey = new SymmetricSecurityKey("otra-clave-distinta-de-32-caracteres-xyz"u8.ToArray()),
            ValidateLifetime = false
        });

        Assert.False(validation.IsValid);
    }
}

public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();
    private readonly User _user = User.Create("a@b.c", "A", "", UserRole.Bidder, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Hash_is_salted_and_never_the_plain_password()
    {
        var first = _hasher.Hash(_user, "Secreta123");
        var second = _hasher.Hash(_user, "Secreta123");

        Assert.DoesNotContain("Secreta123", first, StringComparison.Ordinal);
        Assert.NotEqual(first, second); // salt aleatorio: mismo password, hash distinto
    }

    [Fact]
    public void Verify_accepts_correct_and_rejects_wrong_password()
    {
        var hash = _hasher.Hash(_user, "Secreta123");

        Assert.Equal(PasswordCheck.Success, _hasher.Verify(_user, hash, "Secreta123"));
        Assert.Equal(PasswordCheck.Failed, _hasher.Verify(_user, hash, "secreta123"));
    }
}
