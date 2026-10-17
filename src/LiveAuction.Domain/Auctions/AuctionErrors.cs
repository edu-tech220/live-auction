using LiveAuction.Domain.Common;

namespace LiveAuction.Domain.Auctions;

// Catálogo de errores de subastas. Centralizado para que códigos y mensajes sean consistentes
// entre la API REST, el Hub de SignalR y las pruebas.
public static class AuctionErrors
{
    public static Error NotFound(int id) =>
        Error.NotFound("AuctionNotFound", $"La subasta {id} no existe.");

    public static readonly Error NotStarted =
        Error.Conflict("AuctionNotStarted", "La subasta todavía no ha comenzado.");

    public static readonly Error Ended =
        Error.Conflict("AuctionEnded", "La subasta ya terminó; no acepta más ofertas.");

    public static readonly Error Cancelled =
        Error.Conflict("AuctionCancelled", "La subasta fue cancelada.");

    public static readonly Error SellerCannotBid =
        Error.Forbidden("SellerCannotBid", "No puedes pujar en una subasta que tú creaste.");

    public static readonly Error AlreadyHighestBidder =
        Error.Conflict("AlreadyHighestBidder", "Ya tienes la oferta más alta.");

    public static readonly Error InvalidAmount =
        Error.Validation("InvalidAmount", "El monto debe ser positivo y tener como máximo 2 decimales.");

    public static Error BidTooLow(decimal minimum) =>
        Error.Conflict("BidTooLow", $"La oferta mínima es {minimum:0.00}.");

    public static readonly Error NotEditable =
        Error.Conflict("AuctionNotEditable", "Solo se puede editar una subasta programada y sin ofertas.");

    public static readonly Error NotCancellable =
        Error.Conflict("AuctionNotCancellable", "Solo se puede cancelar una subasta programada o activa.");

    public static readonly Error NotYetEnded =
        Error.Conflict("AuctionNotYetEnded", "La subasta todavía no llega a su hora de cierre.");

    // Validaciones de creación/edición
    public static readonly Error TitleRequired = Error.Validation("TitleRequired", "El título es obligatorio.");
    public static readonly Error InvalidPrices = Error.Validation("InvalidPrices", "El precio inicial y el incremento deben ser mayores a 0 con máximo 2 decimales.");
    public static readonly Error InvalidSchedule = Error.Validation("InvalidSchedule", "La fecha de fin debe ser posterior al inicio y estar en el futuro.");
}
