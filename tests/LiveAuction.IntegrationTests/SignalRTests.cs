using LiveAuction.Api.Realtime;
using LiveAuction.Application.Abstractions;
using LiveAuction.Application.Auctions;
using LiveAuction.Application.Lifecycle;
using LiveAuction.Domain;
using LiveAuction.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace LiveAuction.IntegrationTests;

// Clientes SignalR reales conectados al Hub: lo que vería un navegador.
public class SignalRTests(LiveAuctionFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task JoinAuction_returns_current_state_including_server_time()
    {
        var admin = await CreateAdminAsync();
        var auction = await CreateAuctionAsync(admin, startingPrice: 250);
        var viewer = await ConnectHubAsync(); // anónimo: ver es público

        var snapshot = await viewer.InvokeAsync<AuctionDetailsDto>("JoinAuction", auction.Id);

        Assert.Equal(250m, snapshot.CurrentPrice);
        Assert.Equal(Now, snapshot.ServerTime);
    }

    [Fact]
    public async Task Bid_via_hub_is_broadcast_to_everyone_in_that_auction_only()
    {
        var admin = await CreateAdminAsync();
        var ana = await CreateBidderAsync("Ana");
        var auction = await CreateAuctionAsync(admin, startingPrice: 100, minIncrement: 10);
        var other = await CreateAuctionAsync(admin);

        var anaHub = await ConnectHubAsync(ana);
        var watcher = await ConnectHubAsync();
        var otherWatcher = await ConnectHubAsync();
        await anaHub.InvokeAsync<AuctionDetailsDto>("JoinAuction", auction.Id);
        await watcher.InvokeAsync<AuctionDetailsDto>("JoinAuction", auction.Id);
        await otherWatcher.InvokeAsync<AuctionDetailsDto>("JoinAuction", other.Id);

        var leaked = Collect<BidPlacedEvent>(otherWatcher, nameof(IAuctionClient.BidPlaced));
        var received = NextEventAsync<BidPlacedEvent>(watcher, nameof(IAuctionClient.BidPlaced));

        var outcome = await anaHub.InvokeAsync<BidOutcome>("PlaceBid", auction.Id, 100m);

        Assert.True(outcome.Accepted);
        var evt = await received;
        Assert.Equal(100m, evt.Amount);
        Assert.Equal("Ana", evt.BidderName);
        Assert.Equal(110m, evt.MinimumNextBid);

        await Task.Delay(300);
        Assert.Empty(leaked); // otra subasta = otro grupo: no recibe nada
    }

    [Fact]
    public async Task Rejected_bid_is_sent_only_to_the_caller()
    {
        var admin = await CreateAdminAsync();
        var ana = await CreateBidderAsync("Ana");
        var auction = await CreateAuctionAsync(admin, startingPrice: 100);

        var anaHub = await ConnectHubAsync(ana);
        var watcher = await ConnectHubAsync();
        await anaHub.InvokeAsync<AuctionDetailsDto>("JoinAuction", auction.Id);
        await watcher.InvokeAsync<AuctionDetailsDto>("JoinAuction", auction.Id);

        var watcherRejections = Collect<BidRejectedEvent>(watcher, nameof(IAuctionClient.BidRejected));
        var watcherBids = Collect<BidPlacedEvent>(watcher, nameof(IAuctionClient.BidPlaced));
        var anaRejection = NextEventAsync<BidRejectedEvent>(anaHub, nameof(IAuctionClient.BidRejected));

        var outcome = await anaHub.InvokeAsync<BidOutcome>("PlaceBid", auction.Id, 50m);

        Assert.False(outcome.Accepted);
        Assert.Equal("BidTooLow", outcome.Code);
        Assert.Equal("BidTooLow", (await anaRejection).Code);

        await Task.Delay(300);
        Assert.Empty(watcherRejections);
        Assert.Empty(watcherBids);
    }

    [Fact]
    public async Task Anonymous_connection_cannot_place_bids()
    {
        var admin = await CreateAdminAsync();
        var auction = await CreateAuctionAsync(admin);
        var anonymous = await ConnectHubAsync();

        var error = await Assert.ThrowsAsync<HubException>(() => anonymous.InvokeAsync<BidOutcome>("PlaceBid", auction.Id, 100m));
        Assert.Contains("unauthorized", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Joining_a_missing_auction_fails_cleanly()
    {
        var hub = await ConnectHubAsync();

        var error = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<AuctionDetailsDto>("JoinAuction", 999999));
        Assert.Contains("no existe", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Viewer_count_follows_joins_and_disconnections()
    {
        var admin = await CreateAdminAsync();
        var auction = await CreateAuctionAsync(admin);

        var first = await ConnectHubAsync();
        await first.InvokeAsync<AuctionDetailsDto>("JoinAuction", auction.Id);

        var twoViewers = NextEventAsync<ViewersChangedEvent>(first, nameof(IAuctionClient.ViewersChanged), e => e.Viewers == 2);
        var second = await ConnectHubAsync();
        await second.InvokeAsync<AuctionDetailsDto>("JoinAuction", auction.Id);
        Assert.Equal(2, (await twoViewers).Viewers);

        var oneViewer = NextEventAsync<ViewersChangedEvent>(first, nameof(IAuctionClient.ViewersChanged), e => e.Viewers == 1);
        await second.StopAsync(); // desconexión
        Assert.Equal(1, (await oneViewer).Viewers);
    }

    [Fact]
    public async Task Clients_receive_the_final_result_when_the_auction_closes()
    {
        var admin = await CreateAdminAsync();
        var ana = await CreateBidderAsync("Ana");
        var auction = await CreateAuctionAsync(admin, endsIn: TimeSpan.FromMinutes(5));
        await BidAsync(ana, auction.Id, 100);

        var watcher = await ConnectHubAsync();
        await watcher.InvokeAsync<AuctionDetailsDto>("JoinAuction", auction.Id);
        var finished = NextEventAsync<AuctionFinishedEvent>(watcher, nameof(IAuctionClient.AuctionFinished));

        Factory.Clock.UtcNow = Now.AddMinutes(5);
        await WithServiceAsync<IAuctionLifecycleService, LifecycleRunResult>(s => s.RunAsync(default));

        var result = await finished;
        Assert.Equal(AuctionStatus.Finished, result.Status);
        Assert.Equal("Ana", result.WinnerName);
        Assert.Equal(100m, result.FinalPrice);
    }

    [Fact]
    public async Task Lobby_receives_price_changes_of_every_auction()
    {
        var admin = await CreateAdminAsync();
        var ana = await CreateBidderAsync("Ana");
        var auction = await CreateAuctionAsync(admin);

        var lobby = await ConnectHubAsync();
        await lobby.InvokeAsync("JoinLobby");
        var update = NextEventAsync<BidPlacedEvent>(lobby, nameof(IAuctionClient.BidPlaced));

        await BidAsync(ana, auction.Id, 100); // vía REST: el evento sale igual por SignalR

        Assert.Equal(auction.Id, (await update).AuctionId);
    }
}
