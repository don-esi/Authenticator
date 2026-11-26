using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Authenticator.Data.Models;
using Authenticator.Services;
using Authenticator.Services.Providers;

namespace Authenticator;

public partial class ConsolidateFieldWindow : Window
{
    private static readonly string[] FieldKeys =
        ["Title", "Username", "Password", "URL", "Notes", "Favorite", "TOTP", "Folder"];

    private static readonly HashSet<string> MultiMergeFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Notes", "URL", "Title"
    };

    private static readonly Brush MatchGreenFill = Freeze(new SolidColorBrush(Color.FromArgb(0x28, 0x22, 0xC5, 0x5E)));
    private static readonly Brush MatchGreenBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));
    private static readonly Brush DiffAmberFill = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xF5, 0x9E, 0x0B)));
    private static readonly Brush DiffAmberBorder = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)));

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private readonly VaultItem _survivor;
    private readonly List<VaultItem> _columns;
    private readonly Dictionary<string, Guid> _fieldSource = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<Guid>> _multiSource = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Field, Guid ItemId), Border> _cells = new();
    private bool _showPasswords;
    private string _activeField = "Title";

    public MergeFieldSelection? Selection { get; private set; }

    public ConsolidateFieldWindow(
        VaultItem survivor,
        IReadOnlyList<VaultItem> donors,
        MergeFieldSelection? initialSelection = null)
    {
        InitializeComponent();
        SyncfusionBootstrap.ApplyTo(this);
        _survivor = survivor;
        _columns = new List<VaultItem> { survivor };
        _columns.AddRange(donors);
        foreach (var field in FieldKeys)
            _fieldSource[field] = survivor.Id;
        if (initialSelection is not null)
            ApplyInitialSelection(initialSelection);
        BuildMatrix();
        Highlight();
        LoadEditBox();
    }

    private void ApplyInitialSelection(MergeFieldSelection initial)
    {
        foreach (var field in FieldKeys)
        {
            if (initial.TryGetOverride(field, out var edited) && edited is not null)
            {
                _overrides[field] = edited;
                _fieldSource.Remove(field);
                _multiSource.Remove(field);
                continue;
            }

            if (initial.TryGetMulti(field, out var multi) && multi.Count > 1)
            {
                _multiSource[field] = multi.ToHashSet();
                _fieldSource.Remove(field);
                _overrides.Remove(field);
                continue;
            }

            _fieldSource[field] = initial.Get(field, _survivor.Id);
            _multiSource.Remove(field);
            _overrides.Remove(field);
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        MatrixHost.UpdateLayout();
        MatrixHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var matrix = MatrixHost.DesiredSize;
        var work = SystemParameters.WorkArea;
        Width = Math.Clamp(Math.Ceiling(matrix.Width + 72), MinWidth, Math.Max(MinWidth, work.Width - 32));
        Height = Math.Clamp(Math.Ceiling(matrix.Height + 240), MinHeight, Math.Max(MinHeight, work.Height - 32));
    }

    private void BuildMatrix()
    {
        MatrixHost.Children.Clear();
        MatrixHost.RowDefinitions.Clear();
        MatrixHost.ColumnDefinitions.Clear();
        _cells.Clear();

        MatrixHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        foreach (var _ in _columns)
            MatrixHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 140 });

        MatrixHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var r = 0; r < FieldKeys.Length; r++)
            MatrixHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var corner = new TextBlock
        {
            Text = "Field",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4),
            Foreground = (Brush)FindResource("SecondaryTextBrush")
        };
        Grid.SetRow(corner, 0);
        Grid.SetColumn(corner, 0);
        MatrixHost.Children.Add(corner);

        for (var c = 0; c < _columns.Count; c++)
        {
            var item = _columns[c];
            var isSurvivor = item.Id == _survivor.Id;
            var header = new StackPanel { Margin = new Thickness(4) };
            header.Children.Add(new TextBlock
            {
                Text = isSurvivor ? "Survivor" : "Donor",
                FontSize = 11,
                Foreground = (Brush)FindResource("SecondaryTextBrush")
            });
            var newestId = FindNewestColumnId();
            var isNewest = newestId is Guid nid && nid == item.Id;
            header.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(item.Title) ? "(untitled)" : item.Title,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = item.Title
            });
            header.Children.Add(new TextBlock
            {
                Text = $"Created {VaultConsolidateHints.FormatTimestampLocal(item.CreatedAt)}",
                FontSize = 11,
                Foreground = (Brush)FindResource("SecondaryTextBrush")
            });
            header.Children.Add(new TextBlock
            {
                Text = isNewest
                    ? $"Updated {VaultConsolidateHints.FormatTimestampLocal(item.ModifiedAt ?? item.CreatedAt)} · newer"
                    : $"Updated {VaultConsolidateHints.FormatTimestampLocal(item.ModifiedAt ?? item.CreatedAt)}",
                FontSize = 11,
                FontWeight = isNewest ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isNewest
                    ? (Brush)FindResource("BrandBlueBrush")
                    : (Brush)FindResource("SecondaryTextBrush")
            });
            var providers = string.Join(", ",
                (item.ExternalLinks ?? Array.Empty<ExternalLink>())
                .Where(l => !string.IsNullOrWhiteSpace(l.Provider) &&
                            !string.Equals(l.Provider, "Shared", StringComparison.OrdinalIgnoreCase))
                .Select(l => l.Provider)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(providers))
            {
                header.Children.Add(new TextBlock
                {
                    Text = providers,
                    FontSize = 11,
                    Foreground = (Brush)FindResource("SecondaryTextBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            Grid.SetRow(header, 0);
            Grid.SetColumn(header, c + 1);
            MatrixHost.Children.Add(header);
        }

        for (var r = 0; r < FieldKeys.Length; r++)
        {
            var field = FieldKeys[r];
            var label = new TextBlock
            {
                Text = field,
                Margin = new Thickness(4, 8, 4, 8),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("SecondaryTextBrush"),
                Cursor = Cursors.Hand,
                Tag = field
            };
            label.MouseLeftButtonUp += (_, e) =>
            {
                _activeField = field;
                LoadEditBox();
                Highlight();
                e.Handled = true;
            };
            Grid.SetRow(label, r + 1);
            Grid.SetColumn(label, 0);
            MatrixHost.Children.Add(label);

            for (var c = 0; c < _columns.Count; c++)
            {
                var item = _columns[c];
                var text = FormatField(field, item);
                var cell = new Border
                {
                    Margin = new Thickness(2),
                    Padding = new Thickness(8, 6, 8, 6),
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = text,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 220
                    },
                    Tag = (field, item.Id),
                    ToolTip = MultiMergeFields.Contains(field)
                        ? "Click to pick · Ctrl+click to include in multi-merge"
                        : "Click to pick this value"
                };
                cell.MouseLeftButtonUp += Cell_Click;
                _cells[(field, item.Id)] = cell;
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c + 1);
                MatrixHost.Children.Add(cell);
            }
        }
    }

    private void Cell_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: (string field, Guid itemId) })
            return;

        _activeField = field;
        _overrides.Remove(field);

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && MultiMergeFields.Contains(field))
        {
            if (!_multiSource.TryGetValue(field, out var set))
            {
                set = new HashSet<Guid>();
                if (_fieldSource.TryGetValue(field, out var existing))
                    set.Add(existing);
                _multiSource[field] = set;
            }

            if (!set.Add(itemId))
                set.Remove(itemId);
            if (set.Count <= 1)
            {
                _multiSource.Remove(field);
                _fieldSource[field] = set.Count == 1 ? set.First() : itemId;
            }
            else
                _fieldSource.Remove(field);
        }
        else
        {
            _multiSource.Remove(field);
            _fieldSource[field] = itemId;
        }

        LoadEditBox();
        Highlight();
        e.Handled = true;
    }

    private void LoadEditBox()
    {
        EditFieldLabel.Text = _activeField;
        if (_overrides.TryGetValue(_activeField, out var edited))
        {
            EditValueBox.Text = edited;
            return;
        }

        if (_multiSource.TryGetValue(_activeField, out var multi) && multi.Count > 0)
        {
            var parts = multi
                .Select(id => _columns.FirstOrDefault(c => c.Id == id))
                .Where(i => i is not null)
                .Select(i => (GetRaw(_activeField, i!) ?? "").Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            EditValueBox.Text = _activeField is "Notes"
                ? string.Join(Environment.NewLine + Environment.NewLine, parts)
                : string.Join(" | ", parts);
            return;
        }

        var srcId = _fieldSource.GetValueOrDefault(_activeField, _survivor.Id);
        var src = _columns.FirstOrDefault(c => c.Id == srcId) ?? _survivor;
        var raw = GetRaw(_activeField, src) ?? "";
        if (_activeField is "Password" or "TOTP" && !_showPasswords && raw.Length > 0)
            EditValueBox.Text = new string('•', Math.Min(12, Math.Max(4, raw.Length)));
        else
            EditValueBox.Text = raw;
        EditValueBox.Tag = raw;
    }

    private void ApplyEdit_Click(object sender, RoutedEventArgs e) => CommitEditBox();

    private void EditValueBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Don't auto-commit masked bullets as the password.
    }

    private void CommitEditBox()
    {
        var text = EditValueBox.Text ?? "";
        if (_activeField is "Password" or "TOTP" && !_showPasswords && text.All(c => c == '•'))
            text = EditValueBox.Tag as string ?? text;

        _overrides[_activeField] = text;
        _fieldSource.Remove(_activeField);
        _multiSource.Remove(_activeField);
        Highlight();
        StatusFromEdit();
    }

    private void StatusFromEdit()
    {
        // no status control — hint via label
        EditFieldLabel.Text = _activeField + " ✎";
    }

    private void SoftFill_Click(object sender, RoutedEventArgs e)
    {
        foreach (var field in FieldKeys)
        {
            _overrides.Remove(field);
            _multiSource.Remove(field);
            if (!string.IsNullOrWhiteSpace(GetRaw(field, _survivor)))
            {
                _fieldSource[field] = _survivor.Id;
                continue;
            }

            var donor = _columns.FirstOrDefault(i =>
                i.Id != _survivor.Id && !string.IsNullOrWhiteSpace(GetRaw(field, i)));
            _fieldSource[field] = donor?.Id ?? _survivor.Id;
        }

        LoadEditBox();
        Highlight();
    }

    private void Automerge_Click(object sender, RoutedEventArgs e)
    {
        var donors = _columns.Where(c => c.Id != _survivor.Id).ToList();
        ApplyInitialSelection(VaultConsolidateHints.BuildAutomergeSelection(_survivor, donors));
        BuildMatrix();
        Highlight();
        LoadEditBox();
    }

    private Guid? FindNewestColumnId()
    {
        Guid? bestId = null;
        DateTime? bestUtc = null;
        var dated = 0;
        foreach (var item in _columns)
        {
            var utc = VaultConsolidateHints.EffectiveTimestampUtc(item);
            if (utc is null) continue;
            dated++;
            if (bestUtc is null || utc > bestUtc)
            {
                bestUtc = utc;
                bestId = item.Id;
            }
            else if (utc == bestUtc)
                bestId = null;
        }

        return dated >= 2 ? bestId : null;
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        _showPasswords = !_showPasswords;
        RevealButton.Content = _showPasswords ? "Hide passwords" : "Reveal passwords";
        BuildMatrix();
        Highlight();
        LoadEditBox();
    }

    private void Highlight()
    {
        var brand = (Brush)FindResource("BrandBlueBrush");
        var neutralBg = (Brush)FindResource("SecondaryBackgroundBrush");
        var neutralBorder = (Brush)FindResource("BorderBrushColor");

        foreach (var ((field, itemId), cell) in _cells)
        {
            var item = _columns.First(i => i.Id == itemId);
            var selected = IsSelected(field, itemId);
            var matchesSurvivor = FieldEquals(field, item, _survivor);
            var isActiveRow = string.Equals(field, _activeField, StringComparison.OrdinalIgnoreCase);

            if (_overrides.ContainsKey(field))
            {
                cell.Background = neutralBg;
                cell.BorderBrush = isActiveRow ? brand : neutralBorder;
                cell.Opacity = 0.55;
            }
            else
            {
                cell.Opacity = 1;
                if (item.Id == _survivor.Id)
                {
                    cell.Background = neutralBg;
                    cell.BorderBrush = selected ? brand : neutralBorder;
                }
                else if (matchesSurvivor)
                {
                    cell.Background = MatchGreenFill;
                    cell.BorderBrush = selected ? brand : MatchGreenBorder;
                }
                else
                {
                    cell.Background = DiffAmberFill;
                    cell.BorderBrush = selected ? brand : DiffAmberBorder;
                }
            }

            cell.BorderThickness = new Thickness(selected || isActiveRow ? 2 : 1);
        }
    }

    private bool IsSelected(string field, Guid itemId)
    {
        if (_overrides.ContainsKey(field))
            return false;
        if (_multiSource.TryGetValue(field, out var set))
            return set.Contains(itemId);
        return _fieldSource.GetValueOrDefault(field) == itemId;
    }

    private static bool FieldEquals(string field, VaultItem a, VaultItem b)
    {
        var left = Normalize(field, GetRaw(field, a));
        var right = Normalize(field, GetRaw(field, b));
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    private static string Normalize(string field, string? value)
    {
        var raw = (value ?? "").Trim();
        if (field is "Favorite")
            return bool.TryParse(raw, out var b) && b ? "true" : "false";
        if (field is "Username" or "Title" or "URL")
            return raw.ToLowerInvariant();
        return raw;
    }

    private static string? GetRaw(string field, VaultItem item) => field switch
    {
        "Title" => item.Title,
        "Username" => item.Username,
        "Password" => item.Password,
        "URL" => item.Url,
        "Notes" => item.Notes,
        "Favorite" => item.IsFavorite.ToString(),
        "TOTP" => item.TotpSecret,
        "Folder" => VaultConsolidateHints.FolderDisplayName(item)
                    ?? VaultConsolidateHints.RichestProviderPath(item),
        _ => null
    };

    private string FormatField(string field, VaultItem item)
    {
        var raw = GetRaw(field, item);
        if (field is "Password" or "TOTP")
        {
            if (string.IsNullOrEmpty(raw))
                return "";
            return _showPasswords ? raw : new string('•', Math.Min(12, Math.Max(4, raw.Length)));
        }

        return raw ?? "";
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        // Capture any pending edit if the user typed but didn't click Apply.
        if (!string.IsNullOrEmpty(EditValueBox.Text) &&
            (Keyboard.FocusedElement == EditValueBox || _overrides.ContainsKey(_activeField) ||
             EditValueBox.IsKeyboardFocusWithin))
        {
            // Only auto-apply if text differs from pure pick display
        }

        var selection = new MergeFieldSelection();
        foreach (var field in FieldKeys)
        {
            if (_overrides.TryGetValue(field, out var edited))
            {
                selection.SetOverride(field, edited);
                continue;
            }

            if (_multiSource.TryGetValue(field, out var multi) && multi.Count > 1)
            {
                selection.SetMulti(field, multi);
                continue;
            }

            selection.Set(field, _fieldSource.GetValueOrDefault(field, _survivor.Id));
        }

        Selection = selection;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}