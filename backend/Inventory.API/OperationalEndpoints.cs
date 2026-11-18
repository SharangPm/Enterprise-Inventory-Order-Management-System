using System.Security.Claims;
using System.Net.Mail;
using Inventory.Application;
using Inventory.Domain;
using Inventory.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using MongoDB.Bson;
using MongoDB.Driver;

public static class OperationalEndpoints
{
    public static void MapOperationalEndpoints(this WebApplication app)
    {
        app.MapGet("/api/dashboard", Dashboard).RequireAuthorization();

        app.MapGet("/api/inventory", async (IMongoDatabase db, CancellationToken ct) =>
            Results.Ok(await db.GetCollection<Product>("Products").Find(_ => true).SortBy(x => x.Name).ToListAsync(ct))).RequireAuthorization();
        app.MapGet("/api/inventory/transactions", async (IMongoDatabase db, int page = 1, CancellationToken ct = default) =>
            Results.Ok(await db.GetCollection<InventoryTransaction>("InventoryTransactions").Find(_ => true)
                .SortByDescending(x => x.CreatedAt).Skip((Math.Max(1, page) - 1) * 50).Limit(50).ToListAsync(ct))).RequireAuthorization();
        app.MapPost("/api/inventory/{id}/adjust", async (string id, StockAdjustment input, IInventoryService inventory,
            IHubContext<NotificationsHub> hub, CancellationToken ct) =>
        {
            if (!ObjectId.TryParse(id, out _)) return Results.NotFound();
            var product = await inventory.AdjustStock(id, input.Quantity, input.Quantity > 0 ? "StockIn" : "StockOut", ct);
            await hub.Clients.All.SendAsync("InventoryUpdated", new { productId = product?.Id }, ct);
            if (product is not null && product.Stock <= product.ReorderLevel)
                await hub.Clients.All.SendAsync("LowStock", new { product = product.Name, stock = product.Stock }, ct);
            return Results.Ok(product);
        }).RequireAuthorization("Manager");

        app.MapGet("/api/orders", async (IMongoDatabase db, CancellationToken ct) =>
            Results.Ok(await db.GetCollection<Order>("Orders").Find(_ => true).SortByDescending(x => x.CreatedAt).Limit(200).ToListAsync(ct))).RequireAuthorization();
        app.MapPost("/api/orders", async (CreateOrderRequest input, IInventoryService inventory, IHubContext<NotificationsHub> hub, CancellationToken ct) =>
        {
            var order = await inventory.CreateOrder(input.Customer, input.Items, ct);
            await hub.Clients.All.SendAsync("OrderCreated", new { order.Number, order.Total }, ct);
            return Results.Created($"/api/orders/{order.Id}", order);
        }).RequireAuthorization();
        app.MapPatch("/api/orders/{id}/status", UpdateOrderStatus).RequireAuthorization("Manager");

        app.MapGet("/api/purchases", async (IMongoDatabase db, CancellationToken ct) =>
            Results.Ok(await db.GetCollection<PurchaseOrder>("PurchaseOrders").Find(_ => true).SortByDescending(x => x.CreatedAt).Limit(200).ToListAsync(ct))).RequireAuthorization();
        app.MapPost("/api/purchases", CreatePurchase).RequireAuthorization("Manager");
        app.MapPost("/api/purchases/{id}/receive", ReceivePurchase).RequireAuthorization("Manager");

        MapContacts(app, "suppliers", "Suppliers");
        MapContacts(app, "customers", "Customers");
        MapWarehouses(app);
        MapUsers(app);

        app.MapGet("/api/categories", async (IMongoDatabase db, CancellationToken ct) =>
            Results.Ok(await db.GetCollection<Category>("Categories").Find(_ => true).SortBy(x => x.Name).ToListAsync(ct))).RequireAuthorization();
        app.MapGet("/api/notifications", async (IMongoDatabase db, CancellationToken ct) =>
            Results.Ok(await db.GetCollection<Notification>("Notifications").Find(_ => true).SortByDescending(x => x.CreatedAt).Limit(100).ToListAsync(ct))).RequireAuthorization();
        app.MapPatch("/api/notifications/{id}/read", async (string id, IMongoDatabase db, CancellationToken ct) =>
        {
            if (!ObjectId.TryParse(id, out _)) return Results.NotFound();
            var result = await db.GetCollection<Notification>("Notifications").UpdateOneAsync(x => x.Id == id,
                Builders<Notification>.Update.Set(x => x.Read, true), cancellationToken: ct);
            return result.MatchedCount == 0 ? Results.NotFound() : Results.NoContent();
        }).RequireAuthorization();

        app.MapGet("/api/reports", Reports).RequireAuthorization();
    }

