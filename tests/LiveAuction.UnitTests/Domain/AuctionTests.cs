using LiveAuction.Domain;
using LiveAuction.Domain.Auctions;

namespace LiveAuction.UnitTests.Domain;

// Pruebas del agregado Auction: puras (sin BD, sin reloj real). Cada prueba fija "now".
public class AuctionTests
{
    private const int Seller = 1;
    private const int Alice = 2;
    private const int Bob = 3;

    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = Now.AddMinutes(-10);
    private static readonly DateTimeOffset End = Now.AddMinutes(30);

    // Subasta activa: precio inicial 100, incremento 10.
    private static Auction ActiveAuction(decimal starting = 100m, decimal increment = 10m)
    {
        var created = Auction.Create(Seller, "Reloj vintage", "Desc", null, starting, increment, Start, End, Start.AddMinutes(-1));
        Assert.True(created.IsSuccess);
        return created.Value;
    }

    // ---------------- Creación ----------------

    [Fact]
    public void Create_starts_scheduled_with_current_price_equal_to_starting_price()
    {
        var auction = ActiveAuction();

        Assert.Equal(AuctionStatus.Scheduled, auction.Status);
        Assert.Equal(100m, auction.CurrentPrice);
        Assert.Equal(0, auction.BidCount);
        Assert.Equal(100m, auction.MinimumNextBid);
    }

    [Theory]
    [InlineData("", 100, 10, "TitleRequired")]
    [InlineData("Reloj", 0, 10, "InvalidPrices")]
    [InlineData("Reloj", 100, -1, "InvalidPrices")]
    [InlineData("Reloj", 100.001, 10, "InvalidPrices")]
    public void Create_validates_title_and_prices(string title, decimal starting, decimal increment, string code)
    {
        var result = Auction.Create(Seller, title, null, null, starting, increment, Start, End, Now);

        Assert.True(result.IsFailure);
        Assert.Equal(code, result.Error!.Code);
    }

    [Fact]
    public void Create_rejects_end_before_start_or_in_the_past()
    {
        Assert.Equal("InvalidSchedule", Auction.Create(Seller, "X", null, null, 10, 1, End, Start, Now).Error!.Code);
        Assert.Equal("InvalidSchedule", Auction.Create(Seller, "X", null, null, 10, 1, Now.AddHours(-2), Now.AddHours(-1), Now).Error!.Code);
    }

    // ---------------- Pujas válidas ----------------

