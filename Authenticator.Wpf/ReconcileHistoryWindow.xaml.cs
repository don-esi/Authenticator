using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Authenticator.Services;

namespace Authenticator;

public partial class ReconcileHistoryWindow : Window
{
    private readonly Guid? _itemId;
    private readonly ReconcileHistoryService _history = new();
    private readonly ObservableCollection<HistoryRow> _rows = new();

    /// <param name="itemId">When set, lists history for one vault item; otherwise global recent.</param>
    public ReconcileHistoryWindow(Guid? itemId = null, string? itemTitle = null)
    {
        InitializeComponent();
        SyncfusionBootstrap.ApplyTo(this);
        _itemId = itemId;
        TitleText.Text = itemId is Guid
            ? $"Reconcile history — {itemTitle ?? "item"}"
            : "Reconcile history (all items)";
        HistoryList.ItemsSource = _rows;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            List<ReconcileHistoryEntry> entries;
            if (_itemId is Guid id)
                entries = await _history.ListForItemAsync(id);
            else
                entries = await _history.ListRecentAsync();

            _rows.Clear();
            foreach (var e in entries)
                _rows.Add(HistoryRow.From(e, showItemTitle: _itemId is null));
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to load reconcile history.", nameof(ReconcileHistoryWindow));
            AppDialog.Alert(this, ex.Message, "History", AppDialogKind.Warning);
        }
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid historyId })
            return;

        var choice = AppDialog.Choose(
            this,
            "Restore the vault snapshot from before that Apply?\n\n" +
            "Update all also pushes the restored values to linked providers.",
            "Undo reconcile",
            primaryText: "Update all",
            secondaryText: "Vault only",
            cancelText: "Cancel");
        if (choice == AppDialogChoice.Cancel)
            return;

        var push = choice == AppDialogChoice.Primary;

        try
        {
            await _history.UndoAsync(historyId, pushToProviders: push);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppDialog.Alert(this, ex.Message, "Undo", AppDialogKind.Warning);
            await ReloadAsync();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class HistoryRow
    {
        public Guid Id { get; init; }
        public string TitleLine { get; init; } = "";
        public string DetailLine { get; init; } = "";
        public bool CanUndo { get; init; }

        public static HistoryRow From(ReconcileHistoryEntry e, bool showItemTitle)
        {
            var when = e.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var title = showItemTitle
                ? $"{when} · {e.ItemTitle ?? e.ItemId.ToString("N")[..8]}"
                : when;
            if (e.UndoneAtUtc is not null)
                title += " · undone";

            return new HistoryRow
            {
                Id = e.Id,
                TitleLine = title,
                DetailLine = e.Summary ?? "",
                CanUndo = e.UndoneAtUtc is null
            };
        }
    }
}
