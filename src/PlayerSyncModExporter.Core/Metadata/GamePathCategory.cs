using System.Text.RegularExpressions;

namespace PlayerSyncModExporter.Core.Metadata;

/// <summary>
/// Describes what a game path belongs to, from the game's folder conventions alone (always <see cref="MetadataClassification.Inferred"/>).
/// It names the game item a file replaces, never the mod it came from.
/// </summary>
public static partial class GamePathCategory
{
    private static readonly Dictionary<string, string> EquipSlots = new(StringComparer.Ordinal)
    {
        ["met"] = "head", ["top"] = "body", ["glv"] = "hands", ["dwn"] = "legs", ["sho"] = "feet",
        ["ear"] = "earrings", ["nek"] = "necklace", ["wrs"] = "bracelets", ["rir"] = "right ring", ["ril"] = "left ring",
    };

    public static string Describe(string gamePath)
    {
        var p = gamePath.ToLowerInvariant();
        if (Equipment().Match(p) is { Success: true } eq)
            return $"Equipment e{eq.Groups["id"].Value}{Slot(p)}";
        if (Accessory().Match(p) is { Success: true } acc)
            return $"Accessory a{acc.Groups["id"].Value}{Slot(p)}";
        if (Weapon().Match(p) is { Success: true } wep)
            return $"Weapon w{wep.Groups["id"].Value} body b{wep.Groups["body"].Value}";
        if (Human().Match(p) is { Success: true } hum)
        {
            var part = hum.Groups["part"].Value switch
            {
                "hair" => "hair", "face" => "face", "body" => "body", "tail" => "tail", "zear" => "ears", _ => hum.Groups["part"].Value,
            };
            return $"Character c{hum.Groups["race"].Value} {part} {hum.Groups["kind"].Value}{hum.Groups["id"].Value}";
        }

        if (Monster().Match(p) is { Success: true } mon)
            return $"Monster m{mon.Groups["id"].Value} body b{mon.Groups["body"].Value}";
        if (Demihuman().Match(p) is { Success: true } dem)
            return $"Demihuman d{dem.Groups["id"].Value} equipment e{dem.Groups["body"].Value}";
        if (p.Contains("/animation/", StringComparison.Ordinal) || p.EndsWith(".pap", StringComparison.Ordinal) || p.EndsWith(".tmb", StringComparison.Ordinal))
            return "Animation";
        if (p.StartsWith("vfx/", StringComparison.Ordinal) || p.EndsWith(".avfx", StringComparison.Ordinal) || p.EndsWith(".atex", StringComparison.Ordinal))
            return "Visual effect";
        if (p.StartsWith("sound/", StringComparison.Ordinal) || p.EndsWith(".scd", StringComparison.Ordinal))
            return "Sound";
        if (p.StartsWith("chara/common/", StringComparison.Ordinal))
            return "Shared character resource";
        if (p.StartsWith("shader/", StringComparison.Ordinal))
            return "Shader";
        return "Other game file";
    }

    private static string Slot(string p)
    {
        var m = SlotSuffix().Match(p);
        return m.Success && EquipSlots.TryGetValue(m.Groups["slot"].Value, out var slot) ? $" ({slot})" : string.Empty;
    }

    [GeneratedRegex(@"^chara/equipment/e(?<id>\d{4})/")]
    private static partial Regex Equipment();

    [GeneratedRegex(@"^chara/accessory/a(?<id>\d{4})/")]
    private static partial Regex Accessory();

    [GeneratedRegex(@"^chara/weapon/w(?<id>\d{4})/obj/body/b(?<body>\d{4})/")]
    private static partial Regex Weapon();

    [GeneratedRegex(@"^chara/human/c(?<race>\d{4})/obj/(?<part>hair|face|body|tail|zear)/(?<kind>[hfbtz])(?<id>\d{4})/")]
    private static partial Regex Human();

    [GeneratedRegex(@"^chara/monster/m(?<id>\d{4})/obj/body/b(?<body>\d{4})/")]
    private static partial Regex Monster();

    [GeneratedRegex(@"^chara/demihuman/d(?<id>\d{4})/obj/equipment/e(?<body>\d{4})/")]
    private static partial Regex Demihuman();

    [GeneratedRegex(@"_(?<slot>met|top|glv|dwn|sho|ear|nek|wrs|rir|ril)[_.]")]
    private static partial Regex SlotSuffix();
}
