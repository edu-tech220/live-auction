using LiveAuction.Domain.Users;

namespace LiveAuction.Domain.Auctions;

// Oferta aceptada. Es inmutable: una puja registrada nunca se edita ni se borra (auditoría).
// Solo Auction.PlaceBid puede crearla, así no existe una puja que no pasó las reglas.
public sealed class Bid
{
    private Bid() { }

    internal Bid(int auctionId, int bidderId, decimal amount, DateTimeOffset placedAt)
    {
        AuctionId = auctionId;
        BidderId = bidderId;
        Amount = amount;
        PlacedAt = placedAt;
    }

    public long Id { get; private set; }
    public int AuctionId { get; private set; }
    public int BidderId { get; private set; }
    public decimal Amount { get; private set; }
    public DateTimeOffset PlacedAt { get; private set; }

    public Auction Auction { get; private set; } = null!;
    public User Bidder { get; private set; } = null!;
}
