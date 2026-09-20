using Microsoft.EntityFrameworkCore;
using TicketNow.CatalogService;
using TicketNow.InventoryService;
using TicketNow.OrdersService;

namespace TicketNow.UnitTests;

// DbContexts SOLO para tests: mapean TODO al schema `public`.
//
// Por qué: MassTransit cachea esquema/tabla por TIPO CLR (SqlLockStatementProvider)
// en un static del proceso. Con 3 DbContexts (orders/inventory/catalog) en un mismo
// proceso de tests, el primero envenena la caché y los demás consultan su esquema:
// inbox siempre-miss (duplicados) y outbox jamás drenado (sagas trabadas).
// En producción no pasa (un proceso = un DbContext).
// Mapeando todo a `public` en tests, la caché coincide para todos.
// (Nombres de tablas no colisionan entre servicios: verificado en OutboxMappingTests.)
public sealed class TestCatalogDbContext(DbContextOptions options) : CatalogDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("public");
    }
}

public sealed class TestInventoryDbContext(DbContextOptions options) : InventoryDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("public");
    }
}

public sealed class TestOrdersDbContext(DbContextOptions options) : OrdersDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("public");
    }
}
