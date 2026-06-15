using System;
using System.Globalization;
using System.Windows.Data;
using SkylineCadenza.Core.Scheduling;

namespace SkylineCadenza.App.ViewModels;

/// <summary>
/// One-way IsChecked converter for binding a <see cref="RadioButton"/>
/// group to an enum <see cref="AcquisitionMode"/>. Set the
/// converter parameter to the target mode via the static instances
/// <see cref="Mtm"/> / <see cref="Prm"/>.
/// </summary>
public sealed class ModeEqualsConverter : IValueConverter
{
    public AcquisitionMode Target { get; }

    public static readonly ModeEqualsConverter Mtm = new(AcquisitionMode.Mtm);
    public static readonly ModeEqualsConverter Prm = new(AcquisitionMode.Prm);

    private ModeEqualsConverter(AcquisitionMode target) { Target = target; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is AcquisitionMode m && m == Target;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && b ? Target : Binding.DoNothing;
    }
}
