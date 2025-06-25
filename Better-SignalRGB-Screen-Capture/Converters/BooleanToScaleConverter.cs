using Microsoft.UI.Xaml.Data;
using System;

namespace Better_SignalRGB_Screen_Capture.Converters;

public class BooleanToScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool and true)
        {
            return -1.0;
        }
        return 1.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is double scale)
        {
            return scale == -1.0;
        }
        return false;
    }
}

public class ZoomPercentageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double zoom)
        {
            return $"{zoom * 100:F0}%";
        }
        return "100%";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
} 