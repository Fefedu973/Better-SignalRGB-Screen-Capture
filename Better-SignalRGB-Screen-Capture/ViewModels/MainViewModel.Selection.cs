using Better_SignalRGB_Screen_Capture.Models;
using CommunityToolkit.Mvvm.Input;
using Windows.Foundation;

namespace Better_SignalRGB_Screen_Capture.ViewModels;

public partial class MainViewModel
{
    private bool _updatingSelection;

    public void UpdateSelectedSources(IEnumerable<SourceItem> newSelection)
    {
        var selected = newSelection.Where(Sources.Contains).Distinct().ToArray();
        var selectedSet = selected.ToHashSet();
        foreach (var source in Sources) source.IsSelected = selectedSet.Contains(source);
        if (SelectedSources.SequenceEqual(selected)) return;

        // Notify bound properties/commands once per selection operation, not per item.
        _updatingSelection = true;
        try
        {
            foreach (var stale in SelectedSources.Where(source => !selectedSet.Contains(source)).ToArray()) SelectedSources.Remove(stale);
            for (var index = 0; index < selected.Length; index++)
            {
                var current = SelectedSources.IndexOf(selected[index]);
                if (current < 0) SelectedSources.Insert(index, selected[index]);
                else if (current != index) SelectedSources.Move(current, index);
            }
        }
        finally { _updatingSelection = false; }
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        if (_updatingSelection) return;
        NotifySelectionLocks();
        OnPropertyChanged(nameof(IsSourceSelected));
        OnPropertyChanged(nameof(IsSingleSelect));
        OnPropertyChanged(nameof(IsMultiSelect));
        OnPropertyChanged(nameof(SelectedSourceName));
        OnPropertyChanged(nameof(SelectedSourceOpacity));
        OnPropertyChanged(nameof(SelectedSourceWidth));
        OnPropertyChanged(nameof(SelectedSourceHeight));
        OnPropertyChanged(nameof(SelectedSourceX));
        OnPropertyChanged(nameof(SelectedSourceY));

        OnPropertyChanged(nameof(SelectedSourceIsMirroredHorizontally));
        OnPropertyChanged(nameof(SelectedSourceIsMirroredVertically));
        OnPropertyChanged(nameof(SelectedSourceRotation));
        OnPropertyChanged(nameof(SelectedSourceCropLeftPct));
        OnPropertyChanged(nameof(SelectedSourceCropRightPct));
        OnPropertyChanged(nameof(SelectedSourceCropTopPct));
        OnPropertyChanged(nameof(SelectedSourceCropBottomPct));
        OnPropertyChanged(nameof(SelectedSourceCropRotation));

        AlignLeftCommand.NotifyCanExecuteChanged();
        AlignRightCommand.NotifyCanExecuteChanged();
        AlignCenterCommand.NotifyCanExecuteChanged();
        AlignTopCommand.NotifyCanExecuteChanged();
        AlignBottomCommand.NotifyCanExecuteChanged();
        AlignMiddleCommand.NotifyCanExecuteChanged();
        BringToFrontCommand.NotifyCanExecuteChanged();
        SendToBackCommand.NotifyCanExecuteChanged();
        BringForwardCommand.NotifyCanExecuteChanged();
        SendBackwardCommand.NotifyCanExecuteChanged();
    }

    public bool IsSourceSelected => SelectedSources.Any();
    public bool IsMultiSelect => SelectedSources.Count > 1;
    public bool IsSingleSelect => SelectedSources.Count == 1;

    public async Task NudgeSelectionAsync(int deltaX, int deltaY)
    {
        if (!CanTransformSelection) return;
        var bounds = Rect.Empty;
        foreach (var source in SelectedSources)
        {
            var visible = GetVisibleAreaAabb(source);
            if (visible.IsEmpty) continue;
            if (bounds.IsEmpty) bounds = visible;
            else bounds.Union(visible);
        }
        if (bounds.IsEmpty) return;
        static int Clamp(int delta, double first, double last, int size)
        {
            if (delta == 0) return 0;
            var minimum = Math.Ceiling(-first - 0.001);
            var maximum = Math.Floor(size - last + 0.001);
            return minimum > maximum ? 0 : (int)Math.Clamp(delta, minimum, maximum);
        }
        deltaX = Clamp(deltaX, bounds.Left, bounds.Right, 320);
        deltaY = Clamp(deltaY, bounds.Top, bounds.Bottom, 200);
        if (deltaX == 0 && deltaY == 0) return;
        SaveUndoState();
        foreach (var source in SelectedSources)
        {
            source.CanvasX += deltaX;
            source.CanvasY += deltaY;
        }
        SourcesMoved?.Invoke(this, EventArgs.Empty);
        await SaveSourcesAsync();
    }

