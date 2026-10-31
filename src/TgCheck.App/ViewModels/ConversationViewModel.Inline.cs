using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.App.Helpers;
using TgCheck.Core.Media;
using TgCheck.Core.Models;
using TgCheck.Core.Services;

namespace TgCheck.App.ViewModels;

/// <summary>Результат инлайн-бота в панели над полем ввода: заголовок, описание, картинка.</summary>
public sealed partial class InlineResultViewModel : ObservableObject
{
    public InlineResultViewModel(InlineResult result, MediaService media)
    {
        Result = result;
        if (result.ThumbUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            try
            {
                // Картинка по ссылке грузится сама (в фоне), уменьшенной.
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = uri;
                bmp.DecodePixelWidth = 160;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                _thumb = bmp;
            }
            catch { /* битая ссылка — без картинки */ }
        }
        else if (result.HasMedia) _ = LoadMediaThumbAsync(media);
    }

    public InlineResult Result { get; }
    public string Title => Result.Title ?? Result.Description ?? TypeTitle;
    public string? Description => Result.Title is null ? null : Result.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    [ObservableProperty] private ImageSource? _thumb;

    private string TypeTitle => Result.Type switch
    {
        "photo" => "Фото",
        "gif" => "GIF",
        "video" => "Видео",
        "sticker" => "Стикер",
        "audio" => "Аудио",
        "voice" => "Голосовое",
        "document" => "Файл",
        "location" or "venue" => "Место",
        "contact" => "Контакт",
        "game" => "Игра",
        _ => "Результат",
    };

    private async Task LoadMediaThumbAsync(MediaService media)
    {
        try { Thumb = Images.FromBytes(await media.GetMessagePreviewAsync(Result), 160); }
        catch { /* без картинки */ }
    }
}

