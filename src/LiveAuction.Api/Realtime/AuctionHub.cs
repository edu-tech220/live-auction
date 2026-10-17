using LiveAuction.Api.Common;
using LiveAuction.Application.Auctions;
using LiveAuction.Application.Bidding;
using LiveAuction.Application.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LiveAuction.Api.Realtime;

// Hub de subastas (/hubs/auctions).
//
// - Ver una subasta es público: JoinAuction/LeaveAuction no exigen login.
// - Pujar exige JWT válido: [Authorize] en PlaceBid. El usuario sale de Context.User (token),
//   nunca de un parámetro: un cliente no puede pujar a nombre de otro.
// - Cada subasta es un GRUPO de SignalR ("auction-{id}"): los eventos de una subasta solo llegan
//   a quien se unió a ella. El grupo "lobby" recibe cambios de todas (para la lista).
// - El Hub NO tiene reglas de negocio: delega en los mismos servicios que usa la API REST.
//
// Una instancia del Hub vive solo lo que dura UNA invocación: no guardar estado en campos.
public sealed partial class AuctionHub(
    IAuctionService auctions,
    IBidService bids,
    ConnectionTracker tracker,
    BidThrottle throttle,
    ILogger<AuctionHub> logger) : Hub<IAuctionClient>
{
    public const string LobbyGroup = "lobby";
    public static string AuctionGroup(int auctionId) => $"auction-{auctionId}";

    // Devuelve el estado actual: al conectarse (o RECONECTARSE) el cliente se sincroniza con la
    // fuente de verdad, por si se perdió eventos mientras estuvo desconectado.
    [AllowAnonymous]
    public async Task<AuctionDetailsDto> JoinAuction(int auctionId)
    {
        var details = await auctions.GetAsync(auctionId, Context.ConnectionAborted);
        if (details.IsFailure)
            throw new HubException(details.Error!.Message); // llega al cliente como error de invoke()

        await Groups.AddToGroupAsync(Context.ConnectionId, AuctionGroup(auctionId), Context.ConnectionAborted);
        var viewers = tracker.Join(Context.ConnectionId, auctionId);
        await Clients.Group(AuctionGroup(auctionId)).ViewersChanged(new ViewersChangedEvent(auctionId, viewers));

        return details.Value;
    }

    [AllowAnonymous]
    public async Task LeaveAuction(int auctionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, AuctionGroup(auctionId), Context.ConnectionAborted);
        var viewers = tracker.Leave(Context.ConnectionId, auctionId);
        await Clients.Group(AuctionGroup(auctionId)).ViewersChanged(new ViewersChangedEvent(auctionId, viewers));
    }

    [AllowAnonymous]
    public Task JoinLobby() => Groups.AddToGroupAsync(Context.ConnectionId, LobbyGroup, Context.ConnectionAborted);

    [AllowAnonymous]
    public Task LeaveLobby() => Groups.RemoveFromGroupAsync(Context.ConnectionId, LobbyGroup, Context.ConnectionAborted);

    [Authorize]
    public async Task<BidOutcome> PlaceBid(int auctionId, decimal amount)
    {
        var userId = Context.User!.GetUserId();

        if (!throttle.TryAcquire(userId))
            return await RejectAsync(auctionId, amount, "TooManyBids", "Estás pujando demasiado rápido. Espera unos segundos.");

        var result = await bids.PlaceBidAsync(auctionId, userId, amount, Context.ConnectionAborted);
        if (result.IsFailure)
            return await RejectAsync(auctionId, amount, result.Error!.Code, result.Error.Message);

        // El evento BidPlaced para todo el grupo ya lo emitió BidService (vía IAuctionNotifier)
        // DESPUÉS de guardar. Aquí solo se responde al invocador.
        var placed = result.Value;
        return new BidOutcome(true, null, null, placed.CurrentPrice, placed.MinimumNextBid);
    }

    public override async Task OnConnectedAsync()
    {
        if (logger.IsEnabled(LogLevel.Debug)) // no calcular argumentos si el log está apagado
        {
            var userId = Context.User?.TryGetUserId();
            LogConnected(Context.ConnectionId, userId);
        }
        await base.OnConnectedAsync();
    }

    // SignalR ya sacó la conexión de sus grupos; aquí se actualiza el conteo de espectadores.
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var (auctionId, viewers) in tracker.RemoveConnection(Context.ConnectionId))
            await Clients.Group(AuctionGroup(auctionId)).ViewersChanged(new ViewersChangedEvent(auctionId, viewers));

        LogDisconnected(Context.ConnectionId, exception?.Message);
        await base.OnDisconnectedAsync(exception);
    }

    private async Task<BidOutcome> RejectAsync(int auctionId, decimal amount, string code, string message)
    {
        // Solo al que pujó: el rechazo no le interesa (ni le corresponde saberlo) a nadie más.
        await Clients.Caller.BidRejected(new BidRejectedEvent(auctionId, amount, code, message));
        return new BidOutcome(false, code, message, null, null);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Conexión {ConnectionId} abierta (usuario {UserId}).")]
    private partial void LogConnected(string connectionId, int? userId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Conexión {ConnectionId} cerrada. Motivo: {Reason}")]
    private partial void LogDisconnected(string connectionId, string? reason);
}
