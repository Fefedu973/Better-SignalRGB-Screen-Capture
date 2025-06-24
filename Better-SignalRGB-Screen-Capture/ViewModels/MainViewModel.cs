using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Linq;
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.Contracts.Services;

namespace Better_SignalRGB_Screen_Capture.ViewModels;

public partial class MainViewModel : ObservableRecipient
{
    private readonly ILocalSettingsService _localSettingsService;
    private readonly ICaptureService _captureService;
    private readonly IMjpegStreamingService _mjpegStreamingService;
    private readonly ICompositeFrameService _compositeFrameService;
    private const string SourcesSettingsKey = "SavedSources";
    private const string PreviewFpsSettingsKey = "PreviewFps";
    private CancellationTokenSource? _testFrameCancellation;
    
    public ObservableCollection<SourceItem> Sources { get; } = new();
    
    [ObservableProperty]
    private SourceItem? selectedSource;
    
    [ObservableProperty]
    private int previewFps = 30; // Default to 30 FPS
    
    [ObservableProperty]
    private string? streamingUrl;

    public MainViewModel(ILocalSettingsService localSettingsService, ICaptureService captureService, IMjpegStreamingService mjpegStreamingService, ICompositeFrameService compositeFrameService)
    {
        _localSettingsService = localSettingsService;
        _captureService = captureService;
        _mjpegStreamingService = mjpegStreamingService;
        _compositeFrameService = compositeFrameService;
        
        // Subscribe to events
        _captureService.FrameAvailable += OnFrameAvailable;
        _mjpegStreamingService.StreamingUrlChanged += OnStreamingUrlChanged;
        _compositeFrameService.CompositeFrameAvailable += OnCompositeFrameAvailable;
        
        _ = LoadSourcesAsync();
        _ = LoadSettingsAsync();
        
        // Initialize composite frame service with canvas size
        _compositeFrameService.SetCanvasSize(800, 600);
        
        // Start MJPEG streaming
        _ = Task.Run(async () => await _mjpegStreamingService.StartStreamingAsync());
    }
    
    partial void OnPreviewFpsChanged(int value)
    {
        // Save FPS setting when it changes
        _ = SavePreviewFpsAsync();
        
        // Update capture service framerate
        _captureService.SetFrameRate(value);
    }
    
    [RelayCommand]
    private async Task AddSourceAsync(SourceItem? newSource = null)
    {
        if (newSource != null)
        {
            // Find a good position for the new source on canvas
            var (x, y) = FindAvailableCanvasPosition();
            newSource.CanvasX = x;
            newSource.CanvasY = y;
            
            Sources.Add(newSource);
            await SaveSourcesAsync();
            
            // Start capture for the new source
            await _captureService.StartCaptureAsync(newSource);
        }
    }
    
    [RelayCommand]
    private async Task DeleteSourceAsync(SourceItem? source)
    {
        if (source != null && Sources.Contains(source))
        {
            // Stop capture for the source
            await _captureService.StopCaptureAsync(source);
            
            // Remove from composite frame service
            _compositeFrameService.RemoveSource(source);
            
            Sources.Remove(source);
            await SaveSourcesAsync();
        }
    }
    
    [RelayCommand]
    private async Task EditSourceAsync(SourceItem? source)
    {
        if (source != null)
        {
            // This will be handled by the view to open edit dialog
            // The dialog can modify the source properties directly
            await SaveSourcesAsync();
        }
    }
    
    [RelayCommand]
    private async Task UpdateSourcePositionAsync(SourceItem source)
    {
        // Called when source is moved/resized on canvas
        await SaveSourcesAsync();
    }
    
    private (int x, int y) FindAvailableCanvasPosition()
    {
        const int gridSize = 120; // Size of each grid cell
        const int canvasWidth = 800;
        const int canvasHeight = 600;
        
        for (int row = 0; row < canvasHeight / gridSize; row++)
        {
            for (int col = 0; col < canvasWidth / gridSize; col++)
            {
                int x = col * gridSize + 10; // Small margin
                int y = row * gridSize + 10;
                
                // Check if this position is already occupied
                bool occupied = Sources.Any(s => 
                    Math.Abs(s.CanvasX - x) < 50 && Math.Abs(s.CanvasY - y) < 50);
                
                if (!occupied)
                    return (x, y);
            }
        }
        
        // If all positions occupied, place randomly
        var random = new Random();
        return (random.Next(0, canvasWidth - 100), random.Next(0, canvasHeight - 80));
    }
    
