# Nexstock — Enterprise Inventory & Order Management

A full-stack inventory operations starter built with Vue 3, TypeScript, ASP.NET Core 8, MongoDB and SignalR. It includes a seeded dashboard, role-aware JWT login, product and inventory APIs, order reservation, purchase receiving, notifications, Docker Compose and an Azure Pipelines definition.

## Run locally

### Docker (recommended)

1. Copy `.env.example` to `.env` and set `JWT_KEY` to a random secret of at least 32 characters.
2. Run `docker compose up --build` from the repository root. Compose starts MongoDB as a single-node replica set because order and stock changes use Mongo transactions.
3. Open [http://localhost:5173](http://localhost:5173). The API and Swagger UI are at [http://localhost:8080/swagger](http://localhost:8080/swagger).

The first MongoDB start seeds the collections with sample products, warehouses, contacts, transactions, purchase orders and sales orders. MongoDB data persists in the `mongo_data` volume.

**Demo sign in:** `admin@nexstock.local` / `Nexstock123!`. Change or remove the seeded account before deploying a shared environment.

### Run services separately

- API: `dotnet run --project backend/Inventory.API`
- UI: `cd frontend && npm ci && npm run dev`
- Set `Mongo__ConnectionString`, `Mongo__Database`, `Jwt__Key`, and `Frontend__Origin` for the API process. Set `VITE_API_URL` and `VITE_SIGNALR_URL` for the frontend.

## Projects

- `backend/Inventory.Domain` — Mongo document models.
- `backend/Inventory.Application` — contracts and FluentValidation rules.
- `backend/Inventory.Infrastructure` — MongoDB inventory service and stock operations.
- `backend/Inventory.API` — REST endpoints, JWT roles, Swagger, SignalR hub, seed data and centralized errors.
- `backend/Inventory.Tests` — business input validation unit tests.
- `frontend` — Vue dashboard, product/inventory/order screens, charts, responsive navigation, login and live notifications.

## API outline

- `POST /api/auth/login` — JWT login.
- `GET /api/dashboard` — live KPIs, sales, category distribution, latest orders and stock movements.
- `/api/products` — filtered/paged list, details and role-protected CRUD.
- `/api/inventory` and `/api/inventory/transactions` — on-hand stock and audit history; `POST /api/inventory/{id}/adjust` for manager adjustments.
- `/api/orders` — orders reserve stock atomically per line and reject insufficient stock.
- `/api/purchases` and `POST /api/purchases/{id}/receive` — purchasing and receiving.
- `/api/suppliers`, `/api/customers`, `/api/warehouses`, `/api/reports`, `/api/notifications`.
- `/hubs/notifications` — SignalR `InventoryUpdated`, `LowStock`, `OrderCreated` and `PurchaseUpdated` events.

All operational endpoints except login and health require a JWT. Manager/Admin roles are required for catalog changes, stock adjustments, and purchasing; product deletion and user listing are Admin-only.

## Verify

```sh
cd frontend && npm ci && npm run build
docker compose --profile test run --build --rm backend-tests
```

The integration command starts the Compose Mongo replica set and runs unit and API integration tests covering authentication, role authorization, product CRUD, concurrent inventory adjustments, order reservation/cancellation, purchase receiving, master-data CRUD, date filters, and SignalR delivery. To run the .NET build separately, use `dotnet build backend/Inventory.sln --configuration Release`.

## CI/CD

`azure-pipelines.yml` builds and tests the frontend and .NET solution, then publishes API and frontend images to ACR and updates existing Azure Container Apps. Configure the Azure DevOps `production` environment and these pipeline variables before enabling deployment: `azureServiceConnection`, `containerRegistry` (for example `example.azurecr.io`), `azureResourceGroup`, `apiContainerApp`, `webContainerApp`, `apiBaseUrl`, `frontendOrigin`, `mongoConnectionString` (secret), and `jwtKey` (secret). Provision the Container Apps and MongoDB service first.
