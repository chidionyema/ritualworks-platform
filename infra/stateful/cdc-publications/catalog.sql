-- CDC Publication for Catalog Service
-- Ensures all tables are included in the WAL stream for the 'cdc_publication' publication.

\c catalog

-- 1. Create publication if it doesn't exist
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = 'cdc_publication') THEN
        CREATE PUBLICATION cdc_publication FOR ALL TABLES;
    END IF;
END
$$;

-- 2. Ensure replica identity is FULL for important tables to get old row data
-- This is required for 'updated' and 'deleted' events to include PayloadBefore.
ALTER TABLE "Products" REPLICA IDENTITY FULL;
ALTER TABLE "Categories" REPLICA IDENTITY FULL;
