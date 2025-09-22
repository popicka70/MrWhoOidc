# Na https://aka.ms/customizecontainer se dozvíte, jak si přizpůsobit kontejner ladění a jak Visual Studio používá tento dokument Dockerfile k sestavení vašich imagí pro rychlejší ladění.

# Tato fáze se používá při spuštění z VS v rychlém režimu (výchozí pro konfiguraci ladění).
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081


# Tato fáze slouží k sestavení projektu služby.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["MrWhoOidc.WebAuth/MrWhoOidc.WebAuth.csproj", "MrWhoOidc.WebAuth/"]
COPY ["MrWhoOidc.Auth/MrWhoOidc.Auth.csproj", "MrWhoOidc.Auth/"]
COPY ["MrWhoOidc.ServiceDefaults/MrWhoOidc.ServiceDefaults.csproj", "MrWhoOidc.ServiceDefaults/"]
RUN dotnet restore "./MrWhoOidc.WebAuth/MrWhoOidc.WebAuth.csproj"
COPY . .
WORKDIR "/src/MrWhoOidc.WebAuth"
RUN dotnet build "./MrWhoOidc.WebAuth.csproj" -c $BUILD_CONFIGURATION -o /app/build

# Tato fáze slouží k publikování projektu služby, který se má zkopírovat do konečné fáze.
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./MrWhoOidc.WebAuth.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Tato fáze se používá v produkčním prostředí nebo při spuštění z VS v běžném režimu (výchozí, když se nepoužívá konfigurace ladění).
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "MrWhoOidc.WebAuth.dll"]