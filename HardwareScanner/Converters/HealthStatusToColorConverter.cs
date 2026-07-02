using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using HardwareScanner.Models;

namespace HardwareScanner.Converters
{
    public class HealthStatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is HealthStatus status)
            {
                return status switch
                {
                    HealthStatus.Good => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50")), // Green
                    HealthStatus.Warning => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFC107")), // Amber/Yellow
                    HealthStatus.Bad => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F44336")), // Red
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E9E9E")) // Grey
                };
            }
            return new SolidColorBrush(Colors.Transparent);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
