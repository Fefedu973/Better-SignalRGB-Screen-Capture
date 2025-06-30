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
using Better_SignalRGB_Screen_Capture.Models;
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
    private int _frameRate = 10;

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
        try
        {
            Debug.WriteLine($"🎬 Starting individual recorder for: {source.Name} ({source.Type})");

            // Stop existing recorder for this source if it exists
            if (_recorders.ContainsKey(source.Id))
            {
                await StopCaptureAsync(source);
            }

            // Create recorder options for this specific source (like TestApp does)
            var options = CreateRecorderOptions(source);
            if (options == null)
            {
                Debug.WriteLine($"❌ Failed to create recorder options for {source.Name}");
                return;
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

    public async Task StopCaptureAsync(SourceItem source)
    {
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
        return _recorders.ContainsKey(source.Id) && 
               _recorders.TryGetValue(source.Id, out var recorder) &&
               recorder.Recorder?.Status == RecorderStatus.Recording;
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
        // Return the first available frame, or a black 800x600 frame if none available
        var firstFrame = _lastFrameData.Values.FirstOrDefault();
        if (firstFrame != null)
        {
            return firstFrame;
        }

        // Generate a black 800x600 JPEG frame as fallback
        return CreateBlackFrame800x600();
    }

    private byte[] CreateBlackFrame800x600()
    {
        try
        {
            var blackBitmap = new WriteableBitmap(800, 600);
            
            // Create black frame
            using var stream = new InMemoryRandomAccessStream();
            var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream).AsTask().Result;
            
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                800, 600,
                96, 96,
                new byte[800 * 600 * 4]); // All zeros = black
            
            encoder.FlushAsync().AsTask().Wait();
            
            // Convert to byte array
            stream.Seek(0);
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            reader.LoadAsync((uint)stream.Size).AsTask().Wait();
            var buffer = new byte[stream.Size];
            reader.ReadBytes(buffer);
            
            return buffer;
        }
        catch
        {
            // Return minimal JPEG header as ultimate fallback
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
    }

    private RecorderOptions? CreateRecorderOptions(SourceItem source)
    {
        try
        {
            // Create the recording source based on type
            var recordingSource = CreateRecordingSource(source);
            if (recordingSource == null)
            {
                return null;
            }

            // Configure common settings for all sources
            if (recordingSource is DisplayRecordingSource displaySource)
            {
                displaySource.IsCursorCaptureEnabled = true;
            }
            else if (recordingSource is WindowRecordingSource windowSource)
            {
                windowSource.IsCursorCaptureEnabled = true;
            }
            
            recordingSource.IsVideoCaptureEnabled = true; // Video enabled
            recordingSource.Stretch = StretchMode.Fill; // Fill the aspect ratio
            recordingSource.IsVideoFramePreviewEnabled = true; // Enable frame preview
            // Let the library pick an optimal preview size (full resolution) by not forcing a 150-pixel height.
            // Removing the hard-coded height of 150px avoids unintended down-scaling that caused mismatched resolutions.

            // Apply custom size if draggable source has been resized
            if (source.CanvasWidth > 0 && source.CanvasHeight > 0)
            {
                recordingSource.OutputSize = new ScreenSize((int)source.CanvasWidth, (int)source.CanvasHeight);
            }

            // Apply source rect if cropped
            if (source.CropLeftPct > 0 || source.CropTopPct > 0 || source.CropRightPct > 0 || source.CropBottomPct > 0)
            {
                var sourceRect = CalculateSourceRect(source, recordingSource);
                if (sourceRect != null)
                {
                    recordingSource.SourceRect = sourceRect;
                }
            }

            // Create recorder options (similar to TestApp)
            var options = new RecorderOptions
            {
                SourceOptions = new SourceOptions
                {
                    RecordingSources = new List<RecordingSourceBase> { recordingSource }
                },
                OutputOptions = new OutputOptions
                {
                    RecorderMode = RecorderMode.Video, // Video mode
                    Stretch = StretchMode.Fill,
                    IsVideoCaptureEnabled = true,
                    IsVideoFramePreviewEnabled = true // Let default (full-res) preview size apply
                },
                VideoEncoderOptions = new VideoEncoderOptions
                {
                    Encoder = new H264VideoEncoder
                    {
                        BitrateMode = H264BitrateControlMode.Quality,
                        EncoderProfile = H264Profile.Baseline // Baseline for better performance
                    },
                    Framerate = _frameRate,
                    Quality = 60, // Lower quality for better performance
                    IsHardwareEncodingEnabled = true, // Hardware encoding
                    IsLowLatencyEnabled = true, // Low latency for live preview
                    IsThrottlingDisabled = false, // Keep throttling enabled
                    IsFixedFramerate = false // Variable framerate for performance
                },
                AudioOptions = new AudioOptions
                {
                    IsAudioEnabled = false // No audio as requested
                },
                MouseOptions = new MouseOptions
                {
                    IsMousePointerEnabled = source.Type != SourceType.Webcam // Show mouse except webcam
                },
                LogOptions = new LogOptions
                {
                    IsLogEnabled = false // Disable logging for performance
                }
            };

            // Apply custom output size if set
            if (source.CanvasWidth > 0 && source.CanvasHeight > 0)
            {
                options.OutputOptions.OutputFrameSize = new ScreenSize((int)source.CanvasWidth, (int)source.CanvasHeight);
            }

            Debug.WriteLine($"📋 Recorder options for {source.Name}:");
            Debug.WriteLine($"   - Type: {source.Type}");
            Debug.WriteLine($"   - Output size: {recordingSource.OutputSize?.Width ?? 0}x{recordingSource.OutputSize?.Height ?? 0}");
            Debug.WriteLine($"   - Frame rate: {_frameRate} FPS");
            Debug.WriteLine($"   - Cursor: {(recordingSource is DisplayRecordingSource d ? d.IsCursorCaptureEnabled : recordingSource is WindowRecordingSource w ? w.IsCursorCaptureEnabled : false)}");
            Debug.WriteLine($"   - API: {(recordingSource is DisplayRecordingSource disp ? disp.RecorderApi.ToString() : "N/A")}");

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
            var recordingSource = source.Type switch
            {
                SourceType.Display or SourceType.Monitor => CreateDisplaySource(source) as RecordingSourceBase,
                SourceType.Window or SourceType.Process => CreateWindowSource(source) as RecordingSourceBase,
                SourceType.Region => CreateRegionSource(source) as RecordingSourceBase,
                SourceType.Webcam => CreateWebcamSource(source) as RecordingSourceBase,
                SourceType.Website => null as RecordingSourceBase, // Website will be handled as iframe, not recording
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
            // Only set size during initial creation (when size is default)
            if (source.CanvasWidth <= 100 && source.CanvasHeight <= 80)
            {
                // Use the same backend as TestApp to get proper coordinates and sizing
                var outputDimensions = Recorder.GetOutputDimensionsForRecordingSources(new[] { recordingSource });
                if (outputDimensions.OutputCoordinates.Any())
                {
                    var sourceCoord = outputDimensions.OutputCoordinates.First();
                    
                    // Scale coordinates to canvas size (800x600) while maintaining aspect ratio
                    var sourceWidth = sourceCoord.Coordinates.Width;
                    var sourceHeight = sourceCoord.Coordinates.Height;
                    
                    // Calculate scale factor to fit within canvas bounds
                    var scaleX = 800.0 / sourceWidth;
                    var scaleY = 600.0 / sourceHeight;
                    var scale = Math.Min(scaleX, scaleY); // Maintain aspect ratio
                    
                    // Update source canvas properties with scaled coordinates (only during creation)
                    source.CanvasWidth = (int)(sourceWidth * scale);
                    source.CanvasHeight = (int)(sourceHeight * scale);
                    
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
            
            // For process sources, find window by process ID
            if (source.ProcessId.HasValue)
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
            // Otherwise find by window handle
            else if (!string.IsNullOrEmpty(source.DeviceId))
            {
                if (IntPtr.TryParse(source.DeviceId, out IntPtr handle))
                {
                    targetWindow = windows.FirstOrDefault(w => w.Handle == handle);
                }
            }
            
            if (targetWindow == null)
            {
                Debug.WriteLine($"❌ Window not found for {source.Name}");
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

    private DisplayRecordingSource? CreateRegionSource(SourceItem source)
    {
        try
        {
            if (source.RegionX == null || source.RegionY == null || 
                source.RegionWidth == null || source.RegionHeight == null)
            {
                Debug.WriteLine($"❌ Region coordinates not set for {source.Name}");
                return null;
            }

            // Find which monitor(s) this region spans
            var displays = Recorder.GetDisplays();
            var regionRect = new System.Drawing.Rectangle(
                source.RegionX.Value,
                source.RegionY.Value,
                source.RegionWidth.Value,
                source.RegionHeight.Value
            );

            // Find the primary monitor that contains most of the region
            RecordableDisplay? bestDisplay = null;
            int maxOverlapArea = 0;

            foreach (var display in displays)
            {
                if (display.Position == null || display.OutputSize == null) continue;
                
                var displayRect = new System.Drawing.Rectangle(
                    (int)display.Position.Left,
                    (int)display.Position.Top,
                    (int)display.OutputSize.Width,
                    (int)display.OutputSize.Height
                );

                // Calculate overlap
                var overlap = System.Drawing.Rectangle.Intersect(regionRect, displayRect);
                var overlapArea = overlap.Width * overlap.Height;

                if (overlapArea > maxOverlapArea)
                {
                    maxOverlapArea = overlapArea;
                    bestDisplay = display;
                }
            }

            if (bestDisplay == null)
            {
                // Fallback to primary display
                bestDisplay = displays.FirstOrDefault();
            }

            if (bestDisplay == null)
            {
                Debug.WriteLine($"❌ No display found for region");
                return null;
            }

            Debug.WriteLine($"📐 Creating region source on display: {bestDisplay.FriendlyName}");
            var displaySource = new DisplayRecordingSource(bestDisplay)
            {
                RecorderApi = RecorderApi.WindowsGraphicsCapture,
                IsCursorCaptureEnabled = true,
                IsBorderRequired = false
            };

            // Set the source rect to the region coordinates relative to the display
            if (bestDisplay.Position != null)
            {
                displaySource.SourceRect = new ScreenRect(
                    (int)(regionRect.Left - bestDisplay.Position.Left),
                    (int)(regionRect.Top - bestDisplay.Position.Top),
                    (int)(regionRect.Right - bestDisplay.Position.Left),
                    (int)(regionRect.Bottom - bestDisplay.Position.Top)
                );
            }

            return displaySource;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"❌ Failed to create region source: {ex.Message}");
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

    private ScreenRect? CalculateSourceRect(SourceItem source, RecordingSourceBase recordingSource)
    {
        try
        {
            // Get the natural size of the source
            int naturalWidth = 0, naturalHeight = 0;
            
            if (recordingSource is DisplayRecordingSource display && display.OutputSize != null)
            {
                naturalWidth = (int)display.OutputSize.Width;
                naturalHeight = (int)display.OutputSize.Height;
            }
            else if (recordingSource is WindowRecordingSource window && window.OutputSize != null)
            {
                naturalWidth = (int)window.OutputSize.Width;
                naturalHeight = (int)window.OutputSize.Height;
            }
            
            if (naturalWidth == 0 || naturalHeight == 0)
            {
                return null;
            }

            // Calculate crop rectangle from percentages (0.0 to 1.0)
            var left = (int)(source.CropLeftPct * naturalWidth);
            var top = (int)(source.CropTopPct * naturalHeight);
            var right = (int)(naturalWidth - source.CropRightPct * naturalWidth);
            var bottom = (int)(naturalHeight - source.CropBottomPct * naturalHeight);

            return new ScreenRect(left, top, right, bottom);
        }
        catch
        {
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