    private static async Task<IResult> Dashboard(IMongoDatabase db, DateOnly? startDate, DateOnly? endDate, CancellationToken ct)
    {
        var range = DateRange.Create(startDate, endDate);
        if (!range.IsValid) return Results.ValidationProblem(new Dictionary<string, string[]> { ["dateRange"] = ["Start date must be on or before end date; the range may not exceed 366 days."] });
        var productsCollection = db.GetCollection<Product>("Products");
        var all = await productsCollection.Find(_ => true).ToListAsync(ct);
        var ordersCollection = db.GetCollection<Order>("Orders");
        var rangedOrders = await ordersCollection.Find(x => x.CreatedAt >= range.StartUtc && x.CreatedAt < range.EndExclusiveUtc).ToListAsync(ct);
        var latestOrders = await ordersCollection.Find(_ => true).SortByDescending(x => x.CreatedAt).Limit(5).ToListAsync(ct);
        var transactions = await db.GetCollection<InventoryTransaction>("InventoryTransactions")
            .Find(x => x.CreatedAt >= range.StartUtc && x.CreatedAt < range.EndExclusiveUtc)
            .SortByDescending(x => x.CreatedAt).Limit(5).ToListAsync(ct);
        var timeline = SalesTimeline.Build(rangedOrders, range.StartDate, range.EndDate);
        var colors = new[] { "#3974ed", "#8a72df", "#3bb88d", "#efac46", "#4cb7c0" };
        var categories = all.GroupBy(x => x.Category).Select((g, i) => new
        {
            name = g.Key,
            value = (int)Math.Round(100d * g.Sum(x => x.Stock) / Math.Max(1, all.Sum(p => p.Stock))),
            color = colors[i % colors.Length]
        }).ToList();
        return Results.Ok(new
        {
            startDate = range.StartDate, endDate = range.EndDate,
            totalProducts = all.Count, stockValue = all.Sum(x => x.Price * x.Stock),
            totalOrders = rangedOrders.Count, revenue = rangedOrders.Where(x => x.Status != "Cancelled").Sum(x => x.Total),
            lowStockCount = all.Count(x => x.Stock > 0 && x.Stock <= x.ReorderLevel), outOfStockCount = all.Count(x => x.Stock == 0),
            products = all.OrderBy(x => x.Name).Take(5),
            orders = latestOrders.Select(x => new { id = x.Id, number = x.Number, customer = x.Customer, date = x.CreatedAt.ToString("MMM dd, yyyy"), items = x.Items.Sum(i => i.Quantity), total = x.Total, status = x.Status }),
            salesLabels = timeline.Labels, sales = timeline.Values, categories,
            movements = transactions.Select(x => new
            {
                title = $"{x.Type} Â· {x.Sku}", subtitle = x.Reference, time = x.CreatedAt.ToString("MMM dd, h:mm tt"),
                quantity = (x.Type == "Sale" || x.Type == "StockOut" ? "âˆ’" : "+") + x.Quantity,
                kind = x.Type == "Sale" || x.Type == "StockOut" ? "out" : "in"
            })
        });
    }

    private static async Task<IResult> Reports(IMongoDatabase db, DateOnly? startDate, DateOnly? endDate, CancellationToken ct)
    {
        var range = DateRange.Create(startDate, endDate);
        if (!range.IsValid) return Results.ValidationProblem(new Dictionary<string, string[]> { ["dateRange"] = ["Start date must be on or before end date; the range may not exceed 366 days."] });
        var orders = await db.GetCollection<Order>("Orders").Find(x => x.CreatedAt >= range.StartUtc && x.CreatedAt < range.EndExclusiveUtc).ToListAsync(ct);
        var purchases = await db.GetCollection<PurchaseOrder>("PurchaseOrders").Find(x => x.CreatedAt >= range.StartUtc && x.CreatedAt < range.EndExclusiveUtc).ToListAsync(ct);
        var movements = await db.GetCollection<InventoryTransaction>("InventoryTransactions").Find(x => x.CreatedAt >= range.StartUtc && x.CreatedAt < range.EndExclusiveUtc).ToListAsync(ct);
        var products = await db.GetCollection<Product>("Products").Find(_ => true).ToListAsync(ct);
        var timeline = SalesTimeline.Build(orders, range.StartDate, range.EndDate);
        return Results.Ok(new
        {
            startDate = range.StartDate, endDate = range.EndDate, salesLabels = timeline.Labels, sales = timeline.Values,
            items = new object[]
            {
                new { name = "Sales performance", category = "Sales", stock = orders.Count, price = orders.Where(x => x.Status != "Cancelled").Sum(x => x.Total) },
                new { name = "Inventory valuation", category = "Inventory", stock = products.Sum(x => x.Stock), price = products.Sum(x => x.Stock * x.Price) },
                new { name = "Stock movement", category = "Operations", stock = movements.Count, price = 0m },
                new { name = "Purchase summary", category = "Purchases", stock = purchases.Count, price = purchases.Sum(x => x.Total) }
            }
        });
    }

