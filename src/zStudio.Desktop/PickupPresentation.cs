using System.Windows.Media;
using System.Windows.Media.Imaging;
using zStudio.Core;

namespace zStudio.Desktop;

/// <summary>Pickup catalog presentation shared by source-map rows and their MCP description.</summary>
internal sealed record PickupPresentation(string Type, string Kind, string IconFile)
{
    internal string Badge => Kind switch { "ammo" => "A", "weapon" => "W", _ => "" };
    internal string? BadgeToolTip => Kind switch { "ammo" => "Ammo", "weapon" => "Weapon", _ => null };
    internal ImageSource Image => Images[IconFile].Value;

    internal static PickupPresentation? For(string? type) => type == null ? null : Types.GetValueOrDefault(type);

    // HUD primary/secondary artwork, keyed by the engine's pickup catalog rather than generated instance names.
    // ERFPG is weapon 1.1 (red); the green RFPG icon is the ordinary weapon, which has no pickup type.
    private static PickupPresentation? Describe(MissionPickupType type)
    {
        string? icon = type.Index switch
        {
            0 or 17 => "ic_rfpg2", 1 or 18 => "ic_mort1", 2 or 19 => "ic_mort2",
            3 or 20 => "ic_nplm1", 4 or 21 => "ic_nplm2",
            5 or 22 => "ic_mina1", 6 or 23 => "ic_mina2",
            7 or 24 => "ic_rmin1", 8 or 25 => "ic_rmin2",
            9 or 26 => "ic_lazr1", 10 or 27 => "ic_lazr2",
            11 or 28 => "ic_sonc1", 12 or 29 => "ic_arc1",
            13 or 30 => "ic_miss1", 14 or 31 => "ic_miss2",
            15 or 32 => "ic_nuke1", 16 or 33 => "ic_nuke2",
            34 => "nanite", 35 => "nanite100", 36 => "nanocan",
            37 => "amphchip", 38 => "hovchp01", 39 => "subchp01", _ => null,
        };
        return icon == null ? null : new(type.Name,
            type.Index < 17 ? "ammo" : type.Index < 34 ? "weapon" : type.Index < 37 ? "health" : "technology", icon + ".png");
    }

    private static readonly IReadOnlyDictionary<string, PickupPresentation> Types = MissionPickupType.Catalog
        .Select(Describe).OfType<PickupPresentation>().ToDictionary(p => p.Type, StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, Lazy<ImageSource>> Images = Types.Values.Select(p => p.IconFile)
        .Distinct(StringComparer.Ordinal).ToDictionary(file => file, file => new Lazy<ImageSource>(() =>
        {
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri($"pack://application:,,,/zStudio.Desktop;component/Assets/Pickups/{file}", UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }), StringComparer.Ordinal);
}
