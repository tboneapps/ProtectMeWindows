using System.Globalization;
using System.Windows.Data;

namespace ProtectMe.App;

public sealed class ExeToIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return AppIconHelper.GetIcon(value as string);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
