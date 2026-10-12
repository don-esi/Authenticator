using Authenticator.Data.Models;
using Authenticator.Logging;
using Microsoft.EntityFrameworkCore;

namespace Authenticator.Data;

/// <summary>
/// Entity Framework Core database context for Authenticator app
/// </summary>
public partial class AuthenticatorDbContext : DbContext
{
    public DbSet<Account> Accounts { get; set; } = null!;
    public DbSet<Folder> Folders { get; set; } = null!;
    public DbSet<BackupCode> BackupCodes { get; set; } = null!;
    public DbSet<PasswordEntry> PasswordEntries { get; set; } = null!;
    public DbSet<AppSettings> AppSettings { get; set; } = null!;

    /// <summary>
    /// Existing Authenticator database on .\ZOGEXPRESS2025.
    /// Startup must not create, migrate, or alter tables.
    /// </summary>
    public const string ConnectionString =
        "Server=.\\ZOGEXPRESS2025;Database=Authenticator;Trusted_Connection=True;TrustServerCertificate=True";

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlServer(ConnectionString);
            LoggingConfiguration.LogDebug("DbContext configured for SQL Server .\\ZOGEXPRESS2025, database Authenticator.", nameof(AuthenticatorDbContext));
        }
        else
        {
            LoggingConfiguration.LogDebug("DbContext already configured externally.", nameof(AuthenticatorDbContext));
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        LoggingConfiguration.LogDebug("Applying EF Core model configuration.", nameof(AuthenticatorDbContext));

        // Account configuration
        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasIndex(e => e.ServiceName);
            entity.HasIndex(e => e.IsDeleted);
            entity.HasIndex(e => e.FolderId);

            entity.HasOne(e => e.Folder)
                .WithMany(f => f.Accounts)
                .HasForeignKey(e => e.FolderId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(e => e.BackupCodes)
                .WithOne(bc => bc.Account)
                .HasForeignKey(bc => bc.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Folder configuration
        modelBuilder.Entity<Folder>(entity =>
        {
            entity.HasIndex(e => e.Name);
        });

        // BackupCode configuration
        modelBuilder.Entity<BackupCode>(entity =>
        {
            entity.HasIndex(e => e.AccountId);
            entity.HasIndex(e => new { e.AccountId, e.IsUsed, e.IsDeleted });
        });

        // PasswordEntry configuration
        modelBuilder.Entity<PasswordEntry>(entity =>
        {
            entity.HasIndex(e => e.Source);
            entity.HasIndex(e => e.ServiceName);
            entity.HasIndex(e => e.IsDeleted);
            entity.HasIndex(e => e.LinkedAccountId);
        });

        // Seed default settings
        modelBuilder.Entity<AppSettings>().HasData(new AppSettings
        {
            Id = 1,
            AutoLockTimeout = 0,
            SyncTimeWithInternet = false,
            WindowDockPosition = "None",
            IsWindowCollapsed = false,
            WindowX = 100,
            WindowY = 100,
            WindowWidth = 778,
            WindowHeight = 1490
        });

        LoggingConfiguration.LogDebug("EF Core model configuration applied.", nameof(AuthenticatorDbContext));
    }
}