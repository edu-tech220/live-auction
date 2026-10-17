using LiveAuction.Domain.Common;
using LiveAuction.Domain.Users;

namespace LiveAuction.Domain.Auctions;

// Subasta de un artículo. Producto y subasta son 1:1 en este sistema, así que viven en la
// misma entidad (ver docs/decisions.md).
//
// Es un "agregado rico": todas las reglas que cambian el precio o el estado están aquí
// (PlaceBid, Close, Cancel). Los servicios no pueden poner CurrentPrice a mano: no hay setter
// público. Así una regla no se puede saltar desde otra parte del código.
public sealed class Auction
{
    public const int TitleMaxLength = 120;
    public const int DescriptionMaxLength = 2000;
    public const int ImageUrlMaxLength = 500;
    public const decimal MaxAmount = 1_000_000_000m;

    private Auction() { }

    public int Id { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string? ImageUrl { get; private set; }

    public decimal StartingPrice { get; private set; }
    public decimal MinIncrement { get; private set; }

    // Precio vigente: StartingPrice hasta la primera oferta, luego la oferta más alta.
    public decimal CurrentPrice { get; private set; }
    public int BidCount { get; private set; }
    public int? HighestBidderId { get; private set; }

    // Siempre en UTC. El cliente NUNCA decide si una subasta sigue abierta: se compara contra
    // la hora del servidor (TimeProvider) en cada operación.
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }

    public AuctionStatus Status { get; private set; }

    public int SellerId { get; private set; }
    public User Seller { get; private set; } = null!;

    // Resultado del cierre. WinnerId es una sola columna: imposible tener "dos ganadores".
    public int? WinnerId { get; private set; }
    public User? Winner { get; private set; }
    public decimal? FinalPrice { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    // Token de concurrencia optimista (rowversion en SQL Server). SQL Server lo cambia en cada
    // UPDATE; EF agrega "WHERE RowVersion = @original" y si otra transacción modificó la fila
    // primero, el UPDATE afecta 0 filas => DbUpdateConcurrencyException.
    public byte[] RowVersion { get; private set; } = [];

    public ICollection<Bid> Bids { get; private set; } = new List<Bid>();

    // ---------------- Creación y edición ----------------

    public static Result<Auction> Create(
        int sellerId, string title, string? description, string? imageUrl,
        decimal startingPrice, decimal minIncrement,
        DateTimeOffset startsAt, DateTimeOffset endsAt, DateTimeOffset now)
    {
        var auction = new Auction { SellerId = sellerId, CreatedAt = now, Status = AuctionStatus.Scheduled };
        var error = auction.ApplyDetails(title, description, imageUrl, startingPrice, minIncrement, startsAt, endsAt, now);
        return error is null ? auction : error;
    }

    // Solo mientras está programada y sin ofertas: cambiar precios o fechas con pujas
    // registradas alteraría las reglas a mitad del juego.
    public Result Update(
        string title, string? description, string? imageUrl,
        decimal startingPrice, decimal minIncrement,
        DateTimeOffset startsAt, DateTimeOffset endsAt, DateTimeOffset now)
    {
        if (Status != AuctionStatus.Scheduled || BidCount > 0 || now >= StartsAt)
            return AuctionErrors.NotEditable;

        var error = ApplyDetails(title, description, imageUrl, startingPrice, minIncrement, startsAt, endsAt, now);
        if (error is not null)
            return error;

        UpdatedAt = now;
        return Result.Success();
    }

    private Error? ApplyDetails(
        string title, string? description, string? imageUrl,
        decimal startingPrice, decimal minIncrement,
        DateTimeOffset startsAt, DateTimeOffset endsAt, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(title))
            return AuctionErrors.TitleRequired;

        if (!IsValidMoney(startingPrice) || !IsValidMoney(minIncrement))
            return AuctionErrors.InvalidPrices;

        if (endsAt <= startsAt || endsAt <= now)
            return AuctionErrors.InvalidSchedule;

        Title = title.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        ImageUrl = string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl.Trim();
        StartingPrice = startingPrice;
        MinIncrement = minIncrement;
        CurrentPrice = startingPrice;
        StartsAt = startsAt.ToUniversalTime();
        EndsAt = endsAt.ToUniversalTime();
        return null;
    }

