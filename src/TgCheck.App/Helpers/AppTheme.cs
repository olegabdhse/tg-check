using System.Windows;
using System.Windows.Media;

namespace TgCheck.App.Helpers;

public enum ThemeKind { Default, Dark, Light }

/// <summary>
/// Темы оформления. «Стандартная» — значения из Themes/Theme.xaml; тёмная и светлая лежат отдельным словарём поверх них.
/// Цвета в разметке — DynamicResource, поэтому смена словаря меняет тему сразу и везде: без перезапуска и без анимаций.
/// Островок от темы не зависит: у него свои цвета (Views/IslandWindow.xaml).
/// </summary>
public static class AppTheme
{
    // Тёмная в стиле островка: почти чёрный графит с холодным синим акцентом.
    private static readonly Dictionary<string, string> Dark = new()
    {
        ["Bg"] = "#06080C", ["Surface"] = "#0B1016", ["Surface2"] = "#131A23", ["Surface3"] = "#1C2530",
        ["Border"] = "#1B232D", ["Accent"] = "#4A9EFF", ["AccentHover"] = "#6BB1FF",
        ["Text"] = "#EDF1F6", ["TextSecondary"] = "#7E8C9C",
        ["OutBubble"] = "#16324F", ["InBubble"] = "#121922", ["ChatBg"] = "#07090D", ["SelectedRow"] = "#16324F",
        ["Link"] = "#6FB4FF", ["LinkTint"] = "#1F6FB4FF", ["CodeAccent"] = "#9CCBFF", ["CodeBar"] = "#6FB4FF", ["CodeInline"] = "#9CCBFF",
        ["TextSoft"] = "#B9C4D0", ["BubbleText"] = "#EDF1F6", ["BubbleDim"] = "#7E8C9C",
        ["DatePill"] = "#B30F151C", ["HintPill"] = "#CC0F151C", ["Wave"] = "#4C5F73", ["WaveRing"] = "#5C7085",
        ["MutedBadge"] = "#3A4653", ["Banner"] = "#12263A", ["NotificationCard"] = "#F20F151C",
        ["MiniHeader"] = "#06080C", ["Meta"] = "#6F7F90", ["ScrollHover"] = "#2C3846",
    };

    // Светлая, но не слепящая: приглушённый серо-голубой вместо белого, спокойные голубые исходящие.
    private static readonly Dictionary<string, string> Light = new()
    {
        ["Bg"] = "#D9DEE5", ["Surface"] = "#E6EAEF", ["Surface2"] = "#DBE0E6", ["Surface3"] = "#CFD6DD",
        ["Border"] = "#C6CED6", ["Accent"] = "#2277BD", ["AccentHover"] = "#2F88CF",
        ["Text"] = "#18222C", ["TextSecondary"] = "#5A6773",
        ["Danger"] = "#D64545", ["Gold"] = "#E09A00", ["Success"] = "#2FA84F",
        ["OutBubble"] = "#C3DEF4", ["InBubble"] = "#EDF0F4", ["ChatBg"] = "#CDD4DC", ["SelectedRow"] = "#BDD7EE",
        ["Overlay"] = "#73101820",
        ["Tint"] = "#12000000", ["Tint2"] = "#1C000000", ["Pill"] = "#14000000",
        ["Hover"] = "#0C000000", ["Hover2"] = "#08000000", ["Hover3"] = "#0A000000", ["Hover4"] = "#0A000000",
        ["Link"] = "#1E78C2", ["LinkTint"] = "#141E78C2", ["CodeAccent"] = "#1E78C2", ["CodeBar"] = "#1E78C2", ["CodeInline"] = "#1E78C2",
        ["CodeText"] = "#16202A", ["LinkDesc"] = "#44525F", ["TextSoft"] = "#44525F", ["QuoteText"] = "#3A4A58",
        ["BubbleText"] = "#18222C", ["BubbleDim"] = "#5A6773",
        ["DatePill"] = "#CCE6EAEF", ["HintPill"] = "#E6E6EAEF", ["Wave"] = "#A6B6C5", ["WaveRing"] = "#8FA1B3",
        ["WaveDim"] = "#55000000", ["BotButton"] = "#14000000", ["BotButton2"] = "#10000000",
        ["BotButton3"] = "#1C000000", ["BotButton4"] = "#28000000", ["MutedBadge"] = "#A3AEB9",
        ["Banner"] = "#CADDEE", ["NotificationCard"] = "#F7E6EAEF", ["MiniHeader"] = "#D9DEE5", ["Grip"] = "#33000000",
        ["Mask"] = "#A9B5C1", ["SliderTrack"] = "#33000000", ["CheckBorder"] = "#A9B5C1", ["ScrollHover"] = "#B4BFCA",
        ["Meta"] = "#7A8997",
    };

    public static ThemeKind Current { get; private set; }
    public static bool IsLight => Current == ThemeKind.Light;

    /// <summary>Тема сменилась (окна обновляют то, что не берётся из ресурсов: рамку и заголовок).</summary>
    public static event Action? Changed;

    /// <summary>Фон окна для заголовка Windows.</summary>
    public static Color WindowBackground => Palette() is { } p ? Parse(p["Bg"]) : Color.FromRgb(0x0E, 0x16, 0x21);
    /// <summary>Рамка отдельных окон (мини-чат, быстрый ответ).</summary>
    public static Color WindowBorder => Palette() is { } p ? Parse(p["Border"]) : Color.FromRgb(0x2A, 0x38, 0x46);

    private static Dictionary<string, string>? Palette() => Current switch
    {
        ThemeKind.Dark => Dark,
        ThemeKind.Light => Light,
        _ => null,
    };

    /// <summary>Подставить тему одним словарём: все окна перекрашиваются за один проход.</summary>
    public static void Apply(ThemeKind kind)
    {
        Current = kind;
        var overlay = new ResourceDictionary();
        if (Palette() is { } palette)
            foreach (var (name, hex) in palette)
            {
                var color = Parse(hex);
                overlay[name + "Color"] = color;
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                overlay[name + "Brush"] = brush;
            }
        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 1) merged[1] = overlay;
        else merged.Add(overlay);
    }

    /// <summary>Сменить тему сразу: запомнить, перекрасить, без анимаций.</summary>
    public static void SwitchTo(ThemeKind kind)
    {
        if (kind == Current) return;
        UiSettings.Theme = kind.ToString();
        Anim.InstantUntil = DateTime.UtcNow.AddSeconds(0.5);
        Apply(kind);
        Changed?.Invoke();
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
