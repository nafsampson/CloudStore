-- //summary//
-- Grants the hosted web identity restricted access to the account tables.
-- //summary//
-- //-------------------Start Of File------------------//
-- Run as the configured Microsoft Entra administrator in the accounts database.
-- Replace the placeholder with the webIdentityClientId deployment output.
-- For WITH SID and TYPE = E, managed identities require their client ID.
SET XACT_ABORT ON;
DECLARE @ClientId uniqueidentifier = 'REPLACE_WITH_WEB_IDENTITY_CLIENT_ID';
DECLARE @Name sysname = N'cloudstore_web_identity';
DECLARE @Sid varbinary(16) = CONVERT(varbinary(16), @ClientId);
BEGIN TRANSACTION;
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @Name AND sid <> @Sid)
    THROW 51000, 'Existing database principal has another identity; investigate before changing access.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @Name)
BEGIN
    DECLARE @Create nvarchar(max) = N'CREATE USER ' + QUOTENAME(@Name) + N' WITH SID = '
        + CONVERT(nvarchar(34), @Sid, 1) + N', TYPE = E;';
    EXEC sp_executesql @Create;
END;
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.LoginUsers TO [cloudstore_web_identity];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.AspNetUserClaims TO [cloudstore_web_identity];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.AspNetUserLogins TO [cloudstore_web_identity];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.AspNetUserTokens TO [cloudstore_web_identity];
GRANT SELECT ON OBJECT::dbo.__EFMigrationsHistory TO [cloudstore_web_identity];
COMMIT;
SELECT name, type_desc FROM sys.database_principals WHERE name = @Name;
-- //-------------------End Of File------------------//
