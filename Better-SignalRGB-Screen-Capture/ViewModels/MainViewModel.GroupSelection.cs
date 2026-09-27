using CommunityToolkit.Mvvm.ComponentModel;

namespace Better_SignalRGB_Screen_Capture.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    private bool _isGroupAspectRatioLocked;

    private bool _isUpdatingGroupDimensions;
    private double _groupSelectionX;
    private double _groupSelectionY;
    private double _groupSelectionWidth;
    private double _groupSelectionHeight;
    private double _groupSelectionRotation;

    public double GroupSelectionX { get => _groupSelectionX; set => SetGroupValue(value, nameof(GroupSelectionX)); }
    public double GroupSelectionY { get => _groupSelectionY; set => SetGroupValue(value, nameof(GroupSelectionY)); }
    public double GroupSelectionWidth { get => _groupSelectionWidth; set => SetGroupValue(value, nameof(GroupSelectionWidth)); }
    public double GroupSelectionHeight { get => _groupSelectionHeight; set => SetGroupValue(value, nameof(GroupSelectionHeight)); }
    public double GroupSelectionRotation { get => _groupSelectionRotation; set => SetGroupValue(value, nameof(GroupSelectionRotation)); }

    public event Action? GroupSelectionBoundsChanged;

    private void SetGroupValue(double value, string property)
    {
        if (!CanTransformGroup || _isUpdatingGroupDimensions || !double.IsFinite(value)) return;
        var x = _groupSelectionX;
        var y = _groupSelectionY;
        var width = _groupSelectionWidth;
        var height = _groupSelectionHeight;
        var rotation = _groupSelectionRotation;
        switch (property)
        {
            case nameof(GroupSelectionX): x = value; break;
            case nameof(GroupSelectionY): y = value; break;
            case nameof(GroupSelectionWidth):
                width = Math.Max(1, value);
                if (IsGroupAspectRatioLocked && _groupSelectionWidth > 0)
                    height = Math.Max(1, _groupSelectionHeight * width / _groupSelectionWidth);
                break;
            case nameof(GroupSelectionHeight):
                height = Math.Max(1, value);
                if (IsGroupAspectRatioLocked && _groupSelectionHeight > 0)
                    width = Math.Max(1, _groupSelectionWidth * height / _groupSelectionHeight);
                break;
            case nameof(GroupSelectionRotation): rotation = NormalizeSignedAngle(value); break;
        }
        if (Math.Abs(x - _groupSelectionX) < 0.01 && Math.Abs(y - _groupSelectionY) < 0.01 &&
            Math.Abs(width - _groupSelectionWidth) < 0.01 && Math.Abs(height - _groupSelectionHeight) < 0.01 &&
            Math.Abs(rotation - _groupSelectionRotation) < 0.01) return;

        SaveUndoState();
        _isUpdatingGroupDimensions = true;
        try
        {
            UpdateGroupSelectionBounds(x, y, width, height, rotation);
            // The control validates the transform and reports the accepted geometry synchronously.
            GroupSelectionBoundsChanged?.Invoke();
            _ = SaveSourcesAsync();
        }
        finally { _isUpdatingGroupDimensions = false; }
    }

    public void UpdateGroupSelectionBounds(double x, double y, double width, double height, double rotation)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(rotation)) return;
        var wasUpdating = _isUpdatingGroupDimensions;
        _isUpdatingGroupDimensions = true;
        try
        {
            SetProperty(ref _groupSelectionX, x, nameof(GroupSelectionX));
            SetProperty(ref _groupSelectionY, y, nameof(GroupSelectionY));
            SetProperty(ref _groupSelectionWidth, width, nameof(GroupSelectionWidth));
            SetProperty(ref _groupSelectionHeight, height, nameof(GroupSelectionHeight));
            SetProperty(ref _groupSelectionRotation, NormalizeSignedAngle(rotation), nameof(GroupSelectionRotation));
        }
        finally { _isUpdatingGroupDimensions = wasUpdating; }
    }

    private static double NormalizeSignedAngle(double angle) => ((angle % 360) + 540) % 360 - 180;
}
