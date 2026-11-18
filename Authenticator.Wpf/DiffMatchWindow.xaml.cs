using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Authenticator.Data.Models;
using Authenticator.Services;
using Authenticator.Services.Providers;

namespace Authenticator;

public partial class DiffMatchWindow : Window
{
    public const string VaultSourceId = "vault";

    private static readonly string[] FieldKeys =
        ["Title", "Username", "Password", "URL", "Host", "Notes", "Favorite", "TOTP"];

    private readonly VaultItem _item;
    private readonly List<DiffSource> _sources;
    private readonly VaultImportService _import = new();
    private readonly VaultItemService _vaultItems = new();
    private readonly Dictionary<string, string> _fieldSource = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Field, string SourceId), Border> _cells = new();
    private string? _correctSourceId;
    private bool _showPasswords;

    /// <summary>Multi-provider Diff. <paramref name="providerSources"/> are linked providers (remote may be null).</summary>
    public DiffMatchWindow(
        VaultItem item,
        IReadOnlyList<(string ProviderId, string DisplayName, ProviderEntry? Remote, bool IsMismatch)> providerSources)
    {
        InitializeComponent();
        SyncfusionBootstrap.ApplyTo(this);
        _item = item;
        _sources = BuildSources(item, providerSources);
        TitleText.Text = $"{item.Title} — reconcile ({_sources.Count} sources)";
        foreach (var field in FieldKeys)
            _fieldSource[field] = VaultSourceId;
        BuildMatrix();
        HighlightSelections();
    }

    /// <summary>Legacy single-provider ctor.</summary>
    public DiffMatchWindow(VaultItem item, ProviderEntry? remote, string providerId)
        : this(item,
        [
            (providerId,
                ResolveDisplayName(providerId),
                remote,
                item.ExternalLinks?.Any(l =>
                    string.Equals(l.Provider, providerId, StringComparison.OrdinalIgnoreCase) && l.IsMismatch) == true)
        ])
    {
    }

    private static List<DiffSource> BuildSources(
        VaultItem item,
        IReadOnlyList<(string ProviderId, string DisplayName, ProviderEntry? Remote, bool IsMismatch)> providers)
    {
        var list = new List<DiffSource>
        {
            new()
            {
                Id = VaultSourceId,
                DisplayName = "Vault",
                IsVault = true,
                Title = item.Title,
                Username = item.Username,
                Password = item.Password,
                Url = item.Url,
                Notes = item.Notes,
                IsFavorite = item.IsFavorite,
                Totp = item.TotpSecret
            }
        };

        foreach (var (providerId, displayName, remote, isMismatch) in providers
                     .OrderByDescending(p => p.IsMismatch)
                     .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(new DiffSource
            {
                Id = providerId,
                DisplayName = displayName,
                IsVault = false,
                IsMismatch = isMismatch,
                Title = remote?.Title,
                Username = remote?.Username,
                Password = remote?.Password,
                Url = remote?.Url,
                Notes = remote?.Notes,
                IsFavorite = remote?.IsFavorite ?? false,
                Totp = remote?.Totp,
                Unavailable = remote is null
            });
        }

        return list;
    }

    private static string ResolveDisplayName(string providerId)
    {
        try
        {
            var match = VaultProviderCatalog.CreateAll()
                .FirstOrDefault(p => string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match.DisplayName;
        }
        catch
        {
            // Fall through.
        }

        return string.IsNullOrWhiteSpace(providerId) ? "Provider" : providerId;
    }

    private void BuildMatrix()
    {
        MatrixHost.Children.Clear();
        MatrixHost.RowDefinitions.Clear();
        MatrixHost.ColumnDefinitions.Clear();
        _cells.Clear();

        MatrixHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        foreach (var _ in _sources)
            MatrixHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });

        // Header row
        MatrixHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var fieldHdr = new TextBlock
        {
            Text = "Field",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 8, 10),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(fieldHdr, 0);
        Grid.SetColumn(fieldHdr, 0);
        MatrixHost.Children.Add(fieldHdr);

        for (var c = 0; c < _sources.Count; c++)
        {
            var source = _sources[c];
            var header = BuildSourceHeader(source);
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, c + 1);
            MatrixHost.Children.Add(header);
        }

        for (var r = 0; r < FieldKeys.Length; r++)
        {
            MatrixHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var field = FieldKeys[r];
            var label = new TextBlock
            {
                Text = field,
                Margin = new Thickness(0, 0, 8, 8),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("SecondaryTextBrush")
            };
            Grid.SetRow(label, r + 1);
            Grid.SetColumn(label, 0);
            MatrixHost.Children.Add(label);

            for (var c = 0; c < _sources.Count; c++)
            {
                var source = _sources[c];
                var cell = BuildFieldCell(field, source);
                _cells[(field, source.Id)] = cell;
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c + 1);
                MatrixHost.Children.Add(cell);
            }
        }
    }

    private UIElement BuildSourceHeader(DiffSource source)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 10) };
        var title = new TextBlock
        {
            Text = source.DisplayName + (source.IsMismatch ? " ⚠" : ""),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        };
        var check = new CheckBox
        {
            Content = "This is correct",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Tag = source.Id,
            IsEnabled = !source.Unavailable,
            Padding = new Thickness(4),
            MinHeight = 36
        };
        check.Checked += CorrectSource_Checked;
        check.Unchecked += CorrectSource_Unchecked;
        panel.Children.Add(title);
        panel.Children.Add(check);
        if (source.Unavailable)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "(unavailable — reconnect)",
                FontSize = 11,
                Foreground = (Brush)FindResource("SecondaryTextBrush"),
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        return panel;
    }

    private Border BuildFieldCell(string field, DiffSource source)
    {
        var display = FormatField(field, source, maskPassword: !_showPasswords);
        var isUrl = field == "URL" && LooksLikeUrl(source.Url);
        var panel = new DockPanel { LastChildFill = true };
        if (isUrl && !source.Unavailable)
        {
            var launch = new Button
            {
                Content = "↗",
                Width = 32,
                Margin = new Thickness(6, 0, 0, 0),
                Style = (Style)FindResource("SecondaryButton"),
                Tag = source.Url,
                ToolTip = "Open URL"
            };
            DockPanel.SetDock(launch, Dock.Right);
            launch.Click += (_, _) => LaunchUrl(source.Url);
            panel.Children.Add(launch);
        }

        var text = new TextBlock
        {
            Text = display,
            TextWrapping = field is "Notes" or "URL" ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            MaxHeight = field == "Notes" ? 72 : double.PositiveInfinity
        };
        panel.Children.Add(text);

        var border = new Border
        {
            Child = panel,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 8, 8),
            CornerRadius = new CornerRadius(10),
            Background = (Brush)FindResource("SecondaryBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrushColor"),
            BorderThickness = new Thickness(1),
            Cursor = source.Unavailable ? Cursors.Arrow : Cursors.Hand,
            Tag = (field, source.Id),
            MinHeight = 36,
            Opacity = source.Unavailable ? 0.55 : 1
        };
        if (!source.Unavailable)
            border.MouseLeftButtonUp += FieldCell_Click;
        return border;
    }

    private void FieldCell_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: ValueTuple<string, string> tag })
            return;
        var (field, sourceId) = tag;
        _correctSourceId = null;
        ClearCorrectCheckboxes();
        _fieldSource[field] = sourceId;
        HighlightSelections();
        e.Handled = true;
    }

    private void CorrectSource_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string sourceId })
            return;
        _correctSourceId = sourceId;
        foreach (var child in MatrixHost.Children.OfType<StackPanel>())
        {
            foreach (var cb in child.Children.OfType<CheckBox>())
            {
                if (!ReferenceEquals(cb, sender) && cb.IsChecked == true)
                    cb.IsChecked = false;
            }
        }

        foreach (var field in FieldKeys)
            _fieldSource[field] = sourceId;
        HighlightSelections();
    }

    private void CorrectSource_Unchecked(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string sourceId })
            return;
        if (string.Equals(_correctSourceId, sourceId, StringComparison.OrdinalIgnoreCase))
            _correctSourceId = null;
    }

    private void ClearCorrectCheckboxes()
    {
        foreach (var child in MatrixHost.Children.OfType<StackPanel>())
        {
            foreach (var cb in child.Children.OfType<CheckBox>())
            {
                if (cb.IsChecked == true)
                    cb.IsChecked = false;
            }
        }
    }

    private void HighlightSelections()
    {
        var brand = (Brush)FindResource("BrandBlueBrush");
        var border = (Brush)FindResource("BorderBrushColor");
        foreach (var ((field, sourceId), cell) in _cells)
        {
            var selected = string.Equals(_fieldSource.GetValueOrDefault(field), sourceId, StringComparison.OrdinalIgnoreCase);
            cell.BorderBrush = selected ? brand : border;
            cell.BorderThickness = new Thickness(selected ? 2 : 1);
        }
    }

    private void ShowPasswords_Changed(object sender, RoutedEventArgs e)
    {
        _showPasswords = ShowPasswordsCheck.IsChecked == true;
        BuildMatrix();
        // Restore selection highlights / correct checkbox
        if (_correctSourceId is not null)
        {
            foreach (var child in MatrixHost.Children.OfType<StackPanel>())
            {
                foreach (var cb in child.Children.OfType<CheckBox>())
                {
                    if (cb.Tag is string id &&
                        string.Equals(id, _correctSourceId, StringComparison.OrdinalIgnoreCase))
                        cb.IsChecked = true;
                }
            }
        }

        HighlightSelections();
    }

    private void SoftFill_Click(object sender, RoutedEventArgs e)
    {
        var vault = _sources.First(s => s.IsVault);
        foreach (var field in FieldKeys)
        {
            if (!string.IsNullOrWhiteSpace(GetRaw(field, vault)))
            {
                _fieldSource[field] = VaultSourceId;
                continue;
            }

            var donor = _sources.FirstOrDefault(s => !s.IsVault && !s.Unavailable && !string.IsNullOrWhiteSpace(GetRaw(field, s)));
            _fieldSource[field] = donor?.Id ?? VaultSourceId;
        }

        _correctSourceId = null;
        ClearCorrectCheckboxes();
        HighlightSelections();
    }

    private async void UnlinkSelected_Click(object sender, RoutedEventArgs e)
    {
        var providerId = _correctSourceId;
        if (string.IsNullOrWhiteSpace(providerId) ||
            string.Equals(providerId, VaultSourceId, StringComparison.OrdinalIgnoreCase))
        {
            // Fall back to first mismatched provider field selection
            providerId = _sources.FirstOrDefault(s => !s.IsVault && s.IsMismatch)?.Id
                         ?? _sources.FirstOrDefault(s => !s.IsVault)?.Id;
        }

        if (string.IsNullOrWhiteSpace(providerId) ||
            string.Equals(providerId, VaultSourceId, StringComparison.OrdinalIgnoreCase))
        {
            AppDialog.Alert(this, "Select a provider column (This is correct) to unlink.", "Unlink");
            return;
        }

        var name = _sources.FirstOrDefault(s => s.Id == providerId)?.DisplayName ?? providerId;
        if (!AppDialog.Confirm(
                this,
                $"Unlink {name} from this vault item?\n\nVault row stays; next Sync can import it separately.",
                "Not the same entry",
                confirmText: "Unlink"))
            return;

        try
        {
            await _import.UnlinkProviderAsync(_item.Id, providerId);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppDialog.Alert(this, ex.Message, "Unlink", AppDialogKind.Warning);
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplySelectionsToItem(_item);
            await _vaultItems.SaveAsync(_item);
            await _import.ClearMismatchesForItemAsync(_item.Id);

            var push = AppDialog.Confirm(
                this,
                "Vault updated from your selections.\n\nUpdate all linked sources to match the vault?",
                "Update linked sources",
                confirmText: "Update all",
                cancelText: "Vault only");

            if (push)
            {
                var result = await _import.PushToLinkedProvidersAsync(_item);
                var detail = $"Updated {result.Updated}, skipped {result.Skipped}, failed {result.Failed}.";
                if (result.Messages.Count > 0)
                    detail += "\n" + string.Join("\n", result.Messages.Take(6));
                AppDialog.Alert(this, detail, "Push to providers");
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppDialog.Alert(this, ex.Message, "Apply", AppDialogKind.Warning);
        }
    }

    private void ApplySelectionsToItem(VaultItem item)
    {
        item.Title = Pick("Title") ?? item.Title ?? "";
        item.Username = Pick("Username");
        item.Password = Pick("Password");
        item.Url = Pick("URL");
        item.Notes = Pick("Notes");
        var fav = Pick("Favorite");
        if (bool.TryParse(fav, out var f))
            item.IsFavorite = f;
        var totp = Pick("TOTP");
        // Don't write masked bullets into TOTP
        if (totp is not null && totp != "••••")
            item.TotpSecret = string.IsNullOrWhiteSpace(totp) ? item.TotpSecret : totp;
    }

    private string? Pick(string field)
    {
        var sourceId = _fieldSource.GetValueOrDefault(field, VaultSourceId);
        var source = _sources.FirstOrDefault(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase))
                     ?? _sources[0];
        return GetRaw(field, source);
    }

    private static string? GetRaw(string field, DiffSource source) => field switch
    {
        "Title" => source.Title,
        "Username" => source.Username,
        "Password" => source.Password,
        "URL" => source.Url,
        "Host" => VaultImportService.NormalizeDomain(source.Url),
        "Notes" => source.Notes,
        "Favorite" => source.IsFavorite.ToString(),
        "TOTP" => source.Totp,
        _ => null
    };

    private static string FormatField(string field, DiffSource source, bool maskPassword)
    {
        if (source.Unavailable && field != "Title")
            return "—";
        var raw = GetRaw(field, source);
        if (field == "Password")
        {
            if (string.IsNullOrEmpty(raw))
                return "";
            return maskPassword ? new string('•', Math.Min(12, raw.Length)) : raw;
        }

        if (field == "TOTP")
            return string.IsNullOrEmpty(raw) ? "" : (maskPassword ? "••••" : raw);

        return raw ?? "";
    }

    private static bool LooksLikeUrl(string? value)
    {
        var raw = (value ?? "").Trim();
        if (string.IsNullOrEmpty(raw))
            return false;
        if (raw.Contains("://", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(raw, UriKind.Absolute, out var abs) &&
                   (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps);
        return raw.Contains('.', StringComparison.Ordinal) || raw.Contains('/', StringComparison.Ordinal);
    }

    private void LaunchUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !LooksLikeUrl(url))
            return;
        try
        {
            var launch = url.Contains("://", StringComparison.Ordinal) ? url.Trim() : "https://" + url.Trim();
            Process.Start(new ProcessStartInfo { FileName = launch, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppDialog.Alert(this, ex.Message, "Open site", AppDialogKind.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class DiffSource
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public bool IsVault { get; init; }
        public bool IsMismatch { get; init; }
        public bool Unavailable { get; init; }
        public string? Title { get; init; }
        public string? Username { get; init; }
        public string? Password { get; init; }
        public string? Url { get; init; }
        public string? Notes { get; init; }
        public bool IsFavorite { get; init; }
        public string? Totp { get; init; }
    }
}
