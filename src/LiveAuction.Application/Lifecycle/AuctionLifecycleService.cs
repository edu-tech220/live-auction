using LiveAuction.Application.Abstractions;
using LiveAuction.Domain;
using LiveAuction.Domain.Auctions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LiveAuction.Application.Lifecycle;

public interface IAuctionLifecycleService
{
    // Una pasada completa: activa las que ya empezaron y cierra las vencidas.
    Task<LifecycleRunResult> RunAsync(CancellationToken ct);

    // Cierra una subasta concreta si ya venció. Idempotente.
    Task<bool> CloseAsync(int auctionId, CancellationToken ct);
}

public sealed record LifecycleRunResult(int Activated, int Closed);

// Transiciones automáticas de estado, ejecutadas periódicamente por un BackgroundService (Api).
//
// - Tolerante a reinicios: no guarda temporizadores en memoria; en cada pasada consulta la BD
//   por subastas vencidas. Si el servidor estuvo apagado, al volver cierra todas las pendientes.
// - Idempotente: Auction.Close() no hace nada si ya está cerrada, y RowVersion impide que dos
//   instancias (o dos pasadas) cierren la misma subasta con resultados distintos.
public sealed partial class AuctionLifecycleService(
    IApplicationDbContext db,
    IAuctionNotifier notifier,
    TimeProvider time,
    ILogger<AuctionLifecycleService> logger) : IAuctionLifecycleService
{
    private const int BatchSize = 100;

    public async Task<LifecycleRunResult> RunAsync(CancellationToken ct)
    {
        var activated = await ActivateDueAsync(ct);

        var now = time.GetUtcNow();
        var due = await db.Auctions.AsNoTracking()
            .Where(a => (a.Status == AuctionStatus.Scheduled || a.Status == AuctionStatus.Active) && a.EndsAt <= now)
            .OrderBy(a => a.EndsAt)
            .Select(a => a.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        var closed = 0;
        foreach (var id in due)
        {
            if (await CloseAsync(id, ct))
                closed++;
        }

        return new LifecycleRunResult(activated, closed);
    }

    public async Task<bool> CloseAsync(int auctionId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();

        var auction = await db.Auctions.FirstOrDefaultAsync(a => a.Id == auctionId, ct);
        if (auction is null)
            return false;

        // Ganador según la tabla Bids (fuente de verdad): mayor monto; en empate (imposible por
        // el índice único, pero explícito) gana la más antigua.
        var winningBid = await db.Bids
            .Where(b => b.AuctionId == auctionId)
            .OrderByDescending(b => b.Amount).ThenBy(b => b.PlacedAt)
            .FirstOrDefaultAsync(ct);

        var now = time.GetUtcNow();
        var result = auction.Close(winningBid, now);
        if (result.IsFailure || !result.Value)
            return false; // aún no vence, o ya estaba cerrada/cancelada

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Otra instancia la cerró (o entró una puja de último milisegundo). No pasa nada:
            // la próxima pasada lo reevalúa con datos frescos.
            LogCloseConflict(auctionId);
            return false;
        }

        string? winnerName = null;
        if (auction.WinnerId is { } winnerId)
            winnerName = await db.Users.AsNoTracking().Where(u => u.Id == winnerId).Select(u => u.DisplayName).FirstAsync(ct);

        LogClosed(auctionId, auction.WinnerId, auction.FinalPrice);
        await notifier.AuctionFinishedAsync(new AuctionFinishedEvent(
            auctionId, auction.Status, auction.WinnerId, winnerName, auction.FinalPrice, auction.BidCount, now), ct);

        return true;
    }

    private async Task<int> ActivateDueAsync(CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var now = time.GetUtcNow();

        var due = await db.Auctions
            .Where(a => a.Status == AuctionStatus.Scheduled && a.StartsAt <= now && a.EndsAt > now)
            .Take(BatchSize)
            .ToListAsync(ct);

        var activated = new List<int>();
        foreach (var auction in due)
        {
            if (auction.Activate(now))
                activated.Add(auction.Id);
        }

        if (activated.Count == 0)
            return 0;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Una puja la activó al mismo tiempo; la siguiente pasada lo ve consistente.
            return 0;
        }

        foreach (var id in activated)
            await notifier.AuctionStatusChangedAsync(new AuctionStatusChangedEvent(id, AuctionStatus.Active, now), ct);

        return activated.Count;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Subasta {AuctionId} cerrada. Ganador: {WinnerId}, precio final: {FinalPrice}.")]
    private partial void LogClosed(int auctionId, int? winnerId, decimal? finalPrice);

    [LoggerMessage(Level = LogLevel.Information, Message = "Conflicto al cerrar la subasta {AuctionId}; se reintentará en la siguiente pasada.")]
    private partial void LogCloseConflict(int auctionId);
}