    [Fact]
    public void First_bid_can_equal_starting_price_and_activates_auction()
    {
        var auction = ActiveAuction();

        var result = auction.PlaceBid(Alice, 100m, Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(AuctionStatus.Active, auction.Status);
        Assert.Equal(100m, auction.CurrentPrice);
        Assert.Equal(Alice, auction.HighestBidderId);
        Assert.Equal(1, auction.BidCount);
        Assert.Equal(110m, auction.MinimumNextBid);
        Assert.Equal(Now, result.Value.PlacedAt);
    }

    [Fact]
    public void Next_bid_must_be_at_least_current_plus_increment()
    {
        var auction = ActiveAuction();
        auction.PlaceBid(Alice, 100m, Now);

        var tooLow = auction.PlaceBid(Bob, 109.99m, Now);
        var exact = auction.PlaceBid(Bob, 110m, Now);

        Assert.Equal("BidTooLow", tooLow.Error!.Code);
        Assert.True(exact.IsSuccess);
        Assert.Equal(110m, auction.CurrentPrice);
    }

    // ---------------- Pujas rechazadas ----------------

    [Fact]
    public void Rejected_bid_does_not_change_state()
    {
        var auction = ActiveAuction();
        auction.PlaceBid(Alice, 150m, Now);

        var result = auction.PlaceBid(Bob, 151m, Now);

        Assert.True(result.IsFailure);
        Assert.Equal(150m, auction.CurrentPrice);
        Assert.Equal(Alice, auction.HighestBidderId);
        Assert.Equal(1, auction.BidCount);
    }

    [Fact]
    public void Cannot_bid_before_start() =>
        Assert.Equal("AuctionNotStarted", ActiveAuction().PlaceBid(Alice, 100m, Start.AddSeconds(-1)).Error!.Code);

    [Fact]
    public void Cannot_bid_at_or_after_end_even_if_not_closed_yet()
    {
        var auction = ActiveAuction();

        Assert.Equal("AuctionEnded", auction.PlaceBid(Alice, 100m, End).Error!.Code);
        Assert.Equal("AuctionEnded", auction.PlaceBid(Alice, 100m, End.AddMinutes(5)).Error!.Code);
    }

    [Fact]
    public void Seller_cannot_bid_on_own_auction() =>
        Assert.Equal("SellerCannotBid", ActiveAuction().PlaceBid(Seller, 100m, Now).Error!.Code);

    [Fact]
    public void Highest_bidder_cannot_outbid_themselves()
    {
        var auction = ActiveAuction();
        auction.PlaceBid(Alice, 100m, Now);

        Assert.Equal("AlreadyHighestBidder", auction.PlaceBid(Alice, 200m, Now).Error!.Code);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(120.555)]
    public void Invalid_amounts_are_rejected(decimal amount) =>
        Assert.Equal("InvalidAmount", ActiveAuction().PlaceBid(Alice, amount, Now).Error!.Code);

    [Fact]
    public void Cannot_bid_on_cancelled_auction()
    {
        var auction = ActiveAuction();
        auction.Cancel(Now);

        Assert.Equal("AuctionCancelled", auction.PlaceBid(Alice, 100m, Now).Error!.Code);
    }

    // ---------------- Cierre ----------------

    [Fact]
    public void Close_sets_winner_and_final_price()
    {
        var auction = ActiveAuction();
        auction.PlaceBid(Alice, 100m, Now);
        var winning = auction.PlaceBid(Bob, 130m, Now).Value;

        var closed = auction.Close(winning, End);

        Assert.True(closed.Value);
        Assert.Equal(AuctionStatus.Finished, auction.Status);
        Assert.Equal(Bob, auction.WinnerId);
        Assert.Equal(130m, auction.FinalPrice);
        Assert.Equal(End, auction.ClosedAt);
    }

    [Fact]
    public void Close_without_bids_finishes_without_winner()
    {
        var auction = ActiveAuction();

        Assert.True(auction.Close(null, End).Value);
        Assert.Null(auction.WinnerId);
        Assert.Null(auction.FinalPrice);
        Assert.Equal(AuctionStatus.Finished, auction.Status);
    }

    [Fact]
    public void Close_is_idempotent()
    {
        var auction = ActiveAuction();
        var bid = auction.PlaceBid(Alice, 100m, Now).Value;
        auction.Close(bid, End);

        var second = auction.Close(null, End.AddMinutes(1));

        Assert.False(second.Value);                // no hizo nada
        Assert.Equal(Alice, auction.WinnerId);     // el ganador no cambió
        Assert.Equal(End, auction.ClosedAt);
    }

    [Fact]
    public void Close_before_end_is_rejected() =>
        Assert.Equal("AuctionNotYetEnded", ActiveAuction().Close(null, Now).Error!.Code);

    [Fact]
    public void Cancelled_auction_is_never_closed_with_a_winner()
    {
        var auction = ActiveAuction();
        auction.Cancel(Now);

        Assert.False(auction.Close(null, End).Value);
        Assert.Equal(AuctionStatus.Cancelled, auction.Status);
    }

    // ---------------- Activación, edición y cancelación ----------------

    [Fact]
    public void Activate_only_within_window_and_once()
    {
        var auction = ActiveAuction();

        Assert.False(auction.Activate(Start.AddSeconds(-1)));
        Assert.True(auction.Activate(Now));
        Assert.False(auction.Activate(Now)); // ya activa
    }

    [Fact]
    public void Update_only_while_scheduled_without_bids()
    {
        var created = Auction.Create(Seller, "A", null, null, 10, 1, Now.AddHours(1), Now.AddHours(2), Now).Value;

        Assert.True(created.Update("B", null, null, 20, 2, Now.AddHours(1), Now.AddHours(3), Now).IsSuccess);
        Assert.Equal(20m, created.CurrentPrice);

        var running = ActiveAuction();
        Assert.Equal("AuctionNotEditable", running.Update("B", null, null, 20, 2, Start, End, Now).Error!.Code);
    }

    [Fact]
    public void Cannot_cancel_finished_auction()
    {
        var auction = ActiveAuction();
        auction.Close(null, End);

        Assert.Equal("AuctionNotCancellable", auction.Cancel(End).Error!.Code);
    }
}