    private static async Task<IResult> CreatePurchase(CreatePurchase input, IMongoDatabase db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Supplier)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["supplier"] = ["Supplier is required."] });
        OrderRules.Validate(input.Items);
        foreach (var line in input.Items)
        {
            var product = await db.GetCollection<Product>("Products").Find(x => x.Id == line.ProductId).FirstOrDefaultAsync(ct);
            if (product is null) return Results.NotFound(new { message = "A purchase line references an unknown product." });
            line.Name = product.Name;
            line.Price = product.Price;
        }
        var purchase = new PurchaseOrder
        {
            Supplier = input.Supplier.Trim(), Items = input.Items, Number = $"PO-{Guid.NewGuid():N}",
            Total = OrderRules.CalculateTotal(input.Items)
        };
        await db.GetCollection<PurchaseOrder>("PurchaseOrders").InsertOneAsync(purchase, cancellationToken: ct);
        return Results.Created($"/api/purchases/{purchase.Id}", purchase);
    }

    private static async Task<IResult> ReceivePurchase(string id, IMongoClient client, IMongoDatabase db,
        IHubContext<NotificationsHub> hub, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out _)) return Results.NotFound();
        using var session = await client.StartSessionAsync(cancellationToken: ct);
        var purchaseCollection = db.GetCollection<PurchaseOrder>("PurchaseOrders");
        var received = await session.WithTransactionAsync<PurchaseOrder?>(async (transaction, token) =>
        {
            var purchase = await purchaseCollection.Find(transaction, x => x.Id == id).FirstOrDefaultAsync(token);
            if (purchase is null) return null;
            if (purchase.Status == "Received") throw new InvalidOperationException("Purchase order has already been received.");
            foreach (var line in purchase.Items)
            {
                var product = await db.GetCollection<Product>("Products").FindOneAndUpdateAsync<Product, Product>(transaction,
                    x => x.Id == line.ProductId, Builders<Product>.Update.Inc(x => x.Stock, line.Quantity),
                    new FindOneAndUpdateOptions<Product, Product> { ReturnDocument = ReturnDocument.After }, token);
                if (product is null) throw new InvalidOperationException($"Product {line.ProductId} no longer exists.");
                await db.GetCollection<InventoryTransaction>("InventoryTransactions").InsertOneAsync(transaction, new InventoryTransaction
                {
                    ProductId = product.Id!, Sku = product.Sku, Type = "Purchase", Quantity = line.Quantity, Reference = purchase.Number
                }, cancellationToken: token);
            }
            var update = await purchaseCollection.UpdateOneAsync(transaction, x => x.Id == id && x.Status == purchase.Status,
                Builders<PurchaseOrder>.Update.Set(x => x.Status, "Received"), cancellationToken: token);
            if (update.ModifiedCount != 1) throw new InvalidOperationException("Purchase order changed while receiving.");
            purchase.Status = "Received";
            return purchase;
        }, cancellationToken: ct);
        if (received is null) return Results.NotFound();
        await hub.Clients.All.SendAsync("PurchaseUpdated", new { id, status = received.Status }, ct);
        return Results.Ok(received);
    }

    private static async Task<IResult> UpdateOrderStatus(string id, OrderStatusChange input, IMongoClient client,
        IMongoDatabase db, IHubContext<NotificationsHub> hub, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out _)) return Results.NotFound();
        var transitions = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Pending"] = ["Confirmed", "Cancelled"], ["Confirmed"] = ["Processing", "Cancelled"],
            ["Processing"] = ["Shipped", "Cancelled"], ["Shipped"] = ["Delivered"]
        };
        var collection = db.GetCollection<Order>("Orders");
        using var session = await client.StartSessionAsync(cancellationToken: ct);
        var updated = await session.WithTransactionAsync<Order?>(async (transaction, token) =>
        {
            var order = await collection.Find(transaction, x => x.Id == id).FirstOrDefaultAsync(token);
            if (order is null) return null;
            if (!transitions.TryGetValue(order.Status, out var next) || !next.Contains(input.Status, StringComparer.Ordinal))
                throw new InvalidOperationException($"Cannot move an order from {order.Status} to {input.Status}.");
            var result = await collection.UpdateOneAsync(transaction, x => x.Id == id && x.Status == order.Status,
                Builders<Order>.Update.Set(x => x.Status, input.Status), cancellationToken: token);
            if (result.ModifiedCount != 1) throw new InvalidOperationException("Order changed while applying the status update.");
            if (input.Status == "Cancelled")
            {
                foreach (var line in order.Items)
                {
                    var product = await db.GetCollection<Product>("Products").FindOneAndUpdateAsync<Product, Product>(transaction,
                        x => x.Id == line.ProductId, Builders<Product>.Update.Inc(x => x.Stock, line.Quantity),
                        new FindOneAndUpdateOptions<Product, Product> { ReturnDocument = ReturnDocument.After }, token);
                    if (product is null) throw new InvalidOperationException($"Product {line.ProductId} no longer exists.");
                    await db.GetCollection<InventoryTransaction>("InventoryTransactions").InsertOneAsync(transaction, new InventoryTransaction
                    {
                        ProductId = product.Id!, Sku = product.Sku, Type = "Order cancellation", Quantity = line.Quantity, Reference = order.Number
                    }, cancellationToken: token);
                }
            }
            order.Status = input.Status;
            return order;
        }, cancellationToken: ct);
        if (updated is null) return Results.NotFound();
        await hub.Clients.All.SendAsync("OrderStatusUpdated", new { id, status = updated.Status }, ct);
        return Results.Ok(updated);
    }

    private static void MapContacts(WebApplication app, string route, string collectionName)
    {
        app.MapGet($"/api/{route}", async (IMongoDatabase db, CancellationToken ct) => Results.Ok(
            await db.GetCollection<Contact>(collectionName).Find(_ => true).SortBy(x => x.Name).ToListAsync(ct))).RequireAuthorization();
        app.MapPost($"/api/{route}", async (ContactInput input, IMongoDatabase db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Name is required."] });
            var contact = new Contact { Name = input.Name.Trim(), Email = input.Email.Trim(), Phone = input.Phone.Trim(), Company = input.Company.Trim() };
            await db.GetCollection<Contact>(collectionName).InsertOneAsync(contact, cancellationToken: ct);
            return Results.Created($"/api/{route}/{contact.Id}", contact);
        }).RequireAuthorization("Manager");
        app.MapPut($"/api/{route}/{{id}}", async (string id, ContactInput input, IMongoDatabase db, CancellationToken ct) =>
        {
            if (!ObjectId.TryParse(id, out _)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(input.Name)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Name is required."] });
            var collection = db.GetCollection<Contact>(collectionName);
            var contact = await collection.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
            if (contact is null) return Results.NotFound();
            contact.Name = input.Name.Trim(); contact.Email = input.Email.Trim(); contact.Phone = input.Phone.Trim();
            contact.Company = input.Company.Trim(); contact.Status = input.Status;
            await collection.ReplaceOneAsync(x => x.Id == id, contact, cancellationToken: ct);
            return Results.Ok(contact);
        }).RequireAuthorization("Manager");
        app.MapDelete($"/api/{route}/{{id}}", async (string id, IMongoDatabase db, CancellationToken ct) =>
        {
            if (!ObjectId.TryParse(id, out _)) return Results.NotFound();
            var collection = db.GetCollection<Contact>(collectionName);
            var contact = await collection.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
            if (contact is null) return Results.NotFound();
            var referenced = route == "suppliers"
                ? await db.GetCollection<PurchaseOrder>("PurchaseOrders").Find(x => x.Supplier == contact.Name).AnyAsync(ct)
                : await db.GetCollection<Order>("Orders").Find(x => x.Customer == contact.Name).AnyAsync(ct);
            if (referenced) return Results.Conflict(new { message = "A contact with associated order history cannot be deleted." });
            await collection.DeleteOneAsync(x => x.Id == id, ct);
            return Results.NoContent();
        }).RequireAuthorization("Manager");
    }

    private static void MapWarehouses(WebApplication app)
    {
        app.MapGet("/api/warehouses", async (IMongoDatabase db, CancellationToken ct) => Results.Ok(
            await db.GetCollection<Warehouse>("Warehouses").Find(_ => true).SortBy(x => x.Name).ToListAsync(ct))).RequireAuthorization();
        app.MapPost("/api/warehouses", async (WarehouseInput input, IMongoDatabase db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name) || input.Capacity < 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["warehouse"] = ["Name is required and capacity cannot be negative."] });
            var warehouse = new Warehouse { Name = input.Name.Trim(), Location = input.Location.Trim(), Capacity = input.Capacity };
            await db.GetCollection<Warehouse>("Warehouses").InsertOneAsync(warehouse, cancellationToken: ct);
            return Results.Created($"/api/warehouses/{warehouse.Id}", warehouse);
        }).RequireAuthorization("Manager");
        app.MapPut("/api/warehouses/{id}", async (string id, WarehouseInput input, IMongoDatabase db, CancellationToken ct) =>
        {
            if (!ObjectId.TryParse(id, out _)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(input.Name) || input.Capacity < 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["warehouse"] = ["Name is required and capacity cannot be negative."] });
            var collection = db.GetCollection<Warehouse>("Warehouses");
            var warehouse = await collection.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
            if (warehouse is null) return Results.NotFound();
            if (!string.Equals(warehouse.Name, input.Name.Trim(), StringComparison.OrdinalIgnoreCase) &&
                await db.GetCollection<Product>("Products").Find(x => x.Warehouse == warehouse.Name).AnyAsync(ct))
                return Results.Conflict(new { message = "A warehouse assigned to products cannot be renamed." });
            warehouse.Name = input.Name.Trim(); warehouse.Location = input.Location.Trim(); warehouse.Capacity = input.Capacity;
            await collection.ReplaceOneAsync(x => x.Id == id, warehouse, cancellationToken: ct);
            return Results.Ok(warehouse);
        }).RequireAuthorization("Manager");
        app.MapDelete("/api/warehouses/{id}", async (string id, IMongoDatabase db, CancellationToken ct) =>
        {
            if (!ObjectId.TryParse(id, out _)) return Results.NotFound();
            var warehouse = await db.GetCollection<Warehouse>("Warehouses").Find(x => x.Id == id).FirstOrDefaultAsync(ct);
            if (warehouse is null) return Results.NotFound();
            if (await db.GetCollection<Product>("Products").Find(x => x.Warehouse == warehouse.Name).AnyAsync(ct))
                return Results.Conflict(new { message = "A warehouse assigned to products cannot be deleted." });
            await db.GetCollection<Warehouse>("Warehouses").DeleteOneAsync(x => x.Id == id, ct);
            return Results.NoContent();
        }).RequireAuthorization("Admin");
    }

    private static void MapUsers(WebApplication app)
    {
        app.MapGet("/api/users", async (IMongoDatabase db, CancellationToken ct) => Results.Ok(
            await db.GetCollection<AppUser>("Users").Find(_ => true).Project(x => new { x.Id, x.Name, x.Email, x.Role, status = "Active" }).ToListAsync(ct))).RequireAuthorization("Admin");
        app.MapPost("/api/users", async (UserInput input, IMongoDatabase db, CancellationToken ct) =>
        {
            if (!ValidUser(input, true)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["user"] = ["Name, valid email, supported role and password of at least 12 characters are required."] });
            var collection = db.GetCollection<AppUser>("Users"); var email = input.Email.Trim().ToLowerInvariant();
            if (await collection.Find(x => x.Email == email).AnyAsync(ct)) return Results.Conflict(new { message = "Email is already in use." });
            var user = new AppUser { Name = input.Name.Trim(), Email = email, Role = input.Role, PasswordHash = BCrypt.Net.BCrypt.HashPassword(input.Password!) };
            await collection.InsertOneAsync(user, cancellationToken: ct);
            return Results.Created($"/api/users/{user.Id}", new { user.Id, user.Name, user.Email, user.Role, status = "Active" });
        }).RequireAuthorization("Admin");
        app.MapPut("/api/users/{id}", async (string id, UserInput input, HttpContext context, IMongoDatabase db, CancellationToken ct) =>
        {
            if (!ObjectId.TryParse(id, out _)) return Results.NotFound();
            if (!ValidUser(input, false)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["user"] = ["Name, valid email and supported role are required; a supplied password must contain at least 12 characters."] });
            var collection = db.GetCollection<AppUser>("Users"); var user = await collection.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
            if (user is null) return Results.NotFound();
            var email = input.Email.Trim().ToLowerInvariant();
            if (await collection.Find(x => x.Id != id && x.Email == email).AnyAsync(ct)) return Results.Conflict(new { message = "Email is already in use." });
            if (user.Role == "Admin" && input.Role != "Admin" && await collection.CountDocumentsAsync(x => x.Role == "Admin", cancellationToken: ct) <= 1)
                return Results.Conflict(new { message = "At least one administrator must remain." });
            user.Name = input.Name.Trim(); user.Email = email; user.Role = input.Role;
            if (!string.IsNullOrWhiteSpace(input.Password)) user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(input.Password);
            await collection.ReplaceOneAsync(x => x.Id == id, user, cancellationToken: ct);
            return Results.Ok(new { user.Id, user.Name, user.Email, user.Role, status = "Active" });
        }).RequireAuthorization("Admin");
        app.MapDelete("/api/users/{id}", async (string id, HttpContext context, IMongoDatabase db, CancellationToken ct) =>
        {
            if (!ObjectId.TryParse(id, out _)) return Results.NotFound();
            var collection = db.GetCollection<AppUser>("Users"); var user = await collection.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
            if (user is null) return Results.NotFound();
            if (context.User.FindFirstValue(ClaimTypes.NameIdentifier) == id) return Results.Conflict(new { message = "You cannot delete your own account." });
            if (user.Role == "Admin" && await collection.CountDocumentsAsync(x => x.Role == "Admin", cancellationToken: ct) <= 1)
                return Results.Conflict(new { message = "At least one administrator must remain." });
            await collection.DeleteOneAsync(x => x.Id == id, ct);
            return Results.NoContent();
        }).RequireAuthorization("Admin");
    }

    private static bool ValidUser(UserInput input, bool requirePassword) =>
        !string.IsNullOrWhiteSpace(input.Name) && MailAddress.TryCreate(input.Email, out _) &&
        input.Role is "Admin" or "Manager" or "Staff" &&
        (requirePassword ? !string.IsNullOrWhiteSpace(input.Password) && input.Password.Length >= 12 : input.Password is null || input.Password.Length >= 12);
}

