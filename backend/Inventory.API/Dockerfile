FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Inventory.sln ./
COPY Inventory.Domain/Inventory.Domain.csproj Inventory.Domain/
COPY Inventory.Application/Inventory.Application.csproj Inventory.Application/
COPY Inventory.Infrastructure/Inventory.Infrastructure.csproj Inventory.Infrastructure/
COPY Inventory.API/Inventory.API.csproj Inventory.API/
RUN dotnet restore Inventory.API/Inventory.API.csproj
COPY . .
RUN dotnet build Inventory.API/Inventory.API.csproj -c Release --no-restore
RUN dotnet publish Inventory.API/Inventory.API.csproj -c Release --no-restore -o /app/publish
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:10000
EXPOSE 10000
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Inventory.API.dll"]
