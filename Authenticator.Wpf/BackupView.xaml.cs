using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Authenticator.Logging;
using Authenticator.Services;

namespace Authenticator;

public partial class BackupView : UserControl
{
    private readonly DatabaseService _database = new();
    private List<BackupAccountRow> _accounts = [];
    private int? _openAccountId;

    public BackupView()
    {
        InitializeComponent();
    }

    public async Task ReloadAsync()
    {
        try
        {
            var accounts = await _database.GetAllAccountsAsync();
            _accounts = accounts
                .Select(account =>
                {
                    var codes = account.BackupCodes.Where(code => !code.IsDeleted).ToList();
                    return new BackupAccountRow
                    {
                        Id = account.Id,
                        ServiceName = account.ServiceName,
                        Username = account.Username,
                        Initial = AccountEdits.Initial(account.ServiceName),
                        InitialBrush = AccountEdits.InitialBrush(account.ServiceName),
                        Available = codes.Count(code => !code.IsUsed),
                        Codes = codes.Select(code => new BackupCodeRow
                        {
                            Id = code.Id,
                            Code = code.Code,
                            IsUsed = code.IsUsed
                        }).ToList()
                    };
                })
                .Where(row => row.Codes.Count > 0)
                .OrderBy(row => row.ServiceName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ShowList();
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to load backup codes.", nameof(BackupView));
            EmptyText.Text = ex.Message;
            EmptyText.Visibility = Visibility.Visible;
        }
    }

    private void ShowList()
    {
        _openAccountId = null;
        DetailPanel.Visibility = Visibility.Collapsed;
        ListPanel.Visibility = Visibility.Visible;
        AccountList.ItemsSource = _accounts;
        EmptyText.Visibility = _accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Account_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BackupAccountRow row })
            return;

        _openAccountId = row.Id;
        DetailTitle.Text = row.ServiceName;
        DetailUser.Text = row.Username;
        CodeList.ItemsSource = row.Codes;
        ListPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowList();

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BackupCodeRow row } || string.IsNullOrWhiteSpace(row.Code))
            return;

        Clipboard.SetText(row.Code);
    }

    private async void MarkUsed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BackupCodeRow row })
            return;

        try
        {
            await _database.MarkBackupCodeAsUsedAsync(row.Id);
            var openId = _openAccountId;
            await ReloadAsync();
            var account = _accounts.FirstOrDefault(item => item.Id == openId);
            if (account is not null)
                Account_Click(new Button { Tag = account }, new RoutedEventArgs());
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, $"Failed to mark backup code id {row.Id} used.", nameof(BackupView));
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Backup codes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public sealed class BackupAccountRow
{
    public int Id { get; init; }
    public string ServiceName { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Initial { get; init; } = "?";
    public Brush InitialBrush { get; init; } = Brushes.Gray;
    public int Available { get; init; }
    public string AvailableText => Available.ToString();
    public List<BackupCodeRow> Codes { get; init; } = [];
}

public sealed class BackupCodeRow
{
    public int Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public bool IsUsed { get; init; }
    public bool CanMarkUsed => !IsUsed;
    public string Status => IsUsed ? "Used" : "Available";
}
