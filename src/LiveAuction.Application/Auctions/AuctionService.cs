using LiveAuction.Application.Abstractions;
using LiveAuction.Domain;
using LiveAuction.Domain.Auctions;
using LiveAuction.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace LiveAuction.Application.Auctions;

public interface IAuctionService
{
    Task<PagedResult<AuctionSummaryDto>> ListAsync(AuctionListQuery query, CancellationToken ct);
    Task<Result<AuctionDetailsDto>> GetAsync(int id, CancellationToken ct);
    Task<Result<AuctionDetailsDto>> CreateAsync(int sellerId, AuctionRequest request, CancellationToken ct);
    Task<Result<AuctionDetailsDto>> UpdateAsync(int id, AuctionRequest request, CancellationToken ct);
    Task<Result> CancelAsync(int id, CancellationToken ct);
}

// Consulta y administración de subastas. Las lecturas usan proyecciones LINQ (Select) para traer
// solo las columnas del DTO; las escrituras cargan la entidad y delegan las reglas al dominio.
public sealed class AuctionService(
    IApplicationDbContext db,
    IAuctionNotifier notifier,
    TimeProvider time) : IAuctionService
{
    public async Task<PagedResult<AuctionSummaryDto>> ListAsync(AuctionListQuery query, CancellationToken ct)
    {
        var auctions = db.Auctions.AsNoTracking();

        if (query.Status is { } status)
            auctions = auctions.Where(a => a.Status == status);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            auctions = auctions.Where(a => a.Title.Contains(term)); // LIKE '%term%' parametrizado
        }

        var total = await auctions.CountAsync(ct);

        // Orden útil para el usuario: activas primero (las que cierran antes arriba), luego las
        // programadas, luego el historial.
        var items = await auctions
            .OrderBy(a => a.Status)
            .ThenBy(a => a.EndsAt)
            .ThenBy(a => a.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(a => new AuctionSummaryDto(
                a.Id, a.Title, a.ImageUrl, a.Status, a.CurrentPrice, a.BidCount, a.StartsAt, a.EndsAt))
            .ToListAsync(ct);

        return new PagedResult<AuctionSummaryDto>(items, query.Page, query.PageSize, total);
    }

    public async Task<Result<AuctionDetailsDto>> GetAsync(int id, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // Un solo SELECT con LEFT JOIN a Users para vendedor, mejor postor y ganador.
        var details = await db.Auctions.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new AuctionDetailsDto(
                a.Id, a.Title, a.Description, a.ImageUrl, a.Status,
                a.StartingPrice, a.MinIncrement, a.CurrentPrice,
                a.BidCount == 0 ? a.StartingPrice : a.CurrentPrice + a.MinIncrement,
                a.BidCount,
                a.HighestBidderId,
                db.Users.Where(u => u.Id == a.HighestBidderId).Select(u => u.DisplayName).FirstOrDefault(),
                a.StartsAt, a.EndsAt,
                a.SellerId, a.Seller.DisplayName,
                a.WinnerId, a.Winner != null ? a.Winner.DisplayName : null,
                a.FinalPrice, a.ClosedAt,
                now))
            .FirstOrDefaultAsync(ct);

        return details is null ? AuctionErrors.NotFound(id) : details;
    }

    public async Task<Result<AuctionDetailsDto>> CreateAsync(int sellerId, AuctionRequest request, CancellationToken ct)
    {
        var created = Auction.Create(
            sellerId, request.Title, request.Description, request.ImageUrl,
            request.StartingPrice!.Value, request.MinIncrement!.Value,
            request.StartsAt!.Value, request.EndsAt!.Value, time.GetUtcNow());

        if (created.IsFailure)
            return created.Error!;

        db.Auctions.Add(created.Value);
        await db.SaveChangesAsync(ct);
        return await GetAsync(created.Value.Id, ct);
    }

    public async Task<Result<AuctionDetailsDto>> UpdateAsync(int id, AuctionRequest request, CancellationToken ct)
    {
        var auction = await db.Auctions.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (auction is null)
            return AuctionErrors.NotFound(id);

        var updated = auction.Update(
            request.Title, request.Description, request.ImageUrl,
            request.StartingPrice!.Value, request.MinIncrement!.Value,
            request.StartsAt!.Value, request.EndsAt!.Value, time.GetUtcNow());

        if (updated.IsFailure)
            return updated.Error!;

        // Si entró una puja entre la lectura y este guardado, RowVersion lo detecta (=> 409).
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyConflict();
        }

        return await GetAsync(id, ct);
    }

    public async Task<Result> CancelAsync(int id, CancellationToken ct)
    {
        var auction = await db.Auctions.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (auction is null)
            return AuctionErrors.NotFound(id);

        var now = time.GetUtcNow();
        var cancelled = auction.Cancel(now);
        if (cancelled.IsFailure)
            return cancelled;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyConflict();
        }

        // Solo después de persistir se avisa a los conectados.
        await notifier.AuctionStatusChangedAsync(new AuctionStatusChangedEvent(id, AuctionStatus.Cancelled, now), ct);
        return Result.Success();
    }

    private static Error ConcurrencyConflict() =>
        Error.Conflict("ConcurrencyConflict", "La subasta cambió mientras la editabas. Recarga e intenta de nuevo.");
}
