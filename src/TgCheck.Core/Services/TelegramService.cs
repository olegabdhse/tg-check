using System.Globalization;
using TgCheck.Core.Config;
using TgCheck.Core.Media;
using TgCheck.Core.Models;
using TL;
using WTelegram;

namespace TgCheck.Core.Services;

/// <summary>Что требуется от пользователя на следующем шаге входа по номеру.</summary>
public enum LoginStep { Completed, Code, Password, Email, EmailCode }

/// <summary>
/// Запрос пароля 2FA во время входа по QR. attempt &gt; 1 означает, что предыдущий пароль был неверным.
/// </summary>
public delegate Task<string> PasswordPrompt(string? hint, int attempt, CancellationToken ct);

/// <summary>
/// Весь обмен с Telegram: вход (номер/QR), профиль, подарки, продажа подарков за звёзды.
/// UI работает только с этим классом и моделями из TgCheck.Core.Models.
/// </summary>
public sealed partial class TelegramService : IAsyncDisposable
{
    private readonly AppEnv _env;
    private readonly ProtectedSessionStore _session;
    private readonly LocalCache _cache;
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private Client _client;

    private PasswordPrompt? _qrPasswordPrompt;
    private CancellationToken _qrToken;
    private int _qrPasswordAttempt;
    private string? _passwordHint;
    private int? _convertPeriodSeconds;

    public TelegramService(AppEnv env)
    {
        _env = env;
        _session = new ProtectedSessionStore(Path.Combine(env.DataDirectory, "session.dat"));
        _cache = new LocalCache(Path.Combine(env.DataDirectory, "cache", "chats"));
        Logging.Init(Path.Combine(env.DataDirectory, "logs"));
        _client = CreateClient();
        Media = new MediaService(() => _client, Path.Combine(env.DataDirectory, "cache"));
    }

    public MediaService Media { get; }

    public long UserId => _client.UserId;

    private Client CreateClient()
    {
        var client = new Client(Config, _session.Load(), _session.Save);
        client.OnUpdates += OnUpdatesAsync;
        client.OnOwnUpdates += OnUpdatesAsync;
        if (_env.Proxy is { } proxy)
        {
            if (AppEnv.IsMtProxy(proxy)) client.MTProxyUrl = proxy;
            else
            {
                var uri = new Uri(proxy);
                client.TcpHandler = (host, port) => Socks5.ConnectAsync(uri, host, port);
            }
        }
        return client;
    }

    public bool UsesProxy => _env.Proxy is not null;

    private string? Config(string what) => what switch
    {
        "api_id" => _env.ApiId.ToString(CultureInfo.InvariantCulture),
        "api_hash" => _env.ApiHash,
        "device_model" => "TgCheck Desktop",
        // При входе по номеру возвращаем null — тогда WTelegram отдаёт шаг "password" через Login(),
        // и UI показывает поле пароля. При входе по QR пароль запрашивается здесь.
        "password" => _qrPasswordPrompt is null ? null : RequestQrPassword(_qrPasswordPrompt),
        _ => null,
    };

    // Вызывается WTelegram из фонового потока во время входа по QR, когда включён облачный пароль.
    private string? RequestQrPassword(PasswordPrompt prompt)
    {
        if (_passwordHint is null)
        {
            try { _passwordHint = _client.Account_GetPassword().GetAwaiter().GetResult().hint ?? ""; }
            catch { _passwordHint = ""; }
        }
        var attempt = Interlocked.Increment(ref _qrPasswordAttempt);
        return prompt(NullIfEmpty(_passwordHint), attempt, _qrToken).GetAwaiter().GetResult();
    }

    // ===================================================================== Сессия

