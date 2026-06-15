using System;
using System.Globalization;
using System.Windows.Data;

namespace SkylineCadenza.App.ViewModels;

/// <summary>
/// One-way IsChecked converter for binding RadioButtons to an arbitrary
/// enum value. Set the converter parameter via XAML to the enum value the
/// button should match (e.g. <c>{Binding TargetMode, Converter=...,
/// ConverterParameter={x:Static sched:TargetListMode.Exclusive}}</c>).
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static readonly EnumEqualsConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value != null && parameter != null && value.Equals(parameter);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && b && parameter != null ? parameter : Binding.DoNothing;
    }
}
