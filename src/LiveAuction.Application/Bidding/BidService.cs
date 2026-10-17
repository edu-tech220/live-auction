using LiveAuction.Application.Abstractions;
using LiveAuction.Application.Auctions;
using LiveAuction.Domain.Auctions;
using LiveAuction.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LiveAuction.Application.Bidding;

public interface IBidService
{
    Task<Result<PlaceBidResponse>> PlaceBidAsync(int auctionId, int bidderId, decimal amount, CancellationToken ct);
    Task<Result<PagedResult<BidDto>>> GetBidsAsync(int auctionId, int page, int pageSize, CancellationToken ct);
    Task<IReadOnlyList<MyBidDto>> GetMyBidsAsync(int userId, CancellationToken ct);
}

// =============================================================================================
// Registrar una puja con varios usuarios pujando al mismo tiempo.
//
// Estrategia: CONCURRENCIA OPTIMISTA con RowVersion + reintento.
//   1. Leer la subasta (incluye su RowVersion).
//   2. El dominio valida contra el precio de ESA lectura.
//   3. SaveChanges => UPDATE Auctions ... WHERE Id = @id AND RowVersion = @leido  +  INSERT Bid,
//      en UNA transacción.
//   4. Si otro usuario guardó primero, el UPDATE afecta 0 filas => DbUpdateConcurrencyException,
//      la transacción se revierte (tampoco se inserta la puja) y se reintenta desde el paso 1
//      con el precio nuevo. Normalmente el reintento termina en "BidTooLow".
//   5. Solo cuando SaveChanges confirmó se notifica por SignalR.
//
// Red de seguridad adicional: índice único (AuctionId, Amount) en Bids. Dos pujas del mismo
// monto en la misma subasta son físicamente imposibles aunque falle todo lo demás.
//
// Alternativas evaluadas (docs/decisions.md): bloqueo pesimista (UPDLOCK) serializa a todos los
// postores en la fila aunque la mayoría vaya a ser rechazada; transacción SERIALIZABLE agrega
// rangos bloqueados e interbloqueos. Con pujas cortas, el optimista da mejor throughput y la
// misma garantía.
// =============================================================================================
public sealed partial class BidService(
    IApplicationDbContext db,
    IAuctionNotifier notifier,
    TimeProvider time,
    ILogger<BidService> logger) : IBidService
{
    public const int MaxAttempts = 5;

    public async Task<Result<PlaceBidResponse>> PlaceBidAsync(int auctionId, int bidderId, decimal amount, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // En cada intento se parte de cero: nada de entidades viejas en memoria.
            db.ChangeTracker.Clear();

            var auction = await db.Auctions.FirstOrDefaultAsync(a => a.Id == auctionId, ct);
            if (auction is null)
                return AuctionErrors.NotFound(auctionId);

            // La hora la pone el servidor, no el cliente.
            var placed = auction.PlaceBid(bidderId, amount, time.GetUtcNow());
            if (placed.IsFailure)
                return placed.Error!;

            var bid = placed.Value;
            db.Bids.Add(bid);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                LogConcurrencyRetry(auctionId, bidderId, attempt);
                continue; // otro guardó primero: reintentar con datos frescos
            }
            catch (DbUpdateException ex) when (db.IsUniqueConstraintViolation(ex))
            {
                LogConcurrencyRetry(auctionId, bidderId, attempt);
                continue; // alguien registró ese mismo monto en paralelo
            }

            // ---- Confirmado en SQL Server. A partir de aquí la puja existe. ----
            var bidderName = await db.Users.AsNoTracking()
                .Where(u => u.Id == bidderId).Select(u => u.DisplayName).FirstAsync(ct);

            var response = new PlaceBidResponse(
                new BidDto(bid.Id, bid.Amount, bidderId, bidderName, bid.PlacedAt),
                auction.CurrentPrice, auction.MinimumNextBid, auction.BidCount);

            await NotifySafelyAsync(new BidPlacedEvent(
                auctionId, bid.Id, bid.Amount, bidderId, bidderName, bid.PlacedAt,
                auction.BidCount, auction.MinimumNextBid), ct);

            return response;
        }

        // Contención extrema: tras varios intentos se pide al usuario reintentar.
        return Error.Conflict("BidContention", "Muchas ofertas simultáneas. Revisa el precio actual e intenta de nuevo.");
    }

    public async Task<Result<PagedResult<BidDto>>> GetBidsAsync(int auctionId, int page, int pageSize, CancellationToken ct)
    {
        if (!await db.Auctions.AnyAsync(a => a.Id == auctionId, ct))
            return AuctionErrors.NotFound(auctionId);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var bids = db.Bids.AsNoTracking().Where(b => b.AuctionId == auctionId);
        var total = await bids.CountAsync(ct);

        // Historial público: nombre visible del postor, nunca su correo.
        var items = await bids
            .OrderByDescending(b => b.Amount)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(b => new BidDto(b.Id, b.Amount, b.BidderId, b.Bidder.DisplayName, b.PlacedAt))
            .ToListAsync(ct);

        return new PagedResult<BidDto>(items, page, pageSize, total);
    }

    // Solo las pujas del usuario autenticado (el id sale del token, no de la URL).
    public async Task<IReadOnlyList<MyBidDto>> GetMyBidsAsync(int userId, CancellationToken ct) =>
        await db.Bids.AsNoTracking()
            .Where(b => b.BidderId == userId)
            .OrderByDescending(b => b.PlacedAt)
            .Take(100)
            .Select(b => new MyBidDto(
                b.Id, b.AuctionId, b.Auction.Title, b.Amount, b.PlacedAt,
                b.Auction.Status, b.Auction.CurrentPrice,
                b.Auction.HighestBidderId == userId && b.Auction.CurrentPrice == b.Amount,
                b.Auction.WinnerId == userId && b.Auction.FinalPrice == b.Amount))
            .ToListAsync(ct);

    // Si SignalR falla, la puja YA está guardada: no se revierte ni se reporta como error al
    // postor. Se registra en el log y los clientes se resincronizan al reconectar/consultar.
    private async Task NotifySafelyAsync(BidPlacedEvent notification, CancellationToken ct)
    {
        try
        {
            await notifier.BidPlacedAsync(notification, ct);
        }
#pragma warning disable CA1031 // Se captura todo a propósito: una falla de notificación no invalida la puja.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogNotificationFailed(ex, notification.AuctionId, notification.BidId);
        }
    }

    // LoggerMessage (source generator): logging sin asignaciones ni boxing en la ruta caliente.
    [LoggerMessage(Level = LogLevel.Information, Message = "Conflicto de concurrencia en subasta {AuctionId} (usuario {BidderId}, intento {Attempt}); reintentando.")]
    private partial void LogConcurrencyRetry(int auctionId, int bidderId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "La puja {BidId} de la subasta {AuctionId} se guardó pero no se pudo notificar.")]
    private partial void LogNotificationFailed(Exception exception, int auctionId, long bidId);
}