// Инлайн-боты: «@бот запрос» в поле ввода. Пока набрано только «@имя» — подсказки недавних инлайн-ботов;
// после пробела — результаты бота над полем ввода, выбранный отправляется в чат («с помощью @бот»).
public sealed partial class ConversationViewModel
{
    public ObservableCollection<InlineResultViewModel> InlineResults { get; } = [];
    public ObservableCollection<PeerResultViewModel> InlineSuggestions { get; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasInlinePanel))] private InlineBot? _inlineBot;
    [ObservableProperty] private bool _isInlineGallery;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasInlinePanel))] private bool _isInlineLoading;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasInlinePanel))] private bool _hasInlineSuggestions;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasInlinePanel))] private bool _hasInlineResults;
    public bool HasInlinePanel => HasInlineSuggestions || (InlineBot is not null && (HasInlineResults || IsInlineLoading));
    public string InlineTitle => InlineBot is { } b ? "@" + b.Peer.Username : "";

    private long _inlineQueryId;
    private string? _inlineNextOffset;
    private string _inlineQuery = "";
    private CancellationTokenSource? _inlineCts;
    private IReadOnlyList<InlineBot>? _recentInlineBots;

    [GeneratedRegex(@"^@([A-Za-z][A-Za-z0-9_]{2,31})(?:\s(.*))?$", RegexOptions.Singleline)]
    private static partial Regex InlineRx();

    // Вызывается при каждом изменении текста в поле ввода.
    private async void UpdateInline(string text)
    {
        _inlineCts?.Cancel();
        var match = Editing is null && !HasAttachments ? InlineRx().Match(text) : Match.Empty;
        if (!match.Success)
        {
            if (text.StartsWith('@') && !text.Contains(' ') && Editing is null) { await ShowInlineSuggestionsAsync(text[1..]); return; }
            ClearInline();
            return;
        }
        var username = match.Groups[1].Value;
        if (!match.Groups[2].Success)
        {
            // Набрано только «@имя» — подсказки, имя бота ещё может меняться.
            InlineBot = null;
            HasInlineResults = false;
            await ShowInlineSuggestionsAsync(username);
            return;
        }
        HideInlineSuggestions();
        var query = match.Groups[2].Value;
        var cts = _inlineCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(350, cts.Token); // не на каждую букву — после короткой паузы
            var bot = InlineBot?.Peer.Username?.Equals(username, StringComparison.OrdinalIgnoreCase) == true ? InlineBot : await _tg.ResolveInlineBotAsync(username);
            if (cts.IsCancellationRequested) return;
            if (bot is null) { ClearInline(); return; }
            InlineBot = bot;
            OnPropertyChanged(nameof(InlineTitle));
            IsInlineLoading = true;
            var results = await _tg.GetInlineResultsAsync(Peer, bot, query);
            if (cts.IsCancellationRequested) return;
            ShowInlineResults(results, query, append: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            Logging.Write("inline: " + ex.Message);
            InlineResults.Clear();
            HasInlineResults = false;
        }
        finally { if (!cts.IsCancellationRequested) IsInlineLoading = false; }
    }

    private void ShowInlineResults(InlineResults results, string query, bool append)
    {
        if (!append) InlineResults.Clear();
        _inlineQueryId = results.QueryId;
        _inlineNextOffset = string.IsNullOrEmpty(results.NextOffset) ? null : results.NextOffset;
        _inlineQuery = query;
        IsInlineGallery = results.Gallery;
        foreach (var r in results.Items) InlineResults.Add(new InlineResultViewModel(r, _tg.Media));
        HasInlineResults = InlineResults.Count > 0;
    }

    /// <summary>Догрузить следующую страницу результатов (прокрутили до конца панели).</summary>
    public async Task LoadMoreInlineAsync()
    {
        if (InlineBot is not { } bot || _inlineNextOffset is not { } offset || IsInlineLoading) return;
        IsInlineLoading = true;
        try { ShowInlineResults(await _tg.GetInlineResultsAsync(Peer, bot, _inlineQuery, offset), _inlineQuery, append: true); }
        catch (Exception ex) { Logging.Write("inline more: " + ex.Message); }
        finally { IsInlineLoading = false; }
    }

    // Подсказки: недавние инлайн-боты (свои последние — первыми, затем топ Telegram), до 5 штук.
    private async Task ShowInlineSuggestionsAsync(string prefix)
    {
        InlineBot = null;
        HasInlineResults = false;
        InlineResults.Clear();
        _recentInlineBots ??= await LoadRecentInlineBotsAsync();
        var matches = _recentInlineBots
            .Where(b => b.Peer.Username?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
            .Take(5).ToList();
        InlineSuggestions.Clear();
        foreach (var b in matches)
        {
            var vm = new PeerResultViewModel(b.Peer);
            vm.EnsureAvatar(_tg.Media);
            InlineSuggestions.Add(vm);
        }
        HasInlineSuggestions = InlineSuggestions.Count > 0;
    }

    private async Task<IReadOnlyList<InlineBot>> LoadRecentInlineBotsAsync()
    {
        var result = new List<InlineBot>();
        foreach (var username in UiSettings.RecentInlineBots)
            if (await _tg.ResolveInlineBotAsync(username) is { } bot) result.Add(bot);
        foreach (var bot in await _tg.GetRecentInlineBotsAsync(10))
            if (!result.Any(b => b.Peer.Key == bot.Peer.Key)) result.Add(bot);
        return result;
    }

    private void HideInlineSuggestions()
    {
        InlineSuggestions.Clear();
        HasInlineSuggestions = false;
    }

    private void ClearInline()
    {
        HideInlineSuggestions();
        InlineBot = null;
        InlineResults.Clear();
        HasInlineResults = false;
        IsInlineLoading = false;
    }

    /// <summary>Подсказка выбрана (или нажали «с помощью @бот») — в поле ввода «@бот », дальше пишется запрос.</summary>
    [RelayCommand]
    private void UseInlineBot(object? bot)
    {
        var username = bot switch
        {
            PeerResultViewModel p => p.Peer.Username,
            string s => s,
            _ => null,
        };
        if (string.IsNullOrEmpty(username)) return;
        Draft = "@" + username + " ";
        ComposerFocusRequested?.Invoke();
    }

    /// <summary>Поле ввода должно получить фокус (курсор в конец).</summary>
    public event Action? ComposerFocusRequested;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SendInlineResult(InlineResultViewModel? item)
    {
        if (item is null || InlineBot is not { } bot) return;
        var reply = ReplyTo?.Id ?? SendTopId;
        var queryId = _inlineQueryId;
        Draft = "";
        ReplyTo = null;
        ClearInline();
        try
        {
            await _tg.SendInlineResultAsync(Peer, queryId, item.Result.Id, reply);
            if (bot.Peer.Username is { } u)
            {
                UiSettings.RememberInlineBot(u);
                _recentInlineBots = null; // при следующем «@» — с этим ботом первым
            }
        }
        catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
    }
}
