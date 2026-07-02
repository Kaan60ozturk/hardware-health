using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace HardwareScanner.Converters
{
    /// <summary>
    /// 0-100 puanı dairesel gösterge için StrokeDashArray'e çevirir.
    /// Önemli: WPF'te StrokeDashArray değerleri piksel değil, StrokeThickness'ın
    /// KATLARI cinsindendir. Ayrıca çizgi, elipsin kenarına ortalandığı için
    /// etkin yarıçap = (çap - kalınlık) / 2 olmalıdır.
    /// </summary>
    public class ValueToStrokeDashArrayConverter : IValueConverter
    {
        /// <summary>Çizgi merkezinin yarıçapı. 120px çap, 12px kalınlık için (120-12)/2 = 54.</summary>
        public double Radius { get; set; } = 54;

        /// <summary>Elipste kullanılan StrokeThickness değeri.</summary>
        public double StrokeThickness { get; set; } = 12;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int score)
            {
                double clamped = Math.Max(0, Math.Min(100, score));
                double circumferencePx = 2 * Math.PI * Radius;
                double unit = StrokeThickness > 0 ? StrokeThickness : 1;
                // Piksel uzunluğunu StrokeThickness birimine çevir
                double filled = (clamped / 100.0) * (circumferencePx / unit);

                return new DoubleCollection { filled, 10000 };
            }
            return new DoubleCollection { 0, 10000 };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
