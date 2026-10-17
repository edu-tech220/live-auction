using System.Threading.RateLimiting;

namespace LiveAuction.Api.Common;

// Límite de pujas POR USUARIO, compartido entre REST y el Hub de SignalR.
// El middleware de rate limiting de ASP.NET solo ve peticiones HTTP; las invocaciones a métodos
// del Hub viajan dentro de una conexión WebSocket ya abierta, así que necesitan su propio límite.
//
// Token bucket: cada usuario tiene N fichas que se recargan con el tiempo. Permite ráfagas
// cortas (pujar rápido al final) pero frena bots que disparan cientos de pujas.
public sealed class BidThrottle : IDisposable
{
    private readonly PartitionedRateLimiter<int> _limiter;

    public BidThrottle(int tokensPerPeriod = 5, int periodSeconds = 5)
    {
        _limiter = PartitionedRateLimiter.Create<int, int>(userId =>
            RateLimitPartition.GetTokenBucketLimiter(userId, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = tokensPerPeriod,
                TokensPerPeriod = tokensPerPeriod,
                ReplenishmentPeriod = TimeSpan.FromSeconds(periodSeconds),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    }

    public bool TryAcquire(int userId)
    {
        using var lease = _limiter.AttemptAcquire(userId);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
