using System.Collections.Immutable;
using System.Windows.Threading;
using MediaDashboard.Core;
using MediaDashboard.Platform;

namespace MediaDashboard;

/// <summary>
/// The single source of truth for the UI: system state (displays, audio), the Draft, and settings.
/// Every mutation goes through here and raises <see cref="StateChanged"/>.
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly SettingsStore _store;
    private readonly DisplayConfigService _displays = new();
    private readonly AudioService _audio = new();
    private readonly DispatcherTimer _audioDebounce;

    public AppController(SettingsStore store)
    {
        _store = store;
        Settings = store.Load();
        Snapshot = _displays.Read();
        Draft = Snapshot.Layout;
        ReadAudio();

        // Core Audio fires several callbacks per change (one per role) on worker threads.
        _audioDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _audioDebounce.Tick += (_, _) =>
        {
            _audioDebounce.Stop();
            ReadAudio();
            StateChanged?.Invoke();
        };
        var dispatcher = Dispatcher.CurrentDispatcher;
        _audio.Changed += () => dispatcher.BeginInvoke(() =>
        {
            _audioDebounce.Stop();
            _audioDebounce.Start();
        });
    }

    public AppSettings Settings { get; }
    public DisplaySnapshot Snapshot { get; private set; }
    public DisplayLayout Draft { get; private set; }
    public ImmutableArray<AudioOutput> AudioOutputs { get; private set; }
    public string? ActiveAudioId { get; private set; }

    public bool IsDraftDirty => !Draft.SameAs(Snapshot.Layout);
    public Preset? MatchingPreset => PresetLogic.FindMatching(Settings.Presets, Snapshot.Layout, ActiveAudioId);
    public DisplayEditor Editor => new(Snapshot.Connected);

    /// <summary>Raised on the UI thread after any state change.</summary>
    public event Action? StateChanged;

    /// <summary>A short message for the flyout's status line.</summary>
    public event Action<string>? Notice;

    // ---------- Displays ----------

    /// <summary>Re-reads displays. A hardware change while a Draft is open discards the Draft (decision Q29).</summary>
    public void RefreshDisplays()
    {
        var fresh = _displays.Read();
        bool changed = !fresh.Layout.SameAs(Snapshot.Layout)
            || !fresh.Connected.Select(d => d.Id).ToHashSet().SetEquals(Snapshot.Connected.Select(d => d.Id));
        if (!changed) return;

        if (IsDraftDirty) Notice?.Invoke(Loc.Get("DraftDiscarded"));
        Snapshot = fresh;
        Draft = fresh.Layout;
        StateChanged?.Invoke();
    }

    public void Edit(Func<DisplayEditor, DisplayLayout, EditResult> operation)
    {
        var result = operation(Editor, Draft);
        if (result.Error is { } error)
        {
            Notice?.Invoke(Loc.Get(error == EditError.LastActiveDisplay ? "LastActive" : "DifferentAdapters"));
            return;
        }
        Draft = result.Layout;
        StateChanged?.Invoke();
    }

    public void DiscardDraft()
    {
        if (!IsDraftDirty) return;
        Draft = Snapshot.Layout;
        StateChanged?.Invoke();
    }

    /// <summary>Applies the Draft, then lets the user keep it or roll back within 10 s (decision Q16).</summary>
    public async Task ApplyDraftAsync()
    {
        if (!IsDraftDirty) return;
        var saved = _displays.Capture();
        if (!TryApply(Draft)) return;

        bool keep = await CountdownWindow.AskAsync(PrimaryScreenBounds());
        if (keep) return;

        _displays.Restore(saved);
        ReloadDisplays();
    }

    // ---------- Audio ----------

    public void SetActiveAudio(string outputId)
    {
        if (outputId == ActiveAudioId) return;
        _audio.SetActive(outputId);
        ReadAudio();
        StateChanged?.Invoke();
    }

    public string AudioLabel(AudioOutput output) =>
        Settings.AudioOutputNames.TryGetValue(output.Id, out var name) ? name : output.Name;

    public AudioOutput? ActiveAudio => AudioOutputs.FirstOrDefault(o => o.Id == ActiveAudioId);

    public void RenameAudio(string outputId, string name) => Mutate(() =>
    {
        if (string.IsNullOrWhiteSpace(name)) Settings.AudioOutputNames.Remove(outputId);
        else Settings.AudioOutputNames[outputId] = name.Trim();
    });

    public void SetAudioHidden(string outputId, bool hidden) => Mutate(() =>
    {
        if (hidden) Settings.HiddenAudioOutputs.Add(outputId);
        else Settings.HiddenAudioOutputs.Remove(outputId);
    });

    // ---------- Display names ----------

    public string DisplayLabel(DisplayId id) => Settings.DisplayLabel(id, Snapshot.Connected);

    /// <summary>Identify number: stable order of connected Displays, as on the Identify overlay.</summary>
    public int DisplayNumber(DisplayId id) =>
        Snapshot.Connected.Select(d => d.Id).Order(DisplayIdOrder).ToList().IndexOf(id) + 1;

    public void RenameDisplay(DisplayId id, string name) => Mutate(() =>
    {
        if (string.IsNullOrWhiteSpace(name)) Settings.DisplayNames.Remove(id);
        else Settings.DisplayNames[id] = name.Trim();
    });

    /// <summary>Named Displays that are not connected: candidates for "This is…" on an Unrecognized Display.</summary>
    public IEnumerable<(DisplayId Id, string Name)> DisconnectedNamedDisplays()
    {
        var connected = Snapshot.Connected.Select(d => d.Id).ToHashSet();
        return Settings.DisplayNames.Where(kv => !connected.Contains(kv.Key)).Select(kv => (kv.Key, kv.Value));
    }

    public void RemapDisplay(DisplayId from, DisplayId to) => Mutate(() => Settings.RemapDisplay(from, to));

    // ---------- Presets ----------

    public void SaveCurrentAsPreset(string name) => Mutate(() =>
        Settings.Presets.Add(CapturePreset(name.Trim())));

    public void OverwritePreset(Preset preset) => Mutate(() =>
        Settings.Presets[Settings.Presets.IndexOf(preset)] = CapturePreset(preset.Name));

    public void RenamePreset(Preset preset, string name) => Mutate(() =>
    {
        if (!string.IsNullOrWhiteSpace(name))
            Settings.Presets[Settings.Presets.IndexOf(preset)] = preset with { Name = name.Trim() };
    });

    public void DeletePreset(Preset preset) => Mutate(() => Settings.Presets.Remove(preset));

    public void MovePreset(Preset preset, int newIndex) => Mutate(() =>
    {
        Settings.Presets.Remove(preset);
        Settings.Presets.Insert(Math.Clamp(newIndex, 0, Settings.Presets.Count), preset);
    });

    /// <summary>
    /// Applies what is possible without a countdown (decision Q16) and reports what was missing (Q11):
    /// from the tray as an on-screen plate (Q33), from the flyout in its status line.
    /// </summary>
    public void ApplyPreset(Preset preset, bool fromTray)
    {
        DiscardDraft();
        var plan = PresetLogic.Plan(preset, Snapshot.Connected, AudioOutputs.Select(o => o.Id));
        var problems = plan.Warnings.Select(DescribeWarning).ToList();

        if (plan.Layout is { } layout && !layout.SameAs(Snapshot.Layout) && !TryApply(layout, report: false))
            problems.Add(Loc.Format("ApplyFailed", _lastApplyError));

        if (plan.AudioOutputId is { } audio && audio != ActiveAudioId)
        {
            _audio.SetActive(audio);
            ReadAudio();
        }
        StateChanged?.Invoke();

        if (problems.Count == 0) return;
        var text = string.Join(Environment.NewLine, problems);
        if (fromTray) OsdWindow.Show(text, PrimaryScreenBounds());
        else Notice?.Invoke(text);
    }

    // ---------- Language / autostart ----------

    public void SetLanguage(LanguageChoice choice) => Mutate(() =>
    {
        Settings.Language = choice;
        Loc.Instance.Apply(choice);
    });

    /// <summary>Autostart defaults to on, once (decision Q8).</summary>
    public void InitializeAutostart()
    {
        if (Settings.AutostartInitialized) return;
        Autostart.Set(true);
        Mutate(() => Settings.AutostartInitialized = true);
    }

    public void Dispose() => _audio.Dispose();

    // ---------- internals ----------

    private int _lastApplyError;

    private bool TryApply(DisplayLayout layout, bool report = true)
    {
        try
        {
            _displays.Apply(layout);
            return true;
        }
        catch (DisplayConfigException ex)
        {
            _lastApplyError = ex.Win32Error;
            if (report) Notice?.Invoke(Loc.Format("ApplyFailed", ex.Win32Error));
            return false;
        }
        finally
        {
            ReloadDisplays();
        }
    }

    /// <summary>Re-reads after our own change: the Draft becomes the new reality, nothing is "discarded".</summary>
    private void ReloadDisplays()
    {
        Snapshot = _displays.Read();
        Draft = Snapshot.Layout;
        StateChanged?.Invoke();
    }

    private void ReadAudio()
    {
        AudioOutputs = _audio.GetOutputs();
        ActiveAudioId = _audio.GetActiveOutputId();
    }

    private Preset CapturePreset(string name)
    {
        var audio = ActiveAudio;
        return new Preset(name, Snapshot.Layout, audio?.Id, audio is null ? null : AudioLabel(audio));
    }

    private string DescribeWarning(PresetWarning warning) => warning switch
    {
        PresetWarning.DisplayMissing m => Loc.Format("DisplayMissing",
            Settings.DisplayNames.TryGetValue(m.Id, out var n) ? n : m.Id.Value),
        PresetWarning.AudioOutputMissing a => Loc.Format("AudioMissing", a.Name),
        _ => warning.ToString(),
    };

    private PixelRect PrimaryScreenBounds() =>
        Snapshot.Layout.Primary?.Bounds ?? new PixelRect(0, 0, 1920, 1080);

    private void Mutate(Action change)
    {
        change();
        _store.Save(Settings);
        StateChanged?.Invoke();
    }

    private static readonly IComparer<DisplayId> DisplayIdOrder =
        Comparer<DisplayId>.Create((a, b) => string.CompareOrdinal(a.Value, b.Value));
}
