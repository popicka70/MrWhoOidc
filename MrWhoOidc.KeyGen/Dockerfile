# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY ["MrWhoOidc.KeyGen/MrWhoOidc.KeyGen.csproj", "MrWhoOidc.KeyGen/"]
RUN dotnet restore "MrWhoOidc.KeyGen/MrWhoOidc.KeyGen.csproj"

# Copy source code and build
COPY ["MrWhoOidc.KeyGen/", "MrWhoOidc.KeyGen/"]
WORKDIR "/src/MrWhoOidc.KeyGen"
RUN dotnet build "MrWhoOidc.KeyGen.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "MrWhoOidc.KeyGen.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Install curl for health checks (run as root before switching to app user)
RUN apt-get update && apt-get install -y --no-install-recommends curl && \
    rm -rf /var/lib/apt/lists/*

# Create directories for data persistence and secrets
# Volume mounts:
# - /data: SQLite database storage (keygen.db)
# - /secrets: Licensing private key storage (licensing-private-key.pem)
RUN mkdir -p /data /secrets && \
    chown -R app:app /data /secrets

# Copy published application
COPY --from=publish /app/publish .

# Configure ASP.NET Core
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Expose HTTP port
EXPOSE 8080

# Health check - using curl for HTTP health endpoint
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

# Switch to non-root user
USER app

# Set entrypoint
ENTRYPOINT ["dotnet", "MrWhoOidc.KeyGen.dll"]
