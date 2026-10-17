using System.Net;
using System.Net.Http.Json;
using LiveAuction.Application.Abstractions;
using LiveAuction.Application.Auctions;
using LiveAuction.Application.Bidding;
using LiveAuction.Domain;
using LiveAuction.IntegrationTests.Infrastructure;

namespace LiveAuction.IntegrationTests;

public class AuctionAndBiddingTests(LiveAuctionFactory factory) : IntegrationTestBase(factory)
{
    // ---------------- Administración de subastas ----------------

    [Fact]
    public async Task Admin_creates_auction_with_location_header()
    {
        var admin = await CreateAdminAsync();

        var response = await admin.Client.PostAsJsonAsync("/api/auctions", new
        {
            title = "Cámara Leica", startingPrice = 500.50m, minIncrement = 25m,
            startsAt = Now.AddHours(1), endsAt = Now.AddHours(2)
        }, Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var auction = (await response.Content.ReadFromJsonAsync<AuctionDetailsDto>(Json))!;
        Assert.Equal($"/api/auctions/{auction.Id}", response.Headers.Location!.AbsolutePath);
        Assert.Equal(AuctionStatus.Scheduled, auction.Status);
        Assert.Equal(500.50m, auction.CurrentPrice);
        Assert.Equal(500.50m, auction.MinimumNextBid);
    }

    [Fact]
    public async Task Invalid_schedule_and_prices_are_rejected()
    {
        var admin = await CreateAdminAsync();

        var badSchedule = await admin.Client.PostAsJsonAsync("/api/auctions", new
        {
            title = "Fechas al revés", startingPrice = 10, minIncrement = 1, startsAt = Now.AddHours(2), endsAt = Now.AddHours(1)
        }, Json);
        var badPrice = await admin.Client.PostAsJsonAsync("/api/auctions", new
        {
            title = "Precio raro", startingPrice = 10.123m, minIncrement = 1, startsAt = Now, endsAt = Now.AddHours(1)
        }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, badSchedule.StatusCode);
        Assert.Equal("InvalidSchedule", await ProblemCodeAsync(badSchedule));
        Assert.Equal("InvalidPrices", await ProblemCodeAsync(badPrice));
    }

    [Fact]
    public async Task Auction_can_only_be_edited_while_scheduled()
    {
        var admin = await CreateAdminAsync();
        var scheduled = await CreateAuctionAsync(admin, startsIn: TimeSpan.FromHours(1), endsIn: TimeSpan.FromHours(2));
        var running = await CreateAuctionAsync(admin);

        object Body(string title) => new { title, startingPrice = 200, minIncrement = 20, startsAt = Now.AddHours(1), endsAt = Now.AddHours(3) };

        var ok = await admin.Client.PutAsJsonAsync($"/api/auctions/{scheduled.Id}", Body("Editada"), Json);
        var conflict = await admin.Client.PutAsJsonAsync($"/api/auctions/{running.Id}", Body("Otro título"), Json);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Editada", (await ok.Content.ReadFromJsonAsync<AuctionDetailsDto>(Json))!.Title);
        Assert.Equal("AuctionNotEditable", await ProblemCodeAsync(conflict));
    }

    [Fact]
    public async Task List_filters_by_status_and_paginates()
    {
        var admin = await CreateAdminAsync();
        for (var i = 0; i < 3; i++) await CreateAuctionAsync(admin);
        await CreateAuctionAsync(admin, startsIn: TimeSpan.FromHours(1), endsIn: TimeSpan.FromHours(2));

        var scheduled = await Factory.CreateClient().GetFromJsonAsync<PagedResult<AuctionSummaryDto>>("/api/auctions?status=Scheduled", Json);
        var page = await Factory.CreateClient().GetFromJsonAsync<PagedResult<AuctionSummaryDto>>("/api/auctions?page=2&pageSize=3", Json);

        Assert.Equal(4, scheduled!.TotalCount); // todas siguen "Scheduled" hasta la 1a puja / el worker
        Assert.Single(page!.Items);
        Assert.Equal(2, page.TotalPages);
    }

    [Fact]
    public async Task Cancel_notifies_and_blocks_bids()
    {
        var admin = await CreateAdminAsync();
        var ana = await CreateBidderAsync("Ana");
        var auction = await CreateAuctionAsync(admin);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.PostAsync($"/api/auctions/{auction.Id}/cancel", null)).StatusCode);

