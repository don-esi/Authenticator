using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
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
        ["Title", "Created", "Updated", "Username", "Password", "URL", "Host", "Notes", "Favorite", "TOTP"];

    private static readonly Brush MatchGreenFill = Freeze(new SolidColorBrush(Color.FromArgb(0x28, 0x22, 0xC5, 0x5E)));
    private static readonly Brush MatchGreenBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));
    private static readonly Brush DiffAmberFill = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xF5, 0x9E, 0x0B)));
    private static readonly Brush DiffAmberBorder = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)));

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private readonly VaultItem _item;
    private readonly List<DiffSource> _sources;
    private readonly VaultImportService _import = new();
    private readonly VaultItemService _vaultItems = new();
    private readonly ReconcileHistoryService _history = new();
    private readonly Dictionary<string, string> _fieldSource = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Field, string SourceId), Border> _cells = new();
    private readonly Dictionary<string, TextBlock> _fieldLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _sourceHeaderChrome = new(StringComparer.OrdinalIgnoreCase);
    private string? _correctSourceId;
    private string? _unlinkProviderId;
    private bool _showPasswords;

    // Explorer-style rename: first click selects; slow second click on selected Vault cell edits.
    private (string Field, string SourceId)? _lastSelectKey;
    private DateTime _lastSelectUtc;
    private bool _isEditing;
    private bool _endingEdit;
    private string? _editField;
    private string? _editOriginal;
    private Border? _editBorder;
    private DiffSource? _editSource;

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
        foreach (var field in FieldKeys.Where(f => !IsInfoField(f)))
            _fieldSource[field] = VaultSourceId;
        BuildMatrix();
        HighlightSelections();
    }

    private static bool IsInfoField(string field) => field is "Created" or "Updated";

    private static bool IsEditableVaultField(string field) =>
        field is "Title" or "Username" or "Password" or "URL" or "Notes" or "Favorite" or "TOTP";

    /// <summary>Same threshold Explorer uses to distinguish double-click from rename click.</summary>
    private static TimeSpan RenameClickDelay { get; } = TimeSpan.FromMilliseconds(GetDoubleClickTime());

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private void Window_Loaded(object sender, RoutedEventArgs e) => FitToScreen();

    /// <summary>Grow to fit the matrix when possible; never exceed the work area.</summary>
    private void FitToScreen()
    {
        MatrixHost.UpdateLayout();
        MatrixHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var matrix = MatrixHost.DesiredSize;

        // Header + hint row + footer + paddings around the scroll card.
        const double chromeWidth = 72;
        const double chromeHeight = 176;
        var work = SystemParameters.WorkArea;
        var maxW = Math.Max(MinWidth, work.Width - 32);
        var maxH = Math.Max(MinHeight, work.Height - 32);
        var targetW = Math.Clamp(Math.Ceiling(matrix.Width + chromeWidth), MinWidth, maxW);
        var targetH = Math.Clamp(Math.Ceiling(matrix.Height + chromeHeight), MinHeight, maxH);

        Width = targetW;
        Height = targetH;

        if (Owner is { } owner && owner.IsVisible)
        {
            var left = owner.Left + (owner.ActualWidth - Width) / 2;
            var top = owner.Top + (owner.ActualHeight - Height) / 2;
            Left = Math.Clamp(left, work.Left + 8, work.Left + work.Width - Width - 8);
            Top = Math.Clamp(top, work.Top + 8, work.Top + work.Height - Height - 8);
        }
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
                Totp = item.TotpSecret,
                CreatedAt = item.CreatedAt == default ? null : item.CreatedAt,
                ModifiedAt = item.ModifiedAt ?? (item.CreatedAt == default ? null : item.CreatedAt)
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
                CreatedAt = remote?.CreatedAt,
                ModifiedAt = remote?.ModifiedAt,
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
        _fieldLabels.Clear();
        _sourceHeaderChrome.Clear();

        MatrixHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        foreach (var _ in _sources)
            MatrixHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });

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
            var differs = !IsInfoField(field) && FieldRowDiffers(field);
            var label = new TextBlock
            {
                Text = differs ? $"{field} ●" : field,
                Margin = new Thickness(0, 0, 8, 8),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = differs ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = differs
                    ? DiffAmberBorder
                    : (Brush)FindResource("SecondaryTextBrush"),
                ToolTip = IsInfoField(field)
                    ? "Informational only — not used for match or Apply"
                    : differs
                        ? "Values differ across sources — click a cell to pick"
                        : null
            };
            _fieldLabels[field] = label;
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
        var matchState = GetSourceMatchState(source);
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 10) };

        var chrome = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(1.5),
            Background = matchState switch
            {
                SourceMatchState.MatchesVault => MatchGreenFill,
                SourceMatchState.Differs => DiffAmberFill,
                _ => (Brush)FindResource("SecondaryBackgroundBrush")
            },
            BorderBrush = matchState switch
            {
                SourceMatchState.MatchesVault => MatchGreenBorder,
                SourceMatchState.Differs => DiffAmberBorder,
                _ => (Brush)FindResource("BorderBrushColor")
            }
        };
        _sourceHeaderChrome[source.Id] = chrome;

        var titleRow = new StackPanel();
        titleRow.Children.Add(new TextBlock
        {
            Text = source.DisplayName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 15
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = matchState switch
            {
                SourceMatchState.Vault => "Current vault values",
                SourceMatchState.MatchesVault => "✓ Matches vault",
                SourceMatchState.Differs => "⚠ Differs from vault",
                SourceMatchState.Unavailable => "Unavailable — reconnect",
                _ => ""
            },
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = matchState switch
            {
                SourceMatchState.MatchesVault => MatchGreenBorder,
                SourceMatchState.Differs => DiffAmberBorder,
                _ => (Brush)FindResource("SecondaryTextBrush")
            }
        });
        chrome.Child = titleRow;
        panel.Children.Add(chrome);

        if (!source.Unavailable)
        {
            var useAll = new Button
            {
                Content = "Use all fields",
                FontSize = 12,
                Padding = new Thickness(8, 4, 8, 4),
                Style = (Style)FindResource("SecondaryButton"),
                Tag = source.Id,
                ToolTip = "Select every field from this source (then Apply)",
                HorizontalAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(useAll, $"Use all fields from {source.DisplayName}");
            useAll.Click += UseAllFromSource_Click;
            panel.Children.Add(useAll);

            if (!source.IsVault)
            {
                var unlink = new Button
                {
                    Content = "Unlink…",
                    FontSize = 12,
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 4, 0, 0),
                    Style = (Style)FindResource("TextActionButton"),
                    Tag = source.Id,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    ToolTip = "Not the same entry — remove this provider link"
                };
                unlink.Click += (_, _) =>
                {
                    _unlinkProviderId = source.Id;
                    UnlinkSelected_Click(unlink, new RoutedEventArgs());
                };
                panel.Children.Add(unlink);
            }
        }

        return panel;
    }

    private Border BuildFieldCell(string field, DiffSource source)
    {
        var display = FormatField(field, source, maskPassword: !_showPasswords);
        var infoOnly = IsInfoField(field);
        var isUrl = !infoOnly && field == "URL" && LooksLikeUrl(source.Url);
        var isPassword = !infoOnly && field == "Password" && !string.IsNullOrEmpty(source.Password) && !source.Unavailable;
        var panel = new DockPanel { LastChildFill = true };

        if (isPassword)
        {
            var reveal = new Button
            {
                Content = _showPasswords ? "Hide" : "Show",
                MinWidth = 44,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(6, 2, 6, 2),
                FontSize = 12,
                Style = (Style)FindResource("SecondaryButton"),
                ToolTip = _showPasswords ? "Hide passwords" : "Show passwords",
                Tag = "reveal"
            };
            AutomationProperties.SetName(reveal, _showPasswords ? "Hide passwords" : "Show passwords");
            DockPanel.SetDock(reveal, Dock.Right);
            reveal.Click += RevealPasswords_Click;
            panel.Children.Add(reveal);
        }
        else if (isUrl && !source.Unavailable)
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
            Cursor = infoOnly || source.Unavailable ? Cursors.Arrow : Cursors.Hand,
            Tag = (field, source.Id),
            MinHeight = 36,
            Opacity = source.Unavailable ? 0.55 : 1,
            ToolTip = infoOnly
                ? "Informational only — not used for match or Apply"
                : source.Unavailable
                    ? "Reconnect provider to compare"
                    : source.IsVault && IsEditableVaultField(field)
                        ? "Click to select · Slow second click to edit"
                        : "Click to use this value for Apply"
        };
        if (!infoOnly && !source.Unavailable)
            border.MouseLeftButtonUp += FieldCell_Click;
        return border;
    }

    private void FieldCell_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isEditing)
            return;
        if (sender is not Border { Tag: ValueTuple<string, string> tag } border)
            return;
        var (field, sourceId) = tag;
        if (IsInfoField(field))
            return;

        var alreadySelected = string.Equals(
            _fieldSource.GetValueOrDefault(field),
            sourceId,
            StringComparison.OrdinalIgnoreCase);
        var isVault = string.Equals(sourceId, VaultSourceId, StringComparison.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var slowSecondClick =
            alreadySelected &&
            isVault &&
            IsEditableVaultField(field) &&
            _lastSelectKey == (field, sourceId) &&
            e.ClickCount == 1 &&
            now - _lastSelectUtc >= RenameClickDelay;

        if (slowSecondClick)
        {
            var source = _sources.First(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));
            BeginInlineEdit(border, field, source);
            e.Handled = true;
            return;
        }

        _correctSourceId = null;
        _fieldSource[field] = sourceId;
        _lastSelectKey = (field, sourceId);
        _lastSelectUtc = now;
        HighlightSelections();
        e.Handled = true;
    }

    private void BeginInlineEdit(Border border, string field, DiffSource source)
    {
        if (!source.IsVault || !IsEditableVaultField(field) || source.Unavailable)
            return;

        _isEditing = true;
        _editField = field;
        _editBorder = border;
        _editSource = source;
        _editOriginal = GetEditableRaw(field, source);

        FrameworkElement editor;
        if (field == "Favorite")
        {
            var check = new CheckBox
            {
                Content = "Favorite",
                IsChecked = source.IsFavorite,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0)
            };
            check.KeyDown += InlineEditor_KeyDown;
            check.LostKeyboardFocus += InlineEditor_LostFocus;
            editor = check;
        }
        else
        {
            var box = new TextBox
            {
                Text = _editOriginal ?? "",
                TextWrapping = field is "Notes" or "URL" ? TextWrapping.Wrap : TextWrapping.NoWrap,
                AcceptsReturn = field == "Notes",
                MinHeight = field == "Notes" ? 64 : 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 2, 4, 2)
            };
            if (TryFindResource("DialogInput") is Style inputStyle)
                box.Style = inputStyle;
            box.KeyDown += InlineEditor_KeyDown;
            box.LostKeyboardFocus += InlineEditor_LostFocus;
            editor = box;
        }

        border.Child = editor;
        border.Cursor = Cursors.IBeam;
        border.ToolTip = "Enter to save · Esc to cancel";
        Dispatcher.BeginInvoke(() =>
        {
            editor.Focus();
            if (editor is TextBox tb)
                tb.SelectAll();
            Keyboard.Focus(editor);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void InlineEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            EndInlineEdit(commit: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && sender is not TextBox { AcceptsReturn: true })
        {
            EndInlineEdit(commit: true);
            e.Handled = true;
        }
    }

    private void InlineEditor_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_isEditing || _endingEdit)
            return;
        EndInlineEdit(commit: true);
    }

    private void EndInlineEdit(bool commit)
    {
        if (!_isEditing || _endingEdit || _editField is null || _editSource is null || _editBorder is null)
            return;

        _endingEdit = true;
        try
        {
            var field = _editField;
            var source = _editSource;
            var border = _editBorder;

            if (border.Child is UIElement editor)
            {
                editor.LostKeyboardFocus -= InlineEditor_LostFocus;
                editor.KeyDown -= InlineEditor_KeyDown;
            }

            if (commit)
            {
                if (border.Child is CheckBox check)
                    source.IsFavorite = check.IsChecked == true;
                else if (border.Child is TextBox box)
                    ApplyEditableRaw(source, field, box.Text);
            }
            else
            {
                ApplyEditableRaw(source, field, _editOriginal);
            }

            _isEditing = false;
            _editField = null;
            _editOriginal = null;
            _editBorder = null;
            _editSource = null;
            _fieldSource[field] = VaultSourceId;
            _lastSelectKey = (field, VaultSourceId);
            _lastSelectUtc = DateTime.UtcNow;

            // Rebuild so match colors / display (masked password) refresh from edited vault values.
            RebuildMatrixPreservingSelection();
        }
        finally
        {
            _endingEdit = false;
        }
    }

    private static string? GetEditableRaw(string field, DiffSource source) => field switch
    {
        "Title" => source.Title,
        "Username" => source.Username,
        "Password" => source.Password,
        "URL" => source.Url,
        "Notes" => source.Notes,
        "Favorite" => source.IsFavorite.ToString(),
        "TOTP" => source.Totp,
        _ => null
    };

    private static void ApplyEditableRaw(DiffSource source, string field, string? value)
    {
        var trimmed = value?.Trim() ?? "";
        switch (field)
        {
            case "Title":
                source.Title = trimmed;
                break;
            case "Username":
                source.Username = string.IsNullOrEmpty(trimmed) ? null : trimmed;
                break;
            case "Password":
                source.Password = value; // preserve intentional spaces
                break;
            case "URL":
                source.Url = string.IsNullOrEmpty(trimmed) ? null : trimmed;
                break;
            case "Notes":
                source.Notes = string.IsNullOrEmpty(value) ? null : value;
                break;
            case "Favorite":
                source.IsFavorite = bool.TryParse(trimmed, out var fav) && fav;
                break;
            case "TOTP":
                source.Totp = string.IsNullOrEmpty(trimmed) ? null : trimmed;
                break;
        }
    }

    private void UseAllFromSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string sourceId })
            return;
        _correctSourceId = sourceId;
        _unlinkProviderId = string.Equals(sourceId, VaultSourceId, StringComparison.OrdinalIgnoreCase)
            ? null
            : sourceId;
        foreach (var field in FieldKeys.Where(f => !IsInfoField(f)))
            _fieldSource[field] = sourceId;
        HighlightSelections();
    }

    private void HighlightSelections()
    {
        var brand = (Brush)FindResource("BrandBlueBrush");
        var neutralBg = (Brush)FindResource("SecondaryBackgroundBrush");
        var neutralBorder = (Brush)FindResource("BorderBrushColor");
        var vault = _sources.First(s => s.IsVault);

        foreach (var ((field, sourceId), cell) in _cells)
        {
            var source = _sources.First(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));
            var selected = !IsInfoField(field) &&
                           string.Equals(_fieldSource.GetValueOrDefault(field), sourceId, StringComparison.OrdinalIgnoreCase);
            var matchesVault = !IsInfoField(field) &&
                               (source.IsVault || FieldEquals(field, source, vault));
            var rowDiffers = !IsInfoField(field) && FieldRowDiffers(field);

            if (IsInfoField(field))
            {
                // Timestamps are informational only — no match/selection chrome.
                cell.Background = neutralBg;
                cell.BorderBrush = neutralBorder;
                cell.BorderThickness = new Thickness(1);
                continue;
            }

            if (source.IsVault)
            {
                cell.Background = neutralBg;
                cell.BorderBrush = selected ? brand : neutralBorder;
            }
            else if (matchesVault)
            {
                cell.Background = MatchGreenFill;
                cell.BorderBrush = selected ? brand : MatchGreenBorder;
            }
            else
            {
                cell.Background = DiffAmberFill;
                cell.BorderBrush = selected ? brand : DiffAmberBorder;
            }

            cell.BorderThickness = new Thickness(selected ? (rowDiffers ? 2.5 : 2) : 1);
        }

        foreach (var (field, label) in _fieldLabels)
        {
            var differs = !IsInfoField(field) && FieldRowDiffers(field);
            label.Text = differs ? $"{field} ●" : field;
            label.FontWeight = differs ? FontWeights.SemiBold : FontWeights.Normal;
            label.Foreground = differs
                ? DiffAmberBorder
                : (Brush)FindResource("SecondaryTextBrush");
        }
    }

    private bool FieldRowDiffers(string field)
    {
        if (IsInfoField(field))
            return false;
        var vault = _sources.First(s => s.IsVault);
        return _sources.Any(s =>
            !s.IsVault && !s.Unavailable && !FieldEquals(field, s, vault));
    }

    private SourceMatchState GetSourceMatchState(DiffSource source)
    {
        if (source.IsVault)
            return SourceMatchState.Vault;
        if (source.Unavailable)
            return SourceMatchState.Unavailable;

        var vault = _sources.First(s => s.IsVault);
        var anyDiff = FieldKeys
            .Where(f => !IsInfoField(f))
            .Any(f => !FieldEquals(f, source, vault));
        return anyDiff ? SourceMatchState.Differs : SourceMatchState.MatchesVault;
    }

    private static bool FieldEquals(string field, DiffSource a, DiffSource b)
    {
        var leftText = NormalizeCompare(field, GetRaw(field, a));
        var rightText = NormalizeCompare(field, GetRaw(field, b));
        return string.Equals(leftText, rightText, StringComparison.Ordinal);
    }

    private static string NormalizeCompare(string field, string? value)
    {
        var raw = (value ?? "").Trim();
        if (field is "Favorite")
            return bool.TryParse(raw, out var b) && b ? "true" : "false";
        if (field is "Username" or "Title" or "Host" or "URL")
            return raw.ToLowerInvariant();
        return raw;
    }

    private void RevealPasswords_Click(object sender, RoutedEventArgs e)
    {
        if (_isEditing)
            EndInlineEdit(commit: true);
        _showPasswords = !_showPasswords;
        RebuildMatrixPreservingSelection();
        e.Handled = true;
    }

    private void RebuildMatrixPreservingSelection()
    {
        if (_isEditing)
            EndInlineEdit(commit: true);

        var snapshot = _fieldSource.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var correct = _correctSourceId;
        var lastKey = _lastSelectKey;
        var lastUtc = _lastSelectUtc;
        BuildMatrix();
        foreach (var (field, sourceId) in snapshot)
            _fieldSource[field] = sourceId;
        _correctSourceId = correct;
        _lastSelectKey = lastKey;
        _lastSelectUtc = lastUtc;
        HighlightSelections();
    }

    private void SoftFill_Click(object sender, RoutedEventArgs e)
    {
        var vault = _sources.First(s => s.IsVault);
        foreach (var field in FieldKeys.Where(f => !IsInfoField(f)))
        {
            if (!string.IsNullOrWhiteSpace(GetRaw(field, vault)))
            {
                _fieldSource[field] = VaultSourceId;
                continue;
            }

            var donor = _sources.FirstOrDefault(s =>
                !s.IsVault && !s.Unavailable && !string.IsNullOrWhiteSpace(GetRaw(field, s)));
            _fieldSource[field] = donor?.Id ?? VaultSourceId;
        }

        _correctSourceId = null;
        HighlightSelections();
    }

    private async void UnlinkSelected_Click(object sender, RoutedEventArgs e)
    {
        var providerId = _unlinkProviderId ?? _correctSourceId;
        if (string.IsNullOrWhiteSpace(providerId) ||
            string.Equals(providerId, VaultSourceId, StringComparison.OrdinalIgnoreCase))
        {
            providerId = _sources.FirstOrDefault(s => !s.IsVault && s.IsMismatch)?.Id
                         ?? _sources.FirstOrDefault(s => !s.IsVault)?.Id;
        }

        if (string.IsNullOrWhiteSpace(providerId) ||
            string.Equals(providerId, VaultSourceId, StringComparison.OrdinalIgnoreCase))
        {
            AppDialog.Alert(this, "Use Unlink under a provider column.", "Unlink");
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
        var push = AppDialog.Confirm(
            this,
            "Apply selections to the vault?\n\nChoose Update all to also push the vault values to linked providers.",
            "Apply to vault",
            confirmText: "Update all",
            cancelText: "Vault only");

        try
        {
            // Snapshot DB state before mutate (inline Diff edits live on DiffSource until Apply).
            var beforeItem = await _vaultItems.GetByIdAsync(_item.Id) ?? _item;
            var before = ReconcileHistoryService.CaptureSnapshot(beforeItem);

            ApplySelectionsToItem(_item);
            await _vaultItems.SaveAsync(_item);
            await _import.ClearMismatchesForItemAsync(_item.Id);

            if (push)
                await _import.PushToLinkedProvidersAsync(_item);

            var providerIds = _sources
                .Where(s => !s.IsVault)
                .Select(s => s.Id)
                .ToList();
            await _history.RecordApplyAsync(
                _item.Id,
                before,
                new Dictionary<string, string>(_fieldSource, StringComparer.OrdinalIgnoreCase),
                pushedToProviders: push,
                providerIds);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppDialog.Alert(this, ex.Message, "Apply", AppDialogKind.Warning);
        }
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        var win = new ReconcileHistoryWindow(_item.Id, _item.Title) { Owner = this };
        if (win.ShowDialog() == true)
        {
            DialogResult = true;
            Close();
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
        if (totp is not null && totp != "••••")
            item.TotpSecret = string.IsNullOrWhiteSpace(totp) ? item.TotpSecret : totp;
        // Created/Updated are informational only — vault keeps its own timestamps on Save.
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
        "Created" => FormatTimestamp(source.CreatedAt),
        "Updated" => FormatTimestamp(source.ModifiedAt),
        "Username" => source.Username,
        "Password" => source.Password,
        "URL" => source.Url,
        "Host" => VaultImportService.NormalizeDomain(source.Url),
        "Notes" => source.Notes,
        "Favorite" => source.IsFavorite.ToString(),
        "TOTP" => source.Totp,
        _ => null
    };

    private static string? FormatTimestamp(DateTime? utc)
    {
        if (utc is null || utc == default)
            return null;
        var local = utc.Value.Kind == DateTimeKind.Local
            ? utc.Value
            : DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime();
        return local.ToString("yyyy-MM-dd HH:mm");
    }

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

        if (field is "Created" or "Updated")
            return string.IsNullOrEmpty(raw) ? "—" : raw;

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

    private enum SourceMatchState
    {
        Vault,
        MatchesVault,
        Differs,
        Unavailable
    }

    private sealed class DiffSource
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public bool IsVault { get; init; }
        public bool IsMismatch { get; init; }
        public bool Unavailable { get; init; }
        public string? Title { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? Url { get; set; }
        public string? Notes { get; set; }
        public bool IsFavorite { get; set; }
        public string? Totp { get; set; }
        public DateTime? CreatedAt { get; init; }
        public DateTime? ModifiedAt { get; init; }
    }
}
