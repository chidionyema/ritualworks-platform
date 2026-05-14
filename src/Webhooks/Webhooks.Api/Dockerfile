FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/Webhooks/Webhooks.Api/Webhooks.Api.csproj", "src/Webhooks/Webhooks.Api/"]
COPY ["src/Webhooks/Webhooks.Infrastructure/Webhooks.Infrastructure.csproj", "src/Webhooks/Webhooks.Infrastructure/"]
COPY ["src/Webhooks/Webhooks.Application/Webhooks.Application.csproj", "src/Webhooks/Webhooks.Application/"]
COPY ["src/Webhooks/Webhooks.Domain/Webhooks.Domain.csproj", "src/Webhooks/Webhooks.Domain/"]
COPY ["src/BuildingBlocks/Haworks.BuildingBlocks.csproj", "src/BuildingBlocks/"]
COPY ["src/Contracts/Haworks.Contracts.csproj", "src/Contracts/"]
RUN dotnet restore "src/Webhooks/Webhooks.Api/Webhooks.Api.csproj"
COPY . .
WORKDIR "/src/src/Webhooks/Webhooks.Api"
RUN dotnet build "Webhooks.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Webhooks.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Webhooks.Api.dll"]
