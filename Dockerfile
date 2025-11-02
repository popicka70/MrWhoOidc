# syntax=docker/dockerfile:1

# Build stage: Restore dependencies and build the application
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files first for better layer caching
COPY MrWhoOidc.slnx ./
COPY MrWhoOidc.WebAuth/MrWhoOidc.WebAuth.csproj MrWhoOidc.WebAuth/
COPY MrWhoOidc.Auth/MrWhoOidc.Auth.csproj MrWhoOidc.Auth/
COPY MrWhoOidc.ServiceDefaults/MrWhoOidc.ServiceDefaults.csproj MrWhoOidc.ServiceDefaults/
COPY MrWhoOidc.Security/MrWhoOidc.Security.csproj MrWhoOidc.Security/

# Restore dependencies
RUN dotnet restore "MrWhoOidc.WebAuth/MrWhoOidc.WebAuth.csproj"

# Copy remaining source code
COPY MrWhoOidc.WebAuth/ MrWhoOidc.WebAuth/
COPY MrWhoOidc.Auth/ MrWhoOidc.Auth/
COPY MrWhoOidc.ServiceDefaults/ MrWhoOidc.ServiceDefaults/
COPY MrWhoOidc.Security/ MrWhoOidc.Security/

# Build and publish the application
RUN dotnet publish "MrWhoOidc.WebAuth/MrWhoOidc.WebAuth.csproj" \
    -c Release \
    -o /app/publish \
    -p:UseAppHost=false \
    --no-restore

# Runtime stage: Use chiseled Ubuntu image for minimal attack surface
FROM mcr.microsoft.com/dotnet/aspnet:9.0-jammy-chiseled AS final

# Add OCI labels for metadata
LABEL org.opencontainers.image.title="MrWhoOidc" \
      org.opencontainers.image.description="OpenID Connect Provider with multi-tenancy and IdP chaining support" \
      org.opencontainers.image.vendor="MrWhoOidc Project" \
      org.opencontainers.image.source="https://github.com/popicka70/MrWhoOidc" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.documentation="https://github.com/popicka70/MrWhoOidc/blob/main/README.md"

# Create non-root user for security
# Note: Chiseled images already run as non-root by default (app user, UID 1654)
WORKDIR /app

# Copy published application from build stage
COPY --from=build /app/publish .

# Expose HTTPS port (HTTP port 8080 is exposed by base image)
EXPOSE 8443

# Set environment for production
ENV ASPNETCORE_URLS=https://+:8443;http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true

# Run as non-root user (already default in chiseled image)
USER $APP_UID

ENTRYPOINT ["dotnet", "MrWhoOidc.WebAuth.dll"]
