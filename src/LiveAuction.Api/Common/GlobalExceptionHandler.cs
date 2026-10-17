using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LiveAuction.Api.Common;

// Último recurso para excepciones no controladas: siempre responde ProblemDetails y nunca
// filtra detalles internos (stack trace, SQL) al cliente; esos van al log.
public sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Conflicto",
                "El recurso cambió mientras se procesaba la solicitud. Intenta de nuevo."),
            BadHttpRequestException bad => (bad.StatusCode, "Solicitud inválida", bad.Message),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested =>
                (499, "Solicitud cancelada", "El cliente cerró la conexión."),
            _ => (StatusCodes.Status500InternalServerError, "Error interno",
                "Ocurrió un error inesperado. Si persiste, contacta al soporte con el traceId.")
        };

        if (status >= 500)
            LogUnhandled(exception, httpContext.Request.Method, httpContext.Request.Path);

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail }
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error no controlado en {Method} {Path}")]
    private partial void LogUnhandled(Exception exception, string method, string path);
}
