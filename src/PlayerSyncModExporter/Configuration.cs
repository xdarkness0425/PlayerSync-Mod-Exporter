using Dalamud.Configuration;

namespace PlayerSyncModExporter;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Folder that receives export packages. Must not be inside the PlayerSync cache or Penumbra's mod directory.</summary>
    public string ExportDirectory { get; set; } = string.Empty;

    /// <summary>Optional read-only folder containing copied content-addressed PlayerSync resources. Blank disables lookup.</summary>
    public string AdditionalResourceDirectory { get; set; } = string.Empty;

    public bool WritePmp { get; set; } = true;
    public bool WriteMcdf { get; set; } = true;
    public bool IncludeAppearanceData { get; set; } = true;
    public bool IncludeNonPlayerObjects { get; set; }
    public bool IncludeWorldInLabel { get; set; } = true;

}
