# syntax=docker/dockerfile:1

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore (cached on csproj changes only)
COPY src/GarminMcp.Core/GarminMcp.Core.csproj src/GarminMcp.Core/
COPY src/GarminMcp.Server/GarminMcp.Server.csproj src/GarminMcp.Server/
RUN dotnet restore src/GarminMcp.Server/GarminMcp.Server.csproj

# Build & publish
COPY src/ src/
RUN dotnet publish src/GarminMcp.Server/GarminMcp.Server.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Default: stdio transport (for Claude Desktop via `docker run -i`).
# Set MCP_TRANSPORT=http to expose the Streamable HTTP + REST server on port 8080.
ENV MCP_TRANSPORT=stdio
ENV ASPNETCORE_URLS=http://+:8080
ENV GARMIN_SETUP_PORT=8765

# 8080 = MCP/REST (http mode). 8765 = browser sign-in UI (both modes).
EXPOSE 8080
EXPOSE 8765

ENTRYPOINT ["dotnet", "GarminMcp.Server.dll"]
