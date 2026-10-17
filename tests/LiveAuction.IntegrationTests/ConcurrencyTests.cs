using System.Net;
using LiveAuction.Application.Abstractions;
using LiveAuction.Infrastructure.Persistence;
using LiveAuction.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveAuction.IntegrationTests;

// Muchos usuarios pujando EN PARALELO contra SQL Server real. Estas pruebas fallarían con una
// implementación ingenua (leer precio => validar => guardar sin control de concurrencia).
public class ConcurrencyTests(LiveAuctionFactory factory) : IntegrationTestBase(factory)
{
    private const int Bidders = 20;

    [Fact]
    public async Task Simultaneous_bids_with_the_same_amount_only_one_wins()
    {
        var admin = await CreateAdminAsync();
        var auction = await CreateAuctionAsync(admin, startingPrice: 100, minIncrement: 10);
        var users = await Task.WhenAll(Enumerable.Range(1, Bidders).Select(i => CreateBidderAsync($"Postor{i}")));

        // Todos ven precio 100 y pujan 100 a la vez.
        var responses = await Task.WhenAll(users.Select(u => BidAsync(u, auction.Id, 100)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created),
            r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode)); // BidTooLow / BidContention

        await AssertConsistentAsync(auction.Id, expectedBids: 1);
        Assert.Single(Factory.Notifications.Of<BidPlacedEvent>()); // un solo anuncio de éxito
    }

    [Fact]
    public async Task Simultaneous_increasing_bids_keep_price_bids_and_events_consistent()
    {
        var admin = await CreateAdminAsync();
        var auction = await CreateAuctionAsync(admin, startingPrice: 100, minIncrement: 10);
        var users = await Task.WhenAll(Enumerable.Range(1, Bidders).Select(i => CreateBidderAsync($"Postor{i}")));

        // Montos distintos (100, 110, ..., 290) disparados al mismo tiempo en orden aleatorio.
        var amounts = Enumerable.Range(0, Bidders).Select(i => 100m + (i * 10m)).OrderBy(_ => Random.Shared.Next()).ToArray();
        var responses = await Task.WhenAll(users.Select((u, i) => BidAsync(u, auction.Id, amounts[i])));

        var accepted = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.True(accepted >= 1);

        // Invariantes: lo que dice la subasta coincide con la tabla Bids y con los eventos emitidos.
        var bids = await AssertConsistentAsync(auction.Id, expectedBids: accepted);
        Assert.Equal(accepted, Factory.Notifications.Of<BidPlacedEvent>().Count);

        // Cada puja aceptada supera a la anterior por al menos el incremento mínimo.
        var ordered = bids.OrderBy(b => b.PlacedAt).ThenBy(b => b.Id).Select(b => b.Amount).ToList();
        for (var i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i] >= ordered[i - 1] + 10m, $"Puja {ordered[i]} no supera {ordered[i - 1]} + 10");
    }

    [Fact]
    public async Task Concurrent_closers_produce_a_single_winner_and_a_single_notification()
    {
        var admin = await CreateAdminAsync();
        var ana = await CreateBidderAsync("Ana");
        var luis = await CreateBidderAsync("Luis");
        var auction = await CreateAuctionAsync(admin, endsIn: TimeSpan.FromMinutes(5));
        await BidAsync(ana, auction.Id, 100);
        await BidAsync(luis, auction.Id, 150);

        Factory.Clock.UtcNow = Now.AddMinutes(6);

        // 10 "workers" (p. ej. varias instancias de la API) intentan cerrar a la vez.
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            WithServiceAsync<LiveAuction.Application.Lifecycle.IAuctionLifecycleService, bool>(s => s.CloseAsync(auction.Id, default))));

        Assert.Equal(1, results.Count(r => r));
        var finished = Assert.Single(Factory.Notifications.Of<AuctionFinishedEvent>());
        Assert.Equal(luis.Id, finished.WinnerId);
        Assert.Equal(150m, finished.FinalPrice);
    }

    // Lee la BD directamente y verifica que la subasta y sus pujas no se contradicen.
    private async Task<List<LiveAuction.Domain.Auctions.Bid>> AssertConsistentAsync(int auctionId, int expectedBids)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var auction = await db.Auctions.AsNoTracking().SingleAsync(a => a.Id == auctionId);
        var bids = await db.Bids.AsNoTracking().Where(b => b.AuctionId == auctionId).ToListAsync();

        Assert.Equal(expectedBids, bids.Count);
        Assert.Equal(bids.Count, auction.BidCount);
        Assert.Equal(bids.Max(b => b.Amount), auction.CurrentPrice);
        Assert.Equal(bids.MaxBy(b => b.Amount)!.BidderId, auction.HighestBidderId);
        Assert.Equal(bids.Count, bids.Select(b => b.Amount).Distinct().Count()); // sin montos repetidos
        return bids;
    }
}
