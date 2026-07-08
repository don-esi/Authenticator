namespace Authenticator.Controls;

public sealed partial class TotpProgressBar : UserControl
{
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(
            nameof(Progress),
            typeof(double),
            typeof(TotpProgressBar),
            new PropertyMetadata(0.0, OnProgressChanged));

    public static readonly DependencyProperty SecondsRemainingProperty =
        DependencyProperty.Register(
            nameof(SecondsRemaining),
            typeof(int),
            typeof(TotpProgressBar),
            new PropertyMetadata(0));

    public TotpProgressBar()
    {
        InitializeComponent();
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public int SecondsRemaining
    {
        get => (int)GetValue(SecondsRemainingProperty);
        set => SetValue(SecondsRemainingProperty, value);
    }

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TotpProgressBar control)
        {
            control.UpdateProgressBar();
        }
    }

    private void UpdateProgressBar()
    {
        // Progress bar will be updated via binding in XAML
    }
}