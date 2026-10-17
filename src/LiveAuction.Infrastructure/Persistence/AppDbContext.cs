using LiveAuction.Application.Abstractions;
using LiveAuction.Domain.Auctions;
using LiveAuction.Domain.Users;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace LiveAuction.Infrastructure.Persistence;

// DbContext de EF Core: unidad de trabajo + acceso a las tablas. Implementa el puerto de
// Application para que los casos de uso no dependan de esta clase concreta.
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IApplicationDbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Auction> Auctions => Set<Auction>();
    public DbSet<Bid> Bids => Set<Bid>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        // Aplica todas las IEntityTypeConfiguration<T> de este ensamblado.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

    // 2627: violación de PRIMARY KEY / UNIQUE constraint. 2601: violación de índice único.
    public bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };
}
