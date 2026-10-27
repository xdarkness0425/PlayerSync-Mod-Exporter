namespace PlayerSyncModExporter.Core.Safety;

/// <summary>
/// The user's explicit, per-export confirmation. It is deliberately separate from file availability:
/// being able to read a cached file does not grant the right to redistribute it.
/// </summary>
public sealed record ExportConsent
{
    public const string PermissionStatement =
        "I have permission from the owner of this character to export their synchronized mod files.";

    public const string RedistributionStatement =
        "I understand that mod files remain the work of their creators. Having a file in my cache does not give me the right to share it, "
        + "and I will not redistribute paid or restricted mods against their creators' terms.";

    /// <summary>The user confirmed they have the character owner's permission.</summary>
    public bool PermissionConfirmed { get; init; }

    /// <summary>The user acknowledged creators' redistribution terms.</summary>
    public bool RedistributionAcknowledged { get; init; }

    /// <summary>The user acknowledged that files with unknown sharing status are included at their own judgement.</summary>
    public bool UnknownSharingStatusAcknowledged { get; init; }

    public DateTimeOffset ConfirmedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool IsComplete => PermissionConfirmed && RedistributionAcknowledged && UnknownSharingStatusAcknowledged;
}

public sealed class ConsentRequiredException : InvalidOperationException
{
    public ConsentRequiredException()
        : base("Export refused: the permission and redistribution confirmations have not all been given.")
    {
    }
}
