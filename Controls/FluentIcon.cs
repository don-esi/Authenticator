namespace Authenticator.Controls;

/// <summary>
/// Provides a font icon control for displaying Fluent System icons using the Segoe Fluent Icons font family.
/// </summary>
/// <remarks>Extends <see cref="FontIcon"/> and automatically configures the font family and glyph when the icon
/// is set.
/// Usage:
/// <local:FluentIconEx Icon="Permissions" FontSize="24" />
/// <local:FluentIconEx Icon = "GlobalNavigationButton" />
/// </remarks>
public sealed partial class FluentIcon : FontIcon
{
    public static readonly DependencyProperty FavoriteProperty =
            DependencyProperty.Register(nameof(Favorite), typeof(FluentIconFavorites), typeof(FluentIcon),
                new PropertyMetadata(FluentIconFavorites.None, OnIconChanged));

    public static readonly DependencyProperty SymbolProperty =
            DependencyProperty.Register(nameof(Symbol), typeof(FluentIcons), typeof(FluentIcon),
                new PropertyMetadata(FluentIcons.None, OnIconChanged));

    public FluentIconFavorites Favorite
    {
        get => (FluentIconFavorites)GetValue(FavoriteProperty);
        set => SetValue(FavoriteProperty, value);
    }

    public FluentIcons Symbol
    {
        get => (FluentIcons)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    private static void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FluentIcon control)
        {
            control.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons");

            if (e.NewValue is FluentIcons icon && icon != FluentIcons.None)
            {
                control.Glyph = ((char)(int)icon).ToString();
            }
            else if (e.NewValue is FluentIconFavorites favorite && favorite != FluentIconFavorites.None)
            {
                control.Glyph = ((char)(int)favorite).ToString();
            }
        }
    }
}