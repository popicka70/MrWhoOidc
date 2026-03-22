# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl openssl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY tools/run-dotnet-with-dev-cert.sh /usr/local/bin/run-dotnet-with-dev-cert.sh
RUN chmod +x /usr/local/bin/run-dotnet-with-dev-cert.sh

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY MrWhoOidc.slnx ./
COPY Directory.Packages.props ./
COPY Examples/MrWhoOidc.TestApi/MrWhoOidc.TestApi.csproj Examples/MrWhoOidc.TestApi/MrWhoOidc.TestApi.csproj

RUN dotnet restore "Examples/MrWhoOidc.TestApi/MrWhoOidc.TestApi.csproj"

COPY . .
RUN dotnet publish "Examples/MrWhoOidc.TestApi/MrWhoOidc.TestApi.csproj" -c Release -o /app/publish -p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["/usr/local/bin/run-dotnet-with-dev-cert.sh", "MrWhoOidc.TestApi.dll"]