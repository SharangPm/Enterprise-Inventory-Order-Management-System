FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Inventory.Domain/Inventory.Domain.csproj Inventory.Domain/
COPY Inventory.Application/Inventory.Application.csproj Inventory.Application/
COPY Inventory.Infrastructure/Inventory.Infrastructure.csproj Inventory.Infrastructure/
COPY Inventory.API/Inventory.API.csproj Inventory.API/
RUN dotnet restore Inventory.API/Inventory.API.csproj
COPY . .
RUN dotnet publish Inventory.API/Inventory.API.csproj -c Release --no-restore -o /app/publish
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Inventory.API.dll"]
