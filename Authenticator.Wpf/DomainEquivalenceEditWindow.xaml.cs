using System.Windows;
using Authenticator.Data.Models;
using Authenticator.Services;

namespace Authenticator;

public partial class DomainEquivalenceEditWindow : Window
{
    private readonly DomainEquivalenceGroup _group;
    private readonly DomainEquivalenceService _service = new();

    public DomainEquivalenceEditWindow(DomainEquivalenceGroup? existing = null)
    {
        InitializeComponent();
        _group = existing ?? new DomainEquivalenceGroup();
        TitleText.Text = existing is null ? "Add domain group" : "Edit domain group";
        NameBox.Text = _group.Name ?? "";
        DomainsBox.Text = DomainEquivalenceService.DomainsToEditText(_group.DomainsJson);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _group.Name = NameBox.Text.Trim();
            _group.DomainsJson = DomainsBox.Text;
            await _service.SaveAsync(_group);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppDialog.Alert(this, ex.Message, "Equivalent domains", AppDialogKind.Warning);
        }
    }
}
