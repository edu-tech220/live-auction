using LiveAuction.Domain.Auctions;
using LiveAuction.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace LiveAuction.Application.Abstractions;

// Puerto de persistencia que usan los casos de uso. Lo implementa AppDbContext (Infrastructure).
// Exponer DbSet permite consultas LINQ expresivas sin repositorios genéricos que solo
// duplicarían lo que EF Core ya hace (DbSet = repositorio, SaveChanges = unidad de trabajo).
public interface IApplicationDbContext
{
    DbSet<User> Users { get; }
    DbSet<Auction> Auctions { get; }
    DbSet<Bid> Bids { get; }

    ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    // ¿La excepción es por violar un índice único? (código específico del proveedor: SQL Server
    // usa 2601/2627). Se abstrae aquí para que Application no dependa de SqlClient.
    bool IsUniqueConstraintViolation(DbUpdateException exception);
}