    private async Task LoadSourcesAsync()
    {
        try
        {
            var savedSources = await _localSettingsService.ReadSettingAsync<SourceItem[]>(SourcesSettingsKey);
            if (savedSources != null)
            {
                Sources.Clear();
                foreach (var source in savedSources)
                {
                    Sources.Add(source);
                    
                    // Start capture for loaded sources
                    _ = Task.Run(async () => await _captureService.StartCaptureAsync(source));
                }
            }
        }
        catch
        {
            // If loading fails, start with empty collection
        }
    }
    
    private async Task SaveSourcesAsync()
    {
        try
        {
            await _localSettingsService.SaveSettingAsync(SourcesSettingsKey, Sources.ToArray());
        }
        catch
        {
            // Handle save error if needed
        }
    }
    
    private async Task LoadSettingsAsync()
    {
        try
        {
            var savedFps = await _localSettingsService.ReadSettingAsync<int?>(PreviewFpsSettingsKey);
            if (savedFps.HasValue && savedFps.Value >= 1 && savedFps.Value <= 60)
            {
                PreviewFps = savedFps.Value;
            }
        }
        catch
        {
            // If loading fails, keep default value
        }
    }
    
    private async Task SavePreviewFpsAsync()
    {
        try
        {
            await _localSettingsService.SaveSettingAsync(PreviewFpsSettingsKey, PreviewFps);
        }
        catch
        {
            // Handle save error if needed
        }
    }
    
    private void OnFrameAvailable(object? sender, SourceFrameEventArgs e)
    {
        // Handle frame received from capture service
        // Send individual source frame to composite service
        if (e.FrameData != null)
        {
            _compositeFrameService.UpdateSourceFrame(e.Source, e.FrameData);
        }
    }
    
    private void OnCompositeFrameAvailable(object? sender, byte[] compositeFrame)
    {
        // Send composite frame to MJPEG streaming service
        _mjpegStreamingService.UpdateFrame(compositeFrame);
    }
    
    private void OnStreamingUrlChanged(object? sender, string url)
    {
        StreamingUrl = string.IsNullOrEmpty(url) ? null : url;
    }
    
    [RelayCommand]
    private async Task StartAllCapturesAsync()
    {
        foreach (var source in Sources)
        {
            if (!_captureService.IsCapturing(source))
            {
                await _captureService.StartCaptureAsync(source);
            }
        }
        
        // Also start a test frame generator to ensure MJPEG stream works
        _testFrameCancellation?.Cancel();
        _testFrameCancellation = new CancellationTokenSource();
        
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_testFrameCancellation.Token.IsCancellationRequested)
                {
                    // Generate a test frame every second
                    await Task.Delay(1000, _testFrameCancellation.Token);
                    
                    // Generate a simple test JPEG frame
                    var testFrame = GenerateTestFrame();
                    _mjpegStreamingService.UpdateFrame(testFrame);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
        }, _testFrameCancellation.Token);
    }
    
    private byte[] GenerateTestFrame()
    {
        // Create a simple colored test frame (minimal JPEG)
        return new byte[]
        {
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x01, 0x00, 0x48,
            0x00, 0x48, 0x00, 0x00, 0xFF, 0xDB, 0x00, 0x43, 0x00, 0x08, 0x06, 0x06, 0x07, 0x06, 0x05, 0x08,
            0x07, 0x07, 0x07, 0x09, 0x09, 0x08, 0x0A, 0x0C, 0x14, 0x0D, 0x0C, 0x0B, 0x0B, 0x0C, 0x19, 0x12,
            0x13, 0x0F, 0x14, 0x1D, 0x1A, 0x1F, 0x1E, 0x1D, 0x1A, 0x1C, 0x1C, 0x20, 0x24, 0x2E, 0x27, 0x20,
            0x22, 0x2C, 0x23, 0x1C, 0x1C, 0x28, 0x37, 0x29, 0x2C, 0x30, 0x31, 0x34, 0x34, 0x34, 0x1F, 0x27,
            0x39, 0x3D, 0x38, 0x32, 0x3C, 0x2E, 0x33, 0x34, 0x32, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x64,
            0x00, 0x64, 0x01, 0x01, 0x11, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01, 0xFF, 0xC4, 0x00, 0x15,
            0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x08, 0xFF, 0xC4, 0x00, 0x14, 0x10, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02,
            0x11, 0x03, 0x11, 0x00, 0x3F, 0x00, 0x00, 0xFF, 0xD9
        };
    }
    
    [RelayCommand]
    private async Task StopAllCapturesAsync()
    {
        // Stop test frame generator
        _testFrameCancellation?.Cancel();
        
        await _captureService.StopAllCapturesAsync();
    }
}
