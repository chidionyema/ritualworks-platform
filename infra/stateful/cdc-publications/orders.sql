-- CDC Publication for Orders Service
\c orders

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = 'cdc_publication') THEN
        CREATE PUBLICATION cdc_publication FOR ALL TABLES;
    END IF;
END
$$;

ALTER TABLE "Orders" REPLICA IDENTITY FULL;
ALTER TABLE "OrderItems" REPLICA IDENTITY FULL;
