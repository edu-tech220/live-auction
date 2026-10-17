using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveAuction.Application.Abstractions;
using LiveAuction.Application.Auctions;
using LiveAuction.Application.Auth;
using LiveAuction.Domain;
using LiveAuction.Domain.Users;
using LiveAuction.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace LiveAuction.IntegrationTests.Infrastructure;

[Collection(IntegrationFixtureGroup.Name)]
public abstract class IntegrationTestBase(LiveAuctionFactory factory) : IAsyncLifetime
{
    protected LiveAuctionFactory Factory { get; } = factory;
    protected DateTimeOffset Now => Factory.Clock.UtcNow;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly List<HubConnection> _hubs = [];

    public Task InitializeAsync() => Factory.ResetAsync();

    public async Task DisposeAsync()
    {
        foreach (var hub in _hubs)
            await hub.DisposeAsync();
    }

    // ---------------- Usuarios ----------------

    protected sealed record TestUser(int Id, string Email, string Token, HttpClient Client);

    // Postor normal: se registra por la API (camino real).
    protected async Task<TestUser> CreateBidderAsync(string name)
    {
        var email = $"{name.ToLowerInvariant()}@test.local";
        var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email, displayName = name, password = "Secreta123" }, Json);
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new TestUser(auth.User.Id, email, auth.AccessToken, client);
    }

    // Administrador: no existe registro público de admins, se inserta directo y luego hace login.
    protected async Task<TestUser> CreateAdminAsync(string name = "Admin")
    {
        var email = $"{name.ToLowerInvariant()}@test.local";
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var admin = User.Create(email, name, "", UserRole.Admin, Now);
            admin.ChangePasswordHash(hasher.Hash(admin, "Secreta123"));
            db.Users.Add(admin);
            await db.SaveChangesAsync();
        }

        var client = Factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Secreta123" }, Json);
        login.EnsureSuccessStatusCode();
        var auth = (await login.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new TestUser(auth.User.Id, email, auth.AccessToken, client);
    }

    // ---------------- Subastas ----------------

    // Activa por defecto: empezó hace 1 minuto y termina en 10.
    protected async Task<AuctionDetailsDto> CreateAuctionAsync(
        TestUser admin, decimal startingPrice = 100m, decimal minIncrement = 10m,
        TimeSpan? startsIn = null, TimeSpan? endsIn = null)
    {
        var response = await admin.Client.PostAsJsonAsync("/api/auctions", new
        {
            title = "Reloj de prueba",
            description = "Artículo de prueba",
            startingPrice,
            minIncrement,
            startsAt = Now + (startsIn ?? TimeSpan.FromMinutes(-1)),
            endsAt = Now + (endsIn ?? TimeSpan.FromMinutes(10))
        }, Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuctionDetailsDto>(Json))!;
    }

    protected static Task<HttpResponseMessage> BidAsync(TestUser user, int auctionId, decimal amount) =>
        user.Client.PostAsJsonAsync($"/api/auctions/{auctionId}/bids", new { amount }, Json);

    protected async Task<AuctionDetailsDto> GetAuctionAsync(int id)
    {
        var response = await Factory.CreateClient().GetAsync($"/api/auctions/{id}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuctionDetailsDto>(Json))!;
    }

    protected static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    // Ejecuta un servicio scoped de Application (como lo haría el worker).
    protected async Task<T> WithServiceAsync<TService, T>(Func<TService, Task<T>> action) where TService : notnull
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    // ---------------- SignalR ----------------

    // Cliente SignalR real contra el TestServer. TestServer no expone sockets de red, así que se
    // usa Long Polling por su HttpMessageHandler; el Hub y los grupos son exactamente los mismos.
    protected async Task<HubConnection> ConnectHubAsync(TestUser? user = null)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(Factory.Server.BaseAddress, "hubs/auctions"), options =>
            {
                options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (user is not null)
                    options.AccessTokenProvider = () => Task.FromResult<string?>(user.Token);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

        await hub.StartAsync();
        _hubs.Add(hub);
        return hub;
    }

    // Espera el primer evento que cumpla la condición, con tiempo límite.
    protected static Task<T> NextEventAsync<T>(HubConnection hub, string eventName, Func<T, bool>? predicate = null)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? subscription = null;
        subscription = hub.On<T>(eventName, e =>
        {
            if (predicate is null || predicate(e))
            {
                tcs.TrySetResult(e);
                subscription?.Dispose();
            }
        });
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // Recolecta todos los eventos de un tipo (para afirmar que NO llegó nada).
    protected static List<T> Collect<T>(HubConnection hub, string eventName)
    {
        var list = new List<T>();
        hub.On<T>(eventName, e => { lock (list) list.Add(e); });
        return list;
    }

    protected static AuctionStatus StatusOf(AuctionDetailsDto auction) => auction.Status;
}
