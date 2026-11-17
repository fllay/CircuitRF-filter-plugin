namespace FiltSyn.Core;

/// <summary>One Rogers laminate: design Er and loss tangent at about 10 GHz, and the usual core thicknesses in mil.</summary>
public sealed record RogersMaterial(string Name, double Er, double TanD, int[] ThicknessMil, string Family)
{
    public const double MetresPerMil = 25.4e-6;

    public Substrate Make(double h, double copper = 17.5e-6) => new(Er, h, copper, TanD);
    public Substrate Make(int mil, double copper = 17.5e-6) => Make(mil * MetresPerMil, copper);
    public override string ToString() => $"{Name} (Er {Er:0.##}, tan d {TanD})";
}

/// <summary>
/// Library of common Rogers microwave laminates (Er is the circuit-design value where Rogers gives one, e.g. 3.66 for RO4350B, otherwise the datasheet value; tan d at ~10 GHz, from memory of the
/// published datasheets: check the current datasheet for the exact lot, thickness and frequency before building).
/// Stocked thicknesses vary by distributor, the lists are typical.
/// </summary>
public static class RogersMaterials
{
    public static readonly RogersMaterial[] All =
    {
        new("RT/duroid 5870", 2.33, 0.0012, new[] { 5, 10, 15, 20, 31, 62, 125 }, "RT/duroid"),
        new("RT/duroid 5880", 2.20, 0.0009, new[] { 5, 10, 15, 20, 31, 62, 125 }, "RT/duroid"),
        new("RT/duroid 6002", 2.94, 0.0012, new[] { 5, 10, 20, 30, 60 }, "RT/duroid"),
        new("RT/duroid 6006", 6.15, 0.0019, new[] { 10, 25, 50, 75 }, "RT/duroid"),
        new("RT/duroid 6010LM", 10.2, 0.0023, new[] { 10, 25, 50, 75 }, "RT/duroid"),
        new("RO3003", 3.00, 0.0010, new[] { 5, 10, 20, 30, 60 }, "RO3000"),
        new("RO3203", 3.02, 0.0016, new[] { 10, 20, 30, 60 }, "RO3000"),
        new("RO3006", 6.15, 0.0020, new[] { 10, 25, 50 }, "RO3000"),
        new("RO3010", 10.2, 0.0022, new[] { 5, 10, 25, 50 }, "RO3000"),
        new("RO3035", 3.50, 0.0015, new[] { 10, 20, 30, 60 }, "RO3000"),
        new("RO4003C", 3.55, 0.0027, new[] { 8, 12, 16, 20, 32, 60 }, "RO4000"),
        new("RO4350B", 3.66, 0.0037, new[] { 10, 20, 30, 60 }, "RO4000"),
        new("RO4360G2", 6.15, 0.0038, new[] { 8, 12, 16, 24, 32, 60 }, "RO4000"),
        new("RO4730G3", 3.00, 0.0029, new[] { 20, 30, 60 }, "RO4000"),
        new("RO4835", 3.66, 0.0037, new[] { 10, 20, 30, 60 }, "RO4000"),
        new("TMM3", 3.27, 0.0020, new[] { 15, 20, 25, 30, 50, 60, 100 }, "TMM"),
        new("TMM4", 4.50, 0.0020, new[] { 15, 20, 25, 30, 50, 60, 100 }, "TMM"),
        new("TMM6", 6.00, 0.0023, new[] { 15, 25, 50, 75, 100 }, "TMM"),
        new("TMM10", 9.20, 0.0022, new[] { 15, 25, 50, 75, 100 }, "TMM"),
        new("TMM10i", 9.80, 0.0020, new[] { 15, 25, 50, 75, 100 }, "TMM"),
        new("TMM13i", 12.85, 0.0019, new[] { 15, 25, 50, 75, 100 }, "TMM"),
    };

    public static IEnumerable<string> Names => All.Select(m => m.Name);

    public static RogersMaterial? Find(string? name) =>
        All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
}
