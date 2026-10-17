using LiveAuction.Application.Abstractions;
using LiveAuction.Domain;
using LiveAuction.Domain.Auctions;
using LiveAuction.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveAuction.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    // Docker/demo: aplicar migraciones al arrancar. En producción real se aplican en el pipeline
    // de despliegue (script idempotente), no desde la app.
    public bool MigrateOnStartup { get; set; }

    public SeedOptions Seed { get; set; } = new();
}

public sealed class SeedOptions
{
    public bool Enabled { get; set; }

    // Credenciales del administrador inicial: User Secrets / variables de entorno.
    public string? AdminEmail { get; set; }
    public string? AdminPassword { get; set; }

    // Si se indica, crea dos postores de demostración y subastas de ejemplo.
    public string? DemoPassword { get; set; }
}

// Prepara la BD al arrancar: migraciones (opcional) y datos iniciales (solo si está vacía).
public sealed partial class DbInitializer(
    AppDbContext db,
    IPasswordHasher hasher,
    TimeProvider time,
    IOptions<DatabaseOptions> options,
    ILogger<DbInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var settings = options.Value;

        if (settings.MigrateOnStartup)
        {
            await db.Database.MigrateAsync(ct);
            LogMigrated();
        }

        if (!settings.Seed.Enabled || await db.Users.AnyAsync(ct))
            return;

        var seed = settings.Seed;
        if (string.IsNullOrWhiteSpace(seed.AdminEmail) || string.IsNullOrWhiteSpace(seed.AdminPassword))
        {
            LogSeedSkipped();
            return;
        }

        var now = time.GetUtcNow();
        var admin = CreateUser(seed.AdminEmail, "Administrador", seed.AdminPassword, UserRole.Admin, now);
        db.Users.Add(admin);

        if (!string.IsNullOrWhiteSpace(seed.DemoPassword))
        {
            db.Users.Add(CreateUser("ana@demo.local", "Ana", seed.DemoPassword, UserRole.Bidder, now));
            db.Users.Add(CreateUser("luis@demo.local", "Luis", seed.DemoPassword, UserRole.Bidder, now));
        }

        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(seed.DemoPassword))
            await SeedAuctionsAsync(admin.Id, now, ct);

        LogSeeded(admin.Email);
    }

    private User CreateUser(string email, string name, string password, UserRole role, DateTimeOffset now)
    {
        var user = User.Create(email, name, string.Empty, role, now);
        user.ChangePasswordHash(hasher.Hash(user, password));
        return user;
    }

    private async Task SeedAuctionsAsync(int sellerId, DateTimeOffset now, CancellationToken ct)
    {
        (string Title, string Description, string Image, decimal Price, decimal Increment, int StartMin, int EndMin)[] samples =
        [
            ("Reloj automático vintage 1968", "Caja de acero, movimiento automático revisado. Incluye estuche original.", "https://images.unsplash.com/photo-1523170335258-f5ed11844a49?w=800", 2500m, 100m, -5, 15),
            ("Guitarra eléctrica Stratocaster", "Edición de 1995, pastillas originales, excelente estado.", "https://images.unsplash.com/photo-1564186763535-ebb21ef5277f?w=800", 8000m, 250m, -5, 60),
            ("Cámara Leica M6", "Cámara de rollo telemétrica, lente 35mm f/2.", "https://images.unsplash.com/photo-1516035069371-29a1b244cc32?w=800", 15000m, 500m, -5, 180),
            ("Primera edición firmada", "Novela con firma del autor, 1987. Certificado de autenticidad.", "https://images.unsplash.com/photo-1544947950-fa07a98d237f?w=800", 1200m, 50m, 30, 240)
        ];

        foreach (var s in samples)
        {
            var auction = Auction.Create(sellerId, s.Title, s.Description, s.Image, s.Price, s.Increment,
                now.AddMinutes(s.StartMin), now.AddMinutes(s.EndMin), now.AddMinutes(Math.Min(s.StartMin, 0) - 1)).Value;
            db.Auctions.Add(auction);
        }

        await db.SaveChangesAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Migraciones aplicadas.")]
    private partial void LogMigrated();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Seed habilitado pero faltan Database:Seed:AdminEmail / AdminPassword; no se creó el administrador.")]
    private partial void LogSeedSkipped();

    [LoggerMessage(Level = LogLevel.Information, Message = "Datos iniciales creados. Administrador: {AdminEmail}.")]
    private partial void LogSeeded(string adminEmail);
}
