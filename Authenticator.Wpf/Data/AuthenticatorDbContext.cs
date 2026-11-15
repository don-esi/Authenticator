using Authenticator.Data.Models;
using Authenticator.Logging;
using Microsoft.EntityFrameworkCore;

namespace Authenticator.Data;

/// <summary>
/// Entity Framework Core database context for Authenticator app (shared SQL Express SoR).
/// </summary>
public partial class AuthenticatorDbContext : DbContext
{
    private readonly string? _connectionString;

    public AuthenticatorDbContext()
    {
    }

    public AuthenticatorDbContext(string connectionString)
    {
        _connectionString = connectionString;
    }

    public DbSet<Account> Accounts { get; set; } = null!;
    public DbSet<Folder> Folders { get; set; } = null!;
    public DbSet<BackupCode> BackupCodes { get; set; } = null!;
    public DbSet<PasswordEntry> PasswordEntries { get; set; } = null!;
    public DbSet<AppSettings> AppSettings { get; set; } = null!;
    public DbSet<VaultCrypto> VaultCrypto { get; set; } = null!;
    public DbSet<VaultFolder> VaultFolders { get; set; } = null!;
    public DbSet<VaultItem> VaultItems { get; set; } = null!;
    public DbSet<VaultCustomField> VaultCustomFields { get; set; } = null!;
    public DbSet<ExternalLink> ExternalLinks { get; set; } = null!;
    public DbSet<OfflineChangeQueueEntry> OfflineChangeQueue { get; set; } = null!;

    public static string ConnectionString => ConnectionConfig.ConnectionString;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (optionsBuilder.IsConfigured)
            return;

        var cs = _connectionString ?? ConnectionString;
        optionsBuilder.UseSqlServer(cs);
        LoggingConfiguration.LogDebug(
            $"DbContext configured for SQL Server {ConnectionConfig.Server}, database {ConnectionConfig.Database}.",
            nameof(AuthenticatorDbContext));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasIndex(e => e.ServiceName);
            entity.HasIndex(e => e.IsDeleted);
            entity.HasIndex(e => e.FolderId);
            entity.HasIndex(e => e.IsFavorite);

            entity.HasOne(e => e.Folder)
                .WithMany(f => f.Accounts)
                .HasForeignKey(e => e.FolderId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(e => e.BackupCodes)
                .WithOne(bc => bc.Account)
                .HasForeignKey(bc => bc.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Folder>(entity =>
        {
            entity.HasIndex(e => e.Name);
        });

        modelBuilder.Entity<BackupCode>(entity =>
        {
            entity.HasIndex(e => e.AccountId);
            entity.HasIndex(e => new { e.AccountId, e.IsUsed, e.IsDeleted });
        });

        modelBuilder.Entity<PasswordEntry>(entity =>
        {
            entity.HasIndex(e => e.Source);
            entity.HasIndex(e => e.ServiceName);
            entity.HasIndex(e => e.IsDeleted);
            entity.HasIndex(e => e.LinkedAccountId);
        });

        modelBuilder.Entity<VaultFolder>(entity =>
        {
            entity.HasIndex(e => e.Name);
        });

        modelBuilder.Entity<VaultItem>(entity =>
        {
            entity.HasIndex(e => e.ItemType);
            entity.HasIndex(e => e.Title);
            entity.HasIndex(e => e.IsDeleted);
            entity.HasIndex(e => e.IsFavorite);
            entity.HasIndex(e => e.FolderId);
            entity.HasIndex(e => e.LinkedAccountId);
            entity.HasIndex(e => e.LastUsedAt);

            entity.HasOne(e => e.Folder)
                .WithMany(f => f.Items)
                .HasForeignKey(e => e.FolderId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.LinkedAccount)
                .WithMany()
                .HasForeignKey(e => e.LinkedAccountId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(e => e.CustomFields)
                .WithOne(c => c.Item)
                .HasForeignKey(c => c.ItemId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.ExternalLinks)
                .WithOne(l => l.Item)
                .HasForeignKey(l => l.ItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExternalLink>(entity =>
        {
            entity.HasIndex(e => new { e.Provider, e.ExternalId });
            entity.HasIndex(e => e.ItemId);
            entity.HasIndex(e => e.IsMismatch);
        });

        modelBuilder.Entity<OfflineChangeQueueEntry>(entity =>
        {
            entity.HasIndex(e => e.Applied);
            entity.HasIndex(e => e.CreatedAtUtc);
        });

        modelBuilder.Entity<AppSettings>().HasData(new AppSettings
        {
            Id = 1,
            AutoLockTimeout = 0,
            SyncTimeWithInternet = false,
            WindowDockPosition = "None",
            IsWindowCollapsed = false,
            WindowDockHandleOffsetY = 120,
            WindowX = 100,
            WindowY = 100,
            WindowWidth = 778,
            WindowHeight = 1490
        });
    }
}
