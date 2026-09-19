using CommunityToolkit.Mvvm.ComponentModel;

namespace MFAAvalonia.ViewModels.Pages;

public partial class PipelineEditorViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _status = "正在准备 MaaPipelineEditor…";
}
