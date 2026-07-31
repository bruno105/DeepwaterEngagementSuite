using ExileCore.Shared.Enums;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using SharpDX;

namespace DeepwaterEngagementSuite;

public record struct IconDisplaySettings(
    MapIconsIndex? Icon = null,
    Color? Tint = null,
    bool ShowOnMap = true,
    bool ShowInWorld = true,
    float? SizeScale = null)
{
    public IconDisplaySettings() : this(null)
    {
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public MapIconsIndex? Icon = Icon;

    public bool ShowOnMap = ShowOnMap;
    public bool ShowInWorld = ShowInWorld;
    public Color? Tint = Tint;

    public float? SizeScale = SizeScale;

    public bool ShouldSerializeIcon() => Icon != null;
    public bool ShouldSerializeSizeScale() => SizeScale != null;
}
