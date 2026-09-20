using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace TicketNow.InventoryService;

public class InventoryDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<LedgerEntry> Ledger => Set<LedgerEntry>();
    public DbSet<HoldRecord> Holds => Set<HoldRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("inventory");

        modelBuilder.Entity<LedgerEntry>(entity =>
        {
            entity.ToTable("ledger");
            entity.HasKey(e => e.EntryId);
            entity.Property(e => e.EntryType).HasConversion<string>().HasMaxLength(10);
            entity.Property(e => e.Actor).HasMaxLength(60).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.EventId, e.ZoneId, e.CreatedAt });

            // Segunda barrera de INV-1 (docs/03 §2.1): un hold se confirma una sola vez.
            // Nota: el filtro usa el nombre REAL de la columna (PascalCase entre comillas).
            entity.HasIndex(e => e.HoldId).IsUnique().HasFilter("\"EntryType\" = 'Confirm'");
        });

        modelBuilder.Entity<HoldRecord>(entity =>
        {
            entity.ToTable("holds");
            entity.HasKey(h => h.HoldId);
            entity.Property(h => h.UserId).HasMaxLength(120).IsRequired();
            entity.Property(h => h.Outcome).HasConversion<string>().HasMaxLength(10);
            entity.HasIndex(h => new { h.EventId, h.ExpiresAt });
        });

        // Outbox transaccional (docs/03 §6, ADR-007).
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
