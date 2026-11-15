using System.Globalization;
using System.IO;
using System.Windows.Data;
using MahApps.Metro.IconPacks;

namespace DiscImageStudio.Controls;

/// <summary>Rows in the disc-contents list keep the picked path as a plain string, so the
/// folder-or-file glyph is decided from the path itself rather than carried in the item.</summary>
public sealed class DataSourceKindConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string path && Directory.Exists(path)
            ? PackIconLucideKind.Folder
            : PackIconLucideKind.File;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
