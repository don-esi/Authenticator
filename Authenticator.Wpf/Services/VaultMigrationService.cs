using Authenticator.Data;
using Authenticator.Logging;
using Microsoft.EntityFrameworkCore;

namespace Authenticator.Services;

/// <summary>
/// Migrates/promotes local vault crypto and seeds offline backup after shared is online.
/// </summary>
public sealed class VaultMigrationService
{
    private readonly VaultCryptoService _crypto = new();
    private readonly OfflineVaultService _offline = new();

    public async Task EnsureSchemaOnlyAsync(CancellationToken ct = default)
    {
        await using var db = new AuthenticatorDbContext();
        await EnsureVaultTablesAsync(db, ct);
    }

    public async Task<(string? RecoveryKey, int Accounts)> PromoteAndSeedAsync(CancellationToken ct = default)
    {
        var online = await SharedConnectionMonitor.Instance.RefreshAsync(ct);
        if (!online && ConnectionConfig.Role is ConnectionRole.Host or ConnectionRole.Join)
        {
            LoggingConfiguration.LogInfo("Shared SQL offline; skip promote (will use offline store).", nameof(VaultMigrationService));
            await _offline.EnsureCreatedAsync(ct);
            return (null, 0);
        }

        await using var db = new AuthenticatorDbContext();
        if (!SchemaCheckpoint.IsCurrent())
        {
            await db.Database.EnsureCreatedAsync(ct);
            await EnsureVaultTablesAsync(db, ct);
            SchemaCheckpoint.MarkCurrent();
        }

        string? recovery = null;
        if (VaultKeyServiceUnlocked())
            recovery = await _crypto.EnsureSharedVaultAsync(ct);

        await _offline.SeedFromSharedAsync(ct);

        var accountCount = await db.Accounts.CountAsync(a => !a.IsDeleted, ct);
        LoggingConfiguration.LogInfo($"Vault migration complete. Accounts={accountCount}.", nameof(VaultMigrationService));
        return (recovery, accountCount);
    }

    private static bool VaultKeyServiceUnlocked() =>
        Authenticator.Crypto.VaultKeyService.Instance.IsUnlocked;

