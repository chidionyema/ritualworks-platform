# syntax=docker/dockerfile:1.7
# Build context: repo root (ritualworks-platform/).

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/ ./src/

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked dotnet restore src/RulesEngine/RulesEngine.Api/RulesEngine.Api.csproj

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked dotnet publish src/RulesEngine/RulesEngine.Api/RulesEngine.Api.csproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_gcServer=1
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "RulesEngine.Api.dll"]
