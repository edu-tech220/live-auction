using System.Globalization;
using System.Security.Claims;
using LiveAuction.Domain.Common;
using LiveAuction.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace LiveAuction.Api.Common;

// Base de los controladores: [ApiController] valida DataAnnotations (400 automático) y las
// respuestas de error salen como ProblemDetails (RFC 9457).
[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    // Id del usuario autenticado, tomado del JWT ya validado. NUNCA de un parámetro del cliente.
    protected int CurrentUserId => User.GetUserId();

    protected ObjectResult Problem(Error error)
    {
        var status = error.Kind.ToStatusCode();
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, status, error.Kind.ToTitle(), detail: error.Message);
        problem.Extensions["code"] = error.Code;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}

public static class ErrorMapping
{
    public static int ToStatusCode(this ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status409Conflict
    };

    public static string ToTitle(this ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => "Solicitud inválida",
        ErrorKind.Unauthorized => "No autenticado",
        ErrorKind.Forbidden => "Sin permiso",
        ErrorKind.NotFound => "No encontrado",
        _ => "Conflicto con el estado actual"
    };
}

public static class ClaimsPrincipalExtensions
{
    public static int GetUserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(AppClaims.UserId), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new InvalidOperationException("El token no contiene el id de usuario.");

    public static int? TryGetUserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(AppClaims.UserId), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
}
