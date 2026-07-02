using System;
using System.Globalization;
using System.Windows.Data;

namespace HardwareScanner.Converters
{
    /// <summary>
    /// Negatif (bilinmeyen) sayısal değerleri "Bilinmiyor" olarak gösterir.
    /// ConverterParameter ile birim eki verilebilir (ör. " °C", " Saat", " TB").
    /// </summary>
    public class UnknownValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string suffix = parameter as string ?? "";

            double number;
            switch (value)
            {
                case int i: number = i; break;
                case long l: number = l; break;
                case double d: number = d; break;
                case uint u: number = u; break;
                default: return value?.ToString() ?? "Bilinmiyor";
            }

            if (number < 0) return "Bilinmiyor";
            return number.ToString(CultureInfo.CurrentCulture) + suffix;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
