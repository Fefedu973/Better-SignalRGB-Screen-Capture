using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Better_SignalRGB_Screen_Capture.Contracts.Services;
using Better_SignalRGB_Screen_Capture.Helpers;
using Better_SignalRGB_Screen_Capture.Models;
using Better_SignalRGB_Screen_Capture.ViewModels;
using Better_SignalRGB_Screen_Capture.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using ScreenRecorderLib;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Better_SignalRGB_Screen_Capture.Services;

public class CaptureService : ICaptureService
{
    // One recorder per source - like running multiple TestApp instances
    private readonly ConcurrentDictionary<Guid, SourceRecorder> _recorders = new();
    private readonly ConcurrentDictionary<Guid, byte[]> _lastFrameData = new();
    private readonly ConcurrentDictionary<Guid, Microsoft.UI.Dispatching.DispatcherQueueTimer> _websiteTimers = new();
    private int _frameRate = 15; // Increased from 10 for smoother streaming

    public event EventHandler<SourceFrameEventArgs>? FrameAvailable;

    private class SourceRecorder
    {
        public Recorder Recorder { get; set; }
        public MemoryStream OutputStream { get; set; }
        public SourceItem Source { get; set; }
        public RecorderOptions Options { get; set; }
        public WriteableBitmap? PreviewBitmap { get; set; }

        public SourceRecorder(Recorder recorder, MemoryStream stream, SourceItem source, RecorderOptions options)
        {
            Recorder = recorder;
            OutputStream = stream;
            Source = source;
            Options = options;
        }
    }

    public async Task StartCaptureAsync(SourceItem source)
    {
        if (source == null || IsCapturing(source))
            return;

        if (source.Type == Models.SourceType.Website)
        {
            await StartWebsiteCaptureAsync(source);
            return;
        }

        var options = CreateRecorderOptions(source);
        if (options == null)
            return;

        try
        {
            Debug.WriteLine($"🎬 Starting individual recorder for: {source.Name} ({source.Type})");

            // Stop existing recorder for this source if it exists
            if (_recorders.ContainsKey(source.Id))
            {
                await StopCaptureAsync(source);
            }

            // Create memory stream for output
            var outputStream = new MemoryStream();

            // Create the recorder
            var recorder = Recorder.CreateRecorder(options);
            
            // Store the recorder BEFORE setting up events
            var sourceRecorder = new SourceRecorder(recorder, outputStream, source, options);
            _recorders[source.Id] = sourceRecorder;

            // Set up event handlers
            recorder.OnRecordingComplete += (s, e) => OnRecordingComplete(source.Id, e);
            recorder.OnRecordingFailed += (s, e) => OnRecordingFailed(source.Id, e);
            recorder.OnFrameRecorded += (s, e) => OnFrameRecorded(source.Id, e);
            recorder.OnStatusChanged += (s, e) => OnStatusChanged(source.Id, e);

            // Start recording to stream
            recorder.Record(outputStream);

            Debug.WriteLine($"✅ Started recording for {source.Name} to memory stream");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"❌ Failed to start capture for {source.Name}: {ex.Message}");
            Debug.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }

