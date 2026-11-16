using System.Windows;
using Authenticator.Data.Models;
using Authenticator.Services;
using Authenticator.Services.Providers;

namespace Authenticator;

public partial class DiffMatchWindow : Window
{
    private readonly VaultItem _item;
    private readonly ProviderEntry? _remote;
    private readonly string _providerId;
    private readonly VaultImportService _import = new();

    public DiffMatchWindow(VaultItem item, ProviderEntry? remote, string providerId)
    {
        InitializeComponent();
        SyncfusionBootstrap.ApplyTo(this);
        _item = item;
        _remote = remote;
        _providerId = providerId;
        TitleText.Text = remote is null
            ? $"{item.Title} — Shared (provider side unavailable)"
            : $"{item.Title} — Shared vs {_providerId}";
        SyncSharedButton.IsEnabled = remote is not null;
        DiffGrid.ItemsSource = BuildRows();
    }

    private List<DiffRow> BuildRows()
    {
        string P(string? v) => v ?? "";
        var remote = _remote;
        if (remote is null)
        {
            return
            [
                new DiffRow
                {
                    Field = "(provider)",
                    Shared = "",
                    Provider = $"Could not load {_providerId} entry — reconnect or Sync, then reopen Diff.",
                    Status = "Missing"
                },
                Row("Title", _item.Title, null),
                Row("Username", _item.Username, null),
                Row("Password", Mask(_item.Password), null),
                Row("URL", _item.Url, null),
                Row("Notes", _item.Notes, null),
                Row("Favorite", _item.IsFavorite.ToString(), null),
                Row("TOTP", string.IsNullOrEmpty(_item.TotpSecret) ? "" : "••••", null)
            ];
        }

        return
        [
            Row("Title", _item.Title, remote.Title),
            Row("Username", _item.Username, remote.Username),
            Row("Password", Mask(_item.Password), Mask(remote.Password)),
            Row("URL", _item.Url, remote.Url),
            Row("Notes", _item.Notes, remote.Notes),
            Row("Favorite", _item.IsFavorite.ToString(), remote.IsFavorite.ToString()),
            Row("TOTP", string.IsNullOrEmpty(_item.TotpSecret) ? "" : "••••", string.IsNullOrEmpty(remote.Totp) ? "" : "••••")
        ];

        DiffRow Row(string field, string? shared, string? provider)
        {
            var s = P(shared);
            var p = P(provider);
            var status = remote is null
                ? "—"
                : string.Equals(s, p, StringComparison.Ordinal) ? "Match" : "Diff";
            return new DiffRow { Field = field, Shared = s, Provider = p, Status = status };
        }
    }

    private static string Mask(string? value) =>
        string.IsNullOrEmpty(value) ? "" : new string('•', Math.Min(12, value.Length));

    private async void SyncShared_Click(object sender, RoutedEventArgs e)
    {
        if (_remote is null)
        {
            AppDialog.Alert(this, "No provider entry linked for this item.", "Diff");
            return;
        }
        await _import.SyncEntryToSharedAsync(_item.Id, _remote, _providerId);
        DialogResult = true;
        Close();
    }

    private async void SyncProvider_Click(object sender, RoutedEventArgs e)
    {
        await _import.SyncEntryToProviderAsync(_item.Id, _providerId);
        AppDialog.Alert(this, $"Synced Shared → {_providerId} (origin entry updated when the provider supports write-back).", "Diff");
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public sealed class DiffRow
    {
        public string Field { get; set; } = "";
        public string Shared { get; set; } = "";
        public string Provider { get; set; } = "";
        public string Status { get; set; } = "";
    }
}
