-- Per-bounded-context databases + per-DB "owner" group roles.
-- Bind-mounted into postgres at /docker-entrypoint-initdb.d/init.sql so the
-- official postgres image runs it on first init of the data volume.
--
-- One database per service-with-state. bff-web has no DB (composes others).
-- Vault dynamic credentials issue ephemeral users that join the matching
-- <db>_owner group — no SUPERUSER, no cross-context access.

-- 1. Per-service databases.
SELECT 'CREATE DATABASE catalog'       WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'catalog')\gexec
SELECT 'CREATE DATABASE orders'        WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'orders')\gexec
SELECT 'CREATE DATABASE payments'      WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'payments')\gexec
SELECT 'CREATE DATABASE content'       WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'content')\gexec
SELECT 'CREATE DATABASE identity'      WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'identity')\gexec
SELECT 'CREATE DATABASE checkout'      WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'checkout')\gexec
SELECT 'CREATE DATABASE notifications' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'notifications')\gexec
SELECT 'CREATE DATABASE audit'         WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'audit')\gexec
SELECT 'CREATE DATABASE location'      WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'location')\gexec
SELECT 'CREATE DATABASE webhooks'      WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'webhooks')\gexec
SELECT 'CREATE DATABASE payouts'       WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'payouts')\gexec
SELECT 'CREATE DATABASE scheduler'     WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'scheduler')\gexec
SELECT 'CREATE DATABASE privacy'       WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'privacy')\gexec
SELECT 'CREATE DATABASE merchant'      WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'merchant')\gexec