    private async Task StartWebsiteCaptureAsync(SourceItem source)
    {
        if (string.IsNullOrEmpty(source.WebsiteUrl))
            return;

        // Find the DraggableSourceItem control for this source
        var mainViewModel = App.GetService<MainViewModel>();
        if (mainViewModel == null)
            return;

        // Find the DraggableSourceItem control by searching the MainPage's canvas
        DraggableSourceItem? draggableSource = null;
        var tcs = new TaskCompletionSource<bool>();
        
        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                // Get the DraggableSourceItem through MainViewModel
                draggableSource = mainViewModel.GetDraggableSourceItem(source.Id);
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error finding DraggableSourceItem: {ex.Message}");
                tcs.SetException(ex);
            }
        });
        
        await tcs.Task;

        if (draggableSource == null)
        {
            Debug.WriteLine($"Could not find DraggableSourceItem for website source: {source.Name}");
            return;
        }

        // Create timer on UI thread since we need to access WebView
        Microsoft.UI.Dispatching.DispatcherQueueTimer? timer = null;
        var timerTcs = new TaskCompletionSource<bool>();
        
        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
                timer.Interval = TimeSpan.FromMilliseconds(1000.0 / _frameRate);
                
                timer.Tick += async (s, e) =>
                {
                    try
                    {
                        var frameData = await draggableSource.CaptureWebViewFrameAsync();
                        if (frameData != null)
                        {
                            _lastFrameData[source.Id] = frameData;
                            FrameAvailable?.Invoke(this, new SourceFrameEventArgs(source, null) { FrameData = frameData });
                            Debug.WriteLine($"📱 Website frame captured: {source.Name} ({frameData.Length} bytes)");
                        }
                        else
                        {
                            Debug.WriteLine($"❌ No frame data from website: {source.Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"❌ Error capturing website frame for {source.Name}: {ex.Message}");
                    }
                };
                
                timer.Start();
                timerTcs.SetResult(true);
                Debug.WriteLine($"✅ Website capture timer started for {source.Name} at {_frameRate} FPS");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Error creating website timer: {ex.Message}");
                timerTcs.SetException(ex);
            }
        });
        
        await timerTcs.Task;
        
        if (timer != null)
        {
            _websiteTimers[source.Id] = timer;
        }
    }

    public async Task StopCaptureAsync(SourceItem source)
    {
        if (source == null)
            return;

        if (source.Type == Models.SourceType.Website)
        {
            if (_websiteTimers.TryRemove(source.Id, out var timer))
            {
                timer.Stop();
            }
            return;
        }

        try
        {
            Debug.WriteLine($"🛑 Stopping recorder for: {source.Name}");
            
            if (_recorders.TryRemove(source.Id, out var sourceRecorder))
            {
                // Stop the recorder
                if (sourceRecorder.Recorder?.Status == RecorderStatus.Recording || 
                    sourceRecorder.Recorder?.Status == RecorderStatus.Paused)
                {
                    sourceRecorder.Recorder.Stop();
                }

                // Dispose resources
                sourceRecorder.Recorder?.Dispose();
                sourceRecorder.OutputStream?.Dispose();
                
                // Remove frame data
                _lastFrameData.TryRemove(source.Id, out _);
                
                Debug.WriteLine($"✅ Stopped and removed recorder for {source.Name}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"❌ Error stopping recorder for {source.Name}: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    public async Task StopAllCapturesAsync()
    {
        // Stop website timers
        foreach (var timer in _websiteTimers.Values)
        {
            timer.Stop();
        }
        _websiteTimers.Clear();

        Debug.WriteLine($"🛑 Stopping all {_recorders.Count} recorders");
        
        var tasks = _recorders.Keys.Select(async sourceId =>
        {
            if (_recorders.TryGetValue(sourceId, out var recorder))
            {
                await StopCaptureAsync(recorder.Source);
            }
        });

        await Task.WhenAll(tasks);
        
        _recorders.Clear();
        _lastFrameData.Clear();
        
        Debug.WriteLine($"✅ All recorders stopped");
    }

    public bool IsCapturing(SourceItem source)
    {
        if (source == null)
            return false;

        if (source.Type == Models.SourceType.Website)
        {
            return _websiteTimers.ContainsKey(source.Id);
        }

        return _recorders.ContainsKey(source.Id);
    }

    public Task SetFrameRate(int fps)
    {
        _frameRate = Math.Max(1, Math.Min(60, fps));
        Debug.WriteLine($"🎛️ Frame rate set to {_frameRate} FPS");
        
        // For active recordings, we'd need to restart them
        // This is handled by the caller who should restart captures after changing framerate
        
        return Task.CompletedTask;
    }

    public byte[]? GetMjpegFrame(Guid sourceId)
    {
        return _lastFrameData.TryGetValue(sourceId, out var data) ? data : null;
    }

    public byte[]? GetMjpegFrame()
    {
        // Return the first available frame, or a black 320x200 frame if none available
        var firstFrame = _lastFrameData.Values.FirstOrDefault();
        if (firstFrame != null)
        {
            return firstFrame;
        }

        // Generate a black 320x200 JPEG frame as fallback
        return CreateBlackFrame320x200();
    }

    private byte[] CreateBlackFrame320x200()
    {
        try
        {
            using var bitmap = new System.Drawing.Bitmap(320, 200, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.Clear(System.Drawing.Color.Black);
            using var stream = new MemoryStream();
            var jpegEncoder = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            var encoderParams = new System.Drawing.Imaging.EncoderParameters(1);
            encoderParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 20L);
            bitmap.Save(stream, jpegEncoder, encoderParams);
            return stream.ToArray();
        }
        catch
        {
            return new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x01, 0x00, 0x48,
                0x00, 0x48, 0x00, 0x00, 0xFF, 0xDB, 0x00, 0x43, 0x00, 0x08, 0x06, 0x06, 0x07, 0x06, 0x05, 0x08,
                0x07, 0x07, 0x07, 0x09, 0x09, 0x08, 0x0A, 0x0C, 0x14, 0x0D, 0x0C, 0x0B, 0x0B, 0x0C, 0x19, 0x12,
                0x13, 0x0F, 0x14, 0x1D, 0x1A, 0x1F, 0x1E, 0x1D, 0x1A, 0x1C, 0x1C, 0x20, 0x24, 0x2E, 0x27, 0x20,
                0x22, 0x2C, 0x23, 0x1C, 0x1C, 0x28, 0x37, 0x29, 0x2C, 0x30, 0x31, 0x34, 0x34, 0x34, 0x1F, 0x27,
                0x39, 0x3D, 0x38, 0x32, 0x3C, 0x2E, 0x33, 0x34, 0x32, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x01,
                0x00, 0x01, 0x01, 0x01, 0x11, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01, 0xFF, 0xC4, 0x00, 0x14,
                0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x08, 0xFF, 0xC4, 0x00, 0x14, 0x10, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02,
                0x11, 0x03, 0x11, 0x00, 0x3F, 0x00, 0x00, 0xFF, 0xD9 };
        }
    }

    private RecorderOptions? CreateRecorderOptions(SourceItem source)
    {
        try
        {
            var recordingSources = new List<RecordingSourceBase>();
            if (source.Type == SourceType.Region)
            {
                var regionSources = CreateRegionSources(source);
                if (regionSources == null || regionSources.Count == 0) return null;
                recordingSources = regionSources;
            }
            else
            {
                var single = CreateRecordingSource(source);
                if (single == null) return null;
                recordingSources.Add(single);
            }

            foreach (var recSrc in recordingSources)
            {
                if (recSrc is DisplayRecordingSource d) d.IsCursorCaptureEnabled = true;
                else if (recSrc is WindowRecordingSource w) w.IsCursorCaptureEnabled = true;

                recSrc.IsVideoCaptureEnabled      = true;
                recSrc.IsVideoFramePreviewEnabled = true;

                if (source.Type != SourceType.Region)
                {
                    recSrc.Stretch    = StretchMode.Fill;
                    recSrc.OutputSize = new ScreenSize((int)source.CanvasWidth, (int)source.CanvasHeight);
                }
                else
                {
                    recSrc.Stretch = StretchMode.None;
                }
            }

            var options = new RecorderOptions
            {
                SourceOptions = new SourceOptions { RecordingSources = recordingSources },
                OutputOptions = new OutputOptions
                {
                    RecorderMode             = RecorderMode.Video,
                    Stretch                  = StretchMode.Fill,
                    IsVideoCaptureEnabled    = true,
                    IsVideoFramePreviewEnabled = true
                },
                VideoEncoderOptions = new VideoEncoderOptions
                {
                    Encoder = new H264VideoEncoder
                    {
                        BitrateMode    = H264BitrateControlMode.Quality,
                        EncoderProfile = H264Profile.Baseline
                    },
                    Framerate              = _frameRate,
                    Quality                = 35,
                    IsHardwareEncodingEnabled = true,
                    IsLowLatencyEnabled       = true,
                    IsThrottlingDisabled      = true,
                    IsFixedFramerate          = false
                },
                AudioOptions = new AudioOptions { IsAudioEnabled = false },
                MouseOptions = new MouseOptions { IsMousePointerEnabled = source.Type != SourceType.Webcam },
                LogOptions   = new LogOptions   { IsLogEnabled = false }
            };

            if (source.Type == SourceType.Region && source.RegionWidth > 0 && source.RegionHeight > 0)
            {
                var regionRect = new System.Drawing.Rectangle(source.RegionX ?? 0, source.RegionY ?? 0,
                                                               source.RegionWidth ?? 0, source.RegionHeight ?? 0);
                var displays = Recorder.GetDisplays();
                var intersects = CoordinateMapper.FindIntersectingDisplays(regionRect, displays);
                int width, height;
                if (intersects.Count == 1)
                {
                    width  = source.RegionWidth.Value;
                    height = source.RegionHeight.Value;
                }
                else
                {
                    width  = (int)source.CanvasWidth;
                    height = (int)source.CanvasHeight;
                }
                options.OutputOptions.OutputFrameSize = new ScreenSize(width, height);
            }

            Debug.WriteLine($"📋 Recorder options for {source.Name}:");
            Debug.WriteLine($"   - Type: {source.Type}");
            Debug.WriteLine($"   - Source count: {recordingSources.Count}");
            if (recordingSources.Count == 1)
            {
                var recordingSource = recordingSources[0];
                Debug.WriteLine($"   - Output size: {recordingSource.OutputSize?.Width ?? 0}x{recordingSource.OutputSize?.Height ?? 0}");
                Debug.WriteLine($"   - Cursor: {(recordingSource is DisplayRecordingSource d ? d.IsCursorCaptureEnabled : recordingSource is WindowRecordingSource w ? w.IsCursorCaptureEnabled : false)}");
                Debug.WriteLine($"   - API: {(recordingSource is DisplayRecordingSource disp ? disp.RecorderApi.ToString() : "N/A")}");
            }
            else
            {
                Debug.WriteLine($"   - Multi-source recording for region spanning {recordingSources.Count} monitors");
            }
            Debug.WriteLine($"   - Frame rate: {_frameRate} FPS");

            return options;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"❌ Failed to create recorder options: {ex.Message}");
            return null;
        }
    }

    private RecordingSourceBase? CreateRecordingSource(SourceItem source)
    {
        try
        {
            RecordingSourceBase? recordingSource = source.Type switch
            {
                SourceType.Display or SourceType.Monitor => CreateDisplaySource(source) as RecordingSourceBase,
                SourceType.Window or SourceType.Process => CreateWindowSource(source) as RecordingSourceBase,
                SourceType.Webcam => CreateWebcamSource(source) as RecordingSourceBase,
                SourceType.Website => null as RecordingSourceBase, // Website will be handled as iframe, not recording
                SourceType.Region => null as RecordingSourceBase, // Region is handled separately in CreateRecorderOptions
                _ => null as RecordingSourceBase
            };

            // Use TestApp backend to calculate proper aspect ratio and placement
            if (recordingSource != null)
            {
                UpdateSourceCanvasProperties(source, recordingSource);
            }

            return recordingSource;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"❌ Error creating recording source: {ex.Message}");
            return null;
        }
    }

    private void UpdateSourceCanvasProperties(SourceItem source, RecordingSourceBase recordingSource)
    {
        try
        {
                    // Only set size during initial creation (when size is small placeholder)
        if (source.CanvasWidth <= 60 && source.CanvasHeight <= 50)
        {
            // Use the same backend as TestApp to get proper coordinates and sizing
            var outputDimensions = Recorder.GetOutputDimensionsForRecordingSources(new[] { recordingSource });
            if (outputDimensions.OutputCoordinates.Any())
            {
                var sourceCoord = outputDimensions.OutputCoordinates.First();
                
                // Scale coordinates to canvas size with proper padding calculation
                var sourceWidth = sourceCoord.Coordinates.Width;
                var sourceHeight = sourceCoord.Coordinates.Height;
                
                // Calculate scale factor to fit within canvas bounds accounting for actual padding
                // Canvas is 320x200 with 40px padding, so usable area is 240x120
                var maxCanvasWidth = 240.0; // Actual usable width
                var maxCanvasHeight = 120.0; // Actual usable height
                var scaleX = maxCanvasWidth / sourceWidth;
                var scaleY = maxCanvasHeight / sourceHeight;
                var scale = Math.Min(scaleX, scaleY); // Maintain aspect ratio
                
                // Apply conservative scaling to ensure items fit well within canvas
                scale *= 0.8; // Conservative to prevent overflow and allow multiple items
                
                // Update source canvas properties with scaled coordinates (only during creation)
                source.CanvasWidth = Math.Max(50, (int)(sourceWidth * scale)); // Minimum 50px width
                source.CanvasHeight = Math.Max(40, (int)(sourceHeight * scale)); // Minimum 40px height
                
                Debug.WriteLine($"📐 Source {source.Name} initial size set to: {source.CanvasWidth}x{source.CanvasHeight} " +
                              $"(original: {sourceWidth}x{sourceHeight}, scale: {scale:F3})");
            }
        }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"❌ Failed to update canvas properties for {source.Name}: {ex.Message}");
        }
    }

    private DisplayRecordingSource? CreateDisplaySource(SourceItem source)
    {
        try
        {
            // Only create display sources for monitor/display types
            if (source.Type != SourceType.Monitor && source.Type != SourceType.Display)
            {
                Debug.WriteLine($"❌ CreateDisplaySource called for non-monitor source type: {source.Type}");
                return null;
            }

            var displays = Recorder.GetDisplays();
            var display = displays.FirstOrDefault(d => 
                d.DeviceName == source.DeviceId || 
                d.FriendlyName == source.Name);
            
            if (display == null)
            {
                Debug.WriteLine($"❌ Display not found: {source.Name} ({source.DeviceId})");
                return null;
            }

            Debug.WriteLine($"🖥️ Creating display source: {display.FriendlyName}");
            return new DisplayRecordingSource(display)
            {
                RecorderApi = RecorderApi.WindowsGraphicsCapture, // WGC as requested
                IsCursorCaptureEnabled = true,
                IsBorderRequired = false // No border as requested
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"❌ Failed to create display source: {ex.Message}");
            return null;
        }
    }

    private WindowRecordingSource? CreateWindowSource(SourceItem source)
    {
        try
        {
            var windows = Recorder.GetWindows();
            RecordableWindow? targetWindow = null;
            
            // First try to find by process path (most reliable for saved sources)
            if (!string.IsNullOrEmpty(source.ProcessPath))
            {
                // Get current process ID for the process path
                var currentProcessId = source.GetCurrentProcessId();
                if (currentProcessId.HasValue)
                {
                    targetWindow = windows.FirstOrDefault(w =>
                    {
                        try
                        {
                            uint processId;
                            GetWindowThreadProcessId(w.Handle, out processId);
                            return processId == currentProcessId.Value;
                        }
                        catch
                        {
                            return false;
                        }
                    });
                }
                
                // If no window found by process ID, try to find by process name
                if (targetWindow == null)
                {
                    var processName = System.IO.Path.GetFileNameWithoutExtension(source.ProcessPath);
                    if (!string.IsNullOrEmpty(processName))
                    {
                        targetWindow = windows.FirstOrDefault(w =>
                        {
                            try
                            {
                                uint processId;
                                GetWindowThreadProcessId(w.Handle, out processId);
                                using var process = System.Diagnostics.Process.GetProcessById((int)processId);
                                return string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
                            }
                            catch
                            {
                                return false;
                            }
                        });
                    }
                }
            }
            // Fallback to process ID if available
            else if (source.ProcessId.HasValue)
            {
                targetWindow = windows.FirstOrDefault(w =>
                {
                    try
                    {
                        uint processId;
                        GetWindowThreadProcessId(w.Handle, out processId);
                        return processId == source.ProcessId.Value;
                    }
                    catch
                    {
                        return false;
                    }
                });
            }
            // Last resort: find by window handle
            else if (!string.IsNullOrEmpty(source.DeviceId))
            {
                if (IntPtr.TryParse(source.DeviceId, out IntPtr handle))
                {
                    targetWindow = windows.FirstOrDefault(w => w.Handle == handle);
                }
            }
            
            if (targetWindow == null)
            {
                Debug.WriteLine($"❌ Window not found for {source.Name} (ProcessPath: {source.ProcessPath}, ProcessId: {source.ProcessId})");
                return null;
            }

            Debug.WriteLine($"🪟 Creating window source: {targetWindow.Title}");
            return new WindowRecordingSource(targetWindow)
            {
                IsBorderRequired = false, // No border as requested
                IsCursorCaptureEnabled = true
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"❌ Failed to create window source: {ex.Message}");
            return null;
        }
    }

    private List<RecordingSourceBase>? CreateRegionSources(SourceItem source)
    {
        try
        {
            if (source.RegionX == null || source.RegionY == null || 
                source.RegionWidth == null || source.RegionHeight == null)
            {
                Debug.WriteLine($"❌ Region coordinates not set for {source.Name}");
                return null;
            }

            // Create region rectangle in virtual screen coordinates
            var regionRect = new System.Drawing.Rectangle(
                source.RegionX.Value,
                source.RegionY.Value,
                source.RegionWidth.Value,
                source.RegionHeight.Value
            );

            Debug.WriteLine($"📐 Region to capture: {regionRect.X},{regionRect.Y} {regionRect.Width}x{regionRect.Height}");

            // Find intersecting displays using our shared coordinate mapping utility
            var displays = Recorder.GetDisplays();
            var intersectingDisplays = CoordinateMapper.FindIntersectingDisplays(regionRect, displays);

            if (!intersectingDisplays.Any())
            {
                Debug.WriteLine($"❌ No displays intersect with the region");
                return null;
            }

            // Get output dimensions for coordinate mapping
            var tempSources = intersectingDisplays.Select(d => new DisplayRecordingSource(d.display)
            {
                RecorderApi = RecorderApi.WindowsGraphicsCapture,
                IsCursorCaptureEnabled = false,
                IsBorderRequired = false
            }).Cast<RecordingSourceBase>().ToList();
            
            var outputDimensions = Recorder.GetOutputDimensionsForRecordingSources(tempSources);

            // Use our coordinate mapping utility to get proper SourceRect and Position values
            var mappings = CoordinateMapper.MapRegionToDisplays(regionRect, intersectingDisplays, outputDimensions);

            var sources = new List<RecordingSourceBase>();

            // Create recording sources with proper coordinate mapping
            foreach (var mapping in mappings)
            {
                Debug.WriteLine($"📐 Creating recording source for display: {mapping.Display.FriendlyName}");

                var displaySource = new DisplayRecordingSource(mapping.Display)
                {
                    RecorderApi = RecorderApi.WindowsGraphicsCapture,
                    IsCursorCaptureEnabled = true,
                    IsBorderRequired = false,
                    SourceRect = mapping.SourceRect,
                    Position = mapping.Position,
                    // Set OutputSize to match the actual cropped area for this monitor
                    // This works with the canvas-sized final output to scale properly
                    OutputSize = new ScreenSize(mapping.SourceRect.Width, mapping.SourceRect.Height)
                };

                sources.Add(displaySource);

                Debug.WriteLine($"   ✅ Applied SourceRect: {mapping.SourceRect.Left},{mapping.SourceRect.Top} size {mapping.SourceRect.Width}x{mapping.SourceRect.Height}");
                Debug.WriteLine($"   ✅ Applied OutputSize: {mapping.SourceRect.Width}x{mapping.SourceRect.Height}");
                if (mapping.Position != null)
                {
                    Debug.WriteLine($"   ✅ Applied Position: {mapping.Position.Left},{mapping.Position.Top}");
                }
            }

            Debug.WriteLine($"✅ Created {sources.Count} recording sources for region capture");
            return sources;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"❌ Failed to create region sources: {ex.Message}");
            return null;
        }
    }

    private VideoCaptureRecordingSource? CreateWebcamSource(SourceItem source)
    {
        try
        {
            var cameras = Recorder.GetSystemVideoCaptureDevices();
            var camera = cameras.FirstOrDefault(c => 
                c.DeviceName == source.DeviceId || 
                c.FriendlyName == source.Name);
            
            if (camera == null)
            {
                Debug.WriteLine($"❌ Webcam not found: {source.Name}");
                return null;
            }

            Debug.WriteLine($"📷 Creating webcam source: {camera.FriendlyName}");
            return new VideoCaptureRecordingSource(camera);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"❌ Failed to create webcam source: {ex.Message}");
            return null;
        }
    }

    private void OnFrameRecorded(Guid sourceId, FrameRecordedEventArgs e)
    {
        try
        {
            if (!_recorders.TryGetValue(sourceId, out var sourceRecorder))
            {
                return;
            }

            var frameData = e.BitmapData;
            if (frameData == null || frameData.Width <= 0 || frameData.Height <= 0)
            {
                return;
            }

            Debug.WriteLine($"📸 Frame recorded for {sourceRecorder.Source.Name}: " +
                          $"{e.FrameNumber} ({frameData.Width}x{frameData.Height})");

            // Update preview bitmap
            App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    // Create or update WritableBitmap
                    if (sourceRecorder.PreviewBitmap == null || 
                        sourceRecorder.PreviewBitmap.PixelWidth != frameData.Width || 
                        sourceRecorder.PreviewBitmap.PixelHeight != frameData.Height)
                    {
                        sourceRecorder.PreviewBitmap = new WriteableBitmap(
                            (int)frameData.Width, (int)frameData.Height);
                    }

                    // Copy frame data to bitmap - use direct conversion instead of WriteableBitmap
                    // WriteableBitmap has capacity issues, so we'll skip it and go directly to JPEG
                    var jpegData = ConvertToJpeg(frameData);
                    if (jpegData != null)
                    {
                        _lastFrameData[sourceId] = jpegData;
                        
                        // Convert JPEG back to BitmapImage for UI preview
                        var bitmapImage = ConvertJpegToBitmapImage(jpegData);
                        if (bitmapImage != null)
                        {
                            // Raise frame available event
                            var args = new SourceFrameEventArgs(sourceRecorder.Source, bitmapImage)
                            {
                                FrameData = jpegData
                            };
                            FrameAvailable?.Invoke(this, args);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"❌ Error processing frame: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"❌ Error in OnFrameRecorded: {ex.Message}");
        }
    }

    private BitmapImage? ConvertToBitmapImage(WriteableBitmap writableBitmap)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask().Result;
            
            // Get pixels from WriteableBitmap
            using (var pixelStream = writableBitmap.PixelBuffer.AsStream())
            {
                var pixels = new byte[pixelStream.Length];
                pixelStream.Read(pixels, 0, pixels.Length);
                
                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    (uint)writableBitmap.PixelWidth,
                    (uint)writableBitmap.PixelHeight,
                    96, 96, pixels);
            }
            
            encoder.FlushAsync().AsTask().Wait();
            stream.Seek(0);
            
            var bitmapImage = new BitmapImage();
            bitmapImage.SetSource(stream);
            return bitmapImage;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"❌ Failed to convert to BitmapImage: {ex.Message}");
            return null;
        }
    }

    private byte[]? ConvertToJpeg(FrameBitmapData frameData)
    {
        try
        {
            // ScreenRecorderLib returns BGRA rows that are aligned to a 256-byte boundary. The stride
            // (frameData.Stride) may therefore be larger than Width * 4. Copy row-by-row, skipping the
            // pad bytes, to obtain a tightly-packed buffer before feeding it to the encoder.

            int rowPitch = frameData.Stride;          // bytes per row including padding
            int rowSize  = frameData.Width * 4;       // actual BGRA pixels per row
            var packed   = new byte[rowSize * frameData.Height];

            for (int y = 0; y < frameData.Height; y++)
            {
                IntPtr srcRow = IntPtr.Add(frameData.Data, y * rowPitch);
                Marshal.Copy(srcRow, packed, y * rowSize, rowSize);
            }

            using var stream = new InMemoryRandomAccessStream();
            var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream).AsTask().Result;

            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)frameData.Width,
                (uint)frameData.Height,
                96, 96,
                packed);

            encoder.FlushAsync().AsTask().Wait();

            // Return JPEG bytes
            stream.Seek(0);
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            reader.LoadAsync((uint)stream.Size).AsTask().Wait();
            var buffer = new byte[stream.Size];
            reader.ReadBytes(buffer);

            return buffer;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"❌ Failed to convert to JPEG: {ex.Message}");
            return null;
        }
    }

    private BitmapImage? ConvertJpegToBitmapImage(byte[] jpegData)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using var writer = new DataWriter(stream.GetOutputStreamAt(0));
            writer.WriteBytes(jpegData);
            writer.StoreAsync().AsTask().Wait();
            
            stream.Seek(0);
            var bitmapImage = new BitmapImage();
            bitmapImage.SetSource(stream);
            return bitmapImage;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"❌ Failed to convert JPEG to BitmapImage: {ex.Message}");
            return null;
        }
    }

    private void OnRecordingComplete(Guid sourceId, RecordingCompleteEventArgs e)
    {
        Debug.WriteLine($"✅ Recording complete for source {sourceId}");
    }

    private void OnRecordingFailed(Guid sourceId, RecordingFailedEventArgs e)
    {
        Debug.WriteLine($"❌ Recording failed for source {sourceId}: {e.Error}");
        
        // Clean up the failed recorder
        if (_recorders.TryGetValue(sourceId, out var recorder))
        {
            _ = StopCaptureAsync(recorder.Source);
        }
    }

    private void OnStatusChanged(Guid sourceId, RecordingStatusEventArgs e)
    {
        Debug.WriteLine($"📊 Status changed for source {sourceId}: {e.Status}");
    }

    // Unified backend detection methods using same approach as TestApp
    public static List<RecordableDisplay> GetAvailableDisplays()
    {
        return Recorder.GetDisplays().ToList();
    }

    public static List<RecordableWindow> GetAvailableWindows()
    {
        return Recorder.GetWindows().ToList();
    }

    public static List<RecordableCamera> GetAvailableWebcams()
    {
        return Recorder.GetSystemVideoCaptureDevices().ToList();
    }

    // Get available capture formats for a webcam
    public static List<VideoCaptureFormat> GetWebcamFormats(string deviceName)
    {
        return Recorder.GetSupportedVideoCaptureFormatsForDevice(deviceName).ToList();
    }



    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    
    [DllImport("Microsoft.UI.Xaml.dll")]
    private static extern IntPtr WindowNative_GetWindowHandle(IntPtr pThis);
} 