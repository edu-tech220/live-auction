using LiveAuction.Domain;

namespace LiveAuction.Application.Abstractions;

// Puerto de notificaciones en tiempo real. Los casos de uso llaman a esto DESPUÉS de que la BD
// confirmó el cambio. La implementación (Api) usa SignalR, pero Application no lo sabe: así los
// servicios se prueban con un notificador falso y SignalR podría cambiarse sin tocar reglas.
public interface IAuctionNotifier
{
    Task BidPlacedAsync(BidPlacedEvent notification, CancellationToken cancellationToken = default);
    Task AuctionStatusChangedAsync(AuctionStatusChangedEvent notification, CancellationToken cancellationToken = default);
    Task AuctionFinishedAsync(AuctionFinishedEvent notification, CancellationToken cancellationToken = default);
}

// Eventos (contratos JSON que reciben los clientes). Nunca incluyen correos ni datos privados.

// BidCount funciona como número de secuencia: si dos eventos llegan en desorden, el cliente
// descarta el que tenga BidCount menor al que ya muestra.
public sealed record BidPlacedEvent(
    int AuctionId,
    long BidId,
    decimal Amount,
    int BidderId,
    string BidderName,
    DateTimeOffset PlacedAt,
    int BidCount,
    decimal MinimumNextBid);

public sealed record AuctionStatusChangedEvent(int AuctionId, AuctionStatus Status, DateTimeOffset ChangedAt);

public sealed record AuctionFinishedEvent(
    int AuctionId,
    AuctionStatus Status,
    int? WinnerId,
    string? WinnerName,
    decimal? FinalPrice,
    int BidCount,
    DateTimeOffset ClosedAt);
