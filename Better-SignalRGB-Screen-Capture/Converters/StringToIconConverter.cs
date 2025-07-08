using Microsoft.UI.Xaml.Data;
using Better_SignalRGB_Screen_Capture.Models;

namespace Better_SignalRGB_Screen_Capture.Converters;

public class StringToIconConverter : IValueConverter
{
    public string WebcamIcon { get; set; } = "\uE960"; // Camera icon
    public string MonitorIcon { get; set; } = "\uE7F4"; // Monitor icon  
    public string WebsiteIcon { get; set; } = "\uE774"; // Globe icon
    public string RegionIcon { get; set; } = "\uEF20"; // Crop icon
    public string ProcessIcon { get; set; } = "\uE756"; // Window icon

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string sourceType)
        {
            return sourceType.ToLower() switch
            {
                "webcam" => WebcamIcon,
                "monitor" => MonitorIcon,
                "display" => MonitorIcon, // Display is an alias for Monitor
                "website" => WebsiteIcon,
                "region" => RegionIcon,
                "process" => ProcessIcon,
                "window" => ProcessIcon, // Window is an alias for Process
                _ => "\uE7C3" // Default icon
            };
        }
        else if (value is SourceType enumValue)
        {
            return enumValue switch
            {
                SourceType.Webcam => WebcamIcon,
                SourceType.Monitor => MonitorIcon, // This also handles Display since Display = Monitor
                SourceType.Website => WebsiteIcon,
                SourceType.Region => RegionIcon,
                SourceType.Process => ProcessIcon, // This also handles Window since Window = Process
                _ => "\uE7C3" // Default icon
            };
        }
        return "\uE7C3"; // Default icon
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
} 