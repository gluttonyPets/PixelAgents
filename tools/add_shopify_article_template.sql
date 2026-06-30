-- Añade la columna ArticleTemplate a ShopifyConnections.
--
-- Necesario porque el esquema se crea con EnsureCreated() (no hay migraciones EF),
-- por lo que las bases de datos de tenant YA existentes no reciben la columna nueva
-- automaticamente. Ejecutar este script una vez en cada BD de tenant.
--
-- Es idempotente: si la columna ya existe, no hace nada.

ALTER TABLE "ShopifyConnections"
    ADD COLUMN IF NOT EXISTS "ArticleTemplate" text NULL;
