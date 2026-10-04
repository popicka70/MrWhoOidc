# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl openssl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY tools/run-dotnet-with-dev-cert.sh /usr/local/bin/run-dotnet-with-dev-cert.sh
RUN chmod +x /usr/local/bin/run-dotnet-with-dev-cert.sh
# DataProtection keys (DataProtection__KeysDirectory in docker-compose.dev.yml) must be writable by
# the non-root user; a new named volume mounted here inherits this ownership.
RUN mkdir -p /app/data-protection-keys \
    && chown $APP_UID:$APP_UID /app/data-protection-keys

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY MrWhoOidc.slnx ./
COPY Directory.Packages.props ./
COPY Examples/MrWhoOidc.OidcDemo/MrWhoOidc.OidcDemo.csproj Examples/MrWhoOidc.OidcDemo/MrWhoOidc.OidcDemo.csproj

RUN dotnet restore "Examples/MrWhoOidc.OidcDemo/MrWhoOidc.OidcDemo.csproj"

COPY . .
RUN dotnet publish "Examples/MrWhoOidc.OidcDemo/MrWhoOidc.OidcDemo.csproj" -c Release -o /app/publish -p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
# Non-root (the aspnet image's `app` user); see tools/run-dotnet-with-dev-cert.sh.
USER $APP_UID
ENTRYPOINT ["/usr/local/bin/run-dotnet-with-dev-cert.sh", "MrWhoOidc.OidcDemo.dll"]