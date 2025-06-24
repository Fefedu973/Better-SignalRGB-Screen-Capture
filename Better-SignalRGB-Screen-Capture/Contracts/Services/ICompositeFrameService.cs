using System;
using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Contracts.Services;

public interface ICompositeFrameService
{
    event EventHandler<byte[]>? CompositeFrameAvailable;
    
    void UpdateSourceFrame(SourceItem source, byte[] frameData);
    void RemoveSource(SourceItem source);
    void SetCanvasSize(int width, int height);
    byte[]? GetLatestCompositeFrame();
} 