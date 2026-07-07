using CommunityToolkit.Mvvm.ComponentModel;
using System.Linq;
using Authenticator.Data.Models;

namespace Authenticator.ViewModels;

/// <summary>
/// ViewModel for account items with live TOTP code updates
/// </summary>
public partial class AccountItemViewModel : ObservableObject
{
    private readonly Account _account;
    private string _currentCode = "------";
    private double _progress;
    private int _secondsRemaining;

    public string CurrentCode
    {
        get => _currentCode;
        set => SetProperty(ref _currentCode, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public int SecondsRemaining
    {
        get => _secondsRemaining;
        set => SetProperty(ref _secondsRemaining, value);
    }

    public AccountItemViewModel(Account account)
    {
        _account = account;
    }

    public Account Account => _account;

    public int Id => _account.Id;
    public string ServiceName => _account.ServiceName;
    public string Username => _account.Username;
    public int BackupCodeCount => _account.BackupCodes?.Count(bc => !bc.IsUsed && !bc.IsDeleted) ?? 0;

    public void UpdateCode(string code, double progress, int secondsRemaining)
    {
        CurrentCode = code;
        Progress = progress;
        SecondsRemaining = secondsRemaining;
    }
}
