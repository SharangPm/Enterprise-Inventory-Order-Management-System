using Inventory.Application;
using Inventory.Domain;
using MongoDB.Driver;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure;

public sealed class MongoInventoryService(IMongoClient client, IMongoDatabase db, ILogger<MongoInventoryService> logger) : IInventoryService
{
    private readonly IMongoCollection<Product> products = db.GetCollection<Product>("Products");
    private readonly IMongoCollection<InventoryTransaction> transactions = db.GetCollection<InventoryTransaction>("InventoryTransactions");
    private readonly IMongoCollection<Order> orders = db.GetCollection<Order>("Orders");

    public async Task<Product> CreateProduct(CreateProduct input, CancellationToken ct)
    {
        var product = new Product
        {
            Name = input.Name.Trim(), Sku = input.Sku.Trim(), Category = input.Category.Trim(),
            Price = input.Price, Stock = input.Stock, ReorderLevel = input.ReorderLevel,
            Warehouse = input.Warehouse.Trim()
        };
        using var session = await client.StartSessionAsync(cancellationToken: ct);
        var created = await session.WithTransactionAsync(async (transaction, token) =>
        {
            await products.InsertOneAsync(transaction, product, cancellationToken: token);
            if (product.Stock > 0)
                await transactions.InsertOneAsync(transaction, new InventoryTransaction
                {
                    ProductId = product.Id!, Sku = product.Sku, Type = "Opening stock", Quantity = product.Stock, Reference = "product creation"
                }, cancellationToken: token);
            return product;
        }, cancellationToken: ct);
        logger.LogInformation("Product {ProductId} with SKU {Sku} created", created.Id, created.Sku);
        return created;
    }

    public async Task<Product?> UpdateProduct(string id, CreateProduct input, CancellationToken ct)
    {
        using var session = await client.StartSessionAsync(cancellationToken: ct);
        var updated = await session.WithTransactionAsync<Product?>(async (transaction, token) =>
        {
            var current = await products.Find(transaction, x => x.Id == id).FirstOrDefaultAsync(token);
            if (current is null) return null;
            var delta = input.Stock - current.Stock;
            var replacement = new Product
            {
                Id = current.Id, CreatedAt = current.CreatedAt, Barcode = current.Barcode,
                Name = input.Name.Trim(), Sku = input.Sku.Trim(), Category = input.Category.Trim(),
                Price = input.Price, Stock = input.Stock, ReorderLevel = input.ReorderLevel, Warehouse = input.Warehouse.Trim()
            };
            var result = await products.ReplaceOneAsync(transaction, x => x.Id == id && x.Stock == current.Stock, replacement, cancellationToken: token);
            if (result.ModifiedCount != 1) throw new InvalidOperationException("Product changed while applying the update.");
            if (delta != 0)
                await transactions.InsertOneAsync(transaction, new InventoryTransaction
                {
                    ProductId = id, Sku = replacement.Sku, Type = "Product stock adjustment", Quantity = Math.Abs(delta), Reference = "product update"
                }, cancellationToken: token);
            return replacement;
        }, cancellationToken: ct);
        if (updated is not null) logger.LogInformation("Product {ProductId} updated", id);
        return updated;
    }

    public async Task<Product?> AdjustStock(string id, int delta, string type, CancellationToken ct)
    {
        if (delta == 0 || delta == int.MinValue)
            throw new InvalidOperationException("Quantity must be a non-zero supported value.");

        using var session = await client.StartSessionAsync(cancellationToken: ct);
        var result = await session.WithTransactionAsync(async (transaction, token) =>
        {
            var filter = Builders<Product>.Filter.Eq(x => x.Id, id) &
                (delta < 0 ? Builders<Product>.Filter.Gte(x => x.Stock, -delta) : Builders<Product>.Filter.Empty);
            var updated = await products.FindOneAndUpdateAsync(
                transaction,
                filter,
                Builders<Product>.Update.Inc(x => x.Stock, delta),
                new FindOneAndUpdateOptions<Product, Product> { ReturnDocument = ReturnDocument.After },
                token);

            if (updated is null)
                throw new InvalidOperationException("Product not found or insufficient stock.");

            await transactions.InsertOneAsync(transaction, new InventoryTransaction
            {
                ProductId = id, Sku = updated.Sku, Type = type, Quantity = Math.Abs(delta), Reference = "manual"
            }, cancellationToken: token);
            return updated;
        }, cancellationToken: ct);
        logger.LogInformation("Stock adjusted for product {ProductId} by {Quantity} ({TransactionType})", id, delta, type);
        return result;
    }

    public async Task<Order> CreateOrder(string customer, List<OrderLine> lines, CancellationToken ct)
    {
        OrderRules.Validate(lines);
        if (string.IsNullOrWhiteSpace(customer))
            throw new InvalidOperationException("A customer is required.");

        using var session = await client.StartSessionAsync(cancellationToken: ct);
        var result = await session.WithTransactionAsync(async (transaction, token) =>
        {
            var reserved = new List<Product>(lines.Count);
            foreach (var line in lines)
            {
                var product = await products.FindOneAndUpdateAsync(
                    transaction,
                    Builders<Product>.Filter.Eq(x => x.Id, line.ProductId) & Builders<Product>.Filter.Gte(x => x.Stock, line.Quantity),
                    Builders<Product>.Update.Inc(x => x.Stock, -line.Quantity),
                    new FindOneAndUpdateOptions<Product, Product> { ReturnDocument = ReturnDocument.After },
                    token);

                if (product is null)
                    throw new InvalidOperationException($"Insufficient inventory for product {line.ProductId}.");

                line.Name = product.Name;
                line.Price = product.Price;
                reserved.Add(product);
            }

            var order = new Order
            {
                Customer = customer.Trim(), Items = lines, Number = $"SO-{Guid.NewGuid():N}"[..15].ToUpperInvariant(),
                Total = OrderRules.CalculateTotal(lines), Status = "Confirmed"
            };
            await orders.InsertOneAsync(transaction, order, cancellationToken: token);

            for (var i = 0; i < lines.Count; i++)
            {
                await transactions.InsertOneAsync(transaction, new InventoryTransaction
                {
                    ProductId = reserved[i].Id!, Sku = reserved[i].Sku, Type = "Sale",
                    Quantity = lines[i].Quantity, Reference = order.Number
                }, cancellationToken: token);
            }

            return order;
        }, cancellationToken: ct);
        logger.LogInformation("Order {OrderNumber} created for {Customer}; {LineCount} lines, total {OrderTotal}", result.Number, result.Customer, result.Items.Count, result.Total);
        return result;
    }
}
