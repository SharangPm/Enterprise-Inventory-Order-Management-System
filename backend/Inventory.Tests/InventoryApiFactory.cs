using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace Inventory.Tests;

public sealed class InventoryApiFactory : WebApplicationFactory<Program>
{
    private readonly string connectionString = Environment.GetEnvironmentVariable("TEST_MONGO")
        ?? "mongodb://localhost:27017/?replicaSet=rs0";

    public string DatabaseName { get; } = "nexstock_test_" + Guid.NewGuid().ToString("N");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Mongo:ConnectionString", connectionString);
        builder.UseSetting("Mongo:Database", DatabaseName);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mongo:ConnectionString"] = connectionString,
            ["Mongo:Database"] = DatabaseName,
            ["Jwt:Issuer"] = "Nexstock.Tests",
            ["Jwt:Audience"] = "Nexstock.Tests.Client",
            ["Jwt:Key"] = "integration-test-secret-key-at-least-32-characters-long",
            ["Frontend:Origin"] = "http://localhost"
        }));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { new MongoClient(connectionString).DropDatabase(DatabaseName); }
            catch { /* Test cleanup should not mask the test result if MongoDB stopped. */ }
        }
        base.Dispose(disposing);
    }
}
