using Microsoft.UI.Xaml.Data;
using System;

namespace Better_SignalRGB_Screen_Capture.Converters;

public class StringToUriConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string url && !string.IsNullOrWhiteSpace(url))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return uri;
            }
        }
        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return (value as Uri)?.ToString() ?? string.Empty;
    }
} 