    #region Selection Properties

    public string? SelectedSourceName
    {
        get
        {
            if (SelectedSources.Count == 0) return null;
            if (SelectedSources.Count == 1) return SelectedSources[0].Name;

            var firstName = SelectedSources[0].Name;
            return SelectedSources.Skip(1).All(s => s.Name == firstName) ? firstName : "Multiple Values";
        }
        set
        {
            if (IsCanvasEditable && value != null && SelectedSources.Count > 0 && value != "Multiple Values" && SelectedSources.Any(source => source.Name != value))
            {
                SaveUndoState();
                foreach (var source in SelectedSources)
                {
                    source.Name = value;
                }
                _ = SaveSourcesAsync();
                OnPropertyChanged();
            }
        }
    }

    public double SelectedSourceOpacity
    {
        get => GetSharedValue(source => source.Opacity, 1);
        set
        {
            if (double.IsFinite(value)) SetSelectedValue(source => source.Opacity,
                _ => Math.Clamp(value, 0, 1), (source, opacity) => source.Opacity = opacity, layout: false);
        }
    }

    private double GetSharedValue(Func<SourceItem, double> selector, double fallback = 0)
    {
        if (SelectedSources.Count == 0) return fallback;
        var first = selector(SelectedSources[0]);
        return SelectedSources.All(source => Math.Abs(selector(source) - first) < 0.001) ? first : fallback;
    }

    private void SetSelectedValue<T>(Func<SourceItem, T> selector, Func<SourceItem, T> candidate, Action<SourceItem, T> apply, bool layout = true)
    {
        if (!IsCanvasEditable || (layout && !CanTransformSelection)) return;
        var changes = SelectedSources.Select(source => (Source: source, Value: candidate(source)))
            .Where(change => !EqualityComparer<T>.Default.Equals(selector(change.Source), change.Value)).ToArray();
        if (changes.Length == 0) return;
        SaveUndoState();
        foreach (var change in changes) apply(change.Source, change.Value);
        _ = SaveSourcesAsync();
    }

    public double SelectedSourceWidth
    {
        get => SelectedSources.FirstOrDefault()?.CanvasWidth ?? 0;
        set => SetSelectedDimension(value, isWidth: true);
    }

    public double SelectedSourceHeight
    {
        get => SelectedSources.FirstOrDefault()?.CanvasHeight ?? 0;
        set => SetSelectedDimension(value, isWidth: false);
    }

    private void SetSelectedDimension(double value, bool isWidth)
    {
        if (!IsCanvasEditable || _isUpdatingDimensions || !double.IsFinite(value) || SelectedSources.Count == 0) return;
        var dimension = (int)Math.Clamp(Math.Round(value), 1, isWidth ? 7680 : 4320);
        _isUpdatingDimensions = true;
        try
        {
            SetSelectedValue(source => (source.CanvasWidth, source.CanvasHeight), source =>
            {
                var width = isWidth ? dimension : source.CanvasWidth;
                var height = isWidth ? source.CanvasHeight : dimension;
                if (IsAspectRatioLocked)
                {
                    var ratio = (double)source.CanvasWidth / source.CanvasHeight;
                    if (isWidth) height = Math.Max(1, (int)Math.Round(width / ratio));
                    else width = Math.Max(1, (int)Math.Round(height * ratio));
                }
                return width <= 7680 && height <= 4320 && FitsInCanvas(source.CanvasX, source.CanvasY, width, height, source.Rotation, source)
                    ? (width, height) : (source.CanvasWidth, source.CanvasHeight);
            }, (source, size) => { source.CanvasWidth = size.Item1; source.CanvasHeight = size.Item2; });
        }
        finally { _isUpdatingDimensions = false; }
        // NumberBox must display accepted geometry after rounding or rejecting a request.
        OnPropertyChanged(nameof(SelectedSourceWidth));
        OnPropertyChanged(nameof(SelectedSourceHeight));
    }

    public double SelectedSourceX
    {
        get => SelectedSources.FirstOrDefault()?.CanvasX ?? 0;
        set => SetSelectedPosition(value, horizontal: true);
    }

    public double SelectedSourceY
    {
        get => SelectedSources.FirstOrDefault()?.CanvasY ?? 0;
        set => SetSelectedPosition(value, horizontal: false);
    }

