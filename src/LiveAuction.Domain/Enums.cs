namespace LiveAuction.Domain;

// Valores explícitos: en la BD se guarda el número, así que nunca deben cambiar de significado.
public enum AuctionStatus
{
    Scheduled = 1, // creada, aún no empieza (StartsAt en el futuro)
    Active = 2,    // acepta pujas entre StartsAt y EndsAt
    Finished = 3,  // cerrada por el servidor; ganador (o sin ganador) persistido
    Cancelled = 4  // cancelada por un administrador; nunca tiene ganador
}

public enum UserRole
{
    Bidder = 1,
    Admin = 2
}
