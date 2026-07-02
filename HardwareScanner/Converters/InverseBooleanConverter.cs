using System;
using System.Globalization;
using System.Windows.Data;

namespace HardwareScanner.Converters
{
    /// <summary>Bool değeri tersine çevirir (true -> false). Buton kilitleme için kullanılır.</summary>
    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool b ? !b : value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool b ? !b : value;
        }
    }
}
