-- CDC Publication for Payments Service
\c payments

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = 'cdc_publication') THEN
        CREATE PUBLICATION cdc_publication FOR ALL TABLES;
    END IF;
END
$$;

ALTER TABLE "Payments" REPLICA IDENTITY FULL;
