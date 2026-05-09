# RitualWorks Platform — Docker Compose Environment

This directory provides a Docker Compose environment that mirrors the official Aspire local development setup. It spins up the complete microservices topology along with all necessary infrastructure (Postgres, RabbitMQ, Redis, Vault, LocalStack S3, Tempo, Pact Broker).

## Prerequisites

- Docker and Docker Compose
- Make (optional, but convenient)

## Usage

You can use the provided `Makefile` to manage the environment:

```bash
# Start all services in the background
make up

# View the status of the containers
make status

# View logs from all services (tail)
make logs

# Stop all services
make down

# Stop and remove all services, including volumes and vault credentials
make clean
```

If you prefer to use docker-compose directly:
```bash
docker-compose up -d
docker-compose ps
docker-compose down
```

## Architecture Notes

- **Initialization:** The `vault-init` and `vault-seed` containers are one-shot jobs that configure Vault with the required AppRoles and inject development secrets. They must complete successfully before the microservices start.
- **Replication:** The `catalog-svc` is replicated (`catalog-svc-1` and `catalog-svc-2`) to mirror the Aspire setup, and the BFF load-balances across both instances.
- **Developer Tools:**
  - **pgAdmin:** `http://localhost:5055` (admin@ritualworks.com / admin)
  - **Redis Commander:** `http://localhost:8081`
  - **LocalStack S3:** `http://localhost:4566` (access/secret: `test` / `test`; bucket `content-dev` auto-created on boot)
  - **Pact Broker:** `http://localhost:9292`
  - **Vault:** `http://localhost:8200`
  - **BFF Web:** `http://localhost:5050`
