FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

# Create data directory and give permissions before switching user
RUN mkdir -p /data && chown -R $APP_UID /data

USER $APP_UID

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["NhBackup.WebApplication/NhBackup.WebApplication.csproj", "NhBackup.WebApplication/"]
RUN dotnet restore "./NhBackup.WebApplication/NhBackup.WebApplication.csproj"
COPY . .
WORKDIR "/src/NhBackup.WebApplication"
RUN dotnet build "./NhBackup.WebApplication.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./NhBackup.WebApplication.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "NhBackup.WebApplication.dll"]