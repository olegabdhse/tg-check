using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.App.Helpers;
using TgCheck.Core.Models;
using TgCheck.Core.Services;

namespace TgCheck.App.ViewModels;

public enum GiftFilter { All, Regular, Unique, Convertible, Hidden }

public enum GiftSort { Newest, Oldest, Valuable }

/// <summary>Строка с информацией профиля (с копированием и скрытием секретных значений).</summary>
public sealed partial class InfoRow : ObservableObject
{
    public InfoRow(string icon, string label, string value, bool isSecret = false)
    {
        Icon = icon;
        Label = label;
        Value = value;
        IsSecret = isSecret;
    }

    public string Icon { get; }
    public string Label { get; }
    public string Value { get; }
    public bool IsSecret { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayValue), nameof(RevealIcon))]
    private bool _isRevealed;

    public string DisplayValue => IsSecret && !IsRevealed ? Mask(Value) : Value;
    public string RevealIcon => IsRevealed ? "" : "";

    [RelayCommand]
    private void ToggleReveal() => IsRevealed = !IsRevealed;

    private static string Mask(string v) => v.Length <= 4 ? "••••" : v[..3] + new string('•', v.Length - 5) + v[^2..];
}

/// <summary>Запрос подтверждения действия (оверлей поверх страницы).</summary>
public sealed record ConfirmRequest(string Title, string Text, string ActionText, bool IsDanger, Func<Task> Action);

public sealed partial class ProfileViewModel : ObservableObject
{
    private readonly TelegramService _tg;
    private readonly Func<Task> _logout;
    private readonly ShellViewModel _shell;
    private readonly PeerRef? _peer;
    private CancellationTokenSource? _toastCts;

