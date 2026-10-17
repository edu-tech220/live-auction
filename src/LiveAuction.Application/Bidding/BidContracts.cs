using System.ComponentModel.DataAnnotations;
using LiveAuction.Domain;

namespace LiveAuction.Application.Bidding;

// El cliente solo manda el MONTO. Nunca el usuario (sale del JWT) ni el precio actual
// (sale de la BD): así no puede suplantar a nadie ni "inventar" el precio vigente.
public sealed record PlaceBidRequest
{
    [Required, Range(typeof(decimal), "0.01", "1000000000")]
    public decimal? Amount { get; init; }
}

public sealed record BidDto(long Id, decimal Amount, int BidderId, string BidderName, DateTimeOffset PlacedAt);

public sealed record PlaceBidResponse(BidDto Bid, decimal CurrentPrice, decimal MinimumNextBid, int BidCount);

public sealed record MyBidDto(
    long BidId,
    int AuctionId,
    string AuctionTitle,
    decimal Amount,
    DateTimeOffset PlacedAt,
    AuctionStatus AuctionStatus,
    decimal CurrentPrice,
    bool IsHighest,
    bool IsWinner);
