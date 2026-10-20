using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MediaDashboard.Core;

namespace MediaDashboard;

/// <summary>
/// The window by the tray (decision Q8): display schema, audio outputs and presets.
/// Closes when it loses focus, discarding any Draft (decision Q21).
/// </summary>
public partial class FlyoutWindow : Window
{
    private readonly AppController _controller;
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private DateTime _hiddenAt;
    private bool _showHidden;
    private Action<string>? _editorCommit;
    private PresetDrag? _presetDrag;

    public FlyoutWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            Native.SetToolWindow(this, passive: false);
            Native.ApplyBackdrop(this, Theme.IsDark);
        };
        Theme.Changed += () => Native.ApplyBackdrop(this, Theme.IsDark);
        Deactivated += (_, _) => Hide();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
            _hiddenAt = DateTime.UtcNow;
            CloseEditor();
            _controller.DiscardDraft();
        };
        SizeChanged += (_, _) =>
        {
            if (IsVisible) AnchorToTray();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && EditorBar.Visibility != Visibility.Visible) Hide();
        };

        _controller.StateChanged += () =>
        {
            if (IsVisible) Render();
        };
        _controller.Notice += ShowNotice;
        Loc.Instance.PropertyChanged += (_, _) =>
        {
            if (IsVisible) Render();
        };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            NoticeText.Visibility = Visibility.Collapsed;
        };
        Map.RenameRequested += id => OpenEditor(Loc.Get("NamePrompt"), _controller.DisplayLabel(id), name => _controller.RenameDisplay(id, name));
    }

    /// <summary>True right after the flyout closed by losing focus: the click on the tray icon that caused it must not reopen it.</summary>
    public bool JustClosed => DateTime.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(300);

    public void ShowNearTray()
    {
        _controller.RefreshDisplays();
        NoticeText.Visibility = Visibility.Collapsed;
        Opacity = 0;
        Show();
        Render();
        UpdateLayout();
        AnchorToTray();
        Opacity = 1;
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
    }

    /// <summary>Bottom-right corner of the work area of the screen the cursor is on (where the tray is).</summary>
    private void AnchorToTray()
    {
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        int margin = (int)(12 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        // Twice: moving across monitors with different DPI changes the physical size.
        for (int i = 0; i < 2; i++)
        {
            var (w, h) = Native.PhysicalSize(this);
            Native.MoveTo(this, area.Right - w - margin, area.Bottom - h - margin);
        }
    }

    private void Render()
    {
        Map.Render(_controller);
        DraftBar.Visibility = _controller.IsDraftDirty ? Visibility.Visible : Visibility.Collapsed;
        RenderAudio();
        RenderPresets();
    }

    // ---------- displays ----------

    private async void OnApplyDraft(object sender, RoutedEventArgs e) => await _controller.ApplyDraftAsync();

    private void OnCancelDraft(object sender, RoutedEventArgs e) => _controller.DiscardDraft();

    private void OnIdentify(object sender, RoutedEventArgs e) => IdentifyWindow.ShowAll(_controller);

    // ---------- audio ----------

    private void RenderAudio()
    {
        AudioPanel.Children.Clear();
        var hidden = _controller.Settings.HiddenAudioOutputs;
        var outputs = _controller.AudioOutputs;
        foreach (var output in outputs.Where(o => _showHidden || !hidden.Contains(o.Id)))
            AudioPanel.Children.Add(MakeAudioTile(output, hidden.Contains(output.Id)));

        if (outputs.IsEmpty) AudioPanel.Children.Add(new TextBlock { Text = Loc.Get("NoAudio"), Style = (Style)FindResource("Subtle") });

        int hiddenCount = outputs.Count(o => hidden.Contains(o.Id));
        HiddenToggle.Visibility = hiddenCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        HiddenToggle.Content = _showHidden ? Loc.Get("CollapseHidden") : Loc.Format("ShowHidden", hiddenCount);
    }

    private Border MakeAudioTile(AudioOutput output, bool isHidden)
    {
        bool active = output.Id == _controller.ActiveAudioId;
        var icon = new TextBlock { Text = TrayIcon.Glyph(output.Kind), FontSize = 18, Margin = new Thickness(0, 0, 0, 4) };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, active ? "Accent" : "Text");
        var name = new TextBlock
        {
            Text = _controller.AudioLabel(output),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 32,
            Style = (Style)FindResource("Body"),
        };

        var tile = new Border
        {
            Width = 122,
            Height = 70,
            Margin = new Thickness(0, 0, 11, 8),
            Padding = new Thickness(10, 8, 8, 6),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(active ? 2 : 1),
            Cursor = Cursors.Hand,
            Opacity = isHidden ? 0.5 : 1,
            ToolTip = output.Name,
            Child = new StackPanel { Children = { icon, name } },
        };
        string background = active ? "AccentSoft" : "Tile";
        tile.SetResourceReference(Border.BackgroundProperty, background);
        tile.SetResourceReference(Border.BorderBrushProperty, active ? "Accent" : "TileBorder");
        tile.MouseEnter += (_, _) => { if (!active) tile.SetResourceReference(Border.BackgroundProperty, "TileHover"); };
        tile.MouseLeave += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, background);
        tile.MouseLeftButtonUp += (_, _) => _controller.SetActiveAudio(output.Id);
        tile.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowMenu(tile,
                (Loc.Get("Rename"), () => OpenEditor(Loc.Get("NamePrompt"), _controller.AudioLabel(output), n => _controller.RenameAudio(output.Id, n))),
                isHidden
                    ? (Loc.Get("Unhide"), () => _controller.SetAudioHidden(output.Id, false))
                    : (Loc.Get("Hide"), () => _controller.SetAudioHidden(output.Id, true)));
        };
        return tile;
    }

    private void OnToggleHidden(object sender, RoutedEventArgs e)
    {
        _showHidden = !_showHidden;
        RenderAudio();
    }

    // ---------- presets ----------

    private void RenderPresets()
    {
        PresetPanel.Children.Clear();
        var matching = _controller.MatchingPreset;
        foreach (var preset in _controller.Settings.Presets)
            PresetPanel.Children.Add(MakePresetRow(preset, ReferenceEquals(preset, matching)));
        if (_controller.Settings.Presets.Count == 0)
            PresetPanel.Children.Add(new TextBlock { Text = Loc.Get("NoPresets"), Style = (Style)FindResource("Subtle") });

        SavePresetButton.Content = "+  " + Loc.Get("SaveCurrent");
        SavePresetButton.IsEnabled = !_controller.IsDraftDirty;
        SavePresetButton.ToolTip = _controller.IsDraftDirty ? Loc.Get("SaveDisabledDraft") : null;
    }

    private Border MakePresetRow(Preset preset, bool isMatching)
    {
        var check = new TextBlock { Text = isMatching ? "\uE73E" : "", Width = 20, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        check.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        check.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var name = new TextBlock { Text = preset.Name, Style = (Style)FindResource("Body"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

        var row = new Border
        {
            Height = 34,
            Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Tag = preset,
            Child = new DockPanel { Children = { check, name } },
        };
        DockPanel.SetDock(check, Dock.Left);
        string background = isMatching ? "AccentSoft" : "Tile";
        row.SetResourceReference(Border.BackgroundProperty, background);
        row.SetResourceReference(Border.BorderBrushProperty, isMatching ? "Accent" : "TileBorder");
        row.MouseEnter += (_, _) => { if (!isMatching) row.SetResourceReference(Border.BackgroundProperty, "TileHover"); };
        row.MouseLeave += (_, _) => row.SetResourceReference(Border.BackgroundProperty, background);

        row.MouseLeftButtonDown += (_, e) =>
        {
            _presetDrag = new PresetDrag(row, e.GetPosition(PresetPanel));
            row.CaptureMouse();
            e.Handled = true;
        };
        row.MouseMove += (_, e) =>
        {
            if (_presetDrag?.Row != row) return;
            var p = e.GetPosition(PresetPanel);
            if (!_presetDrag.Moving && Math.Abs(p.Y - _presetDrag.Start.Y) < 5) return;
            _presetDrag.Moving = true;
            row.Opacity = 0.6;
            // Live reorder: slide the row to the slot under the cursor.
            int index = Math.Clamp((int)(p.Y / 38), 0, PresetPanel.Children.Count - 1);
            if (PresetPanel.Children.IndexOf(row) != index)
            {
                PresetPanel.Children.Remove(row);
                PresetPanel.Children.Insert(index, row);
            }
        };
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (_presetDrag?.Row != row) return;
            var drag = _presetDrag;
            _presetDrag = null;
            row.ReleaseMouseCapture();
            row.Opacity = 1;
            if (drag.Moving) _controller.MovePreset(preset, PresetPanel.Children.IndexOf(row));
            else _controller.ApplyPreset(preset, fromTray: false);
        };
        row.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowMenu(row,
                (Loc.Get("Overwrite"), () => _controller.OverwritePreset(preset)),
                (Loc.Get("Rename"), () => OpenEditor(Loc.Get("PresetNamePrompt"), preset.Name, n => _controller.RenamePreset(preset, n))),
                (Loc.Get("Delete"), () => _controller.DeletePreset(preset)));
        };
        return row;
    }

    private sealed class PresetDrag(Border row, Point start)
    {
        public Border Row { get; } = row;
        public Point Start { get; } = start;
        public bool Moving { get; set; }
    }

    private void OnSavePreset(object sender, RoutedEventArgs e)
    {
        string suggestion = _controller.ActiveAudio is { } audio ? _controller.AudioLabel(audio) : "";
        OpenEditor(Loc.Get("PresetNamePrompt"), suggestion, name =>
        {
            if (!string.IsNullOrWhiteSpace(name)) _controller.SaveCurrentAsPreset(name);
        });
    }

    // ---------- shared bits ----------

    private void OpenEditor(string prompt, string initial, Action<string> commit)
    {
        _editorCommit = commit;
        EditorPrompt.Text = prompt;
        EditorBox.Text = initial;
        EditorBar.Visibility = Visibility.Visible;
        EditorBox.Focus();
        EditorBox.SelectAll();
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var commit = _editorCommit;
            var text = EditorBox.Text;
            CloseEditor();
            commit?.Invoke(text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseEditor();
            e.Handled = true;
        }
    }

    private void CloseEditor()
    {
        _editorCommit = null;
        EditorBar.Visibility = Visibility.Collapsed;
    }

    private void ShowNotice(string text)
    {
        NoticeText.Text = text;
        NoticeText.Visibility = Visibility.Visible;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private static void ShowMenu(FrameworkElement target, params (string Header, Action OnClick)[] items)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        foreach (var (header, onClick) in items)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => onClick();
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
