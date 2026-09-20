using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using TicketNow.CatalogService;
using TicketNow.InventoryService;
using TicketNow.OrdersService;

namespace TicketNow.UnitTests;

// Regresión estructural (Fase 3): MassTransit cachea esquema/tabla por TIPO CLR
// en un static del proceso. Como InboxState/OutboxState son el MISMO tipo en 3
// DbContexts, si mapearan schemas distintos, el primero envenenaría a los demás
// (inbox siempre-miss + outbox jamás drenado). Por eso:
//  - producción: un schema por servicio (se verifica acá),
//  - tests: contextos derivados con TODO en `public` (ver TestDbContexts.cs).
public class OutboxMappingTests
{
    private static TContext Unconnected<TContext>(Func<DbContextOptions, TContext> create)
        where TContext : DbContext
    {
        // Solo metadata del modelo: no abre conexiones.
        return create(new DbContextOptionsBuilder().UseNpgsql("Host=localhost;Database=x").Options);
    }

    private static string SchemaOf<TContext>(TContext context, Type entity)
        where TContext : DbContext =>
        context.Model.FindEntityType(entity)?.GetSchema() ?? "<sin schema>";

    [Fact]
    public void Produccion_un_schema_por_servicio()
    {
        using var orders = Unconnected(o => new OrdersDbContext(o));
        using var inventory = Unconnected(o => new InventoryDbContext(o));
        using var catalog = Unconnected(o => new CatalogDbContext(o));

        foreach (var entity in new[] { typeof(InboxState), typeof(OutboxMessage), typeof(OutboxState) })
        {
            Assert.Equal("orders", SchemaOf(orders, entity));
            Assert.Equal("inventory", SchemaOf(inventory, entity));
            Assert.Equal("catalog", SchemaOf(catalog, entity));
        }
    }

    [Fact]
    public void Tests_todo_en_public()
    {
        using var orders = Unconnected(o => new TestOrdersDbContext(o));
        using var inventory = Unconnected(o => new TestInventoryDbContext(o));
        using var catalog = Unconnected(o => new TestCatalogDbContext(o));

        foreach (var entity in new[] { typeof(InboxState), typeof(OutboxMessage), typeof(OutboxState) })
        {
            Assert.Equal("public", SchemaOf(orders, entity));
            Assert.Equal("public", SchemaOf(inventory, entity));
            Assert.Equal("public", SchemaOf(catalog, entity));
        }
    }
}
