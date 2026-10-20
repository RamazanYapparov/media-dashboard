using MediaDashboard.Core;

namespace MediaDashboard.Core.Tests;

public class PresetTests
{
    private static readonly DisplayId Left = new("left"), Right = new("right"), Tv = new("tv");

    private static readonly DisplayInfo[] All =
    [
        new(Left, "Mi Monitor", "amd", new PixelSize(2560, 1440)),
        new(Right, "Mi Monitor", "amd", new PixelSize(2560, 1440)),
        new(Tv, "Mi TV", "amd", new PixelSize(3840, 2160)),
    ];

    /// <summary>"Left off, Right primary, TV on to the right; sound: headphones".</summary>
    private static Preset WorkWithTv() => new("Work",
        new DisplayLayout(
        [
            new LayoutItem([Right], new PixelRect(0, 0, 2560, 1440), true),
            new LayoutItem([Tv], new PixelRect(2560, 0, 3840, 2160), false),
        ]),
        "headphones", "ABKO N800");

    [Fact]
    public void Missing_display_and_audio_output_become_warnings_and_the_rest_is_applied()
    {
        var plan = PresetLogic.Plan(WorkWithTv(), All.Where(d => d.Id != Tv), ["soundbar"]);

        Assert.Equal([Right], plan.Layout!.Items.Single().Members.AsEnumerable());
        Assert.Null(plan.AudioOutputId);
        Assert.Equal(
            [new PresetWarning.DisplayMissing(Tv), new PresetWarning.AudioOutputMissing("ABKO N800")],
            plan.Warnings.AsEnumerable());
    }

    [Fact]
    public void Missing_primary_falls_back_to_the_leftmost_remaining_display()
    {
        var plan = PresetLogic.Plan(WorkWithTv(), All.Where(d => d.Id != Right), ["headphones"]);

        Assert.Equal([Tv], plan.Layout!.Primary!.Members.AsEnumerable());
        Assert.Equal(new PixelRect(0, 0, 3840, 2160), plan.Layout.Primary.Bounds);
    }

    [Fact]
    public void No_connected_display_leaves_displays_untouched_but_still_switches_audio()
    {
        var plan = PresetLogic.Plan(WorkWithTv(), [], ["headphones"]);

        Assert.Null(plan.Layout);
        Assert.Equal("headphones", plan.AudioOutputId);
    }

    [Fact]
    public void Matching_preset_requires_both_layout_and_audio_output()
    {
        var presets = new[] { WorkWithTv(), WorkWithTv() with { Name = "Soundbar", AudioOutputId = "soundbar" } };
        var current = WorkWithTv().Layout;

        Assert.Equal("Soundbar", PresetLogic.FindMatching(presets, current, "soundbar")?.Name);
        Assert.Null(PresetLogic.FindMatching(presets, current, "tv-hdmi"));
    }

    [Fact]
    public void Matching_ignores_translation_and_member_order()
    {
        var mirrored = new DisplayLayout([new LayoutItem([Right, Tv], new PixelRect(0, 0, 2560, 1440), true)]);
        var preset = new Preset("Mirror", new DisplayLayout([new LayoutItem([Tv, Right], new PixelRect(0, 0, 2560, 1440), true)]), null, null);

        Assert.Same(preset, PresetLogic.FindMatching([preset], mirrored, null));
    }

    [Fact]
    public void Remapping_an_unrecognized_display_moves_its_name_and_preset_references()
    {
        var swapped = new DisplayId("right-on-new-port");
        var settings = new AppSettings { DisplayNames = { [Right] = "Правый" }, Presets = { WorkWithTv() } };

        settings.RemapDisplay(Right, swapped);

        Assert.Equal("Правый", settings.DisplayNames[swapped]);
        Assert.False(settings.DisplayNames.ContainsKey(Right));
        Assert.True(settings.Presets[0].Layout.IsEnabled(swapped));
        Assert.False(settings.Presets[0].Layout.IsEnabled(Right));
    }

    [Fact]
    public void Unrecognized_displays_of_the_same_model_are_numbered()
    {
        var settings = new AppSettings { DisplayNames = { [Tv] = "ТВ" } };

        Assert.Equal("Mi Monitor 1", settings.DisplayLabel(Left, All));
        Assert.Equal("Mi Monitor 2", settings.DisplayLabel(Right, All));
        Assert.Equal("ТВ", settings.DisplayLabel(Tv, All));
    }

    [Fact]
    public void Settings_survive_a_save_and_load_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"md-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings { DisplayNames = { [Left] = "Левый" }, Presets = { WorkWithTv() }, Language = LanguageChoice.English });

            var loaded = store.Load();

            Assert.Equal("Левый", loaded.DisplayNames[Left]);
            Assert.Equal(LanguageChoice.English, loaded.Language);
            Assert.True(loaded.Presets[0].Layout.SameAs(WorkWithTv().Layout));
            Assert.Equal("headphones", loaded.Presets[0].AudioOutputId);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
