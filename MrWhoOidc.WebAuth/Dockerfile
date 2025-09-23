# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files first for better caching
COPY MrWhoOidc.slnx ./
COPY MrWhoOidc.WebAuth/MrWhoOidc.csproj MrWhoOidc.WebAuth/MrWhoOidc.csproj
COPY MrWhoOidc.Auth/MrWhoOidc.Auth.csproj MrWhoOidc.Auth/MrWhoOidc.Auth.csproj
COPY MrWhoOidc.ServiceDefaults/MrWhoOidc.ServiceDefaults.csproj MrWhoOidc.ServiceDefaults/MrWhoOidc.ServiceDefaults.csproj

# Restore dependencies for the app and migration projects that were staged
RUN dotnet restore "MrWhoOidc/MrWhoOidc.csproj"

# Copy the rest of the source and publish
COPY . .
RUN dotnet publish "MrWhoOidc.WebAuth/MrWhoOidc.WebAuth.csproj" -c Release -o /app/publish -p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["/bin/sh","-c","ASPNETCORE_URLS=http://+:${PORT:-8080} dotnet MrWhoOidc.WebAuth.dll"]