    /// <summary>Подключается и проверяет сохранённую сессию. true — пользователь уже вошёл.</summary>
    /// <param name="onRetry">Вызывается перед повторной попыткой (номер попытки), чтобы UI показал статус.</param>
    public async Task<bool> TryRestoreSessionAsync(Action<int>? onRetry = null)
    {
        // Соединение с Telegram у некоторых провайдеров рвётся/тормозит — несколько попыток с таймаутом.
        const int attempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var connect = _client.ConnectAsync();
                if (await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(20))) != connect)
                    throw new TimeoutException("Telegram не ответил за 20 секунд.");
                await connect;
                break;
            }
            catch (Exception ex) when (attempt < attempts && !TgErrors.IsSessionDead(ex) && ex is not RpcException)
            {
                Logging.Write($"connect attempt {attempt} failed: {ex.GetType().Name}: {ex.Message}");
                onRetry?.Invoke(attempt + 1);
                await ResetClientAsync(deleteSession: false);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
        }


        if (_client.UserId == 0) return false;
        try
        {
            var users = await _client.Users_GetUsers(InputUser.Self);
            return users.Length > 0 && users[0] is User;
        }
        catch (Exception ex) when (TgErrors.IsSessionDead(ex))
        {
            await ResetClientAsync(deleteSession: true);
            return false;
        }
    }

    /// <summary>Сбрасывает незавершённый вход (например, при переключении номер ↔ QR).</summary>
    public Task ResetLoginAsync() => ResetClientAsync(deleteSession: false);

    public async Task LogoutAsync()
    {
        try { await _client.Auth_LogOut(); }
        catch { /* сессия могла быть уже завершена на сервере */ }
        await ResetClientAsync(deleteSession: true);
    }

    private async Task ResetClientAsync(bool deleteSession)
    {
        await _clientLock.WaitAsync();
        try
        {
            var old = _client;
            await old.DisposeAsync();
            if (deleteSession)
            {
                _session.Delete();
                _cache.Clear();
            }
            _passwordHint = null;
            _client = CreateClient();
        }
        finally { _clientLock.Release(); }
    }

    // ===================================================================== Вход по номеру

    public Task<LoginStep> SubmitPhoneAsync(string phone) => LoginAsync(NormalizePhone(phone));
    public Task<LoginStep> SubmitCodeAsync(string code) => LoginAsync(new string(code.Where(char.IsLetterOrDigit).ToArray()));
    public Task<LoginStep> SubmitPasswordAsync(string password) => LoginAsync(password);
    public Task<LoginStep> SubmitEmailAsync(string email) => LoginAsync(email.Trim());

    private async Task<LoginStep> LoginAsync(string input)
    {
        var what = await _client.Login(input);
        return what switch
        {
            null => LoginStep.Completed,
            "verification_code" => LoginStep.Code,
            "password" => LoginStep.Password,
            "email" => LoginStep.Email,
            "email_verification_code" => LoginStep.EmailCode,
            "name" or "first_name" or "last_name" => throw new LoginException(
                "На этот номер не зарегистрирован аккаунт Telegram. Сначала зарегистрируйтесь в официальном приложении."),
            _ => throw new LoginException($"Telegram запросил неподдерживаемый шаг входа: {what}"),
        };
    }

    /// <summary>Подсказка к облачному паролю (если задана).</summary>
    public async Task<string?> GetPasswordHintAsync()
    {
        if (_passwordHint is not null) return NullIfEmpty(_passwordHint);
        try { _passwordHint = (await _client.Account_GetPassword()).hint ?? ""; }
        catch { _passwordHint = ""; }
        return NullIfEmpty(_passwordHint);
    }

    // ===================================================================== Вход по QR

    /// <param name="onQrUrl">Вызывается с tg://login?token=... каждый раз, когда нужно показать (обновить) QR.</param>
    /// <param name="passwordPrompt">Запрос облачного пароля, если он включён.</param>
    public async Task LoginWithQrAsync(Action<string> onQrUrl, PasswordPrompt passwordPrompt, CancellationToken ct)
    {
        _qrPasswordPrompt = passwordPrompt;
        _qrToken = ct;
        _qrPasswordAttempt = 0;
        try
        {
            var client = _client;
            await Task.Run(() => client.LoginWithQRCode(onQrUrl, logoutFirst: false, ct: ct), ct);
        }
        finally
        {
            _qrPasswordPrompt = null;
        }
    }

    // ===================================================================== Профиль

    /// <summary>Профиль: свой (peer == null) или другого пользователя/группы/канала.</summary>
    /// <param name="withAvatar">false — не скачивать большое фото (мини-профиль, аватар уже есть).</param>
    public async Task<ProfileInfo> GetProfileAsync(PeerRef? peer = null, bool withAvatar = true)
    {
        if (peer is not null) EnsureNotIgnored(peer);
        if (peer is { IsSelf: false, IsUser: false }) return await GetChatProfileAsync(peer, withAvatar);

        InputUserBase input = peer is { IsSelf: false, Info: User target } ? target : InputUser.Self;
        var full = await _client.Users_GetFullUser(input);
        full.CollectUsersChats(_users, _chats);
        var f = full.full_user;
        var me = full.users.TryGetValue(f.id, out var u) ? u : throw new InvalidOperationException("Не удалось получить пользователя.");
        var isSelf = me.id == _client.UserId;

        var (stars, ton) = isSelf ? await GetBalancesAsync() : (0L, (decimal?)null);

        string? channelTitle = null, channelUsername = null;
        if (f.personal_channel_id != 0 && full.chats.TryGetValue(f.personal_channel_id, out var ch))
        {
            channelTitle = ch.Title;
            channelUsername = (ch as Channel)?.MainUsername;
        }

        byte[]? avatar = null;
        if (withAvatar && me.photo is not null)
        {
            try
            {
                using var ms = new MemoryStream();
                await _client.DownloadProfilePhotoAsync(me, ms, big: true);
                avatar = ms.Length > 0 ? ms.ToArray() : null;
            }
            catch (Exception ex) { Logging.Write("avatar: " + ex.Message); }
        }

        var (presence, lastSeen) = me.status switch
        {
            UserStatusOnline => (Presence.Online, (DateTime?)null),
            UserStatusOffline off => (Presence.Offline, off.was_online),
            UserStatusRecently => (Presence.Recently, null),
            UserStatusLastWeek => (Presence.LastWeek, null),
            UserStatusLastMonth => (Presence.LastMonth, null),
            _ => (Presence.Unknown, null),
        };

        var main = me.MainUsername;
        var extra = me.ActiveUsernames?.Where(n => !string.Equals(n, main, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];

        return new ProfileInfo
        {
            Peer = PeerOf(me),
            IsSelf = isSelf,
            Kind = me.IsBot ? PeerKind.Bot : PeerKind.User,
            BotDescription = string.IsNullOrWhiteSpace(f.bot_info?.description) ? null : f.bot_info!.description,
            BotCommands = f.bot_info?.commands?.Select(c => $"/{c.command} — {c.description}").ToList() ?? [],
            Id = me.id,
            FirstName = me.first_name ?? "",
            LastName = me.last_name,
            Username = main,
            ExtraUsernames = extra,
            Phone = string.IsNullOrEmpty(me.phone) ? null : "+" + me.phone.TrimStart('+'),
            Bio = NullIfEmpty(f.about),
            Birthday = f.birthday is { } b
                ? new Models.Birthday(b.day, b.month, b.flags.HasFlag(TL.Birthday.Flags.has_year) ? b.year : null)
                : null,
            LangCode = me.lang_code,
            IsPremium = me.flags.HasFlag(User.Flags.premium),
            IsVerified = me.flags.HasFlag(User.Flags.verified),
            IsScam = me.flags.HasFlag(User.Flags.scam),
            IsFake = me.flags.HasFlag(User.Flags.fake),
            Presence = presence,
            LastSeenUtc = lastSeen,
            StarsBalance = stars,
            TonBalance = ton,
            GiftsCount = f.flags2.HasFlag(UserFull.Flags2.has_stargifts_count) ? f.stargifts_count : null,
            RatingLevel = f.stars_rating?.level,
            RatingStars = f.stars_rating?.stars,
            RatingNextLevelStars = f.stars_rating is { next_level_stars: > 0 } r ? r.next_level_stars : null,
            PersonalChannelTitle = channelTitle,
            PersonalChannelUsername = channelUsername,
            CollectibleStatusTitle = (me.emoji_status as EmojiStatusCollectible)?.title,
            EmojiStatusUntilUtc = me.emoji_status?.Until is { } until && until > DateTime.UnixEpoch ? until : null,
            BusinessAddress = NullIfEmpty(f.business_location?.address),
            Avatar = avatar,
        };
    }

    /// <summary>Все фото профиля (текущее — первое).</summary>
    public async Task<IReadOnlyList<ProfilePhoto>> GetProfilePhotosAsync(PeerRef? peer = null)
    {
        if (peer is { IsSelf: false, Info: not User }) return [];
        InputUserBase input = peer is { IsSelf: false, Info: User target } ? target : InputUser.Self;
        var photos = await _client.Photos_GetUserPhotos(input, 0, 0, 100);
        return photos.photos.OfType<Photo>().Select(p => new ProfilePhoto(p)).ToList();
    }

    /// <summary>Баланс звёзд и TON.</summary>
    public async Task<(long Stars, decimal? Ton)> GetBalancesAsync()
    {
        long stars = 0;
        decimal? ton = null;
        try
        {
            var st = await _client.Payments_GetStarsStatus(InputPeer.Self, ton: false);
            if (st.balance is StarsAmount sa) stars = sa.amount;
        }
        catch (Exception ex) { Logging.Write("stars: " + ex.Message); }
        try
        {
            var st = await _client.Payments_GetStarsStatus(InputPeer.Self, ton: true);
            if (st.balance is StarsTonAmount t && t.amount > 0) ton = t.amount / 1_000_000_000m;
        }
        catch (Exception ex) { Logging.Write("ton: " + ex.Message); }
        return (stars, ton);
    }

    // ===================================================================== Подарки

    /// <summary>Загружает все подарки профиля (с постраничной подгрузкой).</summary>
    public async Task<IReadOnlyList<GiftItem>> GetGiftsAsync(PeerRef? peer = null, IProgress<(int Loaded, int Total)>? progress = null, CancellationToken ct = default)
    {
        var isSelf = peer is null || peer.IsSelf;
        var target = isSelf ? InputPeer.Self : peer!.Info.ToInputPeer();
        var period = await GetConvertPeriodAsync();
        var result = new List<GiftItem>();
        var offset = "";
        do
        {
            ct.ThrowIfCancellationRequested();
            var page = await _client.Payments_GetSavedStarGifts(target, offset, 100);
            page.CollectUsersChats(_users, _chats);
            foreach (var sg in page.gifts)
            {
                if (sg.from_id is { } from && IsIgnored(from)) continue;
                try { result.Add(Map(sg, page, period, isSelf)); }
                catch (Exception ex) { Logging.Write("gift map: " + ex); }
            }
            progress?.Report((result.Count, page.count));
            offset = page.next_offset;
        } while (!string.IsNullOrEmpty(offset));
        return result;
    }

    /// <summary>
    /// Продаёт (конвертирует) обычный подарок за звёзды. Действие необратимо: подарок исчезает из профиля.
    /// </summary>
    public async Task ConvertGiftAsync(GiftItem gift)
    {
        if (!gift.CanConvert || gift.InputRef is null)
            throw new InvalidOperationException("Этот подарок нельзя продать за звёзды.");
        var ok = await _client.Payments_ConvertStarGift(gift.InputRef);
        if (!ok) throw new InvalidOperationException("Telegram отклонил продажу подарка.");
    }

    /// <summary>Сколько времени после получения подарок можно продать (app config Telegram, по умолчанию 90 дней).</summary>
    public async Task<TimeSpan> GetConvertPeriodAsync()
    {
        if (_convertPeriodSeconds is null)
        {
            _convertPeriodSeconds = 0;
            try
            {
                if (await _client.Help_GetAppConfig() is Help_AppConfig { config: JsonObject obj })
                {
                    var v = obj.value.FirstOrDefault(x => x.key == "stargifts_convert_period_max")?.value;
                    if (v is JsonNumber n) _convertPeriodSeconds = (int)n.value;
                }
            }
            catch (Exception ex) { Logging.Write("appconfig: " + ex.Message); }
        }
        return _convertPeriodSeconds > 0 ? TimeSpan.FromSeconds(_convertPeriodSeconds.Value) : DefaultConvertPeriod;
    }

    private static readonly TimeSpan DefaultConvertPeriod = TimeSpan.FromDays(90);

    private GiftItem Map(SavedStarGift sg, Payments_SavedStarGifts page, TimeSpan convertPeriod, bool isSelf)
    {
        var flags = sg.flags;
        string? fromName = null;
        long? fromId = null;
        IPeerInfo? fromPeer = null;
        if (flags.HasFlag(SavedStarGift.Flags.has_from_id) && sg.from_id is not null)
        {
            fromId = sg.from_id.ID;
            fromPeer = page.UserOrChat(sg.from_id);
            fromName = PeerName(fromPeer);
        }

        InputSavedStarGift? input = null;
        if (flags.HasFlag(SavedStarGift.Flags.has_msg_id)) input = new InputSavedStarGiftUser { msg_id = sg.msg_id };
        else if (flags.HasFlag(SavedStarGift.Flags.has_saved_id)) input = new InputSavedStarGiftChat { peer = InputPeer.Self, saved_id = sg.saved_id };

        var refunded = flags.HasFlag(SavedStarGift.Flags.refunded);
        var message = flags.HasFlag(SavedStarGift.Flags.has_message) ? NullIfEmpty(sg.message?.text) : null;

        switch (sg.gift)
        {
            case StarGift g:
            {
                var convert = flags.HasFlag(SavedStarGift.Flags.has_convert_stars) ? sg.convert_stars : 0;
                // Telegram продолжает присылать convert_stars и после окончания срока — проверяем дату сами.
                var deadline = convert > 0 && !refunded ? sg.date + convertPeriod : (DateTime?)null;
                var bg = g.flags.HasFlag(StarGift.Flags.has_background) ? g.background : null;
                var limited = g.flags.HasFlag(StarGift.Flags.limited);
                return new GiftItem
                {
                    Kind = GiftKind.Regular,
                    FromPeer = fromPeer,
                    Sender = fromPeer is null ? null : PeerOf(fromPeer),
                    GiftId = g.id,
                    Title = NullIfEmpty(g.title),
                    DateUtc = sg.date,
                    FromId = fromId,
                    FromName = fromName,
                    Message = message,
                    IsHidden = flags.HasFlag(SavedStarGift.Flags.unsaved),
                    IsNameHidden = flags.HasFlag(SavedStarGift.Flags.name_hidden),
                    IsPinned = flags.HasFlag(SavedStarGift.Flags.pinned_to_top),
                    IsRefunded = refunded,
                    Stars = g.stars,
                    ConvertStars = convert,
                    ConvertDeadlineUtc = deadline,
                    CanConvert = isSelf && deadline > DateTime.UtcNow && input is not null,
                    IsLimited = limited,
                    IsSoldOut = g.flags.HasFlag(StarGift.Flags.sold_out),
                    AvailabilityRemains = limited ? g.availability_remains : null,
                    AvailabilityTotal = limited ? g.availability_total : null,
                    CanUpgrade = flags.HasFlag(SavedStarGift.Flags.can_upgrade),
                    UpgradeStars = flags.HasFlag(SavedStarGift.Flags.has_upgrade_stars) ? sg.upgrade_stars : 0,
                    CenterColor = bg?.center_color,
                    EdgeColor = bg?.edge_color,
                    TextColor = bg?.text_color,
                    InputRef = input,
                    Sticker = g.sticker,
                };
            }
            case StarGiftUnique u:
            {
                var model = u.attributes?.OfType<StarGiftAttributeModel>().FirstOrDefault();
                var backdrop = u.attributes?.OfType<StarGiftAttributeBackdrop>().FirstOrDefault();
                var pattern = u.attributes?.OfType<StarGiftAttributePattern>().FirstOrDefault();
                var original = u.attributes?.OfType<StarGiftAttributeOriginalDetails>().FirstOrDefault();
                var hasValue = u.flags.HasFlag(StarGiftUnique.Flags.has_value_amount);

                return new GiftItem
                {
                    Kind = GiftKind.Unique,
                    FromPeer = fromPeer,
                    Sender = fromPeer is null ? null : PeerOf(fromPeer),
                    GiftId = u.id,
                    Title = NullIfEmpty(u.title),
                    DateUtc = sg.date,
                    FromId = fromId,
                    FromName = fromName,
                    Message = message,
                    IsHidden = flags.HasFlag(SavedStarGift.Flags.unsaved),
                    IsNameHidden = flags.HasFlag(SavedStarGift.Flags.name_hidden),
                    IsPinned = flags.HasFlag(SavedStarGift.Flags.pinned_to_top),
                    IsRefunded = refunded,
                    Number = u.num,
                    Slug = NullIfEmpty(u.slug),
                    Model = model is null ? null : new GiftAttribute(model.name, Rarity(model.rarity)),
                    Backdrop = backdrop is null ? null : new GiftAttribute(backdrop.name, Rarity(backdrop.rarity)),
                    Pattern = pattern is null ? null : new GiftAttribute(pattern.name, Rarity(pattern.rarity)),
                    Issued = u.availability_issued,
                    AvailabilityTotal = u.availability_total,
                    ValueAmount = hasValue ? u.value_amount : null,
                    ValueCurrency = hasValue ? u.value_currency : null,
                    CanTransferAtUtc = flags.HasFlag(SavedStarGift.Flags.has_can_transfer_at) ? sg.can_transfer_at : null,
                    CanResellAtUtc = flags.HasFlag(SavedStarGift.Flags.has_can_resell_at) ? sg.can_resell_at : null,
                    CanExportAtUtc = flags.HasFlag(SavedStarGift.Flags.has_can_export_at) ? sg.can_export_at : null,
                    TransferStars = flags.HasFlag(SavedStarGift.Flags.has_transfer_stars) ? sg.transfer_stars : 0,
                    OriginalSender = original?.sender_id is { } sid ? PeerName(page.UserOrChat(sid)) : null,
                    OriginalMessage = NullIfEmpty(original?.message?.text),
                    CenterColor = backdrop?.center_color,
                    EdgeColor = backdrop?.edge_color,
                    PatternColor = backdrop?.pattern_color,
                    TextColor = backdrop?.text_color,
                    InputRef = input ?? (u.slug is { Length: > 0 } ? new InputSavedStarGiftSlug { slug = u.slug } : null),
                    Sticker = model?.document,
                    PatternDocument = pattern?.document,
                };
            }
            default:
                throw new NotSupportedException(sg.gift?.GetType().Name);
        }
    }

    private static GiftRarity? Rarity(StarGiftAttributeRarityBase? r) => r switch
    {
        StarGiftAttributeRarity p => new GiftRarity(p.permille / 10.0, null),
        StarGiftAttributeRarityUncommon => new GiftRarity(null, "Необычный"),
        StarGiftAttributeRarityRare => new GiftRarity(null, "Редкий"),
        StarGiftAttributeRarityEpic => new GiftRarity(null, "Эпический"),
        StarGiftAttributeRarityLegendary => new GiftRarity(null, "Легендарный"),
        _ => null,
    };

    private static string? PeerName(IPeerInfo? peer) => peer switch
    {
        User u => string.Join(' ', new[] { u.first_name, u.last_name }.Where(s => !string.IsNullOrWhiteSpace(s)))
                  is { Length: > 0 } n ? n : (u.MainUsername is { } un ? "@" + un : null),
        ChatBase c => c.Title,
        _ => null,
    };

    private static string NormalizePhone(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length < 7) throw new LoginException("Введите номер телефона полностью, в международном формате.");
        return "+" + digits;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        _clientLock.Dispose();
    }
}
