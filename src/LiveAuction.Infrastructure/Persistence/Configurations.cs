using LiveAuction.Domain;
using LiveAuction.Domain.Auctions;
using LiveAuction.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveAuction.Infrastructure.Persistence;

// Mapeo con Fluent API: el dominio queda libre de atributos de persistencia.

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users", t => t.HasCheckConstraint("CK_Users_Role", "[Role] IN (1, 2)"));
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Email).HasMaxLength(User.EmailMaxLength).IsRequired();
        builder.Property(u => u.DisplayName).HasMaxLength(User.DisplayNameMaxLength).IsRequired();
        builder.Property(u => u.PasswordHash).HasMaxLength(500).IsRequired();
        builder.HasIndex(u => u.Email).IsUnique();
        builder.Ignore(u => u.IsAdmin);
    }
}

internal sealed class AuctionConfiguration : IEntityTypeConfiguration<Auction>
{
    public void Configure(EntityTypeBuilder<Auction> builder)
    {
        builder.ToTable("Auctions", t =>
        {
            // Las mismas reglas del dominio, como red de seguridad en la BD.
            t.HasCheckConstraint("CK_Auctions_Prices", "[StartingPrice] > 0 AND [MinIncrement] > 0 AND [CurrentPrice] >= [StartingPrice]");
            t.HasCheckConstraint("CK_Auctions_Schedule", "[EndsAt] > [StartsAt]");
            t.HasCheckConstraint("CK_Auctions_Status", "[Status] BETWEEN 1 AND 4");
            t.HasCheckConstraint("CK_Auctions_Winner", "[WinnerId] IS NULL OR [Status] = 3");
        });

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Title).HasMaxLength(Auction.TitleMaxLength).IsRequired();
        builder.Property(a => a.Description).HasMaxLength(Auction.DescriptionMaxLength);
        builder.Property(a => a.ImageUrl).HasMaxLength(Auction.ImageUrlMaxLength);

        // Dinero: decimal(18,2). Nunca float/double (errores de redondeo binario).
        builder.Property(a => a.StartingPrice).HasPrecision(18, 2);
        builder.Property(a => a.MinIncrement).HasPrecision(18, 2);
        builder.Property(a => a.CurrentPrice).HasPrecision(18, 2);
        builder.Property(a => a.FinalPrice).HasPrecision(18, 2);

        builder.Property(a => a.Status).HasConversion<int>();

        // rowversion: SQL Server lo incrementa solo en cada UPDATE. IsRowVersion() lo marca como
        // token de concurrencia: EF agrega "AND RowVersion = @original" a cada UPDATE/DELETE.
        builder.Property(a => a.RowVersion).IsRowVersion();

        builder.Ignore(a => a.MinimumNextBid);

        builder.HasOne(a => a.Seller).WithMany().HasForeignKey(a => a.SellerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(a => a.Winner).WithMany().HasForeignKey(a => a.WinnerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.HighestBidderId).OnDelete(DeleteBehavior.Restrict);

        // El worker busca "activas o programadas cuyo EndsAt ya pasó": índice que cubre ese filtro.
        builder.HasIndex(a => new { a.Status, a.EndsAt });
    }
}

internal sealed class BidConfiguration : IEntityTypeConfiguration<Bid>
{
    public void Configure(EntityTypeBuilder<Bid> builder)
    {
        builder.ToTable("Bids", t => t.HasCheckConstraint("CK_Bids_Amount", "[Amount] > 0"));
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Amount).HasPrecision(18, 2);

        builder.HasOne(b => b.Auction).WithMany(a => a.Bids).HasForeignKey(b => b.AuctionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(b => b.Bidder).WithMany().HasForeignKey(b => b.BidderId).OnDelete(DeleteBehavior.Restrict);

        // Red de seguridad de concurrencia: dos pujas del mismo monto en la misma subasta son
        // imposibles a nivel BD. También sirve para "la más alta" (ORDER BY Amount DESC).
        builder.HasIndex(b => new { b.AuctionId, b.Amount }).IsUnique();

        // "Mis pujas" filtra por postor y ordena por fecha.
        builder.HasIndex(b => new { b.BidderId, b.PlacedAt });
    }
}
