using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Authenticator.Data.Models;

namespace Authenticator.Controls;

public sealed partial class FolderChip : UserControl
{
    public static readonly DependencyProperty FolderDataProperty =
        DependencyProperty.Register(
            nameof(FolderData),
            typeof(Folder),
            typeof(FolderChip),
            new PropertyMetadata(null));

    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(
            nameof(IsSelected),
            typeof(bool),
            typeof(FolderChip),
            new PropertyMetadata(false));

    public event RoutedEventHandler? Click;

    public FolderChip()
    {
        this.InitializeComponent();
    }

    public Folder? FolderData
    {
        get => (Folder?)GetValue(FolderDataProperty);
        set => SetValue(FolderDataProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    private void ChipButton_Click(object sender, RoutedEventArgs e)
    {
        Click?.Invoke(this, e);
    }
}
