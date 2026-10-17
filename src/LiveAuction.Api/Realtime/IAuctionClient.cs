using LiveAuction.Application.Abstractions;

namespace LiveAuction.Api.Realtime;

// Hub fuertemente tipado: en vez de Clients.Group(g).SendAsync("BidPlaced", data) (texto libre
// que se puede escribir mal), se llama a métodos de esta interfaz. El nombre del método es el
// nombre del evento que recibe JavaScript: connection.on("BidPlaced", ...).
public interface IAuctionClient
{
    Task BidPlaced(BidPlacedEvent notification);
    Task BidRejected(BidRejectedEvent notification);
    Task AuctionStatusChanged(AuctionStatusChangedEvent notification);
    Task AuctionFinished(AuctionFinishedEvent notification);
    Task ViewersChanged(ViewersChangedEvent notification);
}

// Solo para quien intentó pujar (Clients.Caller): los demás no necesitan saberlo.
public sealed record BidRejectedEvent(int AuctionId, decimal Amount, string Code, string Message);

public sealed record ViewersChangedEvent(int AuctionId, int Viewers);

// Lo que devuelve el método PlaceBid del Hub al cliente que lo invocó.
public sealed record BidOutcome(bool Accepted, string? Code, string? Message, decimal? CurrentPrice, decimal? MinimumNextBid);