    private void SetSelectedPosition(double value, bool horizontal)
    {
        if (!IsCanvasEditable || _isUpdatingDimensions || !double.IsFinite(value) || value < int.MinValue || value > int.MaxValue) return;
        var position = (int)Math.Round(value);
        SetSelectedValue(source => horizontal ? source.CanvasX : source.CanvasY,
            source => FitsInCanvas(horizontal ? position : source.CanvasX, horizontal ? source.CanvasY : position,
                source.CanvasWidth, source.CanvasHeight, source.Rotation, source)
                    ? position : horizontal ? source.CanvasX : source.CanvasY,
            (source, coordinate) => { if (horizontal) source.CanvasX = coordinate; else source.CanvasY = coordinate; });
        OnPropertyChanged(horizontal ? nameof(SelectedSourceX) : nameof(SelectedSourceY));
    }

    public double SelectedSourceCropLeftPct
    {
        get => GetSharedValue(source => source.CropLeftPct) * 100;
        set => SetSelectedCrop(value, source => source.CropLeftPct, source => source.CropRightPct,
            (source, crop) => source.CropLeftPct = crop);
    }

    public double SelectedSourceCropRightPct
    {
        get => GetSharedValue(source => source.CropRightPct) * 100;
        set => SetSelectedCrop(value, source => source.CropRightPct, source => source.CropLeftPct,
            (source, crop) => source.CropRightPct = crop);
    }

    public double SelectedSourceCropTopPct
    {
        get => GetSharedValue(source => source.CropTopPct) * 100;
        set => SetSelectedCrop(value, source => source.CropTopPct, source => source.CropBottomPct,
            (source, crop) => source.CropTopPct = crop);
    }

    public double SelectedSourceCropBottomPct
    {
        get => GetSharedValue(source => source.CropBottomPct) * 100;
        set => SetSelectedCrop(value, source => source.CropBottomPct, source => source.CropTopPct,
            (source, crop) => source.CropBottomPct = crop);
    }

    private void SetSelectedCrop(double value, Func<SourceItem, double> selector,
        Func<SourceItem, double> opposite, Action<SourceItem, double> apply)
    {
        if (!double.IsFinite(value)) return;
        SetSelectedValue(selector, source => Math.Clamp(value / 100, 0, Math.Max(0, 0.99 - opposite(source))), apply);
        OnPropertyChanged(nameof(SelectedSourceCropLeftPct));
        OnPropertyChanged(nameof(SelectedSourceCropRightPct));
        OnPropertyChanged(nameof(SelectedSourceCropTopPct));
        OnPropertyChanged(nameof(SelectedSourceCropBottomPct));
    }

    public double SelectedSourceCropRotation
    {
        get => GetSharedValue(source => source.CropRotation);
        set
        {
            if (double.IsFinite(value)) SetSelectedValue(source => source.CropRotation,
                _ => (int)NormalizeSignedAngle(Math.Round(value)), (source, angle) => source.CropRotation = angle);
        }
    }

    public bool SelectedSourceIsMirroredHorizontally
    {
        get
        {
            if (SelectedSources.Count == 0) return false;
            // For multi-selection, consider it "on" if ALL are mirrored, otherwise "off"
            return SelectedSources.All(s => s.IsMirroredHorizontally);
        }
        set
        {
            if (SelectedSources.Count > 0 && SelectedSourceIsMirroredHorizontally != value)
            {
                // This setter is now the trigger for the flip action
                _ = ToggleFlipHorizontalCommand.ExecuteAsync(null);
                OnPropertyChanged();
            }
        }
    }

    public bool SelectedSourceIsMirroredVertically
    {
        get
        {
            if (SelectedSources.Count == 0) return false;
            // For multi-selection, consider it "on" if ALL are mirrored, otherwise "off"
            return SelectedSources.All(s => s.IsMirroredVertically);
        }
        set
        {
            if (SelectedSources.Count > 0 && SelectedSourceIsMirroredVertically != value)
            {
                // This setter is now the trigger for the flip action
                _ = ToggleFlipVerticalCommand.ExecuteAsync(null);
                OnPropertyChanged();
            }
        }
    }

    public static Rect GetVisibleAreaAabb(SourceItem source) => Helpers.SourceGeometry.GetVisibleAreaAabb(source);

    public static Rect GetVisibleAreaAabb(SourceItem source, double x, double y, double width, double height, double rotation) =>
        Helpers.SourceGeometry.GetVisibleAreaAabb(source, x, y, width, height, rotation);

    private bool FitsInCanvas(int x, int y, int w, int h, int rotationDeg, SourceItem src)
    {
        var bounds = GetVisibleAreaAabb(src, x, y, w, h, rotationDeg);
        return !bounds.IsEmpty && bounds.Left >= -0.01 && bounds.Top >= -0.01 && bounds.Right <= 320.01 && bounds.Bottom <= 200.01;
    }

