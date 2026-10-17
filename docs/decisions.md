# Decisiones técnicas

Registro de las decisiones de diseño más importantes, con su contexto, las alternativas
evaluadas y sus consecuencias. Es lo que se defendería en una revisión técnica o una entrevista.

---

## 1. Arquitectura por capas (Domain, Application, Infrastructure, Api)

**Contexto.** El sistema tiene reglas de negocio no triviales (pujas, tiempos, cierre) y dos
canales de entrada (REST y SignalR) que deben aplicar exactamente las mismas reglas.

**Decisión.** Cuatro proyectos con dependencias en una sola dirección:

```
Api ──► Infrastructure ──► Application ──► Domain
```

| Capa | Responsabilidad | No conoce |
|---|---|---|
| Domain | Entidades y reglas (`Auction.PlaceBid`, `Close`, `Cancel`) | EF Core, HTTP, SignalR |
| Application | Casos de uso, DTOs, puertos (`IAuctionNotifier`, `IApplicationDbContext`) | SQL Server, SignalR |
| Infrastructure | EF Core + SQL Server, hash de contraseñas, JWT | HTTP |
| Api | Controladores, Hub, worker, autenticación, middleware | — |

**¿Es adecuada para este tamaño?** Sí, sin exagerar: no hay CQRS, MediatR, AutoMapper ni
repositorios genéricos. La separación paga por sí sola en dos lugares concretos:

- El **dominio se prueba sin base de datos** (25 pruebas de reglas en milisegundos).
- **REST y SignalR reutilizan los mismos servicios**: el Hub no tiene reglas propias.

## 2. EF Core visible en Application (sin repositorios genéricos)

**Decisión.** Application depende de `Microsoft.EntityFrameworkCore` y usa `DbSet<T>` a través
de `IApplicationDbContext`.

**Por qué.** `DbSet` ya es un repositorio y `SaveChanges` ya es una unidad de trabajo. Un
`IRepository<T>` genérico encima solo esconde LINQ, impide proyecciones eficientes (`Select` a
DTO en SQL) y obliga a filtrar abstracciones (`Include`, tracking) de todos modos.

**Costo aceptado.** Cambiar de ORM tocaría Application. Es un escenario muy improbable y, si
ocurriera, el dominio (lo valioso) no cambia.

## 3. Concurrencia en las pujas: optimista con `rowversion` + reintento

**Problema.** Dos usuarios leen precio 100 y pujan 110 al mismo tiempo. Una implementación
ingenua (leer → validar → guardar) acepta ambas.

**Alternativas evaluadas:**

| Opción | Cómo funciona | Ventajas | Limitaciones |
|---|---|---|---|
| **Optimista (`rowversion`)** ✅ | `UPDATE ... WHERE RowVersion = @leida`; si afecta 0 filas, otro ganó → reintentar | Sin bloqueos mientras se valida; alto throughput; nativo en EF Core | Bajo contención extrema hay reintentos (acotados a 5) |
| Pesimista (`UPDLOCK, ROWLOCK`) | `SELECT ... WITH (UPDLOCK)` dentro de una transacción | Nunca hay conflicto | Serializa a TODOS los postores en la fila, aunque la mayoría vaya a ser rechazada; requiere SQL crudo; riesgo de bloqueos largos |
| Transacción `SERIALIZABLE` | Bloqueos de rango | Garantía máxima | Interbloqueos (deadlocks) frecuentes con escrituras concurrentes; peor rendimiento |
| Lock en memoria (`SemaphoreSlim`) | Exclusión mutua en el proceso | Simple | **Incorrecto con varias instancias**; no sobrevive a reinicios |

**Decisión.** Concurrencia optimista: `Auction.RowVersion` (`rowversion` de SQL Server) es token
de concurrencia. El `UPDATE` de la subasta y el `INSERT` de la puja van en **una transacción**
(un solo `SaveChanges`). Si hay conflicto, se descarta todo, se recarga y se revalida. El
reintento normalmente termina en `BidTooLow`, que es el resultado correcto.

**Red de seguridad.** Índice único `(AuctionId, Amount)` en `Bids`: dos pujas del mismo monto
en la misma subasta son imposibles a nivel de base de datos.

**Verificado por pruebas.** `ConcurrencyTests` dispara 20 pujas simultáneas contra SQL Server
real y comprueba los invariantes: una sola aceptada para el mismo monto; precio = máximo de
`Bids`; `BidCount` = filas; un evento por puja aceptada.

## 4. El servidor es la fuente de verdad

