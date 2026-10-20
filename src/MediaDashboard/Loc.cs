using System.ComponentModel;
using System.Globalization;
using MediaDashboard.Core;

namespace MediaDashboard;

/// <summary>
/// UI strings in Russian and English (decision Q26/Q28). XAML binds to the indexer
/// (<c>{Binding [Apply], Source={x:Static local:Loc.Instance}}</c>), so switching language updates live.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private static readonly Dictionary<string, (string Ru, string En)> Strings = new()
    {
        ["Displays"] = ("Дисплеи", "Displays"),
        ["Identify"] = ("Определить", "Identify"),
        ["Disabled"] = ("Выключены", "Disabled"),
        ["NothingDisabled"] = ("Все дисплеи включены", "All displays are on"),
        ["Apply"] = ("Применить", "Apply"),
        ["Cancel"] = ("Отменить", "Cancel"),
        ["Duplicate"] = ("Дублировать", "Duplicate"),
        ["Extend"] = ("Расширить", "Extend"),
        ["DropMove"] = ("Отпустите — встанет сюда", "Release to place it here"),
        ["DropDisable"] = ("Отпустите — выключить", "Release to turn it off"),
        ["DropOnTile"] = ("Отпустите — дублировать или расширить {0}", "Release to duplicate or extend {0}"),
        ["SideLeft"] = ("влево", "to the left"),
        ["SideRight"] = ("вправо", "to the right"),
        ["SideTop"] = ("вверх", "upwards"),
        ["SideBottom"] = ("вниз", "downwards"),
        ["Ungroup"] = ("Разгруппировать", "Ungroup"),
        ["Rename"] = ("Переименовать", "Rename"),
        ["RenameNamed"] = ("Переименовать «{0}»", "Rename “{0}”"),
        ["ThisIs"] = ("Это «{0}»", "This is “{0}”"),
        ["Primary"] = ("Основной", "Primary"),
        ["Unrecognized"] = ("Не опознан — правый клик, чтобы назвать", "Unrecognized — right-click to name it"),
        ["Sound"] = ("Звук", "Sound"),
        ["NoAudio"] = ("Нет звуковых выходов", "No audio outputs"),
        ["Hide"] = ("Скрыть", "Hide"),
        ["Unhide"] = ("Показать", "Show"),
        ["ShowHidden"] = ("Показать скрытые ({0})", "Show hidden ({0})"),
        ["CollapseHidden"] = ("Свернуть скрытые", "Collapse hidden"),
        ["Presets"] = ("Пресеты", "Presets"),
        ["NoPresets"] = ("Пресетов пока нет", "No presets yet"),
        ["SaveCurrent"] = ("Сохранить текущее как пресет", "Save current as preset"),
        ["SaveDisabledDraft"] = ("Сначала примените или отмените черновик", "Apply or cancel the draft first"),
        ["Overwrite"] = ("Перезаписать текущим", "Overwrite with current"),
        ["Delete"] = ("Удалить", "Delete"),
        ["NamePrompt"] = ("Имя:", "Name:"),
        ["PresetNamePrompt"] = ("Имя пресета:", "Preset name:"),
        ["Open"] = ("Открыть", "Open"),
        ["Language"] = ("Язык / Language", "Language / Язык"),
        ["LangAuto"] = ("Как в Windows", "Same as Windows"),
        ["Autostart"] = ("Запускать вместе с Windows", "Start with Windows"),
        ["Exit"] = ("Выход", "Exit"),
        ["CustomConfig"] = ("Своя конфигурация", "Custom configuration"),
        ["KeepChanges"] = ("Оставить эти настройки дисплеев?", "Keep these display settings?"),
        ["RevertIn"] = ("Возврат к прежним через {0} с", "Reverting in {0} s"),
        ["Keep"] = ("Оставить", "Keep"),
        ["Revert"] = ("Вернуть", "Revert"),
        ["DraftDiscarded"] = ("Дисплеи изменились — черновик сброшен", "Displays changed — draft discarded"),
        ["LastActive"] = ("Нельзя выключить последний дисплей", "Can't turn off the last display"),
        ["DifferentAdapters"] = ("Эти дисплеи на разных видеокартах — дублирование невозможно", "These displays are on different graphics cards — can't duplicate"),
        ["ApplyFailed"] = ("Windows не приняла конфигурацию (код {0})", "Windows rejected the configuration (code {0})"),
        ["DisplayMissing"] = ("Дисплей не найден: {0}", "Display not found: {0}"),
        ["AudioMissing"] = ("Звуковой выход не найден: {0}", "Audio output not found: {0}"),
    };

    private bool _russian;

    private Loc() => Apply(LanguageChoice.Auto);

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] =>
        Strings.TryGetValue(key, out var s) ? (_russian ? s.Ru : s.En) : key;

    public static string Get(string key) => Instance[key];

    public static string Format(string key, params object[] args) => string.Format(Instance[key], args);

    public void Apply(LanguageChoice choice)
    {
        _russian = choice switch
        {
            LanguageChoice.Russian => true,
            LanguageChoice.English => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru",
        };
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}