    // ---------------- Pujas ----------------

    // Mínimo aceptable para la siguiente oferta. La primera puede igualar el precio inicial;
    // las siguientes deben superar la vigente por al menos el incremento mínimo.
    public decimal MinimumNextBid => BidCount == 0 ? StartingPrice : CurrentPrice + MinIncrement;

    // Valida y aplica una oferta. El orden de las validaciones importa: primero el estado y el
    // tiempo (no tiene sentido evaluar montos en una subasta cerrada), luego quién puja y al final
    // el monto.
    public Result<Bid> PlaceBid(int bidderId, decimal amount, DateTimeOffset now)
    {
        if (Status == AuctionStatus.Cancelled)
            return AuctionErrors.Cancelled;

        // Aunque el worker aún no la haya cerrado, pasada la hora de fin ya no se puja.
        if (Status == AuctionStatus.Finished || now >= EndsAt)
            return AuctionErrors.Ended;

        if (now < StartsAt)
            return AuctionErrors.NotStarted;

        if (bidderId == SellerId)
            return AuctionErrors.SellerCannotBid;

        if (HighestBidderId == bidderId)
            return AuctionErrors.AlreadyHighestBidder;

        if (!IsValidMoney(amount))
            return AuctionErrors.InvalidAmount;

        if (amount < MinimumNextBid)
            return AuctionErrors.BidTooLow(MinimumNextBid);

        // Si el worker todavía no la activó pero ya está en su ventana, se activa aquí.
        Status = AuctionStatus.Active;
        CurrentPrice = amount;
        HighestBidderId = bidderId;
        BidCount++;
        UpdatedAt = now;

        return new Bid(Id, bidderId, amount, now);
    }

    // ---------------- Ciclo de vida ----------------

    // Programada => Activa cuando llega StartsAt. Devuelve true solo si hubo cambio.
    public bool Activate(DateTimeOffset now)
    {
        if (Status != AuctionStatus.Scheduled || now < StartsAt || now >= EndsAt)
            return false;

        Status = AuctionStatus.Active;
        UpdatedAt = now;
        return true;
    }

    // Cierre IDEMPOTENTE: si ya está cerrada o cancelada no hace nada y devuelve false.
    // winningBid es la oferta más alta según la tabla Bids (fuente de verdad).
    public Result<bool> Close(Bid? winningBid, DateTimeOffset now)
    {
        if (Status is AuctionStatus.Finished or AuctionStatus.Cancelled)
            return false;

        if (now < EndsAt)
            return AuctionErrors.NotYetEnded;

        if (winningBid is not null && winningBid.AuctionId != Id)
            throw new InvalidOperationException("La oferta ganadora no pertenece a esta subasta.");

        Status = AuctionStatus.Finished;
        WinnerId = winningBid?.BidderId;
        FinalPrice = winningBid?.Amount;
        ClosedAt = now;
        UpdatedAt = now;
        return true;
    }

    public Result Cancel(DateTimeOffset now)
    {
        if (Status is AuctionStatus.Finished or AuctionStatus.Cancelled || now >= EndsAt)
            return AuctionErrors.NotCancellable;

        Status = AuctionStatus.Cancelled;
        ClosedAt = now;
        UpdatedAt = now;
        return Result.Success();
    }

    // Dinero: positivo, dentro de un tope razonable y con máximo 2 decimales.
    public static bool IsValidMoney(decimal amount) =>
        amount > 0 && amount <= MaxAmount && decimal.Round(amount, 2) == amount;
}
