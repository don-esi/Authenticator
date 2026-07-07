using System;
using System.ComponentModel.DataAnnotations;

namespace Authenticator.Data.Models;

/// <summary>
/// Represents application settings stored in the database
/// </summary>
public class AppSettings
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Auto-lock timeout in seconds (0 = never)
    /// </summary>
    public int AutoLockTimeout { get; set; } = 0;

    /// <summary>
    /// Sync time with internet (NTP)
    /// </summary>
    public bool SyncTimeWithInternet { get; set; }

    /// <summary>
    /// Window docked position: None, Left, Right
    /// </summary>
    [MaxLength(10)]
    public string WindowDockPosition { get; set; } = "None";

    /// <summary>
    /// Is window collapsed to handle
    /// </summary>
    public bool IsWindowCollapsed { get; set; }

    /// <summary>
    /// Last window X position
    /// </summary>
    public int WindowX { get; set; }

    /// <summary>
    /// Last window Y position
    /// </summary>
    public int WindowY { get; set; }

    /// <summary>
    /// Last window width
    /// </summary>
    public int WindowWidth { get; set; } = 778;

    /// <summary>
    /// Last window height
    /// </summary>
    public int WindowHeight { get; set; } = 1490;

    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
