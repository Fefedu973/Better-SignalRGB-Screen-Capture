using Better_SignalRGB_Screen_Capture.Helpers;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class DraggableSourceItem
{
    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Source == null || _isCropping) return;
        var page = FindParent<MainPage>(this);
        var model = page?.ViewModel;
        if (model == null) return;
        var modifiers = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            | Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        var multiSelect = modifiers.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!Source.IsSelected) page!.SelectSourceItem(Source, multiSelect);
        var count = Math.Max(1, model.SelectedSources.Count);
        var menu = CanvasContextMenuBuilder.Create(model, ActualTheme, count,
            (_, args) => CopyRequested?.Invoke(this, args), (_, args) => PasteRequested?.Invoke(this, args),
            (_, args) => CenterRequested?.Invoke(this, args), (_, args) => DeleteRequested?.Invoke(this, args),
            count == 1 ? (_, args) => EditRequested?.Invoke(this, args) : null,
            count == 1 ? (_, _) => EnterCropMode() : null);
        menu.ShowAt(this, e.GetPosition(this));
        e.Handled = true;
    }
}
