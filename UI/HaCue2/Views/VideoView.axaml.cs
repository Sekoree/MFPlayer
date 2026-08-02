using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class VideoView : UserControl
{
    public VideoView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>"Edit ›" beside an output's mapping toggle jumps to the Mapping tab already scoped to
    /// that output — mapping is always the mapping OF one output, never a global mode.</summary>
    private void OnEditMapping(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VideoViewModel video)
            video.SelectedTab = VideoViewModel.MappingTab;
    }
}
