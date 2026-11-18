using System.IdentityModel.Tokens.Jwt;using System.Security.Claims;using System.Text;using FluentValidation;using Inventory.Application;using Inventory.Domain;using Inventory.Infrastructure;using Microsoft.AspNetCore.Authentication.JwtBearer;using Microsoft.AspNetCore.Mvc;using Microsoft.AspNetCore.SignalR;using Microsoft.IdentityModel.Tokens;using MongoDB.Driver;
var builder=WebApplication.CreateBuilder(args);
var isProduction=builder.Environment.IsProduction();
var mongoConnectionString=builder.Configuration["Mongo:ConnectionString"];
var mongoDatabase=builder.Configuration["Mongo:Database"];
var jwtIssuer=jwtIssuer;
var jwtAudience=jwtAudience;
var jwtKey=jwtKey;
var frontendOrigin=isProduction?builder.Configuration["FRONTEND_ORIGIN"]:frontendOrigin??"http://localhost:5174";
if(isProduction){if(string.IsNullOrWhiteSpace(mongoConnectionString)||mongoConnectionString.Contains("localhost",StringComparison.OrdinalIgnoreCase)||mongoConnectionString.Contains("127.0.0.1"))throw new InvalidOperationException("Production Mongo__ConnectionString must be set to a non-local MongoDB connection.");if(string.IsNullOrWhiteSpace(mongoDatabase))throw new InvalidOperationException("Production Mongo__Database is required.");if(string.IsNullOrWhiteSpace(jwtIssuer)||string.IsNullOrWhiteSpace(jwtAudience))throw new InvalidOperationException("Production Jwt__Issuer and Jwt__Audience are required.");if(string.IsNullOrWhiteSpace(jwtKey)||jwtKey.Length<32||jwtKey.Contains("replace-this-development-key",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Production Jwt__Key must be a strong secret of at least 32 characters.");if(string.IsNullOrWhiteSpace(frontendOrigin)||!Uri.TryCreate(frontendOrigin,UriKind.Absolute,out var originUri)||originUri.Scheme!="https")throw new InvalidOperationException("Production FRONTEND_ORIGIN must be the absolute HTTPS Vercel frontend origin.");builder.WebHost.UseUrls("http://0.0.0.0:10000");}builder.Logging.ClearProviders();builder.Logging.AddJsonConsole(options=>options.IncludeScopes=true);builder.Services.AddEndpointsApiExplorer();builder.Services.AddSwaggerGen();builder.Services.AddSignalR();builder.Services.AddCors(o=>o.AddDefaultPolicy(p=>p.WithOrigins(frontendOrigin??"http://localhost:5174").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o=>{o.TokenValidationParameters=new(){ValidateIssuer=true,ValidIssuer=jwtIssuer,ValidateAudience=true,ValidAudience=jwtAudience,ValidateIssuerSigningKey=true,IssuerSigningKey=new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey!)),ValidateLifetime=true};o.Events=new(){OnMessageReceived=ctx=>{var token=ctx.Request.Query["access_token"];if(!string.IsNullOrEmpty(token)&&ctx.HttpContext.Request.Path.StartsWithSegments("/hubs/notifications"))ctx.Token=token;return Task.CompletedTask;},OnAuthenticationFailed=ctx=>{ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("JwtAuthentication").LogWarning(ctx.Exception,"JWT validation failed");return Task.CompletedTask;}};});builder.Services.AddAuthorization(o=>{o.AddPolicy("Manager",p=>p.RequireRole("Admin","Manager"));o.AddPolicy("Admin",p=>p.RequireRole("Admin"));});
var client=new MongoClient(mongoConnectionString);var db=client.GetDatabase(mongoDatabase);builder.Services.AddSingleton<IMongoClient>(client);builder.Services.AddSingleton(db);builder.Services.AddScoped<IInventoryService,MongoInventoryService>();builder.Services.AddScoped<IValidator<CreateProduct>,CreateProductValidator>();var app=builder.Build();app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    var status = exception switch
    {
        FluentValidation.ValidationException => StatusCodes.Status400BadRequest,
        InvalidOperationException => StatusCodes.Status409Conflict,
        MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey } => StatusCodes.Status409Conflict,
        MongoException { } => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };
    logger.LogError(exception, "Request failed with status {StatusCode} for {Method} {Path}; trace {TraceId}", status, context.Request.Method, context.Request.Path, context.TraceIdentifier);
    context.Response.StatusCode = status;
    context.Response.ContentType = "application/problem+json";
    await context.Response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = status,
        Title = status switch { 400 => "Request validation failed", 409 => "Request could not be completed", 503 => "A required service is unavailable", _ => "Unexpected server error" },
        Detail = status is 400 or 409 ? exception?.Message : "An unexpected error occurred.",
        Extensions = { ["traceId"] = context.TraceIdentifier }
    });
}));
app.UseCors();app.UseAuthentication();app.UseAuthorization();app.UseSwagger();app.UseSwaggerUI();

