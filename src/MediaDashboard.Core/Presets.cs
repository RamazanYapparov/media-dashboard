using System.Collections.Immutable;

namespace MediaDashboard.Core;

/// <summary>A named snapshot of the display configuration and the Active Audio Output.</summary>
/// <param name="AudioOutputName">Name at save time, so a missing output can still be named in a warning.</param>
public sealed record Preset(string Name, DisplayLayout Layout, string? AudioOutputId, string? AudioOutputName);

public abstract record PresetWarning
{
    public sealed record DisplayMissing(DisplayId Id) : PresetWarning;
    public sealed record AudioOutputMissing(string Name) : PresetWarning;
}

/// <summary>What applying a Preset will actually do on the current hardware.</summary>
/// <param name="Layout">Null when none of the Preset's enabled Displays is connected: displays stay as they are.</param>
/// <param name="AudioOutputId">Null when the Preset's output is unavailable: audio stays as it is.</param>
public sealed record PresetPlan(DisplayLayout? Layout, string? AudioOutputId, ImmutableArray<PresetWarning> Warnings);

public static class PresetLogic
{
    /// <summary>Applies what is possible; anything missing becomes a warning (decision Q11).</summary>
    public static PresetPlan Plan(Preset preset, IEnumerable<DisplayInfo> connected, IEnumerable<string> availableAudioOutputs)
    {
        var connectedIds = connected.Select(d => d.Id).ToHashSet();
        var warnings = ImmutableArray.CreateBuilder<PresetWarning>();

        foreach (var missing in preset.Layout.EnabledDisplays.Where(id => !connectedIds.Contains(id)))
            warnings.Add(new PresetWarning.DisplayMissing(missing));

        var items = preset.Layout.Items
            .Select(i => i with { Members = i.Members.Where(connectedIds.Contains).ToImmutableArray() })
            .Where(i => !i.Members.IsEmpty)
            .ToImmutableArray();

        DisplayLayout? layout = null;
        if (!items.IsEmpty)
        {
            if (!items.Any(i => i.IsPrimary))
            {
                var leftmost = DisplayEditor.Leftmost(items);
                items = items.Select(i => i with { IsPrimary = ReferenceEquals(i, leftmost) }).ToImmutableArray();
            }
            layout = LayoutEngine.Relayout(new DisplayLayout(items));
        }

        string? audio = null;
        if (preset.AudioOutputId is { } wanted)
        {
            if (availableAudioOutputs.Contains(wanted)) audio = wanted;
            else warnings.Add(new PresetWarning.AudioOutputMissing(preset.AudioOutputName ?? wanted));
        }

        return new PresetPlan(layout, audio, warnings.ToImmutable());
    }

    /// <summary>The Matching Preset: the first one exactly equal to the current state.</summary>
    public static Preset? FindMatching(IEnumerable<Preset> presets, DisplayLayout current, string? activeAudioOutput) =>
        presets.FirstOrDefault(p => p.AudioOutputId == activeAudioOutput && p.Layout.SameAs(current));
}
