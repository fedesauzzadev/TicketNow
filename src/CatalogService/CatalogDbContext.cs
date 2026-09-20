using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace TicketNow.CatalogService;

public class CatalogDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Venue> Venues => Set<Venue>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Zone> Zones => Set<Zone>();
    public DbSet<Onsale> Onsales => Set<Onsale>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("catalog");

        modelBuilder.Entity<Venue>(entity =>
        {
            entity.ToTable("venues");
            entity.Property(v => v.Name).HasMaxLength(120).IsRequired();
            entity.Property(v => v.City).HasMaxLength(80).IsRequired();
            entity.HasData(Seed.Venues);
        });

        modelBuilder.Entity<Event>(entity =>
        {
            entity.ToTable("events");
            entity.Property(e => e.Title).HasMaxLength(160).IsRequired();
            entity.Property(e => e.Artist).HasMaxLength(160).IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasOne(e => e.Venue).WithMany().HasForeignKey(e => e.VenueId);
            entity.HasMany(e => e.Zones).WithOne().HasForeignKey(z => z.EventId);
            entity.HasOne(e => e.Onsale).WithOne().HasForeignKey<Onsale>(o => o.EventId);
            entity.HasIndex(e => e.StartsAt);
            entity.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<Zone>(entity =>
        {
            entity.ToTable("zones");
            entity.Property(z => z.Name).HasMaxLength(80).IsRequired();
            entity.Property(z => z.Price).HasColumnType("numeric(10,2)");
            entity.HasIndex(z => z.EventId);
            entity.HasData(Seed.Zones);
        });

        modelBuilder.Entity<Onsale>(entity =>
        {
            entity.ToTable("onsales");
            entity.HasKey(o => o.EventId);
        });

        modelBuilder.Entity<Event>().HasData(Seed.Events);
        modelBuilder.Entity<Onsale>().HasData(Seed.Onsales);

        // Outbox transaccional (docs/03 §6, ADR-007). El catálogo solo consume,
        // pero el inbox (dedupe) viaja con estas tablas.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}

/// <summary>
/// Seed didáctico con fechas fijas coherentes con "hoy" (2026-09):
/// un onsale ABIERTO (Metallica), uno FUTURO para el countdown (Iron Maiden)
/// y un draft sin onsale. En Fase 6 esto se administra solo vía admin API.
/// </summary>
public static class Seed
{
    public static readonly Guid VenueEstadio = new("10000000-0000-0000-0000-000000000001");
    public static readonly Guid VenueArena = new("10000000-0000-0000-0000-000000000002");
    public static readonly Guid VenueTeatro = new("10000000-0000-0000-0000-000000000003");

    public static readonly Guid EventNeon = new("20000000-0000-0000-0000-000000000001");
    public static readonly Guid EventRitmos = new("20000000-0000-0000-0000-000000000002");
    public static readonly Guid EventIntimos = new("20000000-0000-0000-0000-000000000003");

    public static readonly Venue[] Venues =
    [
        new() { Id = VenueEstadio, Name = "Estadio Central", City = "Buenos Aires", CapacityTotal = 60000 },
        new() { Id = VenueArena, Name = "Arena Norte", City = "Buenos Aires", CapacityTotal = 15000 },
        new() { Id = VenueTeatro, Name = "Teatro del Sol", City = "Córdoba", CapacityTotal = 3000 },
    ];

    public static readonly Event[] Events =
    [
        new()
        {
            Id = EventNeon, Title = "Metallica", Artist = "Metallica",
            StartsAt = new DateTimeOffset(2027, 3, 13, 21, 0, 0, TimeSpan.Zero),
            Status = EventStatus.Announced, VenueId = VenueEstadio,
        },
        new()
        {
            Id = EventRitmos, Title = "Iron Maiden", Artist = "Iron Maiden",
            StartsAt = new DateTimeOffset(2027, 1, 22, 20, 30, 0, TimeSpan.Zero),
            Status = EventStatus.Announced, VenueId = VenueArena,
        },
        new()
        {
            Id = EventIntimos, Title = "Íntimos: María Sola", Artist = "María Sola",
            StartsAt = new DateTimeOffset(2026, 11, 5, 21, 0, 0, TimeSpan.Zero),
            Status = EventStatus.Draft, VenueId = VenueTeatro,
        },
    ];

    public static readonly Onsale[] Onsales =
    [
        new()
        {
            EventId = EventNeon, OpensAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            MaxConcurrentInside = 2000, AdmissionRatePerSec = 50, MaxPerAccount = 4, RequiresQueue = true,
        },
        new()
        {
            EventId = EventRitmos, OpensAt = new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero),
            MaxConcurrentInside = 1500, AdmissionRatePerSec = 40, MaxPerAccount = 4, RequiresQueue = false,
        },
    ];

    public static readonly Zone[] Zones =
    [
        new() { Id = new("30000000-0000-0000-0000-000000000001"), EventId = EventNeon, Name = "Campo", Capacity = 30000, Price = 85m, SortOrder = 1 },
        new() { Id = new("30000000-0000-0000-0000-000000000002"), EventId = EventNeon, Name = "Platea Baja", Capacity = 10000, Price = 110m, SortOrder = 2 },
        new() { Id = new("30000000-0000-0000-0000-000000000003"), EventId = EventNeon, Name = "Platea Alta", Capacity = 15000, Price = 60m, SortOrder = 3 },
        new() { Id = new("30000000-0000-0000-0000-000000000004"), EventId = EventNeon, Name = "VIP", Capacity = 5000, Price = 220m, SortOrder = 4 },

        new() { Id = new("30000000-0000-0000-0000-000000000011"), EventId = EventRitmos, Name = "General", Capacity = 8000, Price = 45m, SortOrder = 1 },
        new() { Id = new("30000000-0000-0000-0000-000000000012"), EventId = EventRitmos, Name = "Palco", Capacity = 4000, Price = 90m, SortOrder = 2 },
        new() { Id = new("30000000-0000-0000-0000-000000000013"), EventId = EventRitmos, Name = "VIP", Capacity = 3000, Price = 150m, SortOrder = 3 },

        new() { Id = new("30000000-0000-0000-0000-000000000021"), EventId = EventIntimos, Name = "Sala", Capacity = 3000, Price = 30m, SortOrder = 1 },
    ];
}
