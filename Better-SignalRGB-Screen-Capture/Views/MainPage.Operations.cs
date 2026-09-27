using Better_SignalRGB_Screen_Capture.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class MainPage
{
    private bool _sourceDialogOpen;

    private async void ResetCanvas_Click(object sender, RoutedEventArgs args) =>
        await ExecuteUiOperationAsync("reset the canvas", () => ViewModel.ResetCanvasCommand.ExecuteAsync(null));

    private async void Undo_Click(object sender, RoutedEventArgs args) =>
        await ExecuteUiOperationAsync("undo the canvas change", () => ViewModel.UndoCommand.ExecuteAsync(null));

    private async void Redo_Click(object sender, RoutedEventArgs args) =>
        await ExecuteUiOperationAsync("redo the canvas change", () => ViewModel.RedoCommand.ExecuteAsync(null));

    internal async Task ExecuteUiOperationAsync(string operation, Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { ReportUiError(operation, exception); }
    }

    private void ExecuteUiOperation(string operation, Action action)
    {
        try { action(); }
        catch (Exception exception) { ReportUiError(operation, exception); }
    }

    private void ReportUiError(string operation, Exception exception)
    {
        ApplicationErrorLog.Write($"MainPage: {operation}", exception);
        ViewModel.StatusMessage = $"Could not {operation}: {exception.Message}";
    }

    internal async Task<ContentDialogResult> ShowSourceDialogAsync(ContentDialog dialog)
    {
        // Keyboard and item events can both request a modal dialog in one turn.
        // WinUI throws when a second ContentDialog is shown on the same XamlRoot.
        if (_sourceDialogOpen || !IsLoaded || XamlRoot == null) return ContentDialogResult.None;
        _sourceDialogOpen = true;
        try
        {
            dialog.XamlRoot = XamlRoot;
            return await dialog.ShowAsync();
        }
        finally { _sourceDialogOpen = false; }
    }
}