    public int SelectedSourceRotation
    {
        get => (int)NormalizeSignedAngle(SelectedSources.FirstOrDefault()?.Rotation ?? 0);
        set
        {
            if (_isUpdatingDimensions) return;
            var angle = (int)NormalizeSignedAngle(value);
            SetSelectedValue(source => (source.CanvasX, source.CanvasY, (int)NormalizeSignedAngle(source.Rotation)), source =>
            {
                var bounds = new Rect(source.CanvasX, source.CanvasY, source.CanvasWidth, source.CanvasHeight);
                var rotated = Helpers.CanvasSourceResize.Rounded(Helpers.CanvasSourceResize.Rotate(source, bounds, source.Rotation, angle));
                return FitsInCanvas((int)rotated.X, (int)rotated.Y, source.CanvasWidth, source.CanvasHeight, angle, source)
                    ? ((int)rotated.X, (int)rotated.Y, angle)
                    : (source.CanvasX, source.CanvasY, (int)NormalizeSignedAngle(source.Rotation));
            }, (source, geometry) =>
            {
                source.CanvasX = geometry.Item1;
                source.CanvasY = geometry.Item2;
                source.Rotation = geometry.Item3;
            });
            OnPropertyChanged(nameof(SelectedSourceRotation));
        }
    }

    #endregion

    #region Alignment Commands

    [RelayCommand(CanExecute = nameof(CanTransformGroup))]
    private Task AlignLeftAsync() => AlignSelectionAsync(horizontal: true, edge: -1);
    [RelayCommand(CanExecute = nameof(CanTransformGroup))]
    private Task AlignRightAsync() => AlignSelectionAsync(horizontal: true, edge: 1);
    [RelayCommand(CanExecute = nameof(CanTransformGroup))]
    private Task AlignCenterAsync() => AlignSelectionAsync(horizontal: true, edge: 0);
    [RelayCommand(CanExecute = nameof(CanTransformGroup))]
    private Task AlignTopAsync() => AlignSelectionAsync(horizontal: false, edge: -1);
    [RelayCommand(CanExecute = nameof(CanTransformGroup))]
    private Task AlignBottomAsync() => AlignSelectionAsync(horizontal: false, edge: 1);
    [RelayCommand(CanExecute = nameof(CanTransformGroup))]
    private Task AlignMiddleAsync() => AlignSelectionAsync(horizontal: false, edge: 0);

    private async Task AlignSelectionAsync(bool horizontal, int edge)
    {
        if (!CanTransformGroup) return;
        var items = SelectedSources.Select(source => (Source: source, Bounds: GetVisibleAreaAabb(source)))
            .Where(item => !item.Bounds.IsEmpty).ToArray();
        if (items.Length < 2) return;
        double Coordinate(Rect bounds) => horizontal
            ? edge < 0 ? bounds.Left : edge > 0 ? bounds.Right : bounds.Left + bounds.Width / 2
            : edge < 0 ? bounds.Top : edge > 0 ? bounds.Bottom : bounds.Top + bounds.Height / 2;
        var positions = items.Select(item => Coordinate(item.Bounds));
        var target = edge < 0 ? positions.Min() : edge > 0 ? positions.Max() : positions.Average();
        SaveUndoState();
        foreach (var item in items)
        {
            var delta = (int)Math.Round(target - Coordinate(item.Bounds));
            if (horizontal) item.Source.CanvasX += delta;
            else item.Source.CanvasY += delta;
        }
        await SaveSourcesAsync();
        SourcesMoved?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    [RelayCommand]
    private async Task ToggleFlipHorizontalAsync()
    {
        await FlipSelectedSourcesAsync(horizontal: true, vertical: false);
    }

    [RelayCommand]
    private async Task ToggleFlipVerticalAsync()
    {
        await FlipSelectedSourcesAsync(horizontal: false, vertical: true);
    }

    private async Task FlipSelectedSourcesAsync(bool horizontal, bool vertical)
    {
        if (!CanTransformSelection) return;
        SaveUndoState();
        var horizontalTarget = !SelectedSources.All(source => source.IsMirroredHorizontally);
        var verticalTarget = !SelectedSources.All(source => source.IsMirroredVertically);
        foreach (var source in SelectedSources)
        {
            if (horizontal) source.IsMirroredHorizontally = horizontalTarget;
            if (vertical) source.IsMirroredVertically = verticalTarget;
        }
        // A mirror affects pixels inside each source, never placement, rotation or the crop mask.
        await SaveSourcesAsync();
    }
}
