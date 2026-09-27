using Better_SignalRGB_Screen_Capture.Models;
using CommunityToolkit.Mvvm.Input;
using Windows.Foundation;

namespace Better_SignalRGB_Screen_Capture.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private async Task AddSourceAsync(SourceItem? newSource = null)
    {
        using var edit = BeginSourceEdit();
        if (edit == null) return;
        if (newSource != null)
        {
            SaveUndoState();

            // Find a good position for the new source on canvas based on its size
            var (x, y) = FindAvailableCanvasPosition(newSource.CanvasWidth, newSource.CanvasHeight);
            newSource.CanvasX = x;
            newSource.CanvasY = y;

            // Set preview state to match global preview toggle
            newSource.IsLivePreviewEnabled = IsPreviewing;

            Sources.Add(newSource);
            UpdateSelectedSources(new[] { newSource });
            await SaveSourcesAsync();

            await StartSourceWhenRecordingAsync(newSource);
        }
    }

    [RelayCommand]
    private async Task DeleteSourceAsync(object? parameter)
    {
        using var edit = BeginSourceEdit();
        if (edit == null) return;
        var candidates = parameter switch
        {
            SourceItem source => new[] { source },
            IEnumerable<SourceItem> sources => sources,
            _ => SelectedSources
        };
        var sourcesToDelete = candidates.Where(Sources.Contains).Distinct().ToArray();
        if (sourcesToDelete.Length == 0) return;
        SaveUndoState();
        foreach (var source in sourcesToDelete) await DeleteSingleSourceAsync(source);
        await SaveSourcesAsync();
    }

    private async Task DeleteSingleSourceAsync(SourceItem source)
    {
        if (Sources.Contains(source))
        {
            await _captureService.StopCaptureAsync(source);
            source.IsSelected = false;
            SelectedSources.Remove(source);
            Sources.Remove(source);
            await _mjpegStreamingService.NotifySourceRemovedAsync(source.Id);
            _kestrelApiService.RemoveSource(source.Id);
        }
    }

    [RelayCommand]
    private void CopySource(object? parameter)
    {
        _copiedSources.Clear();
        var sourcesToCopy = new List<SourceItem>();
        if (parameter is SourceItem source)
        {
            sourcesToCopy.Add(source);
        }
        else if (parameter is IEnumerable<SourceItem> sources)
        {
            sourcesToCopy.AddRange(sources);
        }
        else
        {
            sourcesToCopy.AddRange(SelectedSources);
        }

        if (sourcesToCopy.Any())
        {
            foreach (var s in sourcesToCopy)
            {
                _copiedSources.Add(s.Clone());
            }
        }
        PasteSourceCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanPasteSource))]
    private async Task PasteSourceAsync()
    {
        using var edit = BeginSourceEdit();
        if (edit == null) return;
        if (!_copiedSources.Any()) return;

        IsPasting = true;
        try
        {
            SaveUndoState();
            var newSelection = new List<SourceItem>();

            foreach (var copiedSource in _copiedSources)
            {
                var newSource = copiedSource.Clone();
                newSource.IsLocked = false;
                // Set preview state to match global preview toggle
                newSource.IsLivePreviewEnabled = IsPreviewing;

                Sources.Add(newSource);
                newSelection.Add(newSource);

                await StartSourceWhenRecordingAsync(newSource);
            }

            UpdateSelectedSources(newSelection);
            await SaveSourcesAsync();
        }
        finally
        {
            IsPasting = false;
        }
    }

    public bool CanPasteSource() => _copiedSources.Count > 0 && IsCanvasEditable;

    [RelayCommand]
    private async Task CenterSourceAsync(SourceItem? source)
    {
        if (!IsCanvasEditable) return;
        var sourcesToCenter = new List<SourceItem>();
        if (source != null)
        {
            sourcesToCenter.Add(source);
        }
        else
        {
            sourcesToCenter.AddRange(SelectedSources);
        }

        if (!sourcesToCenter.Any() || sourcesToCenter.Any(item => item.IsLocked)) return;
        SaveUndoState();

        var bounds = Rect.Empty;
        foreach (var item in sourcesToCenter)
        {
            var visible = GetVisibleAreaAabb(item);
            if (bounds.IsEmpty) bounds = visible;
            else bounds.Union(visible);
        }
        if (bounds.IsEmpty) return;
        var dx = 160 - (bounds.Left + bounds.Width / 2);
        var dy = 100 - (bounds.Top + bounds.Height / 2);
        foreach (var item in sourcesToCenter)
        {
            item.CanvasX += (int)Math.Round(dx);
            item.CanvasY += (int)Math.Round(dy);
        }

        await SaveSourcesAsync();
        SourcesMoved?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task ResetCanvasAsync()
    {
        using var edit = BeginSourceEdit();
        if (edit == null) return;
        SaveUndoState();
        await StopAllCapturesAsync();
        IsRecording = false;
        IsPaused = false;
        CanPause = false;
        await _mjpegStreamingService.ResetCanvasAsync();
        Sources.Clear();
        SelectedSources.Clear();
        await SaveSourcesAsync();
    }

    private (int x, int y) FindAvailableCanvasPosition(int sourceWidth = 200, int sourceHeight = 150)
    {
        const int gridSize = 20; // A slightly larger step
        const int canvasWidth = 320;
        const int canvasHeight = 200;
        const int margin = 10;

        // Ensure source fits within canvas with margin
        sourceWidth = Math.Min(sourceWidth, canvasWidth - 2 * margin);
        sourceHeight = Math.Min(sourceHeight, canvasHeight - 2 * margin);

        // Try to find a position in a grid pattern
        for (int row = 0; row * gridSize + sourceHeight + margin < canvasHeight; row++)
        {
            for (int col = 0; col * gridSize + sourceWidth + margin < canvasWidth; col++)
            {
                int x = col * gridSize + margin;
                int y = row * gridSize + margin;

                // Check if this position overlaps with any existing source
                var testRect = new Rect(x, y, sourceWidth, sourceHeight);
                bool occupied = Sources.Any(s =>
                {
                    var sourceRect = new Rect(s.CanvasX, s.CanvasY, s.CanvasWidth, s.CanvasHeight);
                    return RectsIntersect(testRect, sourceRect);
                });

                if (!occupied)
                    return (x, y);
            }
        }

        // If all grid positions are occupied, try to find any free space
        var random = new Random();
        for (int attempt = 0; attempt < 50; attempt++)
        {
            int x = random.Next(margin, Math.Max(margin + 1, canvasWidth - sourceWidth - margin));
            int y = random.Next(margin, Math.Max(margin + 1, canvasHeight - sourceHeight - margin));

            var testRect = new Rect(x, y, sourceWidth, sourceHeight);
            bool occupied = Sources.Any(s =>
            {
                var sourceRect = new Rect(s.CanvasX, s.CanvasY, s.CanvasWidth, s.CanvasHeight);
                return RectsIntersect(testRect, sourceRect);
            });

            if (!occupied)
                return (x, y);
        }

        // Last resort: cascade from top-left
        return (Math.Max(0, (canvasWidth - sourceWidth) / 2), Math.Max(0, (canvasHeight - sourceHeight) / 2));
    }

    [RelayCommand(CanExecute = nameof(CanTransformSelection))]
    private void BringToFront() => ReorderSelection(Helpers.LayerMove.Front);

    [RelayCommand(CanExecute = nameof(CanTransformSelection))]
    private void SendToBack() => ReorderSelection(Helpers.LayerMove.Back);

    [RelayCommand(CanExecute = nameof(CanTransformSelection))]
    private void BringForward() => ReorderSelection(Helpers.LayerMove.Forward);

    [RelayCommand(CanExecute = nameof(CanTransformSelection))]
    private void SendBackward() => ReorderSelection(Helpers.LayerMove.Backward);

    private void ReorderSelection(Helpers.LayerMove move)
    {
        if (!CanTransformSelection) return;
        var ordered = Helpers.SourceLayerOrdering.Reorder(Sources, SelectedSources.ToHashSet(), move);
        if (Sources.SequenceEqual(ordered)) return;
        SaveUndoState();
        for (var index = 0; index < ordered.Length; index++)
            if (!ReferenceEquals(Sources[index], ordered[index])) Sources.Move(Sources.IndexOf(ordered[index]), index);
        _ = SaveSourcesAsync();
    }

    private static bool RectsIntersect(Rect r1, Rect r2)
    {
        return r1.X < r2.X + r2.Width &&
               r1.X + r1.Width > r2.X &&
               r1.Y < r2.Y + r2.Height &&
               r1.Y + r1.Height > r2.Y;
    }
}
