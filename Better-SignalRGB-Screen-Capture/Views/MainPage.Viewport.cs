using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class MainPage
{
    private void UpdateFlipIconsTheme()
    {
        var iconName = this.ActualTheme == ElementTheme.Dark ? "Flip-dark.svg" : "Flip-white.svg";
        var iconUri = new Uri($"ms-appx:///Assets/{iconName}");

        FlipVerticalIcon.Source = new SvgImageSource(iconUri);
        FlipHorizontalIcon.Source = new SvgImageSource(iconUri);
        MultiSelectFlipVerticalIcon.Source = new SvgImageSource(iconUri);
        MultiSelectFlipHorizontalIcon.Source = new SvgImageSource(iconUri);
    }

    private void ZoomToFit_Click(object sender, RoutedEventArgs e)
    {
        var width = ViewModel.CanvasViewportWidth;
        var height = ViewModel.CanvasViewportHeight;
        if (width <= 0 || height <= 0 || CanvasScrollViewer.ViewportWidth <= 0 || CanvasScrollViewer.ViewportHeight <= 0) return;
        var zoom = Math.Clamp(Math.Min(CanvasScrollViewer.ViewportWidth / width, CanvasScrollViewer.ViewportHeight / height),
            CanvasScrollViewer.MinZoomFactor, CanvasScrollViewer.MaxZoomFactor);
        CanvasScrollViewer.ChangeView(Math.Max(0, (width * zoom - CanvasScrollViewer.ViewportWidth) / 2),
            Math.Max(0, (height * zoom - CanvasScrollViewer.ViewportHeight) / 2), (float)zoom);
    }

    private void Zoom100_Click(object sender, RoutedEventArgs e) => ZoomAroundViewportCenter(1);

    private void ZoomAroundViewportCenter(float zoom)
    {
        if (CanvasScrollViewer == null || !float.IsFinite(zoom) || zoom <= 0) return;
        zoom = Math.Clamp(zoom, CanvasScrollViewer.MinZoomFactor, CanvasScrollViewer.MaxZoomFactor);
        if (Math.Abs(zoom - CanvasScrollViewer.ZoomFactor) < 0.001) return;
        var ratio = zoom / CanvasScrollViewer.ZoomFactor;
        var centerX = CanvasScrollViewer.ViewportWidth / 2;
        var centerY = CanvasScrollViewer.ViewportHeight / 2;
        CanvasScrollViewer.ChangeView(Math.Max(0, (CanvasScrollViewer.HorizontalOffset + centerX) * ratio - centerX),
            Math.Max(0, (CanvasScrollViewer.VerticalOffset + centerY) * ratio - centerY), zoom, true);
    }

    private void UpdatePreviewCanvas()
    {
        var isPreview = ViewModel.IsPreviewing;

        CanvasModeTitle.Text = isPreview ? "Live Preview" : "Layout Preview";

    }

    private bool _isUpdatingZoomSlider = false;

    private void ZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isUpdatingZoomSlider && sender is Slider slider) ZoomAroundViewportCenter((float)slider.Value);
    }

    private void CanvasScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        ClearSnapGuides();
        if (CanvasScrollViewer != null && ZoomSlider != null && ZoomPercentageText != null)
        {
            _isUpdatingZoomSlider = true;
            ZoomSlider.Value = CanvasScrollViewer.ZoomFactor;
            ZoomPercentageText.Text = $"{CanvasScrollViewer.ZoomFactor * 100:F0}%";
            _isUpdatingZoomSlider = false;
        }
    }

    private bool _isPropertiesPanelCollapsed;

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        // Toggle the intended state, not the interpolated width. Reversing a transition
        // must cancel its completion callback before it can hide the reopened panel.
        _isPropertiesPanelCollapsed = !_isPropertiesPanelCollapsed;
        var width = RightPanelBorder.Width;
        var contentOpacity = PropertiesScrollViewer.Opacity;
        var titleOpacity = RightPanelTitle.Opacity;
        CollapseAnimation.Completed -= CollapseAnimation_Completed;
        CollapseAnimation.Stop();
        ExpandAnimation.Stop();
        RightPanelBorder.Width = width;
        PropertiesScrollViewer.Opacity = contentOpacity;
        RightPanelTitle.Opacity = titleOpacity;
        PropertiesScrollViewer.Visibility = Visibility.Visible;
        RightPanelTitle.Visibility = Visibility.Visible;

        var animation = _isPropertiesPanelCollapsed ? CollapseAnimation : ExpandAnimation;
        foreach (var transition in animation.Children.OfType<DoubleAnimation>())
        {
            transition.From = Storyboard.GetTargetName(transition) switch
            {
                nameof(RightPanelBorder) => width,
                nameof(PropertiesScrollViewer) => contentOpacity,
                nameof(RightPanelTitle) => titleOpacity,
                _ => transition.From
            };
        }
        if (_isPropertiesPanelCollapsed) CollapseAnimation.Completed += CollapseAnimation_Completed;
        animation.Begin();
        CollapseIcon.Glyph = _isPropertiesPanelCollapsed ? "\uE8A0" : "\uE89F";
        ToolTipService.SetToolTip(CollapseButton, _isPropertiesPanelCollapsed ? "Show Properties" : "Hide Properties");
    }

    private void CollapseAnimation_Completed(object? sender, object e)
    {
        CollapseAnimation.Completed -= CollapseAnimation_Completed;
        if (_isPropertiesPanelCollapsed)
        {
            PropertiesScrollViewer.Visibility = Visibility.Collapsed;
            RightPanelTitle.Visibility = Visibility.Collapsed;
        }
    }

    private async void StreamingLink_Click(object sender, RoutedEventArgs e) =>
        await ExecuteUiOperationAsync("open the streaming link", OpenStreamingLinkAsync);

    private async Task OpenStreamingLinkAsync()
    {
        if (ViewModel.StreamingUrl is string url && !string.IsNullOrWhiteSpace(url))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                if (!await Windows.System.Launcher.LaunchUriAsync(uri))
                    ViewModel.StatusMessage = "No application could open the streaming link.";
            }
        }
    }
}