    /// <param name="peer">null — свой профиль.</param>
    public ProfileViewModel(TelegramService tg, PeerRef? peer, Func<Task> logout, ShellViewModel shell, bool compact = false)
    {
        IsCompact = compact;
        tg.UserStatusChanged += OnUserStatus;
        _tg = tg;
        _peer = peer;
        _logout = logout;
        _shell = shell;
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Gifts);
        view.Filter = FilterGift;
        view.CustomSort = new GiftComparer(this);
        GiftsView = view;
    }

    public ImageSource? Logo => Images.AppLogo;

    // ===================================================================== Профиль

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Initials), nameof(AvatarBrush), nameof(UsernameText), nameof(StatusText), nameof(HasTon), nameof(TonText), nameof(RatingText), nameof(HasRating), nameof(RatingProgressText), nameof(RatingProgress), nameof(NameText), nameof(CanWriteMessage), nameof(KindText), nameof(PageTitle))]
    private ProfileInfo? _profile;

    [ObservableProperty] private ImageSource? _avatar;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _loadingText;
    [ObservableProperty] private string? _loadError;
    [ObservableProperty] private long _starsBalance;

    public ObservableCollection<InfoRow> InfoRows { get; } = [];

    /// <summary>Мини-профиль в боковой панели чата (без подарков).</summary>
    public bool IsCompact { get; }
    /// <summary>Участники группы.</summary>
    public ObservableCollection<PeerResultViewModel> Members { get; } = [];
    public bool HasMembers => Members.Count > 0;
    public bool IsGroupProfile => Profile?.Kind == PeerKind.Group;
    public bool IsBotProfile => Profile?.Kind == PeerKind.Bot;
    public bool HasBotCommands => Profile?.BotCommands.Count > 0;
    public bool ShowGifts => !IsCompact && Profile?.Kind is not (PeerKind.Group or PeerKind.Bot);
    public string MembersTitle => Profile?.MembersCount is { } n ? $"Участники · {Fmt.N(n)}" : "Участники";

    [RelayCommand]
    private void OpenMember(PeerResultViewModel? m)
    {
        if (m is not null) _shell.OpenProfile(m.Peer);
    }

    /// <summary>Esc: закрыть просмотр фото, подтверждение, карточку подарка. false — нечего закрывать.</summary>
    public bool HandleEscape()
    {
        if (IsAvatarViewerOpen) { IsAvatarViewerOpen = false; return true; }
        if (Confirm is not null) { CancelConfirm(); return true; }
        if (SelectedGift is not null) { SelectedGift = null; return true; }
        if (SelectionMode) { SelectionMode = false; return true; }
        return false;
    }

    private async Task LoadMembersAsync()
    {
        if (Profile?.Peer is not { } peer || Profile.Kind != PeerKind.Group) return;
        try
        {
            Members.Clear();
            foreach (var m in await _tg.GetMembersAsync(peer))
            {
                var vm = new PeerResultViewModel(m);
                vm.EnsureAvatar(_tg.Media);
                Members.Add(vm);
            }
            OnPropertyChanged(nameof(HasMembers));
        }
        catch (Exception ex) { TgErrors.Describe(ex); }
    }

    public string Initials => Profile is null ? "" : AvatarColors.Initials(Profile.VisibleName ?? Profile.Username ?? "?");
    public System.Windows.Media.Brush AvatarBrush => AvatarColors.For(Profile?.Id ?? 0);

    public string? UsernameText => Profile?.Username is { } u ? "@" + u : null;

    public bool IsSelf => _peer is null || _peer.IsSelf;
    public bool IsOther => !IsSelf;
    public bool CanSell => IsSelf;
    /// <summary>Имя без невидимых символов (если имя «пустое» — строка скрывается).</summary>
    public string? NameText => Profile?.VisibleName;
    public bool CanWriteMessage => IsOther && Profile?.Peer is not null;
    public string? KindText => Profile?.Kind switch
    {
        PeerKind.Bot => "бот",
        PeerKind.Group => Profile.MembersCount is { } n ? $"группа · {Fmt.N(n)} {Fmt.Plural(n, "участник", "участника", "участников")}" : "группа",
        PeerKind.Channel => Profile.MembersCount is { } n ? $"канал · {Fmt.N(n)} {Fmt.Plural(n, "подписчик", "подписчика", "подписчиков")}" : "канал",
        _ => null,
    };
    public string PageTitle => IsSelf ? "Мой профиль" : Profile?.Kind switch { PeerKind.Group => "Группа", PeerKind.Channel => "Канал", PeerKind.Bot => "Бот", _ => "Профиль" };

    // Живой статус: обновляется событиями Telegram, пока профиль открыт.
    private (Presence Presence, DateTime? Seen)? _livePresence;

    private void OnUserStatus(long userId, Presence presence, DateTime? seen)
    {
        if (Profile is not { Kind: PeerKind.User } p || p.Id != userId) return;
        _livePresence = (presence, seen);
        Anim.Ui(() => OnPropertyChanged(nameof(StatusText)));
    }

    public string StatusText => Profile is null || Profile.Kind is PeerKind.Group or PeerKind.Channel ? "" : (_livePresence?.Presence ?? Profile.Presence) switch
    {
        Presence.Online => "в сети",
        Presence.Offline when (_livePresence?.Seen ?? Profile.LastSeenUtc) is { } t => "был(а) в сети " + Fmt.Date(t),
        Presence.Recently => "был(а) недавно",
        Presence.LastWeek => "был(а) на этой неделе",
        Presence.LastMonth => "был(а) в этом месяце",
        _ => "",
    };

    public string StarsText => Fmt.N(StarsBalance);
    partial void OnStarsBalanceChanged(long value) => OnPropertyChanged(nameof(StarsText));

    public bool HasTon => Profile?.TonBalance is > 0;
    public string TonText => Profile?.TonBalance is { } t ? Fmt.N(t) : "0";
    public bool HasRating => Profile?.RatingLevel is not null;
    public string RatingText => Profile?.RatingLevel is { } l ? l.ToString(Fmt.Ru) : "";
    public string RatingProgressText => Profile?.RatingStars is not { } s ? "" :
        Profile.RatingNextLevelStars is { } next ? $"{Fmt.N(s)} / {Fmt.N(next)}" : Fmt.N(s);
    public double RatingProgress => Profile is { RatingStars: { } s, RatingNextLevelStars: { } n } && n > 0
        ? Math.Clamp((double)s / n, 0, 1) : 1;

    // ===================================================================== Подарки

    // ===================================================================== Истории в профиле

    public ObservableCollection<ProfileStoryViewModel> Stories { get; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StoriesTitle))] private bool _showStoryArchive;
    public bool HasStories => Stories.Count > 0 || IsSelfProfile;
    public bool IsSelfProfile => Profile?.Peer.IsSelf == true;
    public bool ShowNoStories => IsSelfProfile && Stories.Count == 0;
    public string StoriesTitle => ShowStoryArchive ? "Архив историй" : "Истории";

    partial void OnShowStoryArchiveChanged(bool value) => _ = LoadStoriesAsync();

    private async Task LoadStoriesAsync()
    {
        if (Profile?.Peer is not { } peer) return;
        var stories = await _tg.GetProfileStoriesAsync(peer, archive: ShowStoryArchive && peer.IsSelf);
        Stories.Clear();
        foreach (var s in stories)
        {
            var item = new ProfileStoryViewModel(s);
            Stories.Add(item);
            _ = item.LoadPreviewAsync(_tg);
        }
        OnPropertyChanged(nameof(HasStories));
        OnPropertyChanged(nameof(IsSelfProfile));
        OnPropertyChanged(nameof(ShowNoStories));
    }

    [RelayCommand]
    private void ToggleStoryArchive() => ShowStoryArchive = !ShowStoryArchive;

    /// <summary>Открыть историю из профиля: просмотр с неё и дальше по списку.</summary>
    [RelayCommand]
    private void OpenStory(ProfileStoryViewModel? story)
    {
        if (story is null || Profile?.Peer is not { } peer) return;
        var list = Stories.Select(s => s.Story).ToList();
        var start = list.IndexOf(story.Story);
        var ordered = list.Skip(start).Concat(list.Take(start)).ToList();
        var storyPeer = new StoryPeer { Peer = peer, Stories = ordered, MaxReadId = int.MaxValue };
        _shell.OpenStories([new StoryPeerViewModel(storyPeer) { Avatar = Avatar }], 0);
    }

    public ObservableCollection<GiftViewModel> Gifts { get; } = [];
    public ICollectionView GiftsView { get; }

    [ObservableProperty] private GiftFilter _filter = GiftFilter.All;
    [ObservableProperty] private GiftSort _sort = GiftSort.Newest;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _isGiftsLoading;
    [ObservableProperty] private GiftViewModel? _selectedGift;
    [ObservableProperty] private bool _selectionMode;
    [ObservableProperty] private ConfirmRequest? _confirm;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private bool _toastIsError;

    public int CountAll => Gifts.Count;
    public int CountRegular => Gifts.Count(g => !g.IsUnique);
    public int CountUnique => Gifts.Count(g => g.IsUnique);
    public int CountConvertible => Gifts.Count(g => g.CanConvert);
    public int CountHidden => Gifts.Count(g => g.Item.IsHidden);
    public long ConvertibleStars => Gifts.Where(g => g.CanConvert).Sum(g => g.ConvertStars);
    public string ConvertibleSummary => $"{CountConvertible} {Fmt.Plural(CountConvertible, "подарок", "подарка", "подарков")} можно продать на {Fmt.Stars(ConvertibleStars)}";

    public int SelectedCount => Gifts.Count(g => g.IsSelected);
    public long SelectedStars => Gifts.Where(g => g.IsSelected).Sum(g => g.ConvertStars);
    public string SelectedSummary => SelectedCount == 0
        ? "Отметьте подарки, которые хотите продать"
        : $"Выбрано {SelectedCount} — получите {Fmt.Stars(SelectedStars)}";

    public bool IsGiftsEmpty => !IsGiftsLoading && GiftsView.IsEmpty;

    partial void OnFilterChanged(GiftFilter value) => RefreshView();
    partial void OnSortChanged(GiftSort value) => RefreshView();
    partial void OnSearchChanged(string value) => RefreshView();
    partial void OnIsGiftsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsGiftsEmpty));

    partial void OnSelectionModeChanged(bool value)
    {
        if (!value) foreach (var g in Gifts) g.IsSelected = false;
        if (value && Filter is not (GiftFilter.Convertible or GiftFilter.Regular)) Filter = GiftFilter.Convertible;
        OnSelectionChanged();
    }

    private void RefreshView()
    {
        GiftsView.Refresh();
        OnPropertyChanged(nameof(IsGiftsEmpty));
    }

    private bool FilterGift(object o)
    {
        if (o is not GiftViewModel g) return false;
        var ok = Filter switch
        {
            GiftFilter.Regular => !g.IsUnique,
            GiftFilter.Unique => g.IsUnique,
            GiftFilter.Convertible => g.CanConvert,
            GiftFilter.Hidden => g.Item.IsHidden,
            _ => true,
        };
        if (!ok) return false;
        if (string.IsNullOrWhiteSpace(Search)) return true;
        var q = Search.Trim();
        return Contains(g.Title, q) || Contains(g.Item.FromName, q) || Contains(g.Item.Model?.Name, q) ||
               Contains(g.NumberText, q) || Contains(g.Item.Message, q);

        static bool Contains(string? s, string q) => s?.Contains(q, StringComparison.CurrentCultureIgnoreCase) == true;
    }

    private sealed class GiftComparer(ProfileViewModel owner) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not GiftViewModel a || y is not GiftViewModel b) return 0;
            var pin = b.Item.IsPinned.CompareTo(a.Item.IsPinned);
            if (pin != 0 && owner.Sort == GiftSort.Newest) return pin;
            return owner.Sort switch
            {
                GiftSort.Oldest => a.Item.DateUtc.CompareTo(b.Item.DateUtc),
                GiftSort.Valuable => Value(b).CompareTo(Value(a)) is var c and not 0 ? c : b.Item.DateUtc.CompareTo(a.Item.DateUtc),
                _ => b.Item.DateUtc.CompareTo(a.Item.DateUtc),
            };
        }

        // Уникальные — выше обычных; среди обычных — по цене продажи.
        private static double Value(GiftViewModel g) =>
            g.IsUnique ? 1e12 + (g.Item.ValueAmount ?? 0) : Math.Max(g.Item.ConvertStars, g.Item.Stars);
    }

    // ===================================================================== Загрузка

    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadError = null;
        LoadingText = "Загрузка профиля…";
        try
        {
            var profile = await _tg.GetProfileAsync(_peer, withAvatar: !IsCompact);
            Profile = profile;
            StarsBalance = profile.StarsBalance;
            if (profile.Avatar is not null) Avatar = Images.FromBytes(profile.Avatar, 256);
            BuildInfoRows(profile);
            IsLoading = false;
            foreach (var name in new[] { nameof(IsGroupProfile), nameof(IsBotProfile), nameof(HasBotCommands), nameof(ShowGifts), nameof(MembersTitle) })
                OnPropertyChanged(name);
            _ = LoadMembersAsync();
            _ = LoadStoriesAsync();
            if (ShowGifts) await LoadGiftsAsync();
        }
        catch (Exception ex)
        {
            if (TgErrors.IsSessionDead(ex))
            {
                await _logout();
                return;
            }
            LoadError = TgErrors.Describe(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadGiftsAsync()
    {
        IsGiftsLoading = true;
        try
        {
            var progress = new Progress<(int Loaded, int Total)>(p => LoadingText = $"Подарки: {p.Loaded} из {p.Total}");
            var items = await _tg.GetGiftsAsync(_peer, progress);

            foreach (var g in Gifts) g.PropertyChanged -= OnGiftPropertyChanged;
            Gifts.Clear();
            foreach (var item in items)
            {
                var vm = new GiftViewModel(item);
                vm.PropertyChanged += OnGiftPropertyChanged;
                Gifts.Add(vm);
            }
            OnCountsChanged();
            RefreshView();

            // Картинки грузятся в фоне, по мере готовности появляются в карточках.
            _ = Task.WhenAll(Gifts.ToList().Select(g => g.LoadImageAsync(_tg.Media)));
        }
        catch (Exception ex)
        {
            ShowToast(TgErrors.Describe(ex), isError: true);
        }
        finally
        {
            IsGiftsLoading = false;
            LoadingText = null;
        }
    }

    private void OnGiftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GiftViewModel.IsSelected)) OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedStars));
        OnPropertyChanged(nameof(SelectedSummary));
        SellSelectedCommand.NotifyCanExecuteChanged();
    }

    private void OnCountsChanged()
    {
        foreach (var name in new[] { nameof(CountAll), nameof(CountRegular), nameof(CountUnique), nameof(CountConvertible),
                     nameof(CountHidden), nameof(ConvertibleStars), nameof(ConvertibleSummary) })
            OnPropertyChanged(name);
        OnSelectionChanged();
    }

    private void BuildInfoRows(ProfileInfo p)
    {
        InfoRows.Clear();
        InfoRows.Add(new InfoRow("", "ID", p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (p.Phone is { } phone) InfoRows.Add(new InfoRow("", "Телефон", phone, isSecret: true));
        if (p.Username is { } u) InfoRows.Add(new InfoRow("", "Имя пользователя", "@" + u));
        if (p.ExtraUsernames.Count > 0) InfoRows.Add(new InfoRow("", "Доп. юзернеймы", string.Join(", ", p.ExtraUsernames.Select(x => "@" + x))));
        if (p.Bio is { } bio) InfoRows.Add(new InfoRow("", "О себе", bio));
        if (p.Birthday is { } b) InfoRows.Add(new InfoRow("", "День рождения", BirthdayText(b)));
        if (p.PersonalChannelTitle is { } ch)
            InfoRows.Add(new InfoRow("", "Канал", p.PersonalChannelUsername is { } cu ? $"{ch} (@{cu})" : ch));
        if (p.CollectibleStatusTitle is { } cs) InfoRows.Add(new InfoRow("", "Статус-подарок", cs));
        if (p.EmojiStatusUntilUtc is { } until) InfoRows.Add(new InfoRow("", "Статус до", Fmt.Date(until)));
        if (p.BusinessAddress is { } addr) InfoRows.Add(new InfoRow("", "Адрес", addr));
        if (p.MembersCount is { } members)
            InfoRows.Add(new InfoRow("", p.Kind == PeerKind.Channel ? "Подписчики" : "Участники", Fmt.N(members)));
        if (p.OnlineCount is { } online) InfoRows.Add(new InfoRow("\uE8F2", "В сети", Fmt.N(online)));
        if (p.AdminsCount is { } admins) InfoRows.Add(new InfoRow("\uE7EF", "Администраторы", Fmt.N(admins)));
        if (p.InviteLink is { } link) InfoRows.Add(new InfoRow("\uE71B", "Ссылка-приглашение", link));
        if (p.Username is { } pub && p.Kind is PeerKind.Group or PeerKind.Channel) InfoRows.Add(new InfoRow("\uE71B", "Ссылка", $"t.me/{pub}"));
        if (p.LinkedChatTitle is { } linked) InfoRows.Add(new InfoRow("\uE8BD", p.Kind == PeerKind.Channel ? "Обсуждение" : "Канал", linked));
        if (p.BotDescription is { } botDesc) InfoRows.Add(new InfoRow("\uE99A", "О боте", botDesc));
        if (p.IsSupergroup) InfoRows.Add(new InfoRow("\uE716", "Тип", "Супергруппа"));
        if (p.IsSelf)
        if (p.LangCode is { } lang) InfoRows.Add(new InfoRow("", "Язык приложения", lang));
        var flags = new List<string>();
        if (p.IsPremium) flags.Add("Telegram Premium");
        if (p.IsVerified) flags.Add("верифицирован");
        if (p.IsScam) flags.Add("метка SCAM");
        if (p.IsFake) flags.Add("метка FAKE");
        if (flags.Count > 0) InfoRows.Add(new InfoRow("", "Отметки", string.Join(", ", flags)));
    }

    private static string BirthdayText(Core.Models.Birthday b)
    {
        var month = Fmt.Ru.DateTimeFormat.MonthGenitiveNames[b.Month - 1];
        if (b.Year is not { } y) return $"{b.Day} {month}";
        var today = DateTime.Today;
        var age = today.Year - y - (today.Month < b.Month || today.Month == b.Month && today.Day < b.Day ? 1 : 0);
        return $"{b.Day} {month} {y} ({age} {Fmt.Plural(age, "год", "года", "лет")})";
    }

    // ===================================================================== Команды

    // ===================================================================== Просмотр аватарок

    private IReadOnlyList<ProfilePhoto>? _photos;
    private readonly Dictionary<int, (ImageSource? Image, byte[]? Bytes)> _photoCache = [];
    private byte[]? _viewerBytes;

    [ObservableProperty] private bool _isAvatarViewerOpen;
    [ObservableProperty] private ImageSource? _viewerImage;
    [ObservableProperty] private bool _viewerLoading;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ViewerCounter), nameof(ViewerHasMany))]
    private int _viewerIndex;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ViewerCounter), nameof(ViewerHasMany))]
    private int _viewerCount;
    [ObservableProperty] private string? _viewerCaption;

    public string ViewerCounter => ViewerCount > 1 ? $"{ViewerIndex + 1} из {ViewerCount}" : "";
    public bool ViewerHasMany => ViewerCount > 1;

    [RelayCommand]
    private async Task OpenAvatar()
    {
        if (Avatar is null) return;
        IsAvatarViewerOpen = true;
        ViewerImage = Avatar; // сразу показываем то, что уже есть, затем подменяем полным размером
        ViewerIndex = 0;
        try
        {
            _photos ??= await _tg.GetProfilePhotosAsync(_peer);
            ViewerCount = Math.Max(_photos.Count, 1);
            if (_photos.Count == 0) return;
            await ShowPhotoAsync(0);
        }
        catch (Exception ex)
        {
            ViewerCount = 1;
            ShowToast(TgErrors.Describe(ex), isError: true);
        }
    }

    [RelayCommand]
    private Task NextPhoto() => ViewerCount > 1 ? ShowPhotoAsync((ViewerIndex + 1) % ViewerCount) : Task.CompletedTask;

    [RelayCommand]
    private Task PrevPhoto() => ViewerCount > 1 ? ShowPhotoAsync((ViewerIndex - 1 + ViewerCount) % ViewerCount) : Task.CompletedTask;

    [RelayCommand]
    private void CloseAvatar() => IsAvatarViewerOpen = false;

    [RelayCommand]
    private void SavePhoto()
    {
        if (_viewerBytes is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"avatar_{ViewerIndex + 1}.jpg",
            Filter = "JPEG|*.jpg",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            System.IO.File.WriteAllBytes(dlg.FileName, _viewerBytes);
            ShowToast("Фото сохранено");
        }
        catch (Exception ex) { ShowToast(ex.Message, isError: true); }
    }

    private async Task ShowPhotoAsync(int index)
    {
        if (_photos is null || index >= _photos.Count) return;
        ViewerIndex = index;
        var photo = _photos[index];
        ViewerCaption = index == 0 ? $"Текущее фото · {Fmt.DateShort(photo.DateUtc)}" : Fmt.DateShort(photo.DateUtc);
        if (_photoCache.TryGetValue(index, out var cached))
        {
            ViewerImage = cached.Image ?? ViewerImage;
            _viewerBytes = cached.Bytes;
            ViewerLoading = false;
            return;
        }

        ViewerLoading = true;
        var bytes = await _tg.Media.GetProfilePhotoAsync(photo);
        var img = Images.FromBytes(bytes);
        _photoCache[index] = (img, bytes);
        if (ViewerIndex == index)
        {
            _viewerBytes = bytes;
            if (img is not null) ViewerImage = img;
            ViewerLoading = false;
        }
    }

    partial void OnViewerIndexChanged(int value) => _viewerBytes = null;

    [RelayCommand]
    private Task Refresh()
    {
        _photos = null;
        _photoCache.Clear();
        return LoadAsync();
    }

    [RelayCommand]
    private void CopyRow(InfoRow? row)
    {
        if (row is null) return;
        try
        {
            Clipboard.SetText(row.Value);
            ShowToast($"{row.Label}: скопировано");
        }
        catch { /* буфер обмена занят другим приложением */ }
    }

    [RelayCommand]
    private void Logout() => Confirm = new ConfirmRequest(
        "Выйти из аккаунта?",
        "Сессия TgCheck будет завершена на серверах Telegram. Для повторного входа понадобится код или QR.",
        "Выйти", true,
        () => _logout());

    [RelayCommand]
    private void WriteMessage()
    {
        if (Profile?.Peer is { } p) _shell.OpenChat(p);
    }

    [RelayCommand]
    private void GoBack() => _shell.GoBackCommand.Execute(null);

    [RelayCommand]
    private void OpenSender(GiftViewModel? gift)
    {
        if (gift?.Item.Sender is not { } sender) return;
        SelectedGift = null;
        _shell.OpenProfile(sender);
    }

    [RelayCommand]
    private void OpenGift(GiftViewModel? gift)
    {
        if (gift is null) return;
        if (SelectionMode)
        {
            if (gift.CanConvert) gift.IsSelected = !gift.IsSelected;
            return;
        }
        SelectedGift = gift;
    }

    [RelayCommand]
    private void CloseGift() => SelectedGift = null;

    [RelayCommand]
    private void OpenNft(GiftViewModel? gift)
    {
        if (gift?.Item.NftUrl is { } url) Shell.Open(url);
    }

    [RelayCommand]
    private void ToggleSelectionMode() => SelectionMode = !SelectionMode;

    [RelayCommand]
    private void SelectAllConvertible()
    {
        var visible = GiftsView.Cast<GiftViewModel>().Where(g => g.CanConvert).ToList();
        var select = visible.Any(g => !g.IsSelected);
        foreach (var g in visible) g.IsSelected = select;
    }

    [RelayCommand]
    private void SellGift(GiftViewModel? gift)
    {
        if (gift is null || !gift.CanConvert) return;
        Confirm = new ConfirmRequest(
            "Продать подарок?",
            $"«{gift.Title}» будет навсегда удалён из вашего профиля, а на баланс поступит {Fmt.Stars(gift.ConvertStars)}.\n\nОтменить это действие нельзя.",
            $"Продать за {Fmt.N(gift.ConvertStars)}", false,
            async () =>
            {
                await _tg.ConvertGiftAsync(gift.Item);
                RemoveGift(gift);
                SelectedGift = null;
                await UpdateBalanceAsync();
                ShowToast($"Подарок продан: +{Fmt.Stars(gift.ConvertStars)}");
            });
    }

    [RelayCommand(CanExecute = nameof(CanSellSelected))]
    private void SellSelected()
    {
        var selected = Gifts.Where(g => g.IsSelected && g.CanConvert).ToList();
        if (selected.Count == 0) return;
        var total = selected.Sum(g => g.ConvertStars);
        Confirm = new ConfirmRequest(
            $"Продать {selected.Count} {Fmt.Plural(selected.Count, "подарок", "подарка", "подарков")}?",
            $"Выбранные подарки будут навсегда удалены из профиля, на баланс поступит {Fmt.Stars(total)}.\n\nОтменить это действие нельзя.",
            $"Продать за {Fmt.N(total)}", false,
            () => SellManyAsync(selected));
    }

    private bool CanSellSelected() => SelectedCount > 0 && !IsWorking;

    private async Task SellManyAsync(List<GiftViewModel> gifts)
    {
        long earned = 0;
        var sold = 0;
        string? lastError = null;
        for (var i = 0; i < gifts.Count; i++)
        {
            LoadingText = $"Продажа {i + 1} из {gifts.Count}…";
            try
            {
                await _tg.ConvertGiftAsync(gifts[i].Item);
                earned += gifts[i].ConvertStars;
                sold++;
                RemoveGift(gifts[i]);
            }
            catch (Exception ex)
            {
                lastError = TgErrors.Describe(ex);
                gifts[i].IsSelected = false;
            }
            if (i < gifts.Count - 1) await Task.Delay(350); // бережём лимиты Telegram
        }
        LoadingText = null;
        await UpdateBalanceAsync();
        if (sold == gifts.Count) SelectionMode = false;

        var summary = $"Продано {sold} из {gifts.Count}: +{Fmt.Stars(earned)}";
        ShowToast(lastError is null ? summary : $"{summary}. Ошибка: {lastError}", isError: lastError is not null);
    }

    private void RemoveGift(GiftViewModel gift)
    {
        gift.PropertyChanged -= OnGiftPropertyChanged;
        Gifts.Remove(gift);
        OnCountsChanged();
        OnPropertyChanged(nameof(IsGiftsEmpty));
    }

    private async Task UpdateBalanceAsync()
    {
        var (stars, _) = await _tg.GetBalancesAsync();
        StarsBalance = stars;
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        if (!IsWorking) Confirm = null;
    }

    [RelayCommand]
    private async Task AcceptConfirm()
    {
        if (Confirm is not { } request || IsWorking) return;
        IsWorking = true;
        try
        {
            await request.Action();
            Confirm = null;
        }
        catch (Exception ex)
        {
            Confirm = null;
            ShowToast(TgErrors.Describe(ex), isError: true);
        }
        finally
        {
            IsWorking = false;
            SellSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void HideToast() => Toast = null;

    private async void ShowToast(string text, bool isError = false)
    {
        // В мини-профиле своего тоста нет — показываем общий.
        if (IsCompact) { _shell.ShowToast(text, isError); return; }
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        ToastIsError = isError;
        Toast = text;
        try
        {
            await Task.Delay(isError ? 7000 : 3500, cts.Token);
            Toast = null;
        }
        catch (TaskCanceledException) { }
    }
}

/// <summary>Плитка истории в профиле (9:16).</summary>
public sealed partial class ProfileStoryViewModel(StoryInfo story) : ObservableObject
{
    public StoryInfo Story { get; } = story;
    public string DateText => Story.DateUtc.ToLocalTime().ToString("d MMM", Fmt.Ru);
    public bool IsVideo => Story.IsVideo;
    [ObservableProperty] private ImageSource? _preview;

    public async Task LoadPreviewAsync(TelegramService tg) =>
        Preview = Images.FromBytes(await tg.Media.GetMessagePreviewAsync(Story), 240);
}