public sealed record DateRange(DateOnly StartDate, DateOnly EndDate, DateTime StartUtc, DateTime EndExclusiveUtc, bool IsValid)
{
    public static DateRange Create(DateOnly? start, DateOnly? end)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var endDate = end ?? today;
        var startDate = start ?? endDate.AddDays(-29);
        var valid = startDate <= endDate && endDate.DayNumber - startDate.DayNumber <= 365 && endDate < DateOnly.MaxValue;
        var endExclusive = endDate < DateOnly.MaxValue
            ? endDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
            : DateTime.MaxValue;
        return new(startDate, endDate, startDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), endExclusive, valid);
    }
}

public sealed record SalesSeries(string[] Labels, decimal[] Values);
public static class SalesTimeline
{
    public static SalesSeries Build(IReadOnlyCollection<Order> orders, DateOnly start, DateOnly end)
    {
        var daily = end.DayNumber - start.DayNumber <= 62;
        var labels = new List<string>(); var values = new List<decimal>();
        if (daily)
        {
            for (var date = start; date <= end; date = date.AddDays(1))
            {
                labels.Add(date.ToString("MMM d"));
                values.Add(orders.Where(x => DateOnly.FromDateTime(x.CreatedAt) == date && x.Status != "Cancelled").Sum(x => x.Total));
            }
        }
        else
        {
            var cursor = new DateOnly(start.Year, start.Month, 1); var last = new DateOnly(end.Year, end.Month, 1);
            for (; cursor <= last; cursor = cursor.AddMonths(1))
            {
                var month = cursor; labels.Add(month.ToString("MMM yy"));
                values.Add(orders.Where(x => x.CreatedAt.Year == month.Year && x.CreatedAt.Month == month.Month && x.Status != "Cancelled").Sum(x => x.Total));
            }
        }
        return new(labels.ToArray(), values.ToArray());
    }
}
