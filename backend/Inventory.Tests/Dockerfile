FROM mcr.microsoft.com/dotnet/sdk:8.0
WORKDIR /src
COPY . .
ENTRYPOINT ["dotnet", "test", "Inventory.Tests/Inventory.Tests.csproj", "--configuration", "Release", "--logger", "console;verbosity=normal"]
