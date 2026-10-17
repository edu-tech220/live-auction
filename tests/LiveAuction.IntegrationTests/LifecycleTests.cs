using LiveAuction.Application.Abstractions;
using LiveAuction.Application.Lifecycle;
using LiveAuction.Domain;
using LiveAuction.IntegrationTests.Infrastructure;

namespace LiveAuction.IntegrationTests;

public class LifecycleTests(LiveAuctionFactory factory) : IntegrationTestBase(factory)
{
    private Task<LifecycleRunResult> RunLifecycleAsync() =>
        WithServiceAsync<IAuctionLifecycleService, LifecycleRunResult>(s => s.RunAsync(default));

    [Fact]
    public async Task Scheduled_auction_is_activated_when_start_time_arrives()
    {
        var admin = await CreateAdminAsync();
        var auction = await CreateAuctionAsync(admin, startsIn: TimeSpan.FromMinutes(5), endsIn: TimeSpan.FromMinutes(30));

        Assert.Equal(0, (await RunLifecycleAsync()).Activated);

        Factory.Clock.UtcNow = Now.AddMinutes(5);
        Assert.Equal(1, (await RunLifecycleAsync()).Activated);

        Assert.Equal(AuctionStatus.Active, (await GetAuctionAsync(auction.Id)).Status);
        Assert.Contains(Factory.Notifications.Of<AuctionStatusChangedEvent>(),
            e => e.AuctionId == auction.Id && e.Status == AuctionStatus.Active);
    }

    [Fact]
    public async Task Closing_picks_the_highest_bid_and_persists_the_result()
    {
        var admin = await CreateAdminAsync();
        var ana = await CreateBidderAsync("Ana");
        var luis = await CreateBidderAsync("Luis");
        var auction = await CreateAuctionAsync(admin, startingPrice: 100, minIncrement: 10, endsIn: TimeSpan.FromMinutes(5));
        await BidAsync(ana, auction.Id, 100);
        await BidAsync(luis, auction.Id, 110);
        await BidAsync(ana, auction.Id, 200);

        Factory.Clock.UtcNow = Now.AddMinutes(5);
        Assert.Equal(1, (await RunLifecycleAsync()).Closed);

        var closed = await GetAuctionAsync(auction.Id);
        Assert.Equal(AuctionStatus.Finished, closed.Status);
        Assert.Equal(ana.Id, closed.WinnerId);
        Assert.Equal(200m, closed.FinalPrice);
        Assert.Equal(Now, closed.ClosedAt);
    }

    [Fact]
    public async Task Auction_without_bids_finishes_without_winner()
    {
        var admin = await CreateAdminAsync();
        var auction = await CreateAuctionAsync(admin, endsIn: TimeSpan.FromMinutes(1));

        Factory.Clock.UtcNow = Now.AddMinutes(2);
        await RunLifecycleAsync();

        var closed = await GetAuctionAsync(auction.Id);
        Assert.Equal(AuctionStatus.Finished, closed.Status);
        Assert.Null(closed.WinnerId);
        Assert.Null(closed.FinalPrice);
        var evt = Assert.Single(Factory.Notifications.Of<AuctionFinishedEvent>());
        Assert.Null(evt.WinnerName);
    }

    [Fact]
    public async Task Running_the_closer_again_changes_nothing()
    {
        var admin = await CreateAdminAsync();
        var ana = await CreateBidderAsync("Ana");
        var auction = await CreateAuctionAsync(admin, endsIn: TimeSpan.FromMinutes(1));
        await BidAsync(ana, auction.Id, 100);

        Factory.Clock.UtcNow = Now.AddMinutes(2);
        var first = await RunLifecycleAsync();
        Factory.Clock.UtcNow = Now.AddMinutes(10);
        var second = await RunLifecycleAsync();

        Assert.Equal(1, first.Closed);
        Assert.Equal(0, second.Closed);
        Assert.Single(Factory.Notifications.Of<AuctionFinishedEvent>());
        Assert.Equal(ana.Id, (await GetAuctionAsync(auction.Id)).WinnerId);
    }

    [Fact]
    public async Task Overdue_auctions_are_closed_after_a_restart()
    {
        // Simula que el servidor estuvo apagado: varias subastas vencieron sin que nadie las cerrara.
        var admin = await CreateAdminAsync();
        var a1 = await CreateAuctionAsync(admin, endsIn: TimeSpan.FromMinutes(1));
        var a2 = await CreateAuctionAsync(admin, endsIn: TimeSpan.FromMinutes(2));
        var stillOpen = await CreateAuctionAsync(admin, endsIn: TimeSpan.FromHours(5));

        Factory.Clock.UtcNow = Now.AddHours(1); // "el servidor vuelve una hora después"
        var result = await RunLifecycleAsync();

        Assert.Equal(2, result.Closed);
        Assert.Equal(AuctionStatus.Finished, (await GetAuctionAsync(a1.Id)).Status);
        Assert.Equal(AuctionStatus.Finished, (await GetAuctionAsync(a2.Id)).Status);
        Assert.NotEqual(AuctionStatus.Finished, (await GetAuctionAsync(stillOpen.Id)).Status);
    }

    [Fact]
    public async Task Cancelled_auctions_are_never_closed_with_a_winner()
    {
        var admin = await CreateAdminAsync();
        var ana = await CreateBidderAsync("Ana");
        var auction = await CreateAuctionAsync(admin, endsIn: TimeSpan.FromMinutes(5));
        await BidAsync(ana, auction.Id, 100);
        await admin.Client.PostAsync($"/api/auctions/{auction.Id}/cancel", null);

        Factory.Clock.UtcNow = Now.AddMinutes(10);
        await RunLifecycleAsync();

        var details = await GetAuctionAsync(auction.Id);
        Assert.Equal(AuctionStatus.Cancelled, details.Status);
        Assert.Null(details.WinnerId);
        Assert.Empty(Factory.Notifications.Of<AuctionFinishedEvent>());
    }
}
