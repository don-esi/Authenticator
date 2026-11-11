/*
    Optional admin/dev Authenticator schema for .\SQLEXPRESS (or your local instance).

    Normal Setup installs do not need this script — the app creates the database on first run.
    Matches Data/Models and AuthenticatorDbContext.OnModelCreating.
    Id columns are INT IDENTITY.
    SecretKey is NVARCHAR(MAX); at runtime secrets are encrypted at rest when unlock is configured.

    No Accounts, Folders, BackupCodes, or PasswordEntries rows are seeded.
    One default AppSettings row is inserted to match HasData in OnModelCreating.

    Run in SSMS or sqlcmd against .\SQLEXPRESS (or the Server in connection.json).
*/

IF DB_ID(N'Authenticator') IS NULL
BEGIN
    CREATE DATABASE Authenticator;
END
GO

USE Authenticator;
GO

IF OBJECT_ID(N'dbo.Folders', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Folders
    (
        Id        INT            IDENTITY(1, 1) NOT NULL,
        Name      NVARCHAR(100)  NOT NULL,
        Color     NVARCHAR(7)    NOT NULL,
        CreatedAt DATETIME2(7)   NOT NULL,
        CONSTRAINT PK_Folders PRIMARY KEY CLUSTERED (Id)
    );

    CREATE INDEX IX_Folders_Name ON dbo.Folders (Name);
END
GO

IF OBJECT_ID(N'dbo.Accounts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Accounts
    (
        Id           INT            IDENTITY(1, 1) NOT NULL,
        ServiceName  NVARCHAR(200)  NOT NULL,
        ServiceUrl   NVARCHAR(500)  NULL,
        Username     NVARCHAR(200)  NOT NULL,
        SecretKey    NVARCHAR(MAX)  NOT NULL, -- base32 TOTP secret, not encrypted
        Algorithm    NVARCHAR(10)   NOT NULL,
        Digits       INT            NOT NULL,
        Period       INT            NOT NULL,
        FolderId     INT            NULL,
        IsFavorite   BIT            NOT NULL,
        IsDeleted    BIT            NOT NULL,
        CreatedAt    DATETIME2(7)   NOT NULL,
        ModifiedAt   DATETIME2(7)   NULL,
        CONSTRAINT PK_Accounts PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_Accounts_Folders_FolderId
            FOREIGN KEY (FolderId) REFERENCES dbo.Folders (Id) ON DELETE SET NULL
    );

    CREATE INDEX IX_Accounts_ServiceName ON dbo.Accounts (ServiceName);
    CREATE INDEX IX_Accounts_IsDeleted ON dbo.Accounts (IsDeleted);
    CREATE INDEX IX_Accounts_FolderId ON dbo.Accounts (FolderId);
END
GO

IF OBJECT_ID(N'dbo.BackupCodes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BackupCodes
    (
        Id         INT           IDENTITY(1, 1) NOT NULL,
        AccountId  INT           NOT NULL,
        Code       NVARCHAR(50)  NOT NULL,
        IsUsed     BIT           NOT NULL,
        IsDeleted  BIT           NOT NULL,
        CreatedAt  DATETIME2(7)  NOT NULL,
        UsedAt     DATETIME2(7)  NULL,
        CONSTRAINT PK_BackupCodes PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_BackupCodes_Accounts_AccountId
            FOREIGN KEY (AccountId) REFERENCES dbo.Accounts (Id) ON DELETE CASCADE
    );

    CREATE INDEX IX_BackupCodes_AccountId ON dbo.BackupCodes (AccountId);
    CREATE INDEX IX_BackupCodes_AccountId_IsUsed_IsDeleted
        ON dbo.BackupCodes (AccountId, IsUsed, IsDeleted);
END
GO

IF OBJECT_ID(N'dbo.PasswordEntries', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PasswordEntries
    (
        Id                 INT            IDENTITY(1, 1) NOT NULL,
        Source             NVARCHAR(50)   NOT NULL,
        ServiceName        NVARCHAR(200)  NOT NULL,
        ServiceUrl         NVARCHAR(500)  NULL,
        Username           NVARCHAR(200)  NOT NULL,
        EncryptedPassword  NVARCHAR(MAX)  NOT NULL,
        LinkedAccountId    INT            NULL, -- model has no foreign key
        IsDeleted          BIT            NOT NULL,
        CreatedAt          DATETIME2(7)   NOT NULL,
        ModifiedAt         DATETIME2(7)   NULL,
        CONSTRAINT PK_PasswordEntries PRIMARY KEY CLUSTERED (Id)
    );

    CREATE INDEX IX_PasswordEntries_Source ON dbo.PasswordEntries (Source);
    CREATE INDEX IX_PasswordEntries_ServiceName ON dbo.PasswordEntries (ServiceName);
    CREATE INDEX IX_PasswordEntries_IsDeleted ON dbo.PasswordEntries (IsDeleted);
    CREATE INDEX IX_PasswordEntries_LinkedAccountId ON dbo.PasswordEntries (LinkedAccountId);
END
GO

IF OBJECT_ID(N'dbo.AppSettings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AppSettings
    (
        Id                    INT           IDENTITY(1, 1) NOT NULL,
        AutoLockTimeout       INT           NOT NULL,
        SyncTimeWithInternet  BIT           NOT NULL,
        WindowDockPosition    NVARCHAR(10)  NOT NULL,
        IsWindowCollapsed     BIT           NOT NULL,
        WindowX               INT           NOT NULL,
        WindowY               INT           NOT NULL,
        WindowWidth           INT           NOT NULL,
        WindowHeight          INT           NOT NULL,
        ModifiedAt            DATETIME2(7)  NOT NULL,
        CONSTRAINT PK_AppSettings PRIMARY KEY CLUSTERED (Id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings)
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
        0,
        N'None',
        0,
        100,
        100,
        778,
        1490,
        SYSUTCDATETIME()
    );

    SET IDENTITY_INSERT dbo.AppSettings OFF;
END
GO
