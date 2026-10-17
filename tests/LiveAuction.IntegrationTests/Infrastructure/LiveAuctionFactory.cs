using System.Collections.Concurrent;
using LiveAuction.Api.Common;
using LiveAuction.Api.Realtime;
using LiveAuction.Application.Abstractions;
using LiveAuction.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveAuction.IntegrationTests.Infrastructure;

// Levanta la API COMPLETA en memoria (mismo Program.cs, DI, middleware, Hub) contra una base
// SQL Server real. Una BD real es indispensable aquí: rowversion, índices únicos y transacciones
// son justo lo que se quiere probar, y un proveedor en memoria no los implementa.
public sealed class LiveAuctionFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // CI define LIVEAUCTION_TEST_CONNECTION (contenedor de SQL Server); localmente, SQLEXPRESS.
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("LIVEAUCTION_TEST_CONNECTION")
        ?? @"Server=.\SQLEXPRESS;Database=LiveAuction_Tests;Integrated Security=True;Encrypt=True;TrustServerCertificate=True";

    public static readonly DateTimeOffset StartTime = new(2026, 6, 1, 18, 0, 0, TimeSpan.Zero);

    public TestClock Clock { get; } = new(StartTime);

    public RecordingNotifier Notifications => Services.GetRequiredService<RecordingNotifier>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("Jwt:SigningKey", "clave-de-pruebas-de-integracion-0123456789-abcdef");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Database:Seed:Enabled", "false");
        // El worker se apaga: las pruebas invocan el ciclo de vida de forma determinista.
        builder.UseSetting("AuctionLifecycle:Enabled", "false");
        builder.UseSetting("RateLimiting:AuthPerMinute", "100000");
        builder.UseSetting("RateLimiting:GlobalPerMinute", "100000");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            // Notificador real de SignalR envuelto por uno que registra cada evento emitido.
            services.RemoveAll<IAuctionNotifier>();
            services.AddSingleton<SignalRAuctionNotifier>();
            services.AddSingleton(sp => new RecordingNotifier(sp.GetRequiredService<SignalRAuctionNotifier>()));
            services.AddSingleton<IAuctionNotifier>(sp => sp.GetRequiredService<RecordingNotifier>());

            // Límite de pujas holgado: las pruebas de concurrencia disparan muchas.
            services.RemoveAll<BidThrottle>();
            services.AddSingleton(new BidThrottle(tokensPerPeriod: 10_000, periodSeconds: 1));
        });
    }

    public async Task InitializeAsync()
    {
        // BD limpia por ejecución: se borra ANTES de arrancar la app (que luego migra sola).
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options;
        await using (var db = new AppDbContext(options))
            await db.Database.EnsureDeletedAsync();

        _ = Services; // arranca el host => Program.cs aplica migraciones
    }

    // Antes de cada prueba: tablas vacías, reloj y eventos en cero.
    public async Task ResetAsync()
    {
        Clock.UtcNow = StartTime;
        Notifications.Clear();

        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Bids; DELETE FROM Auctions; DELETE FROM Users;");
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;
}

// Reloj controlable (se puede mover hacia adelante y atrás).
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

// Decorador: registra cada evento y luego lo reenvía a SignalR. Permite afirmar cosas como
// "se emitió exactamente UN AuctionFinished" o "no se notificó una puja rechazada".
public sealed class RecordingNotifier(IAuctionNotifier inner) : IAuctionNotifier
{
    public ConcurrentQueue<object> Events { get; } = new();

    public IReadOnlyList<T> Of<T>() => Events.OfType<T>().ToList();

    public void Clear() => Events.Clear();

    public Task BidPlacedAsync(BidPlacedEvent notification, CancellationToken cancellationToken = default)
    {
        Events.Enqueue(notification);
        return inner.BidPlacedAsync(notification, cancellationToken);
    }

    public Task AuctionStatusChangedAsync(AuctionStatusChangedEvent notification, CancellationToken cancellationToken = default)
    {
        Events.Enqueue(notification);
        return inner.AuctionStatusChangedAsync(notification, cancellationToken);
    }

    public Task AuctionFinishedAsync(AuctionFinishedEvent notification, CancellationToken cancellationToken = default)
    {
        Events.Enqueue(notification);
        return inner.AuctionFinishedAsync(notification, cancellationToken);
    }
}

[CollectionDefinition(Name)]
public sealed class IntegrationFixtureGroup : ICollectionFixture<LiveAuctionFactory>
{
    public const string Name = "Integration";
}
