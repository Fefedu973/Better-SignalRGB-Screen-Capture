using CommunityToolkit.Mvvm.Input;
using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.ViewModels;

public partial class MainViewModel
{
    private bool _captureRequested;
    private bool _shuttingDown;
    private void OnStreamingUrlChanged(object? sender, string url) =>
        App.MainWindow.DispatcherQueue.TryEnqueue(() => StreamingUrl = string.IsNullOrEmpty(url) ? null : url);

    private void OnKestrelStreamingUrlChanged(object? sender, string url)
    {
        if (!string.IsNullOrEmpty(url))
            App.MainWindow.DispatcherQueue.TryEnqueue(() => StreamingUrl = url);
    }

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (IsRecordingLoading) return;
        IsRecordingLoading = true;
        StatusMessage = null;
        try
        {
            await Initialization;
            if (IsRecording)
            {
                await StopAllCapturesAsync();
                IsRecording = false;
                CanPause = false;
            }
            else
            {
                await StartAllCapturesAsync();
                IsRecording = true;
                CanPause = true;
            }
            IsPaused = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not change recording state: {ex.Message}";
            IsRecording = CanPause = IsPaused = false;
        }
        finally { IsRecordingLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task TogglePauseAsync()
    {
        if (IsRecordingLoading) return;
        IsRecordingLoading = true;
        try
        {
            if (IsPaused) await StartAllCapturesAsync();
            else await StopAllCapturesAsync();
            IsPaused = !IsPaused;
        }
        catch (Exception ex) { StatusMessage = $"Could not change pause state: {ex.Message}"; }
        finally { IsRecordingLoading = false; }
    }

    [RelayCommand]
    private async Task RefreshStreamAsync()
    {
        if (!IsRecording || IsPaused || IsRecordingLoading) return;
        IsRecordingLoading = true;
        try
        {
            await StopAllCapturesAsync();
            await StartAllCapturesAsync();
            NeedsRefresh = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not refresh capture: {ex.Message}";
            IsRecording = CanPause = false;
        }
        finally { IsRecordingLoading = false; }
    }

    public async Task StartAllCapturesAsync()
    {
        await Initialization;
        if (_shuttingDown) return;
        await _captureLifecycle.WaitAsync();
        try
        {
            if (_shuttingDown) return;
            _captureRequested = true;
            var port = await _localSettingsService.ReadSettingAsync<int?>(StreamingPortKey) ?? 8080;
            await _mjpegStreamingService.StartStreamingAsync(port);
            try { await _kestrelApiService.StartAsync(await GetHttpsPortAsync()); }
            catch (Exception ex) { StatusMessage = $"HTTP streaming is available; HTTPS could not start: {ex.Message}"; }
            foreach (var source in Sources.ToArray()) await TryStartSourceAsync(source);
            if (_availabilityCts == null)
            {
                _availabilityCts = new System.Threading.CancellationTokenSource();
                _availabilityTask = EnsureSourcesLoop(_availabilityCts.Token);
            }
            NeedsRefresh = false;
        }
        catch
        {
            _captureRequested = false;
            await _captureService.StopAllCapturesAsync();
            await _mjpegStreamingService.StopStreamingAsync();
            await _kestrelApiService.StopAsync();
            throw;
        }
        finally { _captureLifecycle.Release(); }
    }

    private async Task TryStartSourceAsync(SourceItem source)
    {
        try
        {
            if (!_captureRequested || _shuttingDown || !Sources.Contains(source)) return;
            await _captureService.StartCaptureAsync(source);
            if (!_captureRequested || _shuttingDown || !Sources.Contains(source)) await _captureService.StopCaptureAsync(source);
        }
        catch (Exception ex) { StatusMessage = $"{source.DisplayName}: {ex.Message}"; }
    }

    private async Task StartSourceWhenRecordingAsync(SourceItem source)
    {
        await _captureLifecycle.WaitAsync();
        try
        {
            if (_captureRequested && !_shuttingDown && IsRecording && !IsPaused)
                await TryStartSourceAsync(source);
        }
        finally { _captureLifecycle.Release(); }
    }

    public async Task StopAllCapturesAsync()
    {
        _captureRequested = false;
        await _captureLifecycle.WaitAsync();
        try
        {
            if (_availabilityCts is { } cancellation)
            {
                _availabilityCts = null;
                cancellation.Cancel();
                if (_availabilityTask != null) await _availabilityTask;
                cancellation.Dispose();
                _availabilityTask = null;
            }
            try { await _captureService.StopAllCapturesAsync(); }
            finally
            {
                try { await _mjpegStreamingService.StopStreamingAsync(); }
                finally { await _kestrelApiService.StopAsync(); }
                StreamingUrl = null;
            }
        }
        finally { _captureLifecycle.Release(); }
    }

    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        await Initialization;
        await StopAllCapturesAsync();
        await _pendingSourceSave;
        await FlushScenesAsync();
    }

    private async Task EnsureSourcesLoop(System.Threading.CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(5000, token);
                if (!WaitForSourceAvailability || IsEditingSources || !_captureRequested) continue;
                // Never queue behind StopAllCaptures, which cancels and awaits this loop while holding the gate.
                if (!await _captureLifecycle.WaitAsync(0, token)) continue;
                try
                {
                    foreach (var source in Sources.ToArray())
                    {
                        token.ThrowIfCancellationRequested();
                        if (IsEditingSources || !_captureRequested) break;
                        if (!_captureService.IsCapturing(source)) await TryStartSourceAsync(source);
                    }
                }
                finally { _captureLifecycle.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { StatusMessage = $"Source availability check failed: {ex.Message}"; }
    }

}
