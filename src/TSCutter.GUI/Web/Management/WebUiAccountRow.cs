using CommunityToolkit.Mvvm.ComponentModel;

namespace TSCutter.GUI.Web;

internal sealed class WebUiAccountRow(string id, string userName) : ObservableObject
{
    private string status = string.Empty;
    private bool hasChanges;
    public string Id { get; } = id;
    public string UserName { get; } = userName;
    public string Status
    {
        get => status; set => SetProperty(ref status, value);
    }
    public bool HasChanges
    {
        get => hasChanges; set => SetProperty(ref hasChanges, value);
    }
}
