using Windows.Graphics;

namespace Better_SignalRGB_Screen_Capture.Models;

public sealed record SceneProfile
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<SceneSourceSnapshot> Sources { get; init; } = Array.Empty<SceneSourceSnapshot>();
}

/// <summary>Portable source configuration, without selection, preview frames or running capture state.</summary>
public sealed record SceneSourceSnapshot
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonRequired]
    public SourceType Type { get; init; }
    public string? MonitorDeviceId { get; init; }
    public int? ProcessId { get; init; }
    public string? ProcessPath { get; init; }
    public long? WindowHandle { get; init; }
    public string? WindowTitle { get; init; }
    public SceneRegion? Region { get; init; }
    public string? WebcamDeviceId { get; init; }
    public string? WebcamFormatId { get; init; }
    public string? WebsiteUrl { get; init; }
    public double WebsiteZoom { get; init; } = 1;
    public int WebsiteRefreshInterval { get; init; }
    public string WebsiteUserAgent { get; init; } = string.Empty;
    public int WebsiteWidth { get; init; } = 1920;
    public int WebsiteHeight { get; init; } = 1080;
    public string? WebsiteNavigationState { get; init; }
    public int CanvasX { get; init; }
    public int CanvasY { get; init; }
    public int CanvasWidth { get; init; }
    public int CanvasHeight { get; init; }
    public int Rotation { get; init; }
    public double Opacity { get; init; } = 1;
    public double CropLeftPct { get; init; }
    public double CropTopPct { get; init; }
    public double CropRightPct { get; init; }
    public double CropBottomPct { get; init; }
    public int CropRotation { get; init; }
    public bool IsMirroredHorizontally { get; init; }
    public bool IsMirroredVertically { get; init; }
    public bool IsLocked { get; init; }

    public static SceneSourceSnapshot Capture(SourceItem source) => new()
    {
        Id = source.Id, Name = source.Name, Type = source.Type,
        MonitorDeviceId = source.MonitorDeviceId, ProcessId = source.ProcessId, ProcessPath = source.ProcessPath,
        WindowHandle = source.WindowHandle, WindowTitle = source.WindowTitle, WebcamFormatId = source.WebcamFormatId,
        Region = source.RegionBounds is { } region ? new(region.X, region.Y, region.Width, region.Height) : null,
        WebcamDeviceId = source.WebcamDeviceId, WebsiteUrl = source.WebsiteUrl,
        WebsiteZoom = source.WebsiteZoom, WebsiteRefreshInterval = source.WebsiteRefreshInterval,
        WebsiteUserAgent = source.WebsiteUserAgent, WebsiteWidth = source.WebsiteWidth, WebsiteHeight = source.WebsiteHeight,
        WebsiteNavigationState = source.WebsiteNavigationState,
        CanvasX = source.CanvasX, CanvasY = source.CanvasY, CanvasWidth = source.CanvasWidth, CanvasHeight = source.CanvasHeight,
        Rotation = source.Rotation, Opacity = source.Opacity,
        CropLeftPct = source.CropLeftPct, CropTopPct = source.CropTopPct,
        CropRightPct = source.CropRightPct, CropBottomPct = source.CropBottomPct, CropRotation = source.CropRotation,
        IsMirroredHorizontally = source.IsMirroredHorizontally, IsMirroredVertically = source.IsMirroredVertically,
        IsLocked = source.IsLocked
    };

    public SourceItem Restore()
    {
        var source = new SourceItem
        {
            Id = Id, Name = Name, Type = Type, MonitorDeviceId = MonitorDeviceId,
            ProcessId = ProcessId, ProcessPath = ProcessPath,
            WindowHandle = WindowHandle, WindowTitle = WindowTitle, WebcamFormatId = WebcamFormatId,
            RegionBounds = Region is { } region ? new RectInt32(region.X, region.Y, region.Width, region.Height) : null,
            WebcamDeviceId = WebcamDeviceId, WebsiteUrl = WebsiteUrl,
            WebsiteZoom = WebsiteZoom, WebsiteRefreshInterval = WebsiteRefreshInterval,
            WebsiteUserAgent = WebsiteUserAgent, WebsiteWidth = WebsiteWidth, WebsiteHeight = WebsiteHeight,
            WebsiteNavigationState = WebsiteNavigationState,
            CanvasX = CanvasX, CanvasY = CanvasY, CanvasWidth = CanvasWidth, CanvasHeight = CanvasHeight,
            Rotation = Rotation, Opacity = Opacity, IsMirroredHorizontally = IsMirroredHorizontally,
            IsMirroredVertically = IsMirroredVertically, IsLocked = IsLocked, IsSelected = false
        };
        source.SetCrop(CropLeftPct, CropTopPct, CropRightPct, CropBottomPct, CropRotation);
        return source;
    }
}

public sealed record SceneRegion(int X, int Y, int Width, int Height);

public sealed record SceneLibraryDocument
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<SceneProfile> Scenes { get; init; } = Array.Empty<SceneProfile>();
}

public sealed record SceneExportDocument
{
    [System.Text.Json.Serialization.JsonRequired]
    public string Format { get; init; } = "BetterSignalRGB.Scene";
    [System.Text.Json.Serialization.JsonRequired]
    public int Version { get; init; } = 1;
    public int CanvasWidth { get; init; } = 320;
    public int CanvasHeight { get; init; } = 200;
    public SceneProfile? Scene { get; init; }
}
