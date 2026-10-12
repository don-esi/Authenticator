using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Authenticator.Data.Models;
using Authenticator.Logging;
using Authenticator.Services;

namespace Authenticator;

public partial class FolderWindow : Window
{
    private readonly DatabaseService _database = new();
    private string _color = AccountEdits.FolderColors[0];
    private bool _busy;

    public FolderWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        BuildColors();
        NameBox.Focus();
    }

    private void BuildColors()
    {
        ColorPanel.Children.Clear();
        foreach (var color in AccountEdits.FolderColors)
        {
            var selected = string.Equals(color, _color, StringComparison.OrdinalIgnoreCase);
            var button = new Button
            {
                Width = 36,
                Height = 36,
                Margin = new Thickness(0, 0, 8, 0),
                Tag = color,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Style = (Style)FindResource("IconButton"),
                Content = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(13),
                    Background = AccountEdits.BrushFromHex(color),
                    BorderBrush = selected ? AccountEdits.BrushFromHex("#1F2937") : Brushes.Transparent,
                    BorderThickness = new Thickness(selected ? 3 : 0)
                }
            };
            AutomationProperties.SetName(button, $"Folder color {color}");
            button.Click += (_, _) =>
            {
                _color = color;
                BuildColors();
            };
            ColorPanel.Children.Add(button);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorText.Text = "Folder name is required.";
            return;
        }

        _busy = true;
        SaveButton.IsEnabled = false;
        try
        {
            await _database.AddFolderAsync(new Folder
            {
                Name = name,
                Color = _color
            });
            LoggingConfiguration.LogInfo("Folder saved.", nameof(FolderWindow));
            DialogResult = true;
        }
        catch (Exception ex)
        {
            LoggingConfiguration.LogException(ex, "Failed to add folder.", nameof(FolderWindow));
            ErrorText.Text = ex.Message;
            _busy = false;
            SaveButton.IsEnabled = true;
        }
    }
}
