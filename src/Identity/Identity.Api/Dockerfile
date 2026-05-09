# syntax=docker/dockerfile:1.7
# Build context: repo root (ritualworks-platform/).

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/ ./src/

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked dotnet restore src/Identity/Identity.Api/Identity.Api.csproj

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked dotnet publish src/Identity/Identity.Api/Identity.Api.csproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Tiny shim that writes Vault__RoleId/SecretId to disk so legacy
# VaultClientFactory + IVaultService consumers find them at the
# RoleIdPath/SecretIdPath VaultOptions specify, then exec dotnet.
# No more curl/jq round-trip — vault is persistent now and creds are
# staged directly as Fly secrets at bootstrap time.
COPY --from=build /app/publish ./
COPY deploy/identity/vault-bootstrap.sh /usr/local/bin/vault-bootstrap.sh
RUN chmod +x /usr/local/bin/vault-bootstrap.sh

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_gcServer=1
EXPOSE 8080

ENTRYPOINT ["/usr/local/bin/vault-bootstrap.sh"]
