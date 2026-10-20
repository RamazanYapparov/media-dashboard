using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaDashboard.Core;

public enum LanguageChoice { Auto, Russian, English }

/// <summary>Everything the user configures. Persisted as JSON.</summary>
public sealed class AppSettings
{
    public LanguageChoice Language { get; set; } = LanguageChoice.Auto;
    public Dictionary<DisplayId, string> DisplayNames { get; set; } = [];
    public Dictionary<string, string> AudioOutputNames { get; set; } = [];
    public HashSet<string> HiddenAudioOutputs { get; set; } = [];
    public List<Preset> Presets { get; set; } = [];

    /// <summary>Autostart is enabled once on first run (decision Q8); afterwards the user's choice stands.</summary>
    public bool AutostartInitialized { get; set; }

    /// <summary>
    /// "This is Left": an Unrecognized Display takes over the name and Preset references of a
    /// named Display that is no longer connected (cables were swapped).
    /// </summary>
    public void RemapDisplay(DisplayId from, DisplayId to)
    {
        if (DisplayNames.Remove(from, out var name)) DisplayNames[to] = name;
        for (int i = 0; i < Presets.Count; i++)
        {
            var p = Presets[i];
            var items = p.Layout.Items
                .Select(item => item with { Members = item.Members.Select(m => m == from ? to : m).ToImmutableArray() })
                .ToImmutableArray();
            Presets[i] = p with { Layout = new DisplayLayout(items) };
        }
    }

    /// <summary>The label shown for a Display: its Display Name, or "Model N" while it is Unrecognized.</summary>
    public string DisplayLabel(DisplayId id, IReadOnlyCollection<DisplayInfo> connected)
    {
        if (DisplayNames.TryGetValue(id, out var name)) return name;
        var info = connected.FirstOrDefault(d => d.Id == id);
        if (info is null) return id.Value;

        var sameModel = connected.Where(d => d.ModelName == info.ModelName).OrderBy(d => d.Id.Value, StringComparer.Ordinal).ToList();
        return sameModel.Count == 1 ? info.ModelName : $"{info.ModelName} {sameModel.IndexOf(info) + 1}";
    }

    public bool IsUnrecognized(DisplayId id) => !DisplayNames.ContainsKey(id);
}

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static SettingsStore Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaDashboard", "settings.json"));

    public AppSettings Load()
    {
        if (!File.Exists(path)) return new AppSettings();
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<AppSettings>(stream, Options) ?? new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, path, overwrite: true);
    }
}
