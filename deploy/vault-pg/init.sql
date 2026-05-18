-- IMPORTANT: This script only runs on FIRST BOOT (empty data directory).
-- To add new roles to an existing deployment:
--   1. Connect to vault-pg: flyctl postgres connect -a haworks-vault-pg
--   2. Run the CREATE ROLE + GRANT statements manually
--   3. Add the role to infra/vault/database/roles.json
--   4. Redeploy vault to run seed.sh: flyctl deploy -c fly.vault.toml
--
-- Init SQL for haworks-vault-pg
-- Creates the bounded-context owner roles that Vault's static database
-- roles will manage. Vault rotates only the password; the username is fixed.
-- Run once on first boot via Postgres init mechanism.
--
-- Usernames must match infra/vault/database/roles.json exactly.
-- Initial passwords are throwaway — Vault rotates them on the first
-- rotation_period tick after seed.sh creates the static roles.

DO $$
BEGIN
  -- identity_owner
  IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'identity_owner') THEN
    CREATE ROLE identity_owner LOGIN PASSWORD 'init-rotate-me';
  END IF;

  -- catalog_owner
  IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'catalog_owner') THEN
    CREATE ROLE catalog_owner LOGIN PASSWORD 'init-rotate-me';
  END IF;

  -- orders_owner
  IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'orders_owner') THEN
    CREATE ROLE orders_owner LOGIN PASSWORD 'init-rotate-me';
  END IF;

  -- payments_owner
  IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'payments_owner') THEN
    CREATE ROLE payments_owner LOGIN PASSWORD 'init-rotate-me';
  END IF;

  -- checkout_owner
  IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'checkout_owner') THEN
    CREATE ROLE checkout_owner LOGIN PASSWORD 'init-rotate-me';
  END IF;

  -- notifications_owner
  IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'notifications_owner') THEN
    CREATE ROLE notifications_owner LOGIN PASSWORD 'init-rotate-me';
  END IF;
END
$$;

-- Grant CONNECT on the default database and USAGE on public schema.
-- These are idempotent (granting an already-held privilege is a no-op).
GRANT CONNECT ON DATABASE postgres TO identity_owner, catalog_owner, orders_owner, payments_owner, checkout_owner, notifications_owner;
GRANT USAGE ON SCHEMA public TO identity_owner, catalog_owner, orders_owner, payments_owner, checkout_owner, notifications_owner;
GRANT CREATE ON SCHEMA public TO identity_owner, catalog_owner, orders_owner, payments_owner, checkout_owner, notifications_owner;

-- Allow each owner full DML on all current and future tables in public.
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO identity_owner, catalog_owner, orders_owner, payments_owner, checkout_owner, notifications_owner;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO identity_owner, catalog_owner, orders_owner, payments_owner, checkout_owner, notifications_owner;

-- Retroactive grants for any tables created before this script ran (e.g., redeployment)
GRANT ALL ON ALL TABLES IN SCHEMA public TO identity_owner, catalog_owner, orders_owner, payments_owner, checkout_owner, notifications_owner;
GRANT ALL ON ALL SEQUENCES IN SCHEMA public TO identity_owner, catalog_owner, orders_owner, payments_owner, checkout_owner, notifications_owner;
