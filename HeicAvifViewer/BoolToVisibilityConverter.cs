using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace HeicAvifViewer;

/// <summary>
/// Converts a <see cref="bool"/> to <see cref="Visibility"/>.
/// True → Visible, False → Collapsed.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility.Visible;
}
