namespace LiveAuction.Application.Realtime;

// Lleva la cuenta de qué conexiones están viendo cada subasta (para "N personas viendo").
//
// SignalR ya maneja los grupos y saca a una conexión de todos sus grupos al desconectarse,
// pero NO expone cuántos miembros tiene un grupo. Esta clase guarda ese conteo.
//
// Limitación consciente: vive en memoria del proceso. Con varias instancias de la API detrás
// de un balanceador, cada una contaría solo sus conexiones; en ese escenario se usaría un
// backplane (Redis / Azure SignalR) y el conteo se movería ahí. Ver docs/decisions.md.
public sealed class ConnectionTracker
{
    private readonly Lock _gate = new(); // System.Threading.Lock (.NET 9+)
    private readonly Dictionary<string, HashSet<int>> _auctionsByConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int> _viewersByAuction = [];

    // Devuelve el número de espectadores de la subasta tras unirse.
    public int Join(string connectionId, int auctionId)
    {
        lock (_gate)
        {
            if (!_auctionsByConnection.TryGetValue(connectionId, out var auctions))
                _auctionsByConnection[connectionId] = auctions = [];

            if (auctions.Add(auctionId)) // unirse dos veces no cuenta doble
                _viewersByAuction[auctionId] = _viewersByAuction.GetValueOrDefault(auctionId) + 1;

            return _viewersByAuction[auctionId];
        }
    }

    public int Leave(string connectionId, int auctionId)
    {
        lock (_gate)
        {
            if (_auctionsByConnection.TryGetValue(connectionId, out var auctions) && auctions.Remove(auctionId))
                Decrement(auctionId);

            return _viewersByAuction.GetValueOrDefault(auctionId);
        }
    }

    // Al desconectarse: sale de todas sus subastas. Devuelve los nuevos conteos para avisar.
    public IReadOnlyList<(int AuctionId, int Viewers)> RemoveConnection(string connectionId)
    {
        lock (_gate)
        {
            if (!_auctionsByConnection.Remove(connectionId, out var auctions))
                return [];

            var changes = new List<(int, int)>(auctions.Count);
            foreach (var auctionId in auctions)
            {
                Decrement(auctionId);
                changes.Add((auctionId, _viewersByAuction.GetValueOrDefault(auctionId)));
            }
            return changes;
        }
    }

    public int ViewerCount(int auctionId)
    {
        lock (_gate)
            return _viewersByAuction.GetValueOrDefault(auctionId);
    }

    private void Decrement(int auctionId)
    {
        var remaining = _viewersByAuction.GetValueOrDefault(auctionId) - 1;
        if (remaining <= 0)
            _viewersByAuction.Remove(auctionId); // no acumular claves de subastas sin espectadores
        else
            _viewersByAuction[auctionId] = remaining;
    }
}