- El cliente **solo envía el monto**. El usuario sale del JWT (`sub`), el precio vigente de la
  BD y la hora de `TimeProvider` del servidor.
- Una puja se anuncia por SignalR **solo después** de que `SaveChanges` confirmó. Si SignalR
  falla, la puja sigue siendo válida (se registra en el log); los clientes se resincronizan al
  reconectar, porque `JoinAuction` devuelve el estado completo.
- Pasada la hora de fin, `PlaceBid` rechaza (`AuctionEnded`) aunque el worker aún no haya cerrado
  la subasta: la regla no depende de la frecuencia del worker.

## 5. Cierre automático idempotente y tolerante a reinicios

**Decisión.** Un `BackgroundService` con `PeriodicTimer` (2 s) busca en la BD subastas vencidas
(índice `(Status, EndsAt)`) y las cierra.

- **Sin temporizadores en memoria.** Si el servidor estuvo apagado, la primera pasada al
  arrancar cierra todo lo pendiente (`Overdue_auctions_are_closed_after_a_restart`).
- **Idempotente.** `Auction.Close()` no hace nada si ya está cerrada. El ganador es una sola
  columna (`WinnerId`), así que no puede haber "dos ganadores". `rowversion` impide que dos
  instancias cierren a la vez con resultados distintos (`Concurrent_closers_produce_a_single_winner...`
  lanza 10 cerradores en paralelo: 1 éxito, 1 notificación).
- **Ganador desde la tabla `Bids`** (fuente de verdad), no desde un valor en memoria.

**Alternativa descartada.** Programar un timer por subasta (`Task.Delay` hasta `EndsAt`): se
pierde con cada reinicio y no escala a muchas subastas.

## 6. SignalR: grupos por subasta y Hub delgado

- Grupo `auction-{id}` por subasta y grupo `lobby` para el catálogo. Nadie recibe eventos de
  subastas a las que no se unió.
- Hub **fuertemente tipado** (`Hub<IAuctionClient>`): los nombres de evento son métodos, no strings.
- `JoinAuction` y `LeaveAuction` son públicos (ver es público); `PlaceBid` lleva `[Authorize]`.
- Rechazos con `Clients.Caller`: solo quien pujó se entera.
- El token JWT en WebSockets viaja en `?access_token=`, aceptado **solo** en `/hubs/auctions`.
- **Límite consciente:** `ConnectionTracker` (conteo de espectadores) vive en memoria del proceso.
  Con varias instancias se necesita un backplane (Redis o Azure SignalR Service). No se agregó
  Redis porque hoy no existe esa necesidad.

## 7. Autenticación y seguridad

- Contraseñas con `PasswordHasher<T>` de ASP.NET Core Identity (PBKDF2-HMAC-SHA512, 100k
  iteraciones, salt aleatorio), sin adoptar todo Identity.
- JWT HMAC-SHA256 validando firma, emisor, audiencia y expiración. La clave **no está en el
  repositorio**: User Secrets en desarrollo, variables de entorno en Docker y CI.
- Política de respaldo (`FallbackPolicy`): todo endpoint exige autenticación salvo `[AllowAnonymous]`.
- El registro público siempre crea `Bidder`; los administradores se crean por seed o configuración.
- Rate limiting: global por IP, estricto en login/registro (fuerza bruta) y **por usuario para
  pujas**, compartido entre REST y el Hub (el middleware HTTP no ve invocaciones del Hub).
- CORS con orígenes explícitos y `AllowCredentials` (requerido por la negociación de SignalR).
- Errores como ProblemDetails sin detalles internos; encabezados `nosniff`, `X-Frame-Options`.

**Compromiso en el frontend.** El JWT se guarda en `localStorage` por simplicidad. Riesgo: un
XSS podría leerlo. Mitigación: todo texto de usuario se escapa antes de insertarse como HTML.
Mejora futura: un BFF con cookie `HttpOnly`.

## 8. Producto y subasta en la misma entidad

En este dominio cada artículo se subasta una sola vez, así que `Product` y `Auction` serían 1:1.
Se modelan juntos en `Auction` (título, descripción, imagen + reglas de la subasta). Si en el
futuro un artículo pudiera re-subastarse, se extraería `Product` con una relación 1:N.

## 9. Dinero y tiempo

- `decimal(18,2)` siempre; nunca `float`/`double`. El dominio rechaza montos con más de 2 decimales.
- `DateTimeOffset` en UTC y `TimeProvider` inyectado en todo el código: las pruebas controlan el
  reloj (adelantar 1 hora para simular un reinicio, por ejemplo).
