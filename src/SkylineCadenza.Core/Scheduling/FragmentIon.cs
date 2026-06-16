namespace SkylineCadenza.Core.Scheduling;

/// <summary>
/// One product-ion observation: <see cref="Mz"/> at <see cref="Charge"/>
/// with relative <see cref="Intensity"/>. Carries enough information to
/// write into a BiblioSpec <c>.blib</c> as a reference-spectrum peak.
/// </summary>
public readonly record struct FragmentIon(double Mz, double Intensity, int Charge);
