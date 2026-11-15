using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace DiscImageStudio.Controls;

/// <summary>List rows show the entry's own name, not the whole path: a picked folder reads
/// "Album" and sits next to the tooltip that spells out where it lives. A trailing separator
/// or a drive root has no leaf name, so the path is shown unchanged.</summary>
public sealed class DataSourceLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0) return string.Empty;
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return name.Length == 0 ? path : name;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
