# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8443
ENV ASPNETCORE_HTTP_PORTS=8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for better caching
COPY MrWhoOidc.slnx ./
COPY Directory.Packages.props ./
COPY MrWhoOidc.WebAuth/MrWhoOidc.WebAuth.csproj MrWhoOidc.WebAuth/MrWhoOidc.WebAuth.csproj
COPY MrWhoOidc.Auth/MrWhoOidc.Auth.csproj MrWhoOidc.Auth/MrWhoOidc.Auth.csproj
COPY MrWhoOidc.ServiceDefaults/MrWhoOidc.ServiceDefaults.csproj MrWhoOidc.ServiceDefaults/MrWhoOidc.ServiceDefaults.csproj
COPY MrWhoOidc.Security/MrWhoOidc.Security.csproj MrWhoOidc.Security/MrWhoOidc.Security.csproj

# Restore dependencies for the app and migration projects that were staged
RUN dotnet restore "MrWhoOidc.WebAuth/MrWhoOidc.WebAuth.csproj"

# Copy the rest of the source and publish
COPY . .
RUN dotnet publish "MrWhoOidc.WebAuth/MrWhoOidc.WebAuth.csproj" -c Release -o /app/publish -p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet","MrWhoOidc.WebAuth.dll"]
