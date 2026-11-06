-- Run once after the first deployment, connected to the InvoiceFlow database as a member of the SQL admin group
-- (e.g. Azure Data Studio / sqlcmd with Entra ID authentication).
-- Replace id-invflow with the managed identity name (deployment output: identityName).

CREATE USER [id-invflow] FROM EXTERNAL PROVIDER;

-- Read/write data + apply EF Core migrations on start (Database__MigrateOnStartup=true).
-- Once migrations run from the release pipeline instead, drop db_ddladmin.
ALTER ROLE db_datareader ADD MEMBER [id-invflow];
ALTER ROLE db_datawriter ADD MEMBER [id-invflow];
ALTER ROLE db_ddladmin  ADD MEMBER [id-invflow];
