using LiveAuction.Application.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace LiveAuction.Api.Realtime;

// Implementación de IAuctionNotifier con SignalR. IHubContext permite enviar mensajes desde
// fuera del Hub (servicios, worker) a grupos de conexiones.
public sealed class SignalRAuctionNotifier(IHubContext<AuctionHub, IAuctionClient> hub) : IAuctionNotifier
{
    public Task BidPlacedAsync(BidPlacedEvent notification, CancellationToken cancellationToken = default) =>
        // Grupo de la subasta (detalle) + lobby (precio en la lista), en un solo envío.
        hub.Clients.Groups(AuctionHub.AuctionGroup(notification.AuctionId), AuctionHub.LobbyGroup).BidPlaced(notification);

    public Task AuctionStatusChangedAsync(AuctionStatusChangedEvent notification, CancellationToken cancellationToken = default) =>
        hub.Clients.Groups(AuctionHub.AuctionGroup(notification.AuctionId), AuctionHub.LobbyGroup).AuctionStatusChanged(notification);

    public Task AuctionFinishedAsync(AuctionFinishedEvent notification, CancellationToken cancellationToken = default) =>
        hub.Clients.Groups(AuctionHub.AuctionGroup(notification.AuctionId), AuctionHub.LobbyGroup).AuctionFinished(notification);
}
