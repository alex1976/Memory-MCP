FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Memory-MCP.sln ./
COPY src/MemoryMcp.Domain/MemoryMcp.Domain.csproj src/MemoryMcp.Domain/
COPY src/MemoryMcp.Application/MemoryMcp.Application.csproj src/MemoryMcp.Application/
COPY src/MemoryMcp.Infrastructure/MemoryMcp.Infrastructure.csproj src/MemoryMcp.Infrastructure/
COPY src/MemoryMcp.Api/MemoryMcp.Api.csproj src/MemoryMcp.Api/
RUN dotnet restore src/MemoryMcp.Api/MemoryMcp.Api.csproj

COPY src/ src/
RUN dotnet publish src/MemoryMcp.Api/MemoryMcp.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

EXPOSE 8080
ENTRYPOINT ["dotnet", "MemoryMcp.Api.dll"]
