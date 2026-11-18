using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace Inventory.Tests;

public sealed class ApiIntegrationTests
{
    [Fact]
    public async Task Authentication_and_role_authorization_are_enforced()
    {
        using var factory = new InventoryApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { email = "admin@nexstock.local", password = "wrong" })).StatusCode);

        await SignIn(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users")).StatusCode);
        var created = await client.PostAsJsonAsync("/api/users", new { name = "Staff Member", email = "staff@example.test", role = "Staff", password = "StaffPassword123!" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var staffClient = factory.CreateClient();
        await SignIn(staffClient, "staff@example.test", "StaffPassword123!");
        Assert.Equal(HttpStatusCode.OK, (await staffClient.GetAsync("/api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staffClient.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staffClient.PostAsJsonAsync("/api/products", ProductInput("STAFF-1", 0))).StatusCode);
    }

    [Fact]
    public async Task Product_crud_and_concurrent_adjustments_never_make_stock_negative()
    {
        using var factory = new InventoryApiFactory();
        using var client = factory.CreateClient();
        await SignIn(client);

        var response = await client.PostAsJsonAsync("/api/products", ProductInput("RACE-1", 10));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetString()!;

        var adjustments = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            client.PostAsJsonAsync($"/api/inventory/{id}/adjust", new { quantity = -2 })));
        Assert.Equal(5, adjustments.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, adjustments.Count(x => x.StatusCode == HttpStatusCode.Conflict));

        var current = await client.GetFromJsonAsync<JsonElement>($"/api/products/{id}");
        Assert.Equal(0, current.GetProperty("stock").GetInt32());
        var history = await client.GetFromJsonAsync<JsonElement>("/api/inventory/transactions");
        Assert.True(history.GetArrayLength() >= 6); // opening stock plus five committed adjustments

        var update = await client.PutAsJsonAsync($"/api/products/{id}", ProductInput("RACE-1", 0, "Renamed race test product"));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/products/{id}")).StatusCode); // retain inventory audit history
        var disposableProduct = await CreateProduct(client, "DELETE-1", 0);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/products/{disposableProduct}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/products/{disposableProduct}")).StatusCode);
        foreach (var item in adjustments) item.Dispose();
    }

    [Fact]
    public async Task Order_reservation_rejects_oversell_and_cancel_restores_stock_atomically()
    {
        using var factory = new InventoryApiFactory();
        using var client = factory.CreateClient();
        await SignIn(client);
        var productId = await CreateProduct(client, "ORDER-1", 4);

        var oversell = await client.PostAsJsonAsync("/api/orders", new { customer = "Order test", items = new[] { new { productId, quantity = 5 } } });
        Assert.Equal(HttpStatusCode.Conflict, oversell.StatusCode);
        Assert.Equal(4, (await client.GetFromJsonAsync<JsonElement>($"/api/products/{productId}")).GetProperty("stock").GetInt32());

        var created = await client.PostAsJsonAsync("/api/orders", new { customer = "Order test", items = new[] { new { productId, quantity = 3 } } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var orderJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var orderId = orderJson.RootElement.GetProperty("id").GetString()!;
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>($"/api/products/{productId}")).GetProperty("stock").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/orders/{orderId}/status", new { status = "Processing" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/orders/{orderId}/status", new { status = "Cancelled" })).StatusCode);
        Assert.Equal(4, (await client.GetFromJsonAsync<JsonElement>($"/api/products/{productId}")).GetProperty("stock").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync($"/api/orders/{orderId}/status", new { status = "Confirmed" })).StatusCode);
    }

    [Fact]
    public async Task Purchase_receiving_is_idempotent_under_concurrent_requests()
    {
        using var factory = new InventoryApiFactory();
        using var client = factory.CreateClient();
        await SignIn(client);
        var productId = await CreateProduct(client, "PURCHASE-1", 2);
        var created = await client.PostAsJsonAsync("/api/purchases", new { supplier = "Receiving test", items = new[] { new { productId, quantity = 5 } } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var purchaseId = json.RootElement.GetProperty("id").GetString()!;

        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.PostAsync($"/api/purchases/{purchaseId}/receive", null)));
        Assert.Single(responses.Where(x => x.StatusCode == HttpStatusCode.OK));
        Assert.Single(responses.Where(x => x.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(7, (await client.GetFromJsonAsync<JsonElement>($"/api/products/{productId}")).GetProperty("stock").GetInt32());
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task Contact_warehouse_and_user_edit_delete_endpoints_enforce_references_and_roles()
    {
        using var factory = new InventoryApiFactory();
        using var client = factory.CreateClient();
        await SignIn(client);

        var contact = await client.PostAsJsonAsync("/api/suppliers", new { name = "CRUD Supplier", email = "crud@example.test", phone = "555-0100", company = "CRUD" });
        Assert.Equal(HttpStatusCode.Created, contact.StatusCode);
        using var contactJson = JsonDocument.Parse(await contact.Content.ReadAsStringAsync());
        var contactId = contactJson.RootElement.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/suppliers/{contactId}", new { name = "Updated Supplier", email = "crud@example.test", phone = "555-0101", company = "CRUD", status = "Active" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/suppliers/{contactId}")).StatusCode);

        var warehouse = await client.PostAsJsonAsync("/api/warehouses", new { name = "CRUD Warehouse", location = "Test", capacity = 50 });
        Assert.Equal(HttpStatusCode.Created, warehouse.StatusCode);
        using var warehouseJson = JsonDocument.Parse(await warehouse.Content.ReadAsStringAsync());
        var warehouseId = warehouseJson.RootElement.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/warehouses/{warehouseId}", new { name = "CRUD Warehouse", location = "Updated", capacity = 75 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/warehouses/{warehouseId}")).StatusCode);

        var user = await client.PostAsJsonAsync("/api/users", new { name = "CRUD Staff", email = "crud-staff@example.test", role = "Staff", password = "LongPassword123!" });
        Assert.Equal(HttpStatusCode.Created, user.StatusCode);
        using var userJson = JsonDocument.Parse(await user.Content.ReadAsStringAsync());
        var userId = userJson.RootElement.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/users/{userId}", new { name = "Edited Staff", email = "crud-staff@example.test", role = "Manager", password = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/users/{userId}")).StatusCode);
    }

    [Fact]
    public async Task Date_ranges_filter_report_analytics_and_reject_invalid_periods()
    {
        using var factory = new InventoryApiFactory();
        using var client = factory.CreateClient();
        await SignIn(client);
        var start = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-14)).ToString("yyyy-MM-dd");
        var end = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var report = await client.GetFromJsonAsync<JsonElement>($"/api/reports?startDate={start}&endDate={end}");
        Assert.Equal(start, report.GetProperty("startDate").GetString());
        Assert.Equal(end, report.GetProperty("endDate").GetString());
        Assert.True(report.GetProperty("salesLabels").GetArrayLength() >= 14);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/reports?startDate=2026-11-20&endDate=2026-11-01")).StatusCode);
    }

    [Fact]
    public async Task SignalR_requires_jwt_and_delivers_inventory_events()
    {
        using var factory = new InventoryApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/hubs/notifications/negotiate?negotiateVersion=1")).StatusCode);
        var token = await SignIn(client);
        using var delivered = new ManualResetEventSlim();
        var connection = new HubConnectionBuilder().WithUrl(new Uri(client.BaseAddress!, "/hubs/notifications"), options =>
        {
            options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            options.WebSocketFactory = (context, cancellationToken) => new ValueTask<System.Net.WebSockets.WebSocket>(
                factory.Server.CreateWebSocketClient().ConnectAsync(context.Uri, cancellationToken));
        }).Build();
        connection.On<JsonElement>("InventoryUpdated", _ => delivered.Set());
        try
        {
            await connection.StartAsync();
            var response = await client.PostAsJsonAsync("/api/products", ProductInput("SIGNALR-1", 0));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.True(delivered.Wait(TimeSpan.FromSeconds(5)), "SignalR inventory event was not received.");
        }
        finally { await connection.DisposeAsync(); }
    }

    private static async Task<string> SignIn(HttpClient client, string email = "admin@nexstock.local", string password = "Nexstock123!")
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = json.RootElement.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return token;
    }

    private static async Task<string> CreateProduct(HttpClient client, string sku, int stock)
    {
        var response = await client.PostAsJsonAsync("/api/products", ProductInput(sku, stock));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private static object ProductInput(string sku, int stock, string name = "Integration product") => new
    {
        name, sku, category = "Test", price = 20m, stock, reorderLevel = 2, warehouse = "Main warehouse"
    };
}
