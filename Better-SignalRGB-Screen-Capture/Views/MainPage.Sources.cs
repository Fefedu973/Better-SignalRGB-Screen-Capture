using Better_SignalRGB_Screen_Capture.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScreenRecorderLib;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class MainPage
{
    private async void Add_Sources(object sender, RoutedEventArgs e) =>
        await ExecuteUiOperationAsync("add a source", AddSourceAsync);

    private async Task AddSourceAsync()
    {
        if (!ViewModel.IsCanvasEditable) return;
        var dlg = new AddSourceDialog { XamlRoot = XamlRoot };
        if (await ShowSourceDialogAsync(dlg) == ContentDialogResult.Primary)
        {
            var newSource = await CreateSourceFromDialogAsync(dlg);
            if (newSource != null)
            {
                await ViewModel.AddSourceCommand.ExecuteAsync(newSource);
            }
        }
    }

    private async Task<SourceItem?> CreateSourceFromDialogAsync(AddSourceDialog dialog)
    {
        var source = new SourceItem
        {
            Name = string.IsNullOrWhiteSpace(dialog.FriendlyName) ? $"Source {ViewModel.Sources.Count + 1}" : dialog.FriendlyName,
            Type = dialog.SelectedSourceType
        };
        UpdateSourceFromDialog(source, dialog);
        if (string.IsNullOrWhiteSpace(dialog.FriendlyName)) source.Name = $"Source {ViewModel.Sources.Count + 1}";
        if (source.Type == SourceType.Region && source.RegionBounds is not { Width: > 0, Height: > 0 }) return null;

        // Resolve native dimensions before adding the source. Recording no longer changes
        // layout, so starting a capture cannot overwrite a user-edited size or undo history.
        var (width, height) = await Task.Run(() => GetInitialSourceSize(source));
        var scale = Math.Min(1, Math.Min(200.0 / width, 120.0 / height));
        source.CanvasWidth = Math.Max(1, (int)Math.Round(width * scale));
        source.CanvasHeight = Math.Max(1, (int)Math.Round(height * scale));
        return source;
    }

    private static (double width, double height) GetInitialSourceSize(SourceItem source)
    {
        try
        {
            if (source.Type == SourceType.Region && source.RegionBounds is { Width: > 0, Height: > 0 } region)
                return (region.Width, region.Height);
            if (source.Type == SourceType.Website) return (source.WebsiteWidth, source.WebsiteHeight);
            RecordingSourceBase? recordingSource = null;
            if (source.Type is SourceType.Monitor or SourceType.WallpaperEngine)
            {
                var display = Recorder.GetDisplays().FirstOrDefault(d => d.DeviceName == source.MonitorDeviceId);
                if (display?.OutputSize is { Width: > 0, Height: > 0 } size) return (size.Width, size.Height);
            }
            else if (source.Type is SourceType.Process or SourceType.Webcam)
                recordingSource = Helpers.CaptureSourceFactory.Create(source).SingleOrDefault();
            if (recordingSource != null)
            {
                var dimensions = Recorder.GetOutputDimensionsForRecordingSources(new List<RecordingSourceBase> { recordingSource });
                if (dimensions.CombinedOutputSize is { Width: > 0, Height: > 0 } size) return (size.Width, size.Height);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not resolve initial source dimensions: {ex.Message}");
        }
        return (160, 90);
    }

    private void SourcesListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.OfType<SourceItem>().Any(source => source.IsLocked)) { e.Cancel = true; return; }
        _isReorderingSources = true;
        ViewModel.SaveUndoState();
    }

    private async void SourcesListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e) =>
        await ExecuteUiOperationAsync("save source order", CompleteSourceReorderAsync);

    private async Task CompleteSourceReorderAsync()
    {
        _isReorderingSources = false;
        UpdateListViewSelection();
        await ViewModel.SaveSourcesAsync();
    }

    private async void OnSourceEditRequested(DraggableSourceItem sender, RoutedEventArgs e) =>
        await ExecuteUiOperationAsync("edit the source", () => EditSourceAsync(sender.Source));

    private async Task EditSourceAsync(SourceItem sourceToEdit)
    {
        if (!ViewModel.IsCanvasEditable || !ViewModel.Sources.Contains(sourceToEdit)) return;
        var dialog = new AddSourceDialog(sourceToEdit)
        {
            XamlRoot = this.XamlRoot,
            Title = "Edit Source"
        };

        var result = await ShowSourceDialogAsync(dialog);

        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.ApplySourceEditAsync(sourceToEdit, source => UpdateSourceFromDialog(source, dialog));

            // The canvas needs to be updated if the source type changed display text
            UpdateCanvas();
        }
    }

    private void UpdateSourceFromDialog(SourceItem source, AddSourceDialog dialog)
    {
        source.Name = dialog.FriendlyName ?? "Unnamed Source";
        source.Type = dialog.SelectedSourceType;

        switch (source.Type)
        {
            case SourceType.Monitor:
            case SourceType.WallpaperEngine:
                source.MonitorDeviceId = dialog.SelectedMonitorDeviceId;
                break;
            case SourceType.Process:
                source.ProcessId = dialog.SelectedProcessId;
                source.ProcessPath = dialog.SelectedProcessPath;
                source.WindowHandle = dialog.SelectedWindowHandle;
                source.WindowTitle = dialog.SelectedWindowTitle;
                break;
            case SourceType.Region:
                source.RegionBounds = dialog.SelectedRegion;
                break;
            case SourceType.Webcam:
                source.WebcamDeviceId = dialog.SelectedWebcamDeviceId;
                source.WebcamFormatId = dialog.SelectedWebcamFormatId;
                break;
            case SourceType.Website:
                source.WebsiteUrl = dialog.WebsiteUrl;

                // Update enhanced website properties
                source.WebsiteZoom = dialog.WebsiteZoom;
                source.WebsiteRefreshInterval = dialog.WebsiteRefreshInterval;
                source.WebsiteUserAgent = dialog.WebsiteUserAgent;
                source.WebsiteWidth = dialog.WebsiteWidth;
                source.WebsiteHeight = dialog.WebsiteHeight;
                source.WebsiteNavigationState = dialog.WebsiteNavigationState;
                break;
        }
    }

    private async void OnSourceDeleteRequested(DraggableSourceItem sender, RoutedEventArgs e) =>
        await ExecuteUiOperationAsync("delete sources", () => DeleteRequestedSourceAsync(sender));

    private async Task DeleteRequestedSourceAsync(DraggableSourceItem sender)
    {
        if (sender.Source == null) return;

        // If the item to be deleted is part of a multiple selection, ask to delete all selected items.
        if (ViewModel.SelectedSources.Count > 1 && ViewModel.SelectedSources.Contains(sender.Source))
        {
            var dialog = new ContentDialog
            {
                Title = "Delete Multiple Sources",
                Content = $"You have {ViewModel.SelectedSources.Count} sources selected. Do you want to delete all of them?",
                PrimaryButtonText = "Delete All",
                SecondaryButtonText = "Delete Only This",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };

            var result = await ShowSourceDialogAsync(dialog);
            if (result == ContentDialogResult.Primary)
            {
                await ViewModel.DeleteSourceCommand.ExecuteAsync(ViewModel.SelectedSources.ToList());
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await ViewModel.DeleteSourceCommand.ExecuteAsync(sender.Source);
            }
        }
        else
        {
            var dialog = new ContentDialog
            {
                Title = "Delete Source",
                Content = $"Are you sure you want to delete '{sender.Source.DisplayName}'?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };

            if (await ShowSourceDialogAsync(dialog) == ContentDialogResult.Primary)
            {
                await ViewModel.DeleteSourceCommand.ExecuteAsync(sender.Source);
            }
        }
    }

    private void OnSourceCopyRequested(DraggableSourceItem sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedSources.Count > 1 && ViewModel.SelectedSources.Contains(sender.Source))
        {
            ViewModel.CopySourceCommand.Execute(ViewModel.SelectedSources.ToList());
        }
        else if (sender.Source != null)
        {
            ViewModel.CopySourceCommand.Execute(sender.Source);
        }
    }

    private async void OnSourcePasteRequested(DraggableSourceItem sender, RoutedEventArgs e) =>
        await ExecuteUiOperationAsync("paste sources", () => ViewModel.PasteSourceCommand.ExecuteAsync(null));

    private async void OnSourceCenterRequested(DraggableSourceItem sender, RoutedEventArgs e) =>
        await ExecuteUiOperationAsync("center sources", () => CenterRequestedSourceAsync(sender));

    private async Task CenterRequestedSourceAsync(DraggableSourceItem sender)
    {
        // If the item is part of a multi-selection, pass null to the command
        // to indicate that the whole selection should be centered as a group.
        if (ViewModel.SelectedSources.Count > 1 && sender.Source.IsSelected)
        {
            await ViewModel.CenterSourceCommand.ExecuteAsync(null);
        }
        else
        {
            // Otherwise, just center the single source that was clicked.
            await ViewModel.CenterSourceCommand.ExecuteAsync(sender.Source);
        }
    }

    private async void DeleteSource_Click(object sender, RoutedEventArgs e) =>
        await ExecuteUiOperationAsync("delete the source", () => DeleteSourceAsync(sender));

    private async Task DeleteSourceAsync(object sender)
    {
        if ((sender as FrameworkElement)?.Tag is SourceItem source)
        {
            var dialog = new ContentDialog
            {
                Title = "Delete Source",
                Content = $"Are you sure you want to delete '{source.DisplayName}'?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };

            if (await ShowSourceDialogAsync(dialog) == ContentDialogResult.Primary)
            {
                await ViewModel.DeleteSourceCommand.ExecuteAsync(source);
            }
        }
    }
}
