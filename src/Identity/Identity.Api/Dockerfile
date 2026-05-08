# syntax=docker/dockerfile:1.7
# Build context: repo root (ritualworks-platform/).

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/ ./src/

RUN dotnet restore src/Identity/Identity.Api/Identity.Api.csproj

RUN dotnet publish src/Identity/Identity.Api/Identity.Api.csproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# curl + jq for the Vault bootstrap shim. Identity is the only service that
# needs to fetch AppRole creds at startup; other services don't have this.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl jq \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./
COPY deploy/identity/vault-bootstrap.sh /usr/local/bin/vault-bootstrap.sh
RUN chmod +x /usr/local/bin/vault-bootstrap.sh

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_gcServer=1
EXPOSE 8080

ENTRYPOINT ["/usr/local/bin/vault-bootstrap.sh"]
