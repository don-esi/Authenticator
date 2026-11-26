using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Authenticator.Services;
using Authenticator.Services.Providers;

namespace Authenticator;

public partial class ConsolidateWindow : Window
{
    private readonly VaultItemService _vault = new();
    private readonly VaultImportService _import = new();
    private readonly VaultUiPreferences _uiPrefs = new();
    private List<VaultItem> _items = new();
    private VaultConsolidateMode _mode = VaultConsolidateMode.Suggested;
    private bool _suppressModeToggle;
    private bool _changedAny;
    private bool _closing;
    private Guid? _dragSourceId;
    private bool _showPasswords;
    private readonly HashSet<string> _providerFilter = new(StringComparer.OrdinalIgnoreCase);
    private string _sortField = "Title";
    private ListSortDirection _sortDir = ListSortDirection.Ascending;
    private List<SelectRow> _selectRows = new();
    private int _loadGeneration;

    public bool MergedAny => _changedAny;

    public ConsolidateWindow()
    {
        InitializeComponent();
        SyncfusionBootstrap.ApplyTo(this);
        Closing += Window_Closing;
        PreviewKeyDown += Window_PreviewKeyDown;
        Loaded += async (_, _) =>
        {
            try
            {
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                LoggingConfiguration.LogException(ex, "Consolidate reload failed.", nameof(ConsolidateWindow));
                if (!_closing && IsLoaded)
                    StatusText.Text = "Failed to load vault items.";
            }
        };
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _closing)
            return;
        e.Handled = true;
        Close_Click(sender, new RoutedEventArgs());
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // Stop focus-save / rebuild work before the dialog tears down — otherwise
        // LostKeyboardFocus → SaveAsync (or AppDialog) during ShowDialog unwind freezes the UI.
        _closing = true;
        _loadGeneration++;
        try
        {
            Keyboard.ClearFocus();
        }
        catch
        {
            // ignore
        }
    }

    private async Task ReloadAsync()
    {
        if (_closing)
            return;

        var gen = ++_loadGeneration;
        StatusText.Text = "Loading…";
        var items = await _vault.GetItemsAsync(favoritesOnly: false, revealSecrets: true);
        if (_closing || gen != _loadGeneration || !IsLoaded)
            return;

        _items = items;
        RebuildProviderFilters();
        var prefs = _uiPrefs.Load();
        SetMode(prefs.ConsolidateMode, persist: false);
        RebuildSurface();
        if (_closing || gen != _loadGeneration || !IsLoaded)
            return;

        var groups = VaultImportService.BuildSuggestedGroups(_items);
        TitleText.Text = groups.Count > 0
            ? $"Consolidate duplicates ({groups.Sum(g => g.Items.Count)} in {groups.Count} groups)"
            : "Consolidate duplicates";
        StatusText.Text = groups.Count > 0
            ? $"{groups.Count} suggested group(s). Edit fields · drag onto survivor · Delete."
            : "No username+host duplicates — try Board or Select.";
    }

    private void RebuildProviderFilters()
    {
        var providers = _items
            .SelectMany(i => i.ExternalLinks ?? Array.Empty<ExternalLink>())
            .Where(l => !string.IsNullOrWhiteSpace(l.Provider) &&
                        !string.Equals(l.Provider, "Shared", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Provider)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ProviderFilterPanel.Children.Clear();
        if (providers.Count == 0)
        {
            ProviderFilterPanel.Children.Add(new TextBlock
            {
                Text = "None linked",
                Foreground = (Brush)FindResource("SecondaryTextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            });
            return;
        }

        foreach (var provider in providers)
        {
            var chip = new ToggleButton
            {
                Content = provider,
                Style = (Style)FindResource("ProviderFilterChip"),
                IsChecked = _providerFilter.Count == 0 || _providerFilter.Contains(provider),
                Tag = provider,
                ToolTip = "Filter by provider (multi-select). All on = no filter."
            };
            chip.Checked += ProviderFilter_Changed;
            chip.Unchecked += ProviderFilter_Changed;
            ProviderFilterPanel.Children.Add(chip);
        }

        SyncProviderFilterFromChips();
    }

    private void ProviderFilter_Changed(object sender, RoutedEventArgs e)
    {
        SyncProviderFilterFromChips();
        RebuildSurface();
    }

    private void SyncProviderFilterFromChips()
    {
        _providerFilter.Clear();
        var chips = ProviderFilterPanel.Children.OfType<ToggleButton>().ToList();
        if (chips.Count == 0) return;
        var checkedChips = chips.Where(c => c.IsChecked == true).ToList();
        if (checkedChips.Count == 0 || checkedChips.Count == chips.Count) return;
        foreach (var chip in checkedChips)
            if (chip.Tag is string p) _providerFilter.Add(p);
    }

    private void RevealAll_Click(object sender, RoutedEventArgs e)
    {
        _showPasswords = !_showPasswords;
        RevealAllButton.Content = _showPasswords ? "Hide passwords" : "Reveal passwords";
        RebuildSurface();
    }

    private void SetMode(VaultConsolidateMode mode, bool persist)
    {
        _mode = mode;
        _suppressModeToggle = true;
        ModeSuggested.IsChecked = mode == VaultConsolidateMode.Suggested;
        ModeBoard.IsChecked = mode == VaultConsolidateMode.Board;
        ModeSelect.IsChecked = mode == VaultConsolidateMode.Select;
        _suppressModeToggle = false;
        SuggestedHost.Visibility = mode == VaultConsolidateMode.Suggested ? Visibility.Visible : Visibility.Collapsed;
        BoardHost.Visibility = mode == VaultConsolidateMode.Board ? Visibility.Visible : Visibility.Collapsed;
        SelectHost.Visibility = mode == VaultConsolidateMode.Select ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = mode switch
        {
            VaultConsolidateMode.Suggested => "Edit · drag onto survivor · Delete",
            VaultConsolidateMode.Board => "Edit · drag any onto survivor · Delete",
            _ => "Sort columns · multi-select Merge/Edit/Delete"
        };
        if (persist)
        {
            var prefs = _uiPrefs.Load();
            prefs.ConsolidateMode = mode;
            _uiPrefs.Save(prefs);
        }
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressModeToggle) return;
        if (sender is not ToggleButton { IsChecked: true } tb) return;
        var mode = tb == ModeBoard ? VaultConsolidateMode.Board
            : tb == ModeSelect ? VaultConsolidateMode.Select
            : VaultConsolidateMode.Suggested;
        SetMode(mode, persist: true);
        RebuildSurface();
    }

    private void Mode_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressModeToggle) return;
        if (ModeSuggested.IsChecked != true && ModeBoard.IsChecked != true && ModeSelect.IsChecked != true)
        {
            _suppressModeToggle = true;
            if (sender is ToggleButton tb) tb.IsChecked = true;
            _suppressModeToggle = false;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildSurface();

    private List<VaultItem> CurrentFiltered()
    {
        var q = (SearchBox.Text ?? "").Trim();
        IEnumerable<VaultItem> filtered = _items;
        if (!string.IsNullOrEmpty(q))
            filtered = filtered.Where(i => MatchesSearch(i, q));
        if (_providerFilter.Count > 0)
            filtered = filtered.Where(i =>
                (i.ExternalLinks ?? Array.Empty<ExternalLink>()).Any(l =>
                    !string.IsNullOrWhiteSpace(l.Provider) && _providerFilter.Contains(l.Provider)));
        return filtered.ToList();
    }

    private void RebuildSurface()
    {
        var filtered = CurrentFiltered();
        switch (_mode)
        {
            case VaultConsolidateMode.Suggested: BuildSuggested(filtered); break;
            case VaultConsolidateMode.Board: BuildBoard(filtered); break;
            default: BuildSelect(filtered); break;
        }
    }

    private static bool MatchesSearch(VaultItem item, string q)
    {
        var host = VaultImportService.NormalizeDomain(item.Url) ?? "";
        return $"{item.Title}\n{item.Username}\n{item.Url}\n{host}\n{item.Password}"
            .Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void BuildSuggested(List<VaultItem> filtered)
    {
        SuggestedPanel.Children.Clear();
        var idSet = filtered.Select(i => i.Id).ToHashSet();
        var groups = VaultImportService.BuildSuggestedGroups(_items)
            .Select(g => new ConsolidateSuggestGroup
            {
                Key = g.Key, Label = g.Label, IsExactHost = g.IsExactHost,
                Items = g.Items.Where(i => idSet.Contains(i.Id)).ToList()
            })
            .Where(g => g.Items.Count >= 2).ToList();

        if (groups.Count == 0)
        {
            SuggestedPanel.Children.Add(new TextBlock
            {
                Text = "No username+host duplicates — try Board or Select.",
                Foreground = (Brush)FindResource("SecondaryTextBrush"),
                Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var group in groups)
        {
            var suggested = VaultConsolidateHints.SuggestSurvivor(group.Items);
            var section = new Border { Style = (Style)FindResource("DialogCard"), Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(12) };
            var stack = new StackPanel();

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
            var autoBtn = new Button
            {
                Content = suggested is null
                    ? "Automerge…"
                    : $"Automerge → {ShortTitle(suggested)}",
                Style = (Style)FindResource("SecondaryButton"),
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 12,
                IsEnabled = suggested is not null,
                ToolTip = "Keep suggested survivor (most links, then newest). Fields: newest non-empty; Notes multi-merged when distinct."
            };
            var groupItems = group.Items.ToList();
            autoBtn.Click += async (_, e) =>
            {
                e.Handled = true;
                if (suggested is null) return;
                await RunAutomergeAsync(suggested.Id, groupItems.Where(i => i.Id != suggested.Id).Select(i => i.Id).ToList());
            };
            DockPanel.SetDock(autoBtn, Dock.Right);
            header.Children.Add(autoBtn);
            header.Children.Add(new TextBlock
            {
                Text = group.Label + (group.IsExactHost ? "" : " · related hosts"),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            stack.Children.Add(header);

            if (suggested is not null)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"Suggestion: keep \"{suggested.Title}\" · Updated {VaultConsolidateHints.FormatTimestampLocal(suggested.ModifiedAt ?? suggested.CreatedAt)} · {VaultConsolidateHints.LinkCount(suggested)} link(s)",
                    FontSize = 12,
                    Foreground = (Brush)FindResource("BrandBlueBrush"),
                    Margin = new Thickness(0, 0, 0, 8),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            var wrap = new WrapPanel();
            var allow = group.Items.Select(i => i.Id).ToHashSet();
            foreach (var item in group.Items)
                wrap.Children.Add(CreateCard(item, allow, suggested?.Id));
            stack.Children.Add(wrap);
            section.Child = stack;
            SuggestedPanel.Children.Add(section);
        }
    }

    private void BuildBoard(List<VaultItem> filtered)
    {
        BoardPanel.Children.Clear();
        // Peer suggestion within username+host groups even on the board.
        var suggestById = new Dictionary<Guid, Guid>();
        foreach (var group in VaultImportService.BuildSuggestedGroups(filtered))
        {
            var s = VaultConsolidateHints.SuggestSurvivor(group.Items);
            if (s is null) continue;
            foreach (var i in group.Items)
                suggestById[i.Id] = s.Id;
        }

        foreach (var item in filtered.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase))
        {
            Guid? peerSuggest = suggestById.TryGetValue(item.Id, out var sid) ? sid : null;
            BoardPanel.Children.Add(CreateCard(item, null, peerSuggest));
        }
    }

    private void BuildSelect(List<VaultItem> filtered)
    {
        var suggestById = new Dictionary<Guid, Guid>();
        foreach (var group in VaultImportService.BuildSuggestedGroups(filtered))
        {
            var s = VaultConsolidateHints.SuggestSurvivor(group.Items);
            if (s is null) continue;
            foreach (var i in group.Items)
                suggestById[i.Id] = s.Id;
        }

        _selectRows = filtered.Select(i =>
        {
            var row = SelectRow.From(i, _showPasswords);
            row.IsSuggestedSurvivor = suggestById.TryGetValue(i.Id, out var sid) && sid == i.Id;
            row.Suggestion = row.IsSuggestedSurvivor
                ? "Automerge"
                : suggestById.ContainsKey(i.Id) ? "→ merge" : "";
            return row;
        }).ToList();
        ApplySelectSort();
        SelectList.ItemsSource = null;
        SelectList.ItemsSource = _selectRows;
        UpdateSortHeaderGlyphs();
        SelectHint.Text = "Select 2+ to Merge · Automerge column = suggested survivor · Del Delete";
    }

    private void ApplySelectSort()
    {
        int Cmp(SelectRow a, SelectRow b)
        {
            var cmp = _sortField switch
            {
                "Username" => string.Compare(a.Username, b.Username, StringComparison.OrdinalIgnoreCase),
                "Password" => string.Compare(a.PasswordPlain, b.PasswordPlain, StringComparison.Ordinal),
                "Host" => string.Compare(a.Host, b.Host, StringComparison.OrdinalIgnoreCase),
                "Providers" => string.Compare(a.Providers, b.Providers, StringComparison.OrdinalIgnoreCase),
                "Created" => DateTime.Compare(a.CreatedAt, b.CreatedAt),
                "Updated" => DateTime.Compare(a.ModifiedAt ?? a.CreatedAt, b.ModifiedAt ?? b.CreatedAt),
                "Suggestion" => string.Compare(a.Suggestion, b.Suggestion, StringComparison.OrdinalIgnoreCase),
                _ => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase)
            };
            return _sortDir == ListSortDirection.Ascending ? cmp : -cmp;
        }
        _selectRows.Sort(Cmp);
    }

    private static string ShortTitle(VaultItem item)
    {
        var t = (item.Title ?? "").Trim();
        return string.IsNullOrEmpty(t) ? "(untitled)" : (t.Length <= 28 ? t : t[..25] + "…");
    }

    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader { Tag: string field }) return;
        if (string.Equals(_sortField, field, StringComparison.OrdinalIgnoreCase))
            _sortDir = _sortDir == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        else { _sortField = field; _sortDir = ListSortDirection.Ascending; }
        ApplySelectSort();
        SelectList.ItemsSource = null;
        SelectList.ItemsSource = _selectRows;
        UpdateSortHeaderGlyphs();
    }

    private void UpdateSortHeaderGlyphs()
    {
        foreach (var col in SelectGridView.Columns)
        {
            if (col.Header is not GridViewColumnHeader header || header.Tag is not string field) continue;
            header.Content = string.Equals(field, _sortField, StringComparison.OrdinalIgnoreCase)
                ? field + (_sortDir == ListSortDirection.Ascending ? " ▲" : " ▼")
                : field;
        }
    }
    private Border CreateCard(VaultItem item, HashSet<Guid>? allowDropFromGroup, Guid? suggestedSurvivorId)
    {
        var providers = string.Join(" · ",
            (item.ExternalLinks ?? Array.Empty<ExternalLink>())
            .Where(l => !string.IsNullOrWhiteSpace(l.Provider) &&
                        !string.Equals(l.Provider, "Shared", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Provider)
            .Distinct(StringComparer.OrdinalIgnoreCase));

        var isSuggested = suggestedSurvivorId is Guid sid && sid == item.Id;
        var body = new StackPanel { Width = 220 };

        if (isSuggested)
        {
            body.Children.Add(new TextBlock
            {
                Text = "Suggested survivor · automerge",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("BrandBlueBrush"),
                Margin = new Thickness(0, 0, 0, 4)
            });
        }
        else if (suggestedSurvivorId is not null)
        {
            body.Children.Add(new TextBlock
            {
                Text = "Merge into suggested",
                FontSize = 11,
                Foreground = (Brush)FindResource("SecondaryTextBrush"),
                Margin = new Thickness(0, 0, 0, 4)
            });
        }

        body.Children.Add(new TextBlock
        {
            Text = $"Created {VaultConsolidateHints.FormatTimestampLocal(item.CreatedAt)}",
            FontSize = 11,
            Foreground = (Brush)FindResource("SecondaryTextBrush")
        });
        body.Children.Add(new TextBlock
        {
            Text = $"Updated {VaultConsolidateHints.FormatTimestampLocal(item.ModifiedAt ?? item.CreatedAt)}",
            FontSize = 11,
            FontWeight = isSuggested ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = isSuggested
                ? (Brush)FindResource("BrandBlueBrush")
                : (Brush)FindResource("SecondaryTextBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        });

        TextBox AddField(string label, string? value, Action<string> assign, bool password = false)
        {
            body.Children.Add(new TextBlock
            {
                Text = label, FontSize = 11,
                Foreground = (Brush)FindResource("SecondaryTextBrush"),
                Margin = new Thickness(0, body.Children.Count == 0 ? 0 : 6, 0, 2)
            });
            var plain = value ?? "";
            var box = new TextBox
            {
                Text = password && !_showPasswords && plain.Length > 0
                    ? new string('•', Math.Min(12, Math.Max(4, plain.Length)))
                    : plain,
                Style = (Style)FindResource("DialogInput"),
                Tag = plain,
                Padding = new Thickness(6, 4, 6, 4),
                FontFamily = password ? new FontFamily("Consolas") : FontFamily
            };

            void Commit()
            {
                var text = box.Text;
                if (password && !_showPasswords && text.All(c => c == '•'))
                    text = box.Tag as string ?? "";
                else if (password)
                    box.Tag = text;
                assign(text);
            }

            box.LostKeyboardFocus += async (_, _) =>
            {
                if (_closing)
                    return;
                Commit();
                if (password && !_showPasswords)
                {
                    var p = box.Tag as string ?? "";
                    box.Text = p.Length == 0 ? "" : new string('•', Math.Min(12, Math.Max(4, p.Length)));
                }
                await SaveItemFieldsAsync(item);
            };
            box.GotKeyboardFocus += (_, _) =>
            {
                if (_closing)
                    return;
                if (password && !_showPasswords)
                    box.Text = box.Tag as string ?? "";
            };
            box.KeyDown += async (_, e) =>
            {
                if (_closing || e.Key != Key.Enter)
                    return;
                Commit();
                await SaveItemFieldsAsync(item);
                if (!_closing)
                    Keyboard.ClearFocus();
                e.Handled = true;
            };
            body.Children.Add(box);
            return box;
        }

        AddField("Title", item.Title, v => item.Title = v);
        AddField("Username", item.Username, v => item.Username = v);
        AddField("Password", item.Password, v => item.Password = v, password: true);
        AddField("URL", item.Url, v => item.Url = v);

        if (!string.IsNullOrEmpty(providers))
        {
            body.Children.Add(new TextBlock
            {
                Text = providers, Margin = new Thickness(0, 8, 0, 0), FontSize = 11,
                FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("BrandBlueBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0)
        };
        if (isSuggested && allowDropFromGroup is { Count: > 1 })
        {
            var autoBtn = new Button
            {
                Content = "Automerge",
                Style = (Style)FindResource("PrimaryButton"),
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 12,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "Merge peers into this suggested survivor (newest field wins)"
            };
            autoBtn.Click += async (_, e) =>
            {
                e.Handled = true;
                var donors = allowDropFromGroup.Where(id => id != item.Id).ToList();
                await RunAutomergeAsync(item.Id, donors);
            };
            actions.Children.Add(autoBtn);
        }

        var deleteBtn = new Button
        {
            Content = "Delete", Style = (Style)FindResource("SecondaryButton"),
            Padding = new Thickness(8, 2, 8, 2), FontSize = 12,
            ToolTip = "Soft-delete this vault item"
        };
        deleteBtn.Click += async (_, e) => { e.Handled = true; await DeleteItemsAsync([item.Id]); };
        actions.Children.Add(deleteBtn);
        body.Children.Add(actions);

        var card = new Border
        {
            Child = body, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("SecondaryBackgroundBrush"),
            BorderBrush = isSuggested
                ? (Brush)FindResource("BrandBlueBrush")
                : (Brush)FindResource("BorderBrushColor"),
            BorderThickness = new Thickness(isSuggested ? 2 : 1),
            AllowDrop = true, Tag = item.Id
        };

        card.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindAncestor<TextBox>(d) is not null)
            { _dragSourceId = null; return; }
            if (s is Border { Tag: Guid id }) _dragSourceId = id;
        };
        card.MouseMove += Card_MouseMove;
        card.DragOver += (_, e) =>
        {
            if (!TryGetDragId(e, out var src) || src == item.Id ||
                (allowDropFromGroup is not null && !allowDropFromGroup.Contains(src)))
            { e.Effects = DragDropEffects.None; e.Handled = true; return; }
            e.Effects = DragDropEffects.Move; e.Handled = true;
        };
        card.Drop += async (_, e) =>
        {
            if (!TryGetDragId(e, out var src) || src == item.Id) return;
            if (allowDropFromGroup is not null && !allowDropFromGroup.Contains(src)) return;
            e.Handled = true;
            await RunMergeAsync(item.Id, [src]);
        };
        return card;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private async Task SaveItemFieldsAsync(VaultItem item)
    {
        if (_closing)
            return;
        try
        {
            await _vault.SaveAsync(item, touchModifiedAt: true);
            if (_closing)
                return;
            _changedAny = true;
            if (IsLoaded)
                StatusText.Text = $"Saved \"{item.Title}\".";
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Consolidate inline save failed.", nameof(ConsolidateWindow));
            // Never modal-alert during teardown — that re-enters the dispatcher and freezes the app.
            if (!_closing && IsLoaded && IsVisible)
                AppDialog.Alert(this, ex.Message, "Save", AppDialogKind.Warning);
        }
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSourceId is null) return;
        if (sender is not Border border) return;
        if (e.OriginalSource is DependencyObject d && FindAncestor<TextBox>(d) is not null) return;
        DragDrop.DoDragDrop(border, new DataObject("VaultConsolidateItemId", _dragSourceId.Value), DragDropEffects.Move);
        _dragSourceId = null;
    }

    private static bool TryGetDragId(DragEventArgs e, out Guid id)
    {
        id = default;
        if (e.Data.GetDataPresent("VaultConsolidateItemId") && e.Data.GetData("VaultConsolidateItemId") is Guid g)
        { id = g; return true; }
        return false;
    }

    private void SelectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var count = SelectList.SelectedItems.Count;
        SelectHint.Text = count == 0 ? "Select 2+ to Merge · double-click Edit · Del Delete"
            : count == 1 ? "1 selected — Edit or Delete"
            : $"{count} selected — Merge / Delete";
    }

    private async void SelectMerge_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectList.SelectedItems.Cast<SelectRow>().ToList();
        if (selected.Count < 2)
        {
            AppDialog.Alert(this, "Select at least two vault items.", "Merge");
            return;
        }

        var items = selected
            .Select(r => _items.FirstOrDefault(i => i.Id == r.Id))
            .Where(i => i is not null)
            .Cast<VaultItem>()
            .ToList();
        var auto = VaultConsolidateHints.SuggestSurvivor(items);
        if (auto is not null &&
            AppDialog.Confirm(
                this,
                $"Use automerge suggestion?\n\nSurvivor: \"{auto.Title}\"\nUpdated: {VaultConsolidateHints.FormatTimestampLocal(auto.ModifiedAt ?? auto.CreatedAt)}\nLinks: {VaultConsolidateHints.LinkCount(auto)}\n\nYes = suggested survivor + newest fields. No = pick survivor manually.",
                "Automerge suggestion",
                confirmText: "Use suggestion",
                cancelText: "Pick survivor…"))
        {
            await RunAutomergeAsync(
                auto.Id,
                selected.Where(r => r.Id != auto.Id).Select(r => r.Id).ToList());
            return;
        }

        var survivor = PickSurvivor(selected);
        if (survivor is null) return;
        await RunMergeAsync(survivor.Id, selected.Where(r => r.Id != survivor.Id).Select(r => r.Id).ToList());
    }

    private async void SelectEdit_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectList.SelectedItems.Cast<SelectRow>().FirstOrDefault();
        if (row is null) { AppDialog.Alert(this, "Select one item to edit.", "Edit"); return; }
        await EditItemAsync(row.Id);
    }

    private async void SelectDelete_Click(object sender, RoutedEventArgs e)
    {
        var ids = SelectList.SelectedItems.Cast<SelectRow>().Select(r => r.Id).ToList();
        if (ids.Count == 0) { AppDialog.Alert(this, "Select one or more items to delete.", "Delete"); return; }
        await DeleteItemsAsync(ids);
    }

    private async void SelectList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectList.SelectedItem is SelectRow row) await EditItemAsync(row.Id);
    }

    private async void SelectList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        var ids = SelectList.SelectedItems.Cast<SelectRow>().Select(r => r.Id).ToList();
        if (ids.Count == 0) return;
        e.Handled = true;
        await DeleteItemsAsync(ids);
    }

    private async Task EditItemAsync(Guid id)
    {
        var item = _items.FirstOrDefault(i => i.Id == id) ?? await _vault.GetByIdAsync(id);
        if (item is null) return;
        var win = new VaultItemEditWindow(item, isNew: false) { Owner = this };
        if (win.ShowDialog() == true) { _changedAny = true; await ReloadAsync(); }
    }

    private async Task DeleteItemsAsync(IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0) return;
        var titles = ids.Select(id => _items.FirstOrDefault(i => i.Id == id)?.Title ?? id.ToString("N")[..8]).Take(5).ToList();
        var more = ids.Count > 5 ? $" (+{ids.Count - 5} more)" : "";
        if (!AppDialog.Confirm(this, $"Soft-delete {ids.Count} vault item(s)?\n\n{string.Join("\n", titles)}{more}", "Delete", confirmText: "Delete"))
            return;
        try
        {
            foreach (var id in ids) await _vault.DeleteAsync(id, softDelete: true);
            _changedAny = true;
            StatusText.Text = $"Deleted {ids.Count} item(s).";
            await ReloadAsync();
        }
        catch (Exception ex) { AppDialog.Alert(this, ex.Message, "Delete", AppDialogKind.Warning); }
    }

    private SelectRow? PickSurvivor(List<SelectRow> selected)
    {
        var ordered = selected.OrderByDescending(r => r.LinkCount).ThenByDescending(r => r.ModifiedAt ?? r.CreatedAt).ToList();
        var list = new ListBox { Margin = new Thickness(16, 0, 16, 8), DisplayMemberPath = "Label" };
        foreach (var row in ordered) list.Items.Add(row);
        list.SelectedItem = ordered[0];
        var hint = new TextBlock
        {
            Text = "Keep this vault row; others become donors.",
            Margin = new Thickness(16, 16, 16, 8),
            Foreground = (Brush)FindResource("SecondaryTextBrush")
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 0, 16, 16) };
        var cancelBtn = new Button { Content = "Cancel", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var okBtn = new Button { Content = "Use as survivor", Style = (Style)FindResource("PrimaryButton"), IsDefault = true };
        buttons.Children.Add(cancelBtn); buttons.Children.Add(okBtn);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(hint, 0); Grid.SetRow(list, 1); Grid.SetRow(buttons, 2);
        grid.Children.Add(hint); grid.Children.Add(list); grid.Children.Add(buttons);
        var win = new Window
        {
            Title = "Choose survivor", Owner = this, Width = 420, Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = (Brush)FindResource("PageBackgroundBrush"),
            Foreground = (Brush)FindResource("PrimaryTextBrush"), Content = grid
        };
        SyncfusionBootstrap.ApplyTo(win);
        var ok = false;
        cancelBtn.Click += (_, _) => win.Close();
        okBtn.Click += (_, _) => { ok = true; win.Close(); };
        win.ShowDialog();
        return ok ? list.SelectedItem as SelectRow : null;
    }

    private async Task RunAutomergeAsync(Guid survivorId, IReadOnlyList<Guid> donorIds)
    {
        var survivor = _items.FirstOrDefault(i => i.Id == survivorId);
        var donors = donorIds.Select(id => _items.FirstOrDefault(i => i.Id == id)).Where(i => i is not null).Cast<VaultItem>().ToList();
        if (survivor is null || donors.Count == 0) return;

        var selection = VaultConsolidateHints.BuildAutomergeSelection(survivor, donors);
        var picker = new ConsolidateFieldWindow(survivor, donors, selection)
        {
            Owner = this,
            Title = "Automerge suggestion"
        };
        if (picker.ShowDialog() != true || picker.Selection is null) return;
        await CommitMergeAsync(survivorId, donorIds, picker.Selection);
    }

    private async Task RunMergeAsync(Guid survivorId, IReadOnlyList<Guid> donorIds)
    {
        var survivor = _items.FirstOrDefault(i => i.Id == survivorId);
        var donors = donorIds.Select(id => _items.FirstOrDefault(i => i.Id == id)).Where(i => i is not null).Cast<VaultItem>().ToList();
        if (survivor is null || donors.Count == 0) return;
        if (!AppDialog.Confirm(this,
                $"Merge {donors.Count} item(s) into \"{survivor.Title}\"?\n\nDonors are soft-deleted in Shared. Update all will align linked vaults (same fields/folder), delete same-provider duplicates remotely, and create missing provider entries.",
                "Consolidate", confirmText: "Continue"))
            return;

        var suggested = VaultConsolidateHints.BuildAutomergeSelection(survivor, donors);
        var picker = new ConsolidateFieldWindow(survivor, donors, suggested) { Owner = this };
        if (picker.ShowDialog() != true || picker.Selection is null) return;
        await CommitMergeAsync(survivorId, donorIds, picker.Selection);
    }

    private async Task CommitMergeAsync(
        Guid survivorId,
        IReadOnlyList<Guid> donorIds,
        MergeFieldSelection selection)
    {
        var choice = AppDialog.Choose(this,
            "Apply merge so vaults match?\n\nUpdate all: push survivor fields/folder to linked providers, delete same-provider remote duplicates, create missing provider entries.\n\nVault only: Shared merge only (remote duplicates remain; dropped origins are suppressed from Sync).",
            "Merge into vault", primaryText: "Update all", secondaryText: "Vault only", cancelText: "Cancel");
        if (choice == AppDialogChoice.Cancel) return;

        try
        {
            StatusText.Text = "Merging…";
            var result = await _import.MergeVaultItemsAsync(survivorId, donorIds, selection,
                updateLinkedSources: choice == AppDialogChoice.Primary);
            _changedAny = true;
            var msg =
                $"Merged into survivor. Links transferred: {result.LinksTransferred}. Same-provider dropped: {result.LinksDroppedSameProvider}." +
                $"\nRemote deleted: {result.OriginsDeleted}. Created: {result.OriginsCreated}." +
                (string.IsNullOrEmpty(result.CanonicalPath) ? "" : $"\nFolder: {result.CanonicalPath}");
            if (result.Messages.Count > 0) msg += "\n" + string.Join("\n", result.Messages.Take(8));
            if (result.Push is { } push && (push.Failed > 0 || push.Updated > 0 || push.Messages.Count > 0))
                msg += $"\nProviders: {push.Updated} updated, {push.Skipped} skipped, {push.Failed} failed.";
            var warn = result.Push is { Failed: > 0 } ||
                       result.Messages.Any(m => m.Contains("failed", StringComparison.OrdinalIgnoreCase));
            AppDialog.Alert(this, msg, "Consolidate",
                warn ? AppDialogKind.Warning : AppDialogKind.Info);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            AppDialog.Alert(this, ex.Message, "Consolidate", AppDialogKind.Warning);
            StatusText.Text = "Merge failed.";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // Do not call Close() after setting DialogResult — ShowDialog already ends.
        // Do not use IsCancel alone: we need DialogResult == MergedAny for the caller.
        if (_closing)
            return;
        _closing = true;
        try
        {
            Keyboard.ClearFocus();
        }
        catch
        {
            // ignore
        }

        try
        {
            // Only set once; a second assign (e.g. IsCancel) throws and can wedge the dispatcher.
            if (DialogResult is null)
                DialogResult = _changedAny;
        }
        catch (InvalidOperationException)
        {
            try
            {
                Close();
            }
            catch
            {
                // ignore
            }
        }

        e.Handled = true;
    }

    private sealed class SelectRow
    {
        public Guid Id { get; init; }
        public string Title { get; init; } = "";
        public string Username { get; init; } = "";
        public string PasswordPlain { get; init; } = "";
        public string PasswordDisplay { get; init; } = "";
        public string Host { get; init; } = "";
        public string Providers { get; init; } = "";
        public string CreatedDisplay { get; init; } = "";
        public string UpdatedDisplay { get; init; } = "";
        public string Suggestion { get; set; } = "";
        public bool IsSuggestedSurvivor { get; set; }
        public int LinkCount { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? ModifiedAt { get; init; }
        public string Label => $"{Title}  ·  {Username}  ·  {Host}  ({LinkCount} link{(LinkCount == 1 ? "" : "s")})";

        public static SelectRow From(VaultItem item, bool showPasswords)
        {
            var links = (item.ExternalLinks ?? Array.Empty<ExternalLink>())
                .Where(l => !string.IsNullOrWhiteSpace(l.Provider) &&
                            !string.Equals(l.Provider, "Shared", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var plain = item.Password ?? "";
            return new SelectRow
            {
                Id = item.Id,
                Title = item.Title ?? "",
                Username = item.Username ?? "",
                PasswordPlain = plain,
                PasswordDisplay = string.IsNullOrEmpty(plain) ? ""
                    : showPasswords ? plain : new string('•', Math.Min(12, Math.Max(4, plain.Length))),
                Host = VaultImportService.NormalizeDomain(item.Url) ?? "",
                Providers = string.Join(", ", links.Select(l => l.Provider).Distinct(StringComparer.OrdinalIgnoreCase)),
                CreatedDisplay = VaultConsolidateHints.FormatTimestampLocal(item.CreatedAt),
                UpdatedDisplay = VaultConsolidateHints.FormatTimestampLocal(item.ModifiedAt ?? item.CreatedAt),
                LinkCount = links.Count,
                CreatedAt = item.CreatedAt,
                ModifiedAt = item.ModifiedAt
            };
        }
    }
}