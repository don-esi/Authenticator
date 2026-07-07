using System;
using Authenticator.Data.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Authenticator.Controls;

public sealed partial class AccountCard : UserControl
{
    public static readonly DependencyProperty AccountDataProperty =
        DependencyProperty.Register(
            nameof(AccountData),
            typeof(Account),
            typeof(AccountCard),
            new PropertyMetadata(null));

    public static readonly DependencyProperty CurrentCodeProperty =
        DependencyProperty.Register(
            nameof(CurrentCode),
            typeof(string),
            typeof(AccountCard),
            new PropertyMetadata("------"));

    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(
            nameof(Progress),
            typeof(double),
            typeof(AccountCard),
            new PropertyMetadata(0.0));

    public static readonly DependencyProperty SecondsRemainingProperty =
        DependencyProperty.Register(
            nameof(SecondsRemaining),
            typeof(int),
            typeof(AccountCard),
            new PropertyMetadata(0));

    public AccountCard()
    {
        InitializeComponent();
    }

    public event EventHandler<AccountCardActionEventArgs>? CopyRequested;

    public event EventHandler<AccountCardActionEventArgs>? MoreRequested;

    public Account? AccountData
    {
        get => (Account?)GetValue(AccountDataProperty);
        set => SetValue(AccountDataProperty, value);
    }

    public string CurrentCode
    {
        get => (string)GetValue(CurrentCodeProperty);
        set => SetValue(CurrentCodeProperty, value);
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

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (AccountData is null || string.IsNullOrWhiteSpace(CurrentCode))
        {
            return;
        }

        CopyRequested?.Invoke(this, new AccountCardActionEventArgs(AccountData, CurrentCode));
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (AccountData is null)
        {
            return;
        }

        MoreRequested?.Invoke(this, new AccountCardActionEventArgs(AccountData, CurrentCode));
    }
}

public sealed class AccountCardActionEventArgs(Account account, string currentCode) : EventArgs
{
    public Account Account { get; } = account;

    public string CurrentCode { get; } = currentCode;
}
