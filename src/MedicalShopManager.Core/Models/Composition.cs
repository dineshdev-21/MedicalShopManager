namespace MedicalShopManager.Core.Models;

/// <summary>
/// A salt / composition (e.g. "Paracetamol 500mg"), normalised so substitutes, refill follow-ups and the
/// composition search can key on <see cref="Id"/>. Medicines keep the text in Medicine.Composition and the
/// repository keeps Medicine.CompositionId in step with it.
/// </summary>
public class Composition
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Long-term medication; drives refill follow-ups (DoctorsFollowups module).</summary>
    public bool IsChronic { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
