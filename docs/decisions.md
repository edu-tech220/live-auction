# Technical decisions

A record of the most important design decisions: their context, the alternatives that were
evaluated and their consequences.

---

## 1. Layered architecture (Domain, Application, Infrastructure, Api)

**Context.** The system has non-trivial business rules (bidding, timing, closing) and two entry
points (REST and SignalR) that must apply exactly the same rules.

**Decision.** Four projects with dependencies in a single direction:

```
Api ──► Infrastructure ──► Application ──► Domain
```

| Layer | Responsibility | Does not know about |
|---|---|---|
| Domain | Entities and rules (`Auction.PlaceBid`, `Close`, `Cancel`) | EF Core, HTTP, SignalR |
| Application | Use cases, DTOs, ports (`IAuctionNotifier`, `IApplicationDbContext`) | SQL Server, SignalR |
| Infrastructure | EF Core + SQL Server, password hashing, JWT | HTTP |
| Api | Controllers, hub, background worker, authentication, middleware | — |

**Is it appropriate for this size?** Yes, without overdoing it: there is no CQRS, MediatR,
AutoMapper or generic repositories. The separation pays off in two concrete places:

- The **domain is tested without a database** (25 rule tests that run in milliseconds).
- **REST and SignalR reuse the same services**: the hub has no rules of its own.

## 2. EF Core visible in Application (no generic repositories)

**Decision.** Application depends on `Microsoft.EntityFrameworkCore` and uses `DbSet<T>` through
`IApplicationDbContext`.

**Why.** `DbSet` already is a repository and `SaveChanges` already is a unit of work. A generic
`IRepository<T>` on top only hides LINQ, prevents efficient projections (`Select` to a DTO in SQL)
and leaks abstractions (`Include`, tracking) anyway.

**Accepted cost.** Switching ORMs would touch Application. That is very unlikely and, if it
happened, the domain (the valuable part) would not change.

## 3. Bidding concurrency: optimistic with `rowversion` + retry

**Problem.** Two users read price 100 and bid 110 at the same time. A naive implementation
(read → validate → save) accepts both.

**Alternatives evaluated:**

| Option | How it works | Pros | Cons |
|---|---|---|---|
| **Optimistic (`rowversion`)** ✅ | `UPDATE ... WHERE RowVersion = @read`; 0 rows affected means someone else won → retry | No locks while validating; high throughput; native in EF Core | Retries under extreme contention (bounded to 5) |
| Pessimistic (`UPDLOCK, ROWLOCK`) | `SELECT ... WITH (UPDLOCK)` inside a transaction | Never conflicts | Serializes EVERY bidder on the row even though most will be rejected; needs raw SQL; risk of long blocking |
| `SERIALIZABLE` transaction | Range locks | Strongest guarantee | Frequent deadlocks under concurrent writes; worse performance |
| In-memory lock (`SemaphoreSlim`) | In-process mutual exclusion | Simple | **Wrong with several instances**; does not survive restarts |

**Decision.** Optimistic concurrency: `Auction.RowVersion` (SQL Server `rowversion`) is the
concurrency token. The auction `UPDATE` and the bid `INSERT` run in **one transaction** (a single
`SaveChanges`). On conflict everything is discarded, reloaded and revalidated. The retry usually ends
in `BidTooLow`, which is the correct outcome.

**Safety net.** A unique index `(AuctionId, Amount)` on `Bids`: two bids with the same amount on the
same auction are impossible at the database level.

**Verified by tests.** `ConcurrencyTests` fires 20 simultaneous bids against a real SQL Server and
checks the invariants: only one accepted for the same amount; price = maximum of `Bids`;
`BidCount` = rows; one event per accepted bid.

## 4. The server is the source of truth

- The client **only sends the amount**. The user comes from the JWT (`sub`), the current price from
  the database and the time from the server's `TimeProvider`.
- A bid is announced through SignalR **only after** `SaveChanges` succeeded. If SignalR fails the bid
  is still valid (it is logged); clients resynchronize on reconnect because `JoinAuction` returns the
  full state.
- After the end time, `PlaceBid` rejects (`AuctionEnded`) even if the worker has not closed the
  auction yet: the rule does not depend on how often the worker runs.

## 5. Idempotent, restart-tolerant automatic closing

**Decision.** A `BackgroundService` with a `PeriodicTimer` (2 s) queries the database for expired
auctions (index `(Status, EndsAt)`) and closes them.

- **No in-memory timers.** If the server was down, the first pass on startup closes everything
  overdue (`Overdue_auctions_are_closed_after_a_restart`).
- **Idempotent.** `Auction.Close()` does nothing if the auction is already closed. The winner is a
  single column (`WinnerId`), so there can never be "two winners". `rowversion` prevents two
  instances from closing at the same time with different results
  (`Concurrent_closers_produce_a_single_winner...` runs 10 closers in parallel: 1 success,
  1 notification).
- **The winner comes from the `Bids` table** (the source of truth), not from an in-memory value.

**Rejected alternative.** One timer per auction (`Task.Delay` until `EndsAt`): it is lost on every
restart and does not scale to many auctions.

## 6. SignalR: one group per auction and a thin hub

- Group `auction-{id}` per auction and a `lobby` group for the catalog. Nobody receives events from
  auctions they did not join.
- **Strongly typed** hub (`Hub<IAuctionClient>`): event names are methods, not strings.
- `JoinAuction` and `LeaveAuction` are public (viewing is public); `PlaceBid` requires `[Authorize]`.
- Rejections go through `Clients.Caller`: only the bidder is told.
- The JWT travels as `?access_token=` for WebSockets and is accepted **only** on `/hubs/auctions`.
- **Known limitation:** `ConnectionTracker` (viewer counts) lives in process memory. With several
  instances a backplane is needed (Redis or Azure SignalR Service). Redis was not added because
  there is no such need today.

## 7. Authentication and security

- Passwords hashed with ASP.NET Core Identity's `PasswordHasher<T>` (PBKDF2-HMAC-SHA512, 100k
  iterations, random salt) without adopting the whole Identity stack.
- HMAC-SHA256 JWT validating signature, issuer, audience and expiration. The key is **not in the
  repository**: User Secrets in development, environment variables in Docker and CI.
- Fallback policy: every endpoint requires authentication unless it is `[AllowAnonymous]`.
- Public registration always creates a `Bidder`; administrators are created by seeding or
  configuration.
- Rate limiting: global per IP, strict on login/registration (brute force) and **per user for bids**,
  shared between REST and the hub (the HTTP middleware does not see hub invocations).
- CORS with explicit origins and `AllowCredentials` (required by SignalR negotiation).
- Errors as ProblemDetails without internal details; `nosniff` and `X-Frame-Options` headers.

**Frontend trade-off.** The JWT is stored in `localStorage` for simplicity. Risk: an XSS could read
it. Mitigation: React escapes all rendered text and `dangerouslySetInnerHTML` is never used. Future
improvement: a BFF with an `HttpOnly` cookie.

## 8. Product and auction in the same entity

In this domain each item is auctioned exactly once, so `Product` and `Auction` would be 1:1. They
are modeled together in `Auction` (title, description, image + auction rules). If an item could be
re-auctioned in the future, `Product` would be extracted with a 1:N relationship.

## 9. Money and time

- Always `decimal(18,2)`, never `float`/`double`. The domain rejects amounts with more than two
  decimals.
- `DateTimeOffset` in UTC and an injected `TimeProvider` everywhere: tests control the clock
  (for example, moving it forward one hour to simulate a restart).
