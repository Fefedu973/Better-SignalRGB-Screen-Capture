using Microsoft.UI.Xaml.Controls;

namespace Better_SignalRGB_Screen_Capture.Helpers;

public static class FrameExtensions
{
    public static object? GetPageViewModel(this Frame frame) => frame?.Content?.GetType().GetProperty("ViewModel")?.GetValue(frame.Content, null);
}
