using Microsoft.UI.Xaml.Data;
using System;

namespace Better_SignalRGB_Screen_Capture.Converters;

public class BooleanToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (parameter is string param && param.Contains("|"))
        {
            var parts = param.Split('|');
            if (parts.Length == 2)
            {
                var trueValue = parts[0];
                var falseValue = parts[1];
                
                if (value is bool boolValue)
                {
                    return boolValue ? trueValue : falseValue;
                }
            }
        }
        
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class BooleanToDoubleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (parameter is string param && param.Contains("|"))
        {
            var parts = param.Split('|');
            if (parts.Length == 2 && 
                double.TryParse(parts[0], out var trueValue) && 
                double.TryParse(parts[1], out var falseValue))
            {
                if (value is bool boolValue)
                {
                    return boolValue ? trueValue : falseValue;
                }
            }
        }
        
        return 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
} 