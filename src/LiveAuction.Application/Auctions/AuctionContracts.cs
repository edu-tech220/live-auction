using System.ComponentModel.DataAnnotations;
using LiveAuction.Domain;
using LiveAuction.Domain.Auctions;

namespace LiveAuction.Application.Auctions;

// Entrada para crear/editar. Las DataAnnotations dan un 400 rápido; las reglas de negocio
// (fechas coherentes, 2 decimales...) las vuelve a validar el dominio.
public sealed record AuctionRequest
{
    [Required, StringLength(Auction.TitleMaxLength, MinimumLength = 3)]
    public string Title { get; init; } = string.Empty;

    [StringLength(Auction.DescriptionMaxLength)]
    public string? Description { get; init; }

    [Url, StringLength(Auction.ImageUrlMaxLength)]
    public string? ImageUrl { get; init; }

    [Required, Range(typeof(decimal), "0.01", "1000000000")]
    public decimal? StartingPrice { get; init; }

    [Required, Range(typeof(decimal), "0.01", "1000000000")]
    public decimal? MinIncrement { get; init; }

    [Required]
    public DateTimeOffset? StartsAt { get; init; }

    [Required]
    public DateTimeOffset? EndsAt { get; init; }
}

public sealed record AuctionListQuery
{
    public AuctionStatus? Status { get; init; }

    [StringLength(100)]
    public string? Search { get; init; }

    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [Range(1, 50)]
    public int PageSize { get; init; } = 12;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed record AuctionSummaryDto(
    int Id,
    string Title,
    string? ImageUrl,
    AuctionStatus Status,
    decimal CurrentPrice,
    int BidCount,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt);

public sealed record AuctionDetailsDto(
    int Id,
    string Title,
    string? Description,
    string? ImageUrl,
    AuctionStatus Status,
    decimal StartingPrice,
    decimal MinIncrement,
    decimal CurrentPrice,
    decimal MinimumNextBid,
    int BidCount,
    int? HighestBidderId,
    string? HighestBidderName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    int SellerId,
    string SellerName,
    int? WinnerId,
    string? WinnerName,
    decimal? FinalPrice,
    DateTimeOffset? ClosedAt,
    // Hora del servidor al responder: el cliente calcula su desfase para mostrar la cuenta
    // regresiva, pero NUNCA decide con ella si la subasta sigue abierta.
    DateTimeOffset ServerTime);
