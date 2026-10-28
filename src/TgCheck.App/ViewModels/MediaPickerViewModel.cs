using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.App.Helpers;
using TgCheck.Core.Media;
using TgCheck.Core.Models;
using TgCheck.Core.Services;

namespace TgCheck.App.ViewModels;

/// <summary>Связывает единственный аудиоплеер с пузырём голосового, который сейчас играет.</summary>
public static class VoicePlayback
{
    private static readonly Dictionary<string, WeakReference<MessageViewModel>> Bubbles = [];
    private static MessageViewModel? _current;

    static VoicePlayback()
    {
        AudioPlayer.Instance.Changed += () => Anim.Ui(Update);
    }

    public static void Attach(MessageViewModel vm)
    {
        if (!(vm.IsVoice || vm.IsAudio)) return;
        lock (Bubbles) Bubbles[vm.AudioKey] = new WeakReference<MessageViewModel>(vm);
        if (AudioPlayer.Instance.Key == vm.AudioKey) Update();
    }

    private static void Update()
    {
        var player = AudioPlayer.Instance;
        MessageViewModel? vm = null;
        lock (Bubbles)
            if (player.Key is { } key && Bubbles.TryGetValue(key, out var weak)) weak.TryGetTarget(out vm);

        if (_current is not null && _current != vm)
        {
            _current.IsPlaying = false;
            _current.PlayProgress = 0;
            _current.PositionText = null;
        }
        _current = vm;
        if (vm is null) return;
        vm.IsPlaying = player.IsPlaying;
        var total = player.Duration.TotalSeconds;
        vm.PlayProgress = total > 0 ? player.Position.TotalSeconds / total : 0;
        vm.PositionText = player.IsPlaying || vm.PlayProgress > 0 ? Fmt.Duration(player.Position.TotalSeconds) : null;
    }
}

public sealed class EmojiItem(string emoji)
{
    public string Text { get; } = emoji;
    public ImageSource? Image => EmojiImages.Get(Text);
}

public sealed class EmojiCategoryViewModel(string icon, string title, IEnumerable<string> items)
{
    public string Icon { get; } = icon;
    public string Title { get; } = title;
    public ImageSource? IconImage => EmojiImages.Get(Icon);
    public IReadOnlyList<EmojiItem> Items { get; } = items.Select(e => new EmojiItem(e)).ToList();
}

/// <summary>Набор стикеров во вкладке (иконка = первый стикер).</summary>
public sealed partial class StickerSetViewModel(StickerSetInfo? info, string title) : ObservableObject
{
    public StickerSetInfo? Info { get; } = info;
    public string Title { get; } = title;
    public ObservableCollection<StickerItem> Stickers { get; } = [];
    [ObservableProperty] private MediaHolder? _cover;
    public bool Loaded { get; set; }
}

public enum PickerTab { Emoji, Stickers, Gifs }

/// <summary>Панель выбора: эмодзи, стикеры (наборы пользователя + недавние), сохранённые GIF.</summary>
public sealed partial class MediaPickerViewModel : ObservableObject
{
    private readonly TelegramService _tg;
    private readonly string _recentPath;
    private Task? _loading;

    public MediaPickerViewModel(TelegramService tg, string dataDir)
    {
        _tg = tg;
        _recentPath = Path.Combine(dataDir, "recent_emoji.json");
        foreach (var c in EmojiData.Categories) Categories.Add(new EmojiCategoryViewModel(c.Icon, c.Title, c.Items));
        LoadRecentEmoji();
    }

    [ObservableProperty] private PickerTab _tab = PickerTab.Emoji;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private StickerSetViewModel? _selectedSet;

    public ObservableCollection<EmojiCategoryViewModel> Categories { get; } = [];
    public ObservableCollection<EmojiItem> RecentEmoji { get; } = [];
    public ObservableCollection<StickerSetViewModel> StickerSets { get; } = [];
    public ObservableCollection<GifItem> Gifs { get; } = [];

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var recent = await _tg.GetRecentStickersAsync();
            var recentVm = new StickerSetViewModel(null, "Недавние") { Loaded = true, Cover = recent.FirstOrDefault() };
            foreach (var s in recent) recentVm.Stickers.Add(s);
            if (recent.Count > 0) StickerSets.Add(recentVm);

            foreach (var set in await _tg.GetStickerSetsAsync())
                StickerSets.Add(new StickerSetViewModel(set, set.Title));
            SelectedSet = StickerSets.FirstOrDefault();

            foreach (var g in await _tg.GetSavedGifsAsync()) Gifs.Add(g);

            // Обложки наборов — в фоне, понемногу.
            _ = LoadCoversAsync();
        }
        catch (Exception ex)
        {
            TgErrors.Describe(ex);
            _loading = null;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadCoversAsync()
    {
        foreach (var set in StickerSets.Where(s => !s.Loaded).Take(40).ToList())
        {
            await LoadSetAsync(set);
            await Task.Delay(60);
        }
    }

    private async Task LoadSetAsync(StickerSetViewModel set)
    {
        if (set.Loaded || set.Info is null) return;
        set.Loaded = true;
        try
        {
            var stickers = await _tg.GetStickerSetAsync(set.Info);
            foreach (var s in stickers) set.Stickers.Add(s);
            set.Cover = stickers.FirstOrDefault();
        }
        catch (Exception ex)
        {
            set.Loaded = false;
            TgErrors.Describe(ex);
        }
    }

    partial void OnSelectedSetChanged(StickerSetViewModel? value)
    {
        if (value is not null) _ = LoadSetAsync(value);
    }

    [RelayCommand]
    private void SelectTab(PickerTab tab) => Tab = tab;

    [RelayCommand]
    private void SelectSet(StickerSetViewModel? set) => SelectedSet = set;

    public void RememberEmoji(string emoji)
    {
        var existing = RecentEmoji.FirstOrDefault(e => e.Text == emoji);
        if (existing is not null) RecentEmoji.Remove(existing);
        RecentEmoji.Insert(0, existing ?? new EmojiItem(emoji));
        while (RecentEmoji.Count > 24) RecentEmoji.RemoveAt(RecentEmoji.Count - 1);
        try { File.WriteAllText(_recentPath, JsonSerializer.Serialize(RecentEmoji.Select(e => e.Text))); }
        catch { /* не критично */ }
    }

    private void LoadRecentEmoji()
    {
        try
        {
            if (!File.Exists(_recentPath)) return;
            foreach (var e in JsonSerializer.Deserialize<string[]>(File.ReadAllText(_recentPath)) ?? [])
                RecentEmoji.Add(new EmojiItem(e));
        }
        catch { /* не критично */ }
    }
}
