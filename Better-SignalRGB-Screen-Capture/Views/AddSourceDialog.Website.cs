using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Core.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;

namespace Better_SignalRGB_Screen_Capture.Views;

public sealed partial class AddSourceDialog
{
    private readonly WebsitePreviewState _websitePreviewState = new();
    private string? _previewDefaultUserAgent;
    private string? _previewAddress;

    private void WebsiteZoomSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (WebsiteZoomLabel != null) WebsiteZoomLabel.Text = $"{(int)(e.NewValue * 100)}%";
        if (_isWebViewInitialized) _ = ApplyZoomToPreviewAsync(e.NewValue);
    }

    private void WebsiteUserAgentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WebsiteUserAgentCombo.SelectedItem is ComboBoxItem item && WebsiteCustomUserAgentBox != null)
            WebsiteCustomUserAgentBox.Visibility = item.Tag?.ToString() == "custom" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void WebsiteUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (WebsitePreviewSection == null) return;
        WebsitePreviewSection.Visibility = string.IsNullOrWhiteSpace(WebsiteUrlBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (LoadWebsiteButton != null) LoadWebsiteButton.IsEnabled = IsWebsiteUrl(WebsiteUrlBox.Text?.Trim());
    }

    private static bool IsWebsiteUrl(string? url) => CanvasSourceValidation.TryGetWebsiteUri(url, out _);

    private async void ChooseWebsiteFile_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        if (button != null) button.IsEnabled = false;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary
            };
            foreach (var extension in new[] { ".html", ".htm", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".avif", ".mp4", ".webm", ".m4v", ".mov", ".ogv" })
                picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (_isClosed || file == null) return;
            WebsiteUrlBox.Text = new Uri(file.Path).AbsoluteUri;
            await NavigateWebsitePreviewAsync(WebsiteUrlBox.Text);
        }
        catch (Exception exception) { if (!_isClosed) ShowValidationError($"Could not open the local file: {exception.Message}"); }
        finally { if (!_isClosed && button != null) button.IsEnabled = true; }
    }

    private async void LoadWebsiteButton_Click(object sender, RoutedEventArgs e) =>
        await NavigateWebsitePreviewAsync(WebsiteUrlBox.Text?.Trim());

    private async Task NavigateWebsitePreviewAsync(string? url)
    {
        if (_isClosed || !IsWebsiteUrl(url)) return;
        var generation = _websitePreviewState.BeginInitialization();
        var address = WebsiteUrlBox.Text?.Trim();
        try
        {
            WebsiteLoadingRing.Visibility = Visibility.Visible;
            LoadWebsiteButton.IsEnabled = false;
            await WebsitePreview.EnsureCoreWebView2Async();
            if (_isClosed || !_websitePreviewState.IsInitializationCurrent(generation)) return;
            _isWebViewInitialized = true;
            ApplyPreviewUserAgent();
            UpdateWebsitePreviewViewport();
            _previewAddress = address;
            _websitePreviewState.RequestNavigation(url!);
            CanvasSourceValidation.TryGetWebsiteUri(url, out var uri);
            var browser = WebsitePreview.CoreWebView2;
            browser.ClearVirtualHostNameToFolderMapping(WebsiteMedia.VirtualHost);
            if (WebsiteMedia.TryCreateVideoDocument(url, out var folder, out var document))
            {
                browser.SetVirtualHostNameToFolderMapping(WebsiteMedia.VirtualHost, folder, CoreWebView2HostResourceAccessKind.DenyCors);
                browser.NavigateToString(document);
            }
            else browser.Navigate(uri!.AbsoluteUri);
        }
        catch (Exception exception)
        {
            if (!_isClosed && _websitePreviewState.IsInitializationCurrent(generation))
            {
                WebsiteLoadingRing.Visibility = Visibility.Collapsed;
                LoadWebsiteButton.IsEnabled = IsWebsiteUrl(WebsiteUrlBox.Text?.Trim());
                ShowValidationError($"Failed to load website: {exception.Message}");
            }
        }
    }

    private void ApplyPreviewUserAgent()
    {
        if (WebsitePreview.CoreWebView2 is not { } browser) return;
        _previewDefaultUserAgent ??= browser.Settings.UserAgent;
        var agent = GetSelectedUserAgent();
        browser.Settings.UserAgent = string.IsNullOrWhiteSpace(agent) ? _previewDefaultUserAgent : agent;
    }

    private void WebsiteViewport_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdateWebsitePreviewViewport();

    private void UpdateWebsitePreviewViewport()
    {
        if (WebsitePreview == null) return;
        var width = WebsiteWidthBox?.Value ?? 1920;
        var height = WebsiteHeightBox?.Value ?? 1080;
        WebsitePreview.Width = double.IsFinite(width) ? Math.Clamp(width, 320, 7680) : 1920;
        WebsitePreview.Height = double.IsFinite(height) ? Math.Clamp(height, 240, 4320) : 1080;
    }

    private async void RefreshWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed || !_isWebViewInitialized) return;
        try
        {
            if (WebsiteMedia.TryCreateVideoDocument(WebsiteUrlBox.Text, out _, out _))
            {
                await NavigateWebsitePreviewAsync(WebsiteUrlBox.Text?.Trim());
                return;
            }
            ApplyPreviewUserAgent();
            WebsitePreview.CoreWebView2.Reload();
        }
        catch (Exception exception) { ShowValidationError($"Failed to refresh website: {exception.Message}"); }
    }

    private void WebsitePreview_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (_isClosed) return;
        _websitePreviewState.NavigationStarted(args.NavigationId);
        WebsiteLoadingRing.Visibility = Visibility.Visible;
    }

    private async void WebsitePreview_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_isClosed || args.NavigationId != _websitePreviewState.NavigationId) return;
        var generation = _websitePreviewState.Generation;
        if (args.IsSuccess)
        {
            try
            {
                if (WebsiteMedia.PreparationScript(WebsitePreview.CoreWebView2?.Source) is { } script)
                    await WebsitePreview.CoreWebView2!.ExecuteScriptAsync(script);
            }
            catch (Exception exception) { Debug.WriteLine($"Local media preview failed: {exception.Message}"); }
            await ApplyZoomToPreviewAsync(WebsiteZoomSlider.Value);
        }
        if (_isClosed || !_websitePreviewState.IsCurrent(generation)) return;
        _websitePreviewState.CompleteNavigation(args.NavigationId, args.IsSuccess);
        WebsiteLoadingRing.Visibility = Visibility.Collapsed;
        LoadWebsiteButton.IsEnabled = IsWebsiteUrl(WebsiteUrlBox.Text?.Trim());
        WebsitePreview.Visibility = args.IsSuccess ? Visibility.Visible : Visibility.Collapsed;
        WebsitePreviewPlaceholder.Visibility = args.IsSuccess ? Visibility.Collapsed : Visibility.Visible;
        RefreshWebsiteButton.Visibility = Visibility.Visible;
        if (!args.IsSuccess)
            WebsitePreviewPlaceholder.Text = $"Failed to load website ({args.WebErrorStatus}). Check the URL and try again.";
    }

    private async Task ApplyZoomToPreviewAsync(double zoom)
    {
        if (_isClosed || WebsitePreview.CoreWebView2 is not { } browser) return;
        try { await browser.ExecuteScriptAsync(WebsitePreviewState.ZoomScript(zoom)); }
        catch (Exception exception) { Debug.WriteLine($"Website preview zoom failed: {exception.Message}"); }
    }

    private string GetSelectedUserAgent() => WebsiteUserAgentCombo?.SelectedItem is ComboBoxItem item
        ? item.Tag?.ToString() == "custom" ? WebsiteCustomUserAgentBox?.Text ?? "" : item.Tag?.ToString() ?? ""
        : "";

    private string? GetNavigationState()
    {
        // Typing another address without loading it must not save the previous preview page.
        if (!_websitePreviewState.Ready || _previewAddress != WebsiteUrlBox.Text?.Trim()) return null;
        var url = WebsitePreview.CoreWebView2?.Source;
        return IsWebsiteUrl(url) ? url : null;
    }

    private Task RestoreNavigationStateAsync(string navigationState) => NavigateWebsitePreviewAsync(navigationState);
}
