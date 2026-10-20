using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaDashboard.Core;

/// <summary>
/// Identity of a Display: the connector it is plugged into (monitor device path), not its EDID.
/// See docs/adr/0002-display-identity-by-connector.md.
/// </summary>
[JsonConverter(typeof(DisplayIdJsonConverter))]
public readonly record struct DisplayId(string Value)
{
    public override string ToString() => Value;
}

public sealed class DisplayIdJsonConverter : JsonConverter<DisplayId>
{
    public override DisplayId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("DisplayId must be a string"));

    public override void Write(Utf8JsonWriter writer, DisplayId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);

    public override DisplayId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("DisplayId must be a string"));

    public override void WriteAsPropertyName(Utf8JsonWriter writer, DisplayId value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Value);
}

/// <summary>Hardware facts about a connected Display.</summary>
/// <param name="AdapterKey">Displays can only form a Mirror Group when they share a graphics adapter.</param>
/// <param name="PreferredSize">Native resolution; the size the Display gets when it is enabled.</param>
public sealed record DisplayInfo(DisplayId Id, string ModelName, string AdapterKey, PixelSize PreferredSize);

/// <summary>
/// One enabled entry of the Layout: a single Display (one member) or a Mirror Group (several members).
/// </summary>
public sealed record LayoutItem(ImmutableArray<DisplayId> Members, PixelRect Bounds, bool IsPrimary)
{
    public bool IsMirrorGroup => Members.Length > 1;

    public bool Contains(DisplayId id) => Members.Contains(id);
}

/// <summary>The Layout: every enabled Display / Mirror Group and where it sits on the desktop.</summary>
public sealed record DisplayLayout(ImmutableArray<LayoutItem> Items)
{
    public static readonly DisplayLayout Empty = new(ImmutableArray<LayoutItem>.Empty);

    public LayoutItem? Primary => Items.FirstOrDefault(i => i.IsPrimary);

    public LayoutItem? ItemOf(DisplayId id) => Items.FirstOrDefault(i => i.Contains(id));

    public bool IsEnabled(DisplayId id) => ItemOf(id) is not null;

    public IEnumerable<DisplayId> EnabledDisplays => Items.SelectMany(i => i.Members);

    /// <summary>
    /// Structural equality after translating so the Primary sits at the origin.
    /// Member order inside a Mirror Group and item order do not matter.
    /// </summary>
    public bool SameAs(DisplayLayout other) => Canonical() == other.Canonical();

    private string Canonical()
    {
        var primary = Primary;
        int ox = primary?.Bounds.X ?? 0, oy = primary?.Bounds.Y ?? 0;
        var parts = Items
            .Select(i =>
            {
                var b = i.Bounds.Offset(-ox, -oy);
                var members = string.Join("+", i.Members.Select(m => m.Value).Order(StringComparer.Ordinal));
                return $"{members}@{b.X},{b.Y},{b.Width},{b.Height}{(i.IsPrimary ? "*" : "")}";
            })
            .Order(StringComparer.Ordinal);
        return string.Join("|", parts);
    }
}
