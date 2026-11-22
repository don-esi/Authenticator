using Authenticator.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Authenticator.Data;

/// <summary>
/// File-based offline backup (SQLite) under LocalAppData — used when Shared SQL is unreachable.
/// </summary>
public sealed class OfflineVaultDbContext : DbContext
{
    private readonly string _path;

    public OfflineVaultDbContext(string? path = null)
    {
        _path = path ?? ConnectionConfig.OfflineStorePath;
    }

    public DbSet<Account> Accounts { get; set; } = null!;
    public DbSet<Folder> Folders { get; set; } = null!;
    public DbSet<BackupCode> BackupCodes { get; set; } = null!;
    public DbSet<VaultCrypto> VaultCrypto { get; set; } = null!;
    public DbSet<VaultFolder> VaultFolders { get; set; } = null!;
    public DbSet<VaultItem> VaultItems { get; set; } = null!;
    public DbSet<VaultCustomField> VaultCustomFields { get; set; } = null!;
    public DbSet<ExternalLink> ExternalLinks { get; set; } = null!;
    public DbSet<OfflineChangeQueueEntry> OfflineChangeQueue { get; set; } = null!;
    public DbSet<ReconcileHistoryEntry> ReconcileHistory { get; set; } = null!;
    public DbSet<DomainEquivalenceGroup> DomainEquivalenceGroups { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
            optionsBuilder.UseSqlite($"Data Source={_path}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(e =>
        {
            e.HasIndex(x => x.ServiceName);
            e.HasIndex(x => x.IsFavorite);
            e.Ignore(x => x.Folder);
            e.Ignore(x => x.BackupCodes);
        });
        modelBuilder.Entity<BackupCode>(e => e.Ignore(x => x.Account));
        modelBuilder.Entity<VaultItem>(e =>
        {
            e.HasIndex(x => x.IsFavorite);
            e.Ignore(x => x.Folder);
            e.Ignore(x => x.LinkedAccount);
            e.Ignore(x => x.HasMismatch);
            e.HasMany(x => x.CustomFields).WithOne(c => c.Item).HasForeignKey(c => c.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.ExternalLinks).WithOne(l => l.Item).HasForeignKey(l => l.ItemId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<VaultCustomField>(e => e.Ignore(x => x.Item));
        modelBuilder.Entity<ExternalLink>(e => e.Ignore(x => x.Item));
        modelBuilder.Entity<VaultFolder>();
        modelBuilder.Entity<Folder>();
        modelBuilder.Entity<VaultCrypto>();
        modelBuilder.Entity<OfflineChangeQueueEntry>();
        modelBuilder.Entity<ReconcileHistoryEntry>(e =>
        {
            e.HasIndex(x => x.ItemId);
            e.HasIndex(x => x.CreatedAtUtc);
            e.Ignore(x => x.ItemTitle);
        });
        modelBuilder.Entity<DomainEquivalenceGroup>(e => e.HasIndex(x => x.Name));
    }
}