app.MapGet("/health",()=>Results.Ok(new{status="healthy"}));app.MapHub<NotificationsHub>("/hubs/notifications").RequireAuthorization();
app.MapPost("/api/auth/login",async(LoginRequest input,IMongoDatabase database,IConfiguration config) => {var user=await database.GetCollection<AppUser>("Users").Find(x=>x.Email==input.Email.ToLowerInvariant()).FirstOrDefaultAsync();if(user is null||!BCrypt.Net.BCrypt.Verify(input.Password,user.PasswordHash))return Results.Unauthorized();var claims=new[]{new Claim(ClaimTypes.NameIdentifier,user.Id!),new Claim(ClaimTypes.Name,user.Name),new Claim(ClaimTypes.Email,user.Email),new Claim(ClaimTypes.Role,user.Role)};var signingKey=new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));var token=new JwtSecurityToken(config["Jwt:Issuer"],config["Jwt:Audience"],claims,expires:DateTime.UtcNow.AddHours(8),signingCredentials:new SigningCredentials(signingKey,SecurityAlgorithms.HmacSha256));return Results.Ok(new{token=new JwtSecurityTokenHandler().WriteToken(token),user=new{name=user.Name,email=user.Email,role=user.Role}});});

app.MapGet("/api/products", async (IMongoDatabase database, string? search, CancellationToken ct) =>
{
    var products = database.GetCollection<Product>("Products");
    var filter = string.IsNullOrWhiteSpace(search) ? Builders<Product>.Filter.Empty : Builders<Product>.Filter.Or(
        Builders<Product>.Filter.Regex(x => x.Name, new MongoDB.Bson.BsonRegularExpression(search, "i")),
        Builders<Product>.Filter.Regex(x => x.Sku, new MongoDB.Bson.BsonRegularExpression(search, "i")));
    return Results.Ok(await products.Find(filter).SortBy(x => x.Name).ToListAsync(ct));
}).RequireAuthorization();
app.MapGet("/api/products/{id}", async (string id, IMongoDatabase database, CancellationToken ct) =>
{
    if (!MongoDB.Bson.ObjectId.TryParse(id, out _)) return Results.NotFound();
    var product = await database.GetCollection<Product>("Products").Find(x => x.Id == id).FirstOrDefaultAsync(ct);
    return product is null ? Results.NotFound() : Results.Ok(product);
}).RequireAuthorization();
app.MapPost("/api/products", async (CreateProductInput input, IValidator<CreateProduct> validator, IInventoryService inventory,
    IHubContext<NotificationsHub> hub, CancellationToken ct) =>
{
    var command = new CreateProduct(input.Name, input.Sku, input.Category, input.Price, input.Stock, input.ReorderLevel, input.Warehouse);
    await validator.ValidateAndThrowAsync(command, ct);
    var product = await inventory.CreateProduct(command, ct);
    await hub.Clients.All.SendAsync("InventoryUpdated", new { productId = product.Id }, ct);
    return Results.Created($"/api/products/{product.Id}", product);
}).RequireAuthorization("Manager");
app.MapPut("/api/products/{id}", async (string id, CreateProductInput input, IValidator<CreateProduct> validator, IInventoryService inventory, CancellationToken ct) =>
{
    if (!MongoDB.Bson.ObjectId.TryParse(id, out _)) return Results.NotFound();
    var command = new CreateProduct(input.Name, input.Sku, input.Category, input.Price, input.Stock, input.ReorderLevel, input.Warehouse);
    await validator.ValidateAndThrowAsync(command, ct);
    var product = await inventory.UpdateProduct(id, command, ct);
    return product is null ? Results.NotFound() : Results.Ok(product);
}).RequireAuthorization("Manager");
app.MapDelete("/api/products/{id}", async (string id, IMongoDatabase database, CancellationToken ct) =>
{
    if (!MongoDB.Bson.ObjectId.TryParse(id, out _)) return Results.NotFound();
    var transactions = database.GetCollection<InventoryTransaction>("InventoryTransactions");
    if (await transactions.Find(x => x.ProductId == id).AnyAsync(ct)) return Results.Conflict(new { error = "Products with inventory history cannot be deleted." });
    if (await database.GetCollection<Order>("Orders").Find(x => x.Items.Any(i => i.ProductId == id)).AnyAsync(ct)) return Results.Conflict(new { error = "Products referenced by orders cannot be deleted." });
    var result = await database.GetCollection<Product>("Products").DeleteOneAsync(x => x.Id == id, ct);
    return result.DeletedCount == 0 ? Results.NotFound() : Results.NoContent();
}).RequireAuthorization("Admin");

app.MapOperationalEndpoints();
await Seed.Initialize(db);
app.Run();
public record LoginRequest(string Email,string Password);public record CreateProductInput(string Name,string Sku,string Category,decimal Price,int Stock,int ReorderLevel,string Warehouse);public record StockAdjustment(int Quantity);public record CreateOrderRequest(string Customer,List<OrderLine> Items);public record CreatePurchase(string Supplier,List<OrderLine> Items);public record OrderStatusChange(string Status);public record ContactInput(string Name,string Email,string Phone,string Company,string Status="Active");public record WarehouseInput(string Name,string Location,int Capacity);public record UserInput(string Name,string Email,string Role,string? Password);public sealed class NotificationsHub(ILogger<NotificationsHub> logger):Hub{public override async Task OnConnectedAsync(){logger.LogInformation("Authenticated SignalR client connected: {ConnectionId}",Context.ConnectionId);await base.OnConnectedAsync();}public override async Task OnDisconnectedAsync(Exception? exception){logger.LogInformation(exception,"SignalR client disconnected: {ConnectionId}",Context.ConnectionId);await base.OnDisconnectedAsync(exception);}}
public partial class Program{}