-- 2. Per-DB owner group roles (NOLOGIN — they're groups Vault users join).

SELECT 'CREATE ROLE catalog_owner  NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'catalog_owner')\gexec
SELECT 'CREATE ROLE orders_owner   NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'orders_owner')\gexec
SELECT 'CREATE ROLE payments_owner NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'payments_owner')\gexec
SELECT 'CREATE ROLE content_owner  NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'content_owner')\gexec
SELECT 'CREATE ROLE identity_owner NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'identity_owner')\gexec
SELECT 'CREATE ROLE checkout_owner NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'checkout_owner')\gexec
SELECT 'CREATE ROLE notifications_owner NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'notifications_owner')\gexec
SELECT 'CREATE ROLE audit_owner    NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'audit_owner')\gexec
SELECT 'CREATE ROLE location_owner NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'location_owner')\gexec
SELECT 'CREATE ROLE webhooks_owner NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'webhooks_owner')\gexec
SELECT 'CREATE ROLE payouts_owner  NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'payouts_owner')\gexec
SELECT 'CREATE ROLE scheduler_owner NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'scheduler_owner')\gexec
SELECT 'CREATE ROLE privacy_owner   NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'privacy_owner')\gexec
SELECT 'CREATE ROLE merchant_owner  NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'merchant_owner')\gexec

-- 3. Transfer database ownership + grant ALL.
ALTER DATABASE catalog       OWNER TO catalog_owner;
ALTER DATABASE orders        OWNER TO orders_owner;
ALTER DATABASE payments      OWNER TO payments_owner;
ALTER DATABASE content       OWNER TO content_owner;
ALTER DATABASE identity      OWNER TO identity_owner;
ALTER DATABASE checkout      OWNER TO checkout_owner;
ALTER DATABASE notifications OWNER TO notifications_owner;
ALTER DATABASE audit         OWNER TO audit_owner;
ALTER DATABASE location      OWNER TO location_owner;
ALTER DATABASE webhooks      OWNER TO webhooks_owner;
ALTER DATABASE payouts       OWNER TO payouts_owner;
ALTER DATABASE scheduler     OWNER TO scheduler_owner;
ALTER DATABASE privacy       OWNER TO privacy_owner;
ALTER DATABASE merchant      OWNER TO merchant_owner;

GRANT ALL PRIVILEGES ON DATABASE catalog       TO catalog_owner;
GRANT ALL PRIVILEGES ON DATABASE orders        TO orders_owner;
GRANT ALL PRIVILEGES ON DATABASE payments      TO payments_owner;
GRANT ALL PRIVILEGES ON DATABASE content       TO content_owner;
GRANT ALL PRIVILEGES ON DATABASE identity      TO identity_owner;
GRANT ALL PRIVILEGES ON DATABASE checkout      TO checkout_owner;
GRANT ALL PRIVILEGES ON DATABASE notifications TO notifications_owner;
GRANT ALL PRIVILEGES ON DATABASE audit         TO audit_owner;
GRANT ALL PRIVILEGES ON DATABASE location      TO location_owner;
GRANT ALL PRIVILEGES ON DATABASE webhooks      TO webhooks_owner;
GRANT ALL PRIVILEGES ON DATABASE payouts       TO payouts_owner;
GRANT ALL PRIVILEGES ON DATABASE scheduler     TO scheduler_owner;
GRANT ALL PRIVILEGES ON DATABASE privacy       TO privacy_owner;
GRANT ALL PRIVILEGES ON DATABASE merchant      TO merchant_owner;

-- 4. Per-DB schema grants + default privileges so EF migrations work cleanly.
\c catalog
GRANT ALL ON SCHEMA public TO catalog_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE catalog_owner GRANT ALL ON TABLES    TO catalog_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE catalog_owner GRANT ALL ON SEQUENCES TO catalog_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE catalog_owner GRANT ALL ON FUNCTIONS TO catalog_owner;

\c orders
GRANT ALL ON SCHEMA public TO orders_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE orders_owner GRANT ALL ON TABLES    TO orders_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE orders_owner GRANT ALL ON SEQUENCES TO orders_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE orders_owner GRANT ALL ON FUNCTIONS TO orders_owner;

\c payments
GRANT ALL ON SCHEMA public TO payments_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE payments_owner GRANT ALL ON TABLES    TO payments_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE payments_owner GRANT ALL ON SEQUENCES TO payments_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE payments_owner GRANT ALL ON FUNCTIONS TO payments_owner;

\c content
GRANT ALL ON SCHEMA public TO content_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE content_owner GRANT ALL ON TABLES    TO content_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE content_owner GRANT ALL ON SEQUENCES TO content_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE content_owner GRANT ALL ON FUNCTIONS TO content_owner;

\c identity
GRANT ALL ON SCHEMA public TO identity_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE identity_owner GRANT ALL ON TABLES    TO identity_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE identity_owner GRANT ALL ON SEQUENCES TO identity_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE identity_owner GRANT ALL ON FUNCTIONS TO identity_owner;

\c checkout
GRANT ALL ON SCHEMA public TO checkout_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE checkout_owner GRANT ALL ON TABLES    TO checkout_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE checkout_owner GRANT ALL ON SEQUENCES TO checkout_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE checkout_owner GRANT ALL ON FUNCTIONS TO checkout_owner;

\c notifications
GRANT ALL ON SCHEMA public TO notifications_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE notifications_owner GRANT ALL ON TABLES    TO notifications_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE notifications_owner GRANT ALL ON SEQUENCES TO notifications_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE notifications_owner GRANT ALL ON FUNCTIONS TO notifications_owner;

\c audit
GRANT ALL ON SCHEMA public TO audit_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE audit_owner GRANT ALL ON TABLES    TO audit_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE audit_owner GRANT ALL ON SEQUENCES TO audit_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE audit_owner GRANT ALL ON FUNCTIONS TO audit_owner;

\c location
GRANT ALL ON SCHEMA public TO location_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE location_owner GRANT ALL ON TABLES    TO location_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE location_owner GRANT ALL ON SEQUENCES TO location_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE location_owner GRANT ALL ON FUNCTIONS TO location_owner;

\c webhooks
GRANT ALL ON SCHEMA public TO webhooks_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE webhooks_owner GRANT ALL ON TABLES    TO webhooks_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE webhooks_owner GRANT ALL ON SEQUENCES TO webhooks_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE webhooks_owner GRANT ALL ON FUNCTIONS TO webhooks_owner;

\c payouts
GRANT ALL ON SCHEMA public TO payouts_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE payouts_owner GRANT ALL ON TABLES    TO payouts_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE payouts_owner GRANT ALL ON SEQUENCES TO payouts_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE payouts_owner GRANT ALL ON FUNCTIONS TO payouts_owner;

\c scheduler
GRANT ALL ON SCHEMA public TO scheduler_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE scheduler_owner GRANT ALL ON TABLES    TO scheduler_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE scheduler_owner GRANT ALL ON SEQUENCES TO scheduler_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE scheduler_owner GRANT ALL ON FUNCTIONS TO scheduler_owner;

\c privacy
GRANT ALL ON SCHEMA public TO privacy_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE privacy_owner GRANT ALL ON TABLES    TO privacy_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE privacy_owner GRANT ALL ON SEQUENCES TO privacy_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE privacy_owner GRANT ALL ON FUNCTIONS TO privacy_owner;

\c merchant
GRANT ALL ON SCHEMA public TO merchant_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE merchant_owner GRANT ALL ON TABLES    TO merchant_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE merchant_owner GRANT ALL ON SEQUENCES TO merchant_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE merchant_owner GRANT ALL ON FUNCTIONS TO merchant_owner;
