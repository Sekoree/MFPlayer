using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using HaCue2.Controls;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class AudioView : UserControl
{
    public AudioView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnPatchGesture(object? sender, MatrixGesture gesture)
    {
        if (DataContext is AudioViewModel audio)
            audio.ApplyPatchGesture(gesture);
    }

    /// <summary>
    /// Closes the drag's coalescing group when the pointer comes up, so the whole drag is one undo
    /// step and the next drag on the same cell starts a new one.
    /// </summary>
    private void OnPatchGestureEnded(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is AudioViewModel audio)
            audio.EndPatchGesture();
    }
}
