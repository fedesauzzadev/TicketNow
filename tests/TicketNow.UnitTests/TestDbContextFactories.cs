using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TicketNow.UnitTests;

// Factorías solo para `dotnet ef migrations` (los contextos derivados viven en tests
// y sus migraciones también: crean todo en `public`).
public sealed class TestOrdersDbContextFactory : IDesignTimeDbContextFactory<TestOrdersDbContext>
{
    public TestOrdersDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TestOrdersDbContext>().UseNpgsql("Host=localhost;Database=x").Options);
}

public sealed class TestInventoryDbContextFactory : IDesignTimeDbContextFactory<TestInventoryDbContext>
{
    public TestInventoryDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TestInventoryDbContext>().UseNpgsql("Host=localhost;Database=x").Options);
}

public sealed class TestCatalogDbContextFactory : IDesignTimeDbContextFactory<TestCatalogDbContext>
{
    public TestCatalogDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TestCatalogDbContext>().UseNpgsql("Host=localhost;Database=x").Options);
}
