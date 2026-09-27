using Better_SignalRGB_Screen_Capture.Helpers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class GroupSelectionControl
{
    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var page = CanvasInteractionVisuals.FindParent<MainPage>(this);
        if (page == null) return;
        var model = page.ViewModel;
        var menu = CanvasContextMenuBuilder.Create(model, ActualTheme, SelectedSources.Count,
            (_, _) => model.CopySourceCommand.Execute(SelectedSources.ToList()),
            async (_, _) => await page.ExecuteUiOperationAsync("paste sources", () => model.PasteSourceCommand.ExecuteAsync(null)),
            async (_, _) => await page.ExecuteUiOperationAsync("center sources", () => model.CenterSourceCommand.ExecuteAsync(null)),
            async (_, _) => await page.ExecuteUiOperationAsync("delete sources", async () =>
            {
                var sources = SelectedSources.ToList();
                var dialog = new ContentDialog { Title = "Delete Multiple Sources",
                    Content = $"Are you sure you want to delete these {sources.Count} selected items?",
                    PrimaryButtonText = "Delete", CloseButtonText = "Cancel", XamlRoot = XamlRoot };
                if (await page.ShowSourceDialogAsync(dialog) == ContentDialogResult.Primary)
                    await model.DeleteSourceCommand.ExecuteAsync(sources);
            }));
        menu.ShowAt(this, e.GetPosition(this));
        e.Handled = true;
    }
}
