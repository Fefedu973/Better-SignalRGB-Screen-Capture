using Microsoft.Windows.ApplicationModel.Resources;

namespace Better_SignalRGB_Screen_Capture.Helpers;

public static class ResourceExtensions
{
    private static readonly ResourceLoader _resourceLoader = new();

    public static string GetLocalized(this string resourceKey) => _resourceLoader.GetString(resourceKey);
}
