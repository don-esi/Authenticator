/*
    Copy existing rows from Authenticator.db into the existing Authenticator tables.

    Source file (not modified by this script):
      bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\AppX\Authenticator.db

    Row counts in that file on 2026-10-11:
      Folders          1
      Accounts         2
      BackupCodes      0
      PasswordEntries  0
      AppSettings      1

    Target:
      Server=.\ZOGEXPRESS2025;Database=Authenticator;Trusted_Connection=True;TrustServerCertificate=True

    This script only inserts or updates rows. It does not create a database,
    create tables, alter tables, or run a migration.

    dbo.AppSettings already has the seeded Id 1 row, so that row is updated
    to the SQLite values. The other tables are empty and receive the SQLite ids.

    SecretKey values are the base32 TOTP secrets from Authenticator.db.
    Review this script before running it. It has not been executed.
*/

USE Authenticator;
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

IF NOT EXISTS (SELECT 1 FROM dbo.Folders WHERE Id = 1)
BEGIN
    SET IDENTITY_INSERT dbo.Folders ON;

    INSERT INTO dbo.Folders (Id, Name, Color, CreatedAt)
    VALUES (1, N'stuff', N'#4ECDC4', '2026-07-06 23:42:34.7235772');

    SET IDENTITY_INSERT dbo.Folders OFF;
END;

IF NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE Id = 1)
BEGIN
    SET IDENTITY_INSERT dbo.Accounts ON;

    INSERT INTO dbo.Accounts
    (
        Id,
        ServiceName,
        ServiceUrl,
        Username,
        SecretKey,
        Algorithm,
        Digits,
        Period,
        FolderId,
        IsFavorite,
        IsDeleted,
        CreatedAt,
        ModifiedAt
    )
    VALUES
    (
        1,
        N'GitHub',
        N'https://github.com',
        N'don-esi',
        N'XZQGTYYWMHPSABC4',
        N'SHA1',
        6,
        30,
        NULL,
        0,
        0,
        '2026-07-06 23:31:05.9469011',
        NULL
    );

    SET IDENTITY_INSERT dbo.Accounts OFF;
END;

IF NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE Id = 2)
BEGIN
    SET IDENTITY_INSERT dbo.Accounts ON;

    INSERT INTO dbo.Accounts
    (
        Id,
        ServiceName,
        ServiceUrl,
        Username,
        SecretKey,
        Algorithm,
        Digits,
        Period,
        FolderId,
        IsFavorite,
        IsDeleted,
        CreatedAt,
        ModifiedAt
    )
    VALUES
    (
        2,
        N'LastPass',
        NULL,
        N'donhartzog@rogers.com',
        N'VSURFXCZ536WMOYX',
        N'SHA1',
        6,
        30,
        1,
        0,
        0,
        '2026-07-06 23:53:13.4145524',
        '2026-07-08 00:45:01.6170288'
    );

    SET IDENTITY_INSERT dbo.Accounts OFF;
END;

-- BackupCodes: no rows in Authenticator.db.
-- PasswordEntries: no rows in Authenticator.db.

UPDATE dbo.AppSettings
SET
    AutoLockTimeout = 0,
    SyncTimeWithInternet = 1,
    WindowDockPosition = N'None',
    IsWindowCollapsed = 0,
    WindowX = 100,
    WindowY = 100,
    WindowWidth = 778,
    WindowHeight = 1490,
    ModifiedAt = '2026-07-06 23:25:01.1086649'
WHERE Id = 1;

IF @@ROWCOUNT = 0
BEGIN
    SET IDENTITY_INSERT dbo.AppSettings ON;

    INSERT INTO dbo.AppSettings
    (
        Id,
        AutoLockTimeout,
        SyncTimeWithInternet,
        WindowDockPosition,
        IsWindowCollapsed,
        WindowX,
        WindowY,
        WindowWidth,
        WindowHeight,
        ModifiedAt
    )
    VALUES
    (
        1,
        0,
        1,
        N'None',
        0,
        100,
        100,
        778,
        1490,
        '2026-07-06 23:25:01.1086649'
    );

    SET IDENTITY_INSERT dbo.AppSettings OFF;
END;

COMMIT TRANSACTION;
GO

SELECT Id, Name, Color, CreatedAt
FROM dbo.Folders
ORDER BY Id;

SELECT
    Id,
    ServiceName,
    ServiceUrl,
    Username,
    SecretKey,
    Algorithm,
    Digits,
    Period,
    FolderId,
    IsFavorite,
    IsDeleted,
    CreatedAt,
    ModifiedAt
FROM dbo.Accounts
ORDER BY Id;

SELECT COUNT(*) AS BackupCodeCount FROM dbo.BackupCodes;
SELECT COUNT(*) AS PasswordEntryCount FROM dbo.PasswordEntries;

SELECT
    Id,
    AutoLockTimeout,
    SyncTimeWithInternet,
    WindowDockPosition,
    IsWindowCollapsed,
    WindowX,
    WindowY,
    WindowWidth,
    WindowHeight,
    ModifiedAt
FROM dbo.AppSettings
ORDER BY Id;
GO
