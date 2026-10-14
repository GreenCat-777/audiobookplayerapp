using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AudiobookPlayer;

public static class Ui
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.RegisterAttached("IsActive", typeof(bool), typeof(Ui), new PropertyMetadata(false));

    public static bool GetIsActive(DependencyObject d) => (bool)d.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject d, bool v) => d.SetValue(IsActiveProperty, v);
}

/// Visible when the value is null (placeholders).
public sealed class NullToVis : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v == null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// Visible when the value is not null.
public sealed class NotNullToVis : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v == null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// Caps the now-playing chrome so the chapter list keeps the height that is left in the window.
public sealed class ChromeMaxHeightConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        double total = v is double d && d > 0 ? d : 640;
        return Math.Max(180d, total - 220d);
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// Visible when the bool is false.
public sealed class InvBoolToVis : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
