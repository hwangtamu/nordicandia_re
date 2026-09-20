FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY server/Nordicandia.Contracts/Nordicandia.Contracts.csproj server/Nordicandia.Contracts/
COPY server/Nordicandia.Server/Nordicandia.Server.csproj server/Nordicandia.Server/
RUN dotnet restore server/Nordicandia.Server/Nordicandia.Server.csproj

COPY server/Nordicandia.Contracts/ server/Nordicandia.Contracts/
COPY server/Nordicandia.Server/ server/Nordicandia.Server/
RUN dotnet publish server/Nordicandia.Server/Nordicandia.Server.csproj \
    --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_ENVIRONMENT=Production \
    PORT=8080
COPY --from=build /app/publish/ .
USER $APP_UID
ENTRYPOINT ["dotnet", "Nordicandia.Server.dll"]
