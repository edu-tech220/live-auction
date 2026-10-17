using System.ComponentModel.DataAnnotations;
using LiveAuction.Application.Lifecycle;
using Microsoft.Extensions.Options;

namespace LiveAuction.Api.Background;

public sealed class LifecycleOptions
{
    public const string SectionName = "AuctionLifecycle";

    public bool Enabled { get; set; } = true;

    // Cada cuánto se revisan subastas por activar/cerrar. Las pujas tardías se rechazan por
    // la hora del servidor aunque el worker aún no haya pasado: el intervalo solo afecta cuándo
    // se ANUNCIA el cierre, no la regla.
    [Range(1, 300)]
    public int IntervalSeconds { get; set; } = 2;
}

// BackgroundService = tarea de larga duración que vive junto con la app.
// PeriodicTimer espera entre pasadas sin bloquear hilos y sin solaparse (si una pasada tarda
// más que el intervalo, la siguiente espera a que termine).
public sealed partial class AuctionLifecycleWorker(
    IServiceScopeFactory scopes,
    IOptions<LifecycleOptions> options,
    ILogger<AuctionLifecycleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.IntervalSeconds));

        // do/while: la primera pasada corre de inmediato al arrancar. Así, tras un reinicio,
        // las subastas que vencieron mientras el servidor estaba apagado se cierran enseguida.
        do
        {
            try
            {
                // El servicio es Scoped (usa DbContext): un scope nuevo por pasada.
                await using var scope = scopes.CreateAsyncScope();
                var lifecycle = scope.ServiceProvider.GetRequiredService<IAuctionLifecycleService>();
                var result = await lifecycle.RunAsync(stoppingToken);

                if (result.Activated > 0 || result.Closed > 0)
                    LogRun(result.Activated, result.Closed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Un error en una pasada (p. ej. BD caída) no debe matar al worker.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ciclo de vida: {Activated} activadas, {Closed} cerradas.")]
    private partial void LogRun(int activated, int closed);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló la pasada del ciclo de vida de subastas; se reintentará.")]
    private partial void LogFailed(Exception exception);
}