        var bid = await BidAsync(ana, auction.Id, 100);
        Assert.Equal("AuctionCancelled", await ProblemCodeAsync(bid));
        Assert.Equal(AuctionStatus.Cancelled, (await GetAuctionAsync(auction.Id)).Status);
        Assert.Contains(Factory.Notifications.Of<AuctionStatusChangedEvent>(), e => e.Status == AuctionStatus.Cancelled);
    }

    // ---------------- Pujas ----------------

    [Fact]
    public async Task Valid_bid_is_persisted_then_notified()
    {
        var admin = await CreateAdminAsync();
        var ana = await CreateBidderAsync("Ana");
        var auction = await CreateAuctionAsync(admin, startingPrice: 100, minIncrement: 10);

        var response = await BidAsync(ana, auction.Id, 100);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var placed = (await response.Content.ReadFromJsonAsync<PlaceBidResponse>(Json))!;
        Assert.Equal(110m, placed.MinimumNextBid);

        var details = await GetAuctionAsync(auction.Id);
        Assert.Equal(AuctionStatus.Active, details.Status);
        Assert.Equal(100m, details.CurrentPrice);
        Assert.Equal("ana", details.HighestBidderName!.ToLowerInvariant());

        var evt = Assert.Single(Factory.Notifications.Of<BidPlacedEvent>());
        Assert.Equal(placed.Bid.Id, evt.BidId); // el evento lleva el Id que asignó la BD => ya estaba guardada
    }

    [Fact]
    public async Task Server_ignores_client_price_and_rejects_low_bids_without_notifying()
    {
        var admin = await CreateAdminAsync();
        var ana = await CreateBidderAsync("Ana");
        var luis = await CreateBidderAsync("Luis");
        var auction = await CreateAuctionAsync(admin, startingPrice: 100, minIncrement: 10);
        await BidAsync(ana, auction.Id, 150);

        // Aunque el cliente "crea" que el precio es 100, el servidor usa el de la BD (150).
        var low = await BidAsync(luis, auction.Id, 155);

        Assert.Equal(HttpStatusCode.Conflict, low.StatusCode);
        Assert.Equal("BidTooLow", await ProblemCodeAsync(low));
        Assert.Single(Factory.Notifications.Of<BidPlacedEvent>()); // solo la válida
    }

    [Fact]
    public async Task Bids_after_end_time_are_rejected_even_before_the_closer_runs()
    {
        var admin = await CreateAdminAsync();
        var ana = await CreateBidderAsync("Ana");
        var auction = await CreateAuctionAsync(admin, endsIn: TimeSpan.FromMinutes(5));

        Factory.Clock.UtcNow = Now.AddMinutes(5); // exactamente la hora de fin

        var response = await BidAsync(ana, auction.Id, 100);
        Assert.Equal("AuctionEnded", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Bids_before_start_are_rejected()
    {
        var admin = await CreateAdminAsync();
        var ana = await CreateBidderAsync("Ana");
        var auction = await CreateAuctionAsync(admin, startsIn: TimeSpan.FromMinutes(5), endsIn: TimeSpan.FromMinutes(30));

        Assert.Equal("AuctionNotStarted", await ProblemCodeAsync(await BidAsync(ana, auction.Id, 100)));
    }

    [Fact]
    public async Task Seller_and_current_leader_cannot_bid()
    {
        var admin = await CreateAdminAsync();
        var ana = await CreateBidderAsync("Ana");
        var auction = await CreateAuctionAsync(admin);
        await BidAsync(ana, auction.Id, 100);

        Assert.Equal("SellerCannotBid", await ProblemCodeAsync(await BidAsync(admin, auction.Id, 500)));
        Assert.Equal("AlreadyHighestBidder", await ProblemCodeAsync(await BidAsync(ana, auction.Id, 500)));
    }

    [Fact]
    public async Task History_is_public_without_emails_and_my_bids_are_private()
    {
        var admin = await CreateAdminAsync();
        var ana = await CreateBidderAsync("Ana");
        var luis = await CreateBidderAsync("Luis");
        var auction = await CreateAuctionAsync(admin);
        await BidAsync(ana, auction.Id, 100);
        await BidAsync(luis, auction.Id, 120);

        var history = await Factory.CreateClient().GetStringAsync($"/api/auctions/{auction.Id}/bids");
        Assert.DoesNotContain("@test.local", history, StringComparison.OrdinalIgnoreCase);

        var page = await Factory.CreateClient().GetFromJsonAsync<PagedResult<BidDto>>($"/api/auctions/{auction.Id}/bids", Json);
        Assert.Equal([120m, 100m], page!.Items.Select(b => b.Amount));

        var anaBids = await ana.Client.GetFromJsonAsync<List<MyBidDto>>("/api/me/bids", Json);
        var mine = Assert.Single(anaBids!);
        Assert.Equal(100m, mine.Amount);
        Assert.False(mine.IsHighest);
    }

    [Fact]
    public void Per_user_bid_throttle_limits_each_user_independently()
    {
        // Factory usa un límite holgado; aquí se prueba el limitador real con un límite bajo.
        using var throttle = new LiveAuction.Api.Common.BidThrottle(tokensPerPeriod: 2, periodSeconds: 60);

        Assert.True(throttle.TryAcquire(1));
        Assert.True(throttle.TryAcquire(1));
        Assert.False(throttle.TryAcquire(1)); // tercera en la ventana: bloqueada
        Assert.True(throttle.TryAcquire(2));  // otro usuario tiene su propio cubo
    }
}
