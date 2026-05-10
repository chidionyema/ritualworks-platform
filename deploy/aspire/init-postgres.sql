-- Per-bounded-context databases + per-DB "owner" group roles.
-- Bind-mounted into postgres at /docker-entrypoint-initdb.d/init.sql so the
-- official postgres image runs it on first init of the data volume.
--
-- One database per service-with-state. bff-web has no DB (composes others).
-- Vault dynamic credentials issue ephemeral users that join the matching
-- <db>_owner group — no SUPERUSER, no cross-context access.

-- 1. Per-service databases.
SELECT 'CREATE DATABASE catalog'  WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'catalog')\gexec
SELECT 'CREATE DATABASE orders'   WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'orders')\gexec
SELECT 'CREATE DATABASE payments' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'payments')\gexec
SELECT 'CREATE DATABASE content'  WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'content')\gexec
SELECT 'CREATE DATABASE identity' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'identity')\gexec
SELECT 'CREATE DATABASE checkout' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'checkout')\gexec
SELECT 'CREATE DATABASE audit'    WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'audit')\gexec

-- 2. Per-DB owner group roles (NOLOGIN — they're groups Vault users join).
SELECT 'CREATE ROLE catalog_owner  NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'catalog_owner')\gexec
SELECT 'CREATE ROLE orders_owner   NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'orders_owner')\gexec
SELECT 'CREATE ROLE payments_owner NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'payments_owner')\gexec
SELECT 'CREATE ROLE content_owner  NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'content_owner')\gexec
SELECT 'CREATE ROLE identity_owner NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'identity_owner')\gexec
SELECT 'CREATE ROLE checkout_owner NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'checkout_owner')\gexec
SELECT 'CREATE ROLE audit_owner    NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'audit_owner')\gexec

-- 3. Transfer database ownership + grant ALL.
ALTER DATABASE catalog  OWNER TO catalog_owner;
ALTER DATABASE orders   OWNER TO orders_owner;
ALTER DATABASE payments OWNER TO payments_owner;
ALTER DATABASE content  OWNER TO content_owner;
ALTER DATABASE identity OWNER TO identity_owner;
ALTER DATABASE checkout OWNER TO checkout_owner;
ALTER DATABASE audit    OWNER TO audit_owner;

GRANT ALL PRIVILEGES ON DATABASE catalog  TO catalog_owner;
GRANT ALL PRIVILEGES ON DATABASE orders   TO orders_owner;
GRANT ALL PRIVILEGES ON DATABASE payments TO payments_owner;
GRANT ALL PRIVILEGES ON DATABASE content  TO content_owner;
GRANT ALL PRIVILEGES ON DATABASE identity TO identity_owner;
GRANT ALL PRIVILEGES ON DATABASE checkout TO checkout_owner;
GRANT ALL PRIVILEGES ON DATABASE audit    TO audit_owner;

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

\c audit
GRANT ALL ON SCHEMA public TO audit_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE audit_owner GRANT ALL ON TABLES    TO audit_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE audit_owner GRANT ALL ON SEQUENCES TO audit_owner;
ALTER DEFAULT PRIVILEGES FOR ROLE audit_owner GRANT ALL ON FUNCTIONS TO audit_owner;
