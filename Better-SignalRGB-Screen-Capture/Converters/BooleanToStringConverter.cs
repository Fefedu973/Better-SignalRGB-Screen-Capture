using Microsoft.UI.Xaml.Data;
using System;

namespace Better_SignalRGB_Screen_Capture.Converters;

public class BooleanToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool booleanValue && parameter is string stringParameter)
        {
            var parts = stringParameter.Split('|');
            if (parts.Length == 2)
            {
                return booleanValue ? parts[0] : parts[1];
            }
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
} 