    private static async Task EnsureVaultTablesAsync(AuthenticatorDbContext db, CancellationToken ct = default)
    {
        // EnsureCreated does not alter existing DBs — add tables if missing.
        await db.Database.ExecuteSqlRawAsync(
            """
            IF OBJECT_ID(N'dbo.VaultCrypto', N'U') IS NULL
            BEGIN
              CREATE TABLE dbo.VaultCrypto (
                Id int NOT NULL PRIMARY KEY,
                CryptoFormatVersion int NOT NULL,
                VdekFingerprint nvarchar(128) NOT NULL,
                RecoveryWrap nvarchar(max) NOT NULL,
                RecoverySalt nvarchar(max) NOT NULL,
                CreatedAt datetime2 NOT NULL,
                ModifiedAt datetime2 NULL
              );
            END
            """, ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            IF OBJECT_ID(N'dbo.VaultFolders', N'U') IS NULL
            BEGIN
              CREATE TABLE dbo.VaultFolders (
                Id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                Name nvarchar(200) NOT NULL,
                Color nvarchar(20) NULL,
                CreatedAt datetime2 NOT NULL
              );
            END
            """, ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            IF OBJECT_ID(N'dbo.VaultItems', N'U') IS NULL
            BEGIN
              CREATE TABLE dbo.VaultItems (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                ItemType int NOT NULL,
                Title nvarchar(300) NOT NULL,
                FolderId int NULL,
                Notes nvarchar(max) NULL,
                IsFavorite bit NOT NULL,
                IsDeleted bit NOT NULL,
                Username nvarchar(300) NULL,
                Password nvarchar(max) NULL,
                Url nvarchar(max) NULL,
                EquivalentDomains nvarchar(max) NULL,
                LinkedAccountId int NULL,
                TotpSecret nvarchar(max) NULL,
                PayloadJson nvarchar(max) NULL,
                CreatedAt datetime2 NOT NULL,
                ModifiedAt datetime2 NULL,
                LastUsedAt datetime2 NULL,
                AllowAutoFill bit NOT NULL CONSTRAINT DF_VaultItems_AllowAutoFill DEFAULT 0,
                AllowAutoLogin bit NOT NULL CONSTRAINT DF_VaultItems_AllowAutoLogin DEFAULT 0
              );
            END
            """, ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            IF OBJECT_ID(N'dbo.VaultItems', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.VaultItems', N'LastUsedAt') IS NULL
            BEGIN
              ALTER TABLE dbo.VaultItems ADD LastUsedAt datetime2 NULL;
            END
            """, ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            IF OBJECT_ID(N'dbo.VaultItems', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.VaultItems', N'AllowAutoFill') IS NULL
            BEGIN
              ALTER TABLE dbo.VaultItems ADD AllowAutoFill bit NOT NULL
                CONSTRAINT DF_VaultItems_AllowAutoFill DEFAULT 0;
            END
            """, ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            IF OBJECT_ID(N'dbo.VaultItems', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.VaultItems', N'AllowAutoLogin') IS NULL
            BEGIN
              ALTER TABLE dbo.VaultItems ADD AllowAutoLogin bit NOT NULL
                CONSTRAINT DF_VaultItems_AllowAutoLogin DEFAULT 0;
            END
            """, ct);

        // LastPass (and similar) store OAuth/hash URLs longer than the original nvarchar(1000).
        await db.Database.ExecuteSqlRawAsync(
            """
            IF EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = N'dbo' AND TABLE_NAME = N'VaultItems' AND COLUMN_NAME = N'Url'
                  AND DATA_TYPE = N'nvarchar'
                  AND CHARACTER_MAXIMUM_LENGTH IS NOT NULL
                  AND CHARACTER_MAXIMUM_LENGTH > 0
                  AND CHARACTER_MAXIMUM_LENGTH < 4000)
            BEGIN
              ALTER TABLE dbo.VaultItems ALTER COLUMN Url nvarchar(max) NULL;
            END
            """, ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            IF OBJECT_ID(N'dbo.VaultItems', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.VaultItems', N'EquivalentDomains') IS NULL
            BEGIN
              ALTER TABLE dbo.VaultItems ADD EquivalentDomains nvarchar(max) NULL;
            END
            """, ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            IF OBJECT_ID(N'dbo.DomainEquivalenceGroups', N'U') IS NULL
            BEGIN
              CREATE TABLE dbo.DomainEquivalenceGroups (
                Id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                Name nvarchar(200) NOT NULL,
                DomainsJson nvarchar(max) NOT NULL,
                CreatedAt datetime2 NOT NULL,
                ModifiedAt datetime2 NULL
              );
              CREATE INDEX IX_DomainEquivalenceGroups_Name ON dbo.DomainEquivalenceGroups (Name);
            END
            """, ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            IF OBJECT_ID(N'dbo.VaultCustomFields', N'U') IS NULL
            BEGIN
              CREATE TABLE dbo.VaultCustomFields (
                Id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                ItemId uniqueidentifier NOT NULL,
                Name nvarchar(200) NOT NULL,
                Value nvarchar(max) NULL,
                IsSecret bit NOT NULL,
                SortOrder int NOT NULL
              );
            END
            """, ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            IF OBJECT_ID(N'dbo.ExternalLinks', N'U') IS NULL
            BEGIN
              CREATE TABLE dbo.ExternalLinks (
                Id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                ItemId uniqueidentifier NOT NULL,
                Provider nvarchar(50) NOT NULL,
                ExternalId nvarchar(200) NOT NULL,
                LastSyncedAt datetime2 NULL,
                ContentHash nvarchar(128) NULL,
                IsMismatch bit NOT NULL
              );
            END
            """, ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            IF OBJECT_ID(N'dbo.OfflineChangeQueue', N'U') IS NULL
            BEGIN
              CREATE TABLE dbo.OfflineChangeQueue (
                Id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
                EntityType nvarchar(50) NOT NULL,
                EntityId nvarchar(64) NOT NULL,
                Operation nvarchar(20) NOT NULL,
                PayloadHash nvarchar(128) NULL,
                PayloadJson nvarchar(max) NULL,
                CreatedAtUtc datetime2 NOT NULL,
                Applied bit NOT NULL
              );
            END
            """, ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            IF OBJECT_ID(N'dbo.ReconcileHistory', N'U') IS NULL
            BEGIN
              CREATE TABLE dbo.ReconcileHistory (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                ItemId uniqueidentifier NOT NULL,
                CreatedAtUtc datetime2 NOT NULL,
                Summary nvarchar(500) NULL,
                BeforeJson nvarchar(max) NOT NULL,
                FieldSourcesJson nvarchar(max) NULL,
                PushedToProviders bit NOT NULL,
                ProviderIds nvarchar(500) NULL,
                UndoneAtUtc datetime2 NULL
              );
              CREATE INDEX IX_ReconcileHistory_ItemId ON dbo.ReconcileHistory (ItemId);
              CREATE INDEX IX_ReconcileHistory_CreatedAtUtc ON dbo.ReconcileHistory (CreatedAtUtc);
            END
            """, ct);
    }
}
