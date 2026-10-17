namespace LiveAuction.Domain.Common;

// Tipo de error. La capa API lo traduce a código HTTP (400, 401, 403, 404, 409).
public enum ErrorKind
{
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict
}

// Error esperado de negocio. Code es estable (el frontend puede reaccionar a "BidTooLow"
// sin parsear el texto); Message es legible para el usuario.
public sealed record Error(ErrorKind Kind, string Code, string Message)
{
    public static Error Validation(string code, string message) => new(ErrorKind.Validation, code, message);
    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);
    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
    public static Error Unauthorized(string code, string message) => new(ErrorKind.Unauthorized, code, message);
    public static Error Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);
}

// Patrón Result: las reglas de negocio devuelven éxito o un Error en vez de lanzar excepciones
// para casos esperados (puja baja, subasta cerrada...). Las excepciones quedan para lo inesperado.
public class Result
{
    protected Result(Error? error) => Error = error;

    public Error? Error { get; }
    public bool IsSuccess => Error is null;
    public bool IsFailure => !IsSuccess;

    public static Result Success() => new(null);
    public static implicit operator Result(Error error) => new(error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T value) : base(null) => _value = value;
    private Result(Error error) : base(error) { }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"No hay valor: el resultado falló con {Error!.Code}.");

    public static implicit operator Result<T>(T value) => new(value);
    public static implicit operator Result<T>(Error error) => new(error);
}
