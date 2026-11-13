namespace MedicalShopManager.Core.Modules.ControlCenter;

/// <summary>One user-facing validation problem; <see cref="Field"/> is the property name it belongs to.</summary>
public sealed record SettingsError(string Field, string Message)
{
    public override string ToString() => Message;
}

/// <summary>Thrown by Control Center services when input fails validation. Nothing has been saved.</summary>
public sealed class ControlCenterValidationException : Exception
{
    public ControlCenterValidationException(IReadOnlyList<SettingsError> errors)
        : base(errors.Count == 0 ? "Invalid settings." : string.Join(Environment.NewLine, errors.Select(e => e.Message)))
    {
        Errors = errors;
    }

    public ControlCenterValidationException(string field, string message)
        : this(new[] { new SettingsError(field, message) })
    {
    }

    public IReadOnlyList<SettingsError> Errors { get; }
}
