FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY api/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "HoleLauncherBackend.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS access
WORKDIR /app
COPY access/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "HoleLauncherBackend.AccessService.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS orchestrator
WORKDIR /app
COPY orchestrator/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "HoleLauncherBackend.Orchestrator.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS storage
WORKDIR /app
COPY storage/ ./
RUN install -d -o "$APP_UID" /data/storage
USER $APP_UID
ENTRYPOINT ["dotnet", "HoleLauncherBackend.Storage.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS repository
WORKDIR /app
COPY repository/ ./
RUN install -d -o "$APP_UID" /data/repository
USER $APP_UID
ENTRYPOINT ["dotnet", "HoleLauncherBackend.Repository.dll"]
