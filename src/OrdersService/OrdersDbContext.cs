using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace TicketNow.OrdersService;

public class OrdersDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Ticket> Tickets => Set<Ticket>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("orders");

        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("orders");
            entity.Property(o => o.UserId).HasMaxLength(120).IsRequired();
            entity.Property(o => o.IdempotencyKey).HasMaxLength(80).IsRequired();
            entity.Property(o => o.Status).HasConversion<string>().HasMaxLength(12);
            entity.Property(o => o.Total).HasColumnType("numeric(10,2)");
            entity.Property(o => o.PaymentToken).HasMaxLength(80);
            entity.HasIndex(o => o.HoldId).IsUnique(); // un hold → una sola orden (INV-3)
            entity.HasIndex(o => o.IdempotencyKey).IsUnique(); // INV-5
            entity.HasMany(o => o.Items).WithOne().HasForeignKey(i => i.OrderId);
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.ToTable("order_items");
            entity.Property(i => i.UnitPrice).HasColumnType("numeric(10,2)");
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.ToTable("payments");
            entity.Property(p => p.Status).HasConversion<string>().HasMaxLength(12);
            entity.Property(p => p.Amount).HasColumnType("numeric(10,2)");
            entity.HasIndex(p => p.OrderId).IsUnique();
        });

        modelBuilder.Entity<Ticket>(entity =>
        {
            entity.ToTable("tickets");
            entity.HasIndex(t => t.QrJti).IsUnique();
            entity.HasIndex(t => t.OrderId);
        });

        // Saga: la PK es el CorrelationId (= OrderId). Sin token de concurrencia
        // a propósito: Npgsql no soporta rowversion-xmin como columna, y la saga es
        // conmutativamente segura (puerta Lua + instancias finalizadas descartan
        // eventos tardíos). El endpoint corre serializado (ver Program).
        modelBuilder.Entity<OrderState>(entity =>
        {
            entity.ToTable("saga_instances");
            entity.HasKey(s => s.CorrelationId);
            entity.Property(s => s.CurrentState).HasMaxLength(64);
        });

        // Outbox transaccional (docs/03 §6, ADR-007).
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
