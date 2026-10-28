using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.App.Helpers;
using TgCheck.Core.Media;
using TgCheck.Core.Models;
using TgCheck.Core.Services;

namespace TgCheck.App.ViewModels;

/// <summary>Файл, прикреплённый к сообщению перед отправкой.</summary>
public sealed partial class AttachmentViewModel(string path) : ObservableObject
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public string SizeText => Fmt.FileSize(new FileInfo(Path).Length);
    public bool IsImage => System.IO.Path.GetExtension(Path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".gif";
    public ImageSource? Thumb { get; } = LoadThumb(path);
    [ObservableProperty] private double _progress;

    private static ImageSource? LoadThumb(string path)
    {
        if (System.IO.Path.GetExtension(path).ToLowerInvariant() is not (".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif")) return null;
        try { return Images.FromBytes(File.ReadAllBytes(path), 96); }
        catch { return null; }
    }
}

/// <summary>Открытый чат: история, отправка, ответы, правка, удаление, медиа, опросы, вложения.</summary>
public sealed partial class ConversationViewModel : PeerAvatarViewModel
{
    private readonly TelegramService _tg;
    private readonly ChatsViewModel _owner;
    private readonly Dictionary<int, MessageViewModel> _byId = [];
    private CancellationTokenSource? _typingCts;
    private int _readOutboxMaxId;
    private int _lastMarkedRead;
    private bool _loadedOnce;

    public ConversationViewModel(TelegramService tg, ChatsViewModel owner, PeerRef peer, int readOutboxMaxId) : base(peer)
    {
        _tg = tg;
        _owner = owner;
        _readOutboxMaxId = readOutboxMaxId;
        UpdateStatus();
        AudioPlayer.Instance.Ended += OnAudioEnded;
    }

    public ChatsViewModel Owner => _owner;
    public MediaPickerViewModel Picker => _owner.Shell.Picker;

    /// <summary>Сообщения и разделители дат в хронологическом порядке.</summary>
    public ObservableCollection<object> Items { get; } = [];
    public ObservableCollection<AttachmentViewModel> Attachments { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoadingOlder;
    [ObservableProperty] private bool _hasMoreHistory = true;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isTypingNow;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private string _draft = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasComposerBar), nameof(ComposerBarTitle), nameof(ComposerBarText))]
    private MessageViewModel? _replyTo;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasComposerBar), nameof(ComposerBarTitle), nameof(ComposerBarText))]
    private MessageViewModel? _editing;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private bool _isSending;
    [ObservableProperty] private bool _sendAsFiles;
    [ObservableProperty] private bool _isPickerOpen;

    // Просмотр фото и видео
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsViewerOpen))] private ImageSource? _viewerImage;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsViewerOpen), nameof(ViewerIsVideo))] private MessageViewModel? _viewerMessage;
    [ObservableProperty] private bool _viewerLoading;
    [ObservableProperty] private double _viewerProgress;
    [ObservableProperty] private string? _viewerVideoPath;

    public bool IsViewerOpen => ViewerMessage is not null;
    public bool ViewerIsVideo => ViewerMessage?.Item.Media is MediaKind.Video or MediaKind.Gif;

    // Мини-профиль справа
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsSideProfileOpen))] private ProfileViewModel? _sideProfile;
    public bool IsSideProfileOpen => SideProfile is not null;

    /// <summary>Сколько новых сообщений пришло, пока пользователь читал историю выше.</summary>
    [ObservableProperty] private int _pendingNewCount;

    public bool CanWrite => Peer.CanWrite;
    public bool HasComposerBar => ReplyTo is not null || Editing is not null;
    public bool HasAttachments => Attachments.Count > 0;
    public string ComposerBarTitle => Editing is not null ? "Редактирование" : $"В ответ {(ReplyTo is { IsOut: true } ? "себе" : ReplyTo?.SenderName)}";
    public string ComposerBarText => (Editing ?? ReplyTo)?.Item.Preview ?? "";

    /// <summary>View: прокрутить вниз (true — плавно).</summary>
    public event Action<bool>? ScrollToBottomRequested;
    /// <summary>View: вставить текст (эмодзи) в позицию курсора.</summary>
    public event Action<string>? InsertTextRequested;
    /// <summary>View: сейчас пользователь внизу ленты?</summary>
    public Func<bool>? IsAtBottom { get; set; }

    public int LastMessageId => _byId.Count == 0 ? 0 : _byId.Keys.Max();

    // ===================================================================== Загрузка

    public async Task LoadAsync()
    {
        if (IsLoading) return;
        Error = null;

        // 1) Мгновенно — из локального кэша.
        var cached = _loadedOnce ? null : await Task.Run(() => _tg.GetCachedHistory(Peer));
        if (cached is { Count: > 0 } && Items.Count == 0)
        {
            ResetItems(cached);
            ScrollToBottomRequested?.Invoke(false);
        }
        else IsLoading = Items.Count == 0;

        // 2) Из сети — слияние без перерисовки всей ленты.
        try
        {
            var history = await _tg.GetHistoryAsync(Peer, 0, 50);
            if (Items.Count == 0) ResetItems(history);
            else Merge(history);
            HasMoreHistory = history.Count >= 50;
            await ResolveRepliesAsync(history);
            UpdateReplyKeyboard();
            if (!_loadedOnce)
            {
                ScrollToBottomRequested?.Invoke(false);
                _ = LoadPinnedAsync();
                _ = LoadBotMenuAsync();
            }
            _loadedOnce = true;
            MarkRead();
        }
        catch (Exception ex)
        {
            if (Items.Count == 0) Error = TgErrors.Describe(ex);
            else _owner.ShowToast(TgErrors.Describe(ex), true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ResetItems(IReadOnlyList<MessageItem> messages)
    {
        Items.Clear();
        _byId.Clear();
        AppendRange(messages, animate: false);
        foreach (var vm in _byId.Values.Distinct())
            if (vm.Item.ReplyToId is { } r && _byId.TryGetValue(r, out var t)) vm.SetReply(t.Item);
    }

    // Сеть vs то, что уже показано: новые — добавить, изменённые — обновить, удалённые в этом диапазоне — убрать.
    private void Merge(IReadOnlyList<MessageItem> fresh)
    {
        if (fresh.Count == 0) return;
        var freshIds = fresh.Select(m => m.Id).ToHashSet();
        var min = fresh.Min(m => m.Id);
        var gone = _byId.Keys.Where(id => id >= min && !freshIds.Contains(id)).ToList();
        if (gone.Count > 0) OnMessagesDeleted(gone);
        foreach (var m in fresh.Where(m => _byId.ContainsKey(m.Id))) OnMessageEdited(m);
        var newer = fresh.Where(m => !_byId.ContainsKey(m.Id)).ToList();
        if (newer.Count > 0)
        {
            var maxShown = _byId.Count == 0 ? 0 : _byId.Keys.Max();
            AppendRange(newer.Where(m => m.Id > maxShown), animate: _loadedOnce);
            var older = newer.Where(m => m.Id < maxShown).ToList();
            if (older.Count > 0) PrependRange(older);
        }
    }

    /// <summary>Подгрузить более старые сообщения (при прокрутке вверх).</summary>
    public async Task<bool> LoadOlderAsync()
    {
        if (IsLoadingOlder || !HasMoreHistory || IsLoading || _byId.Count == 0) return false;
        IsLoadingOlder = true;
        try
        {
            var oldest = _byId.Keys.Min();
            var older = await _tg.GetHistoryAsync(Peer, oldest, 50);
            HasMoreHistory = older.Count >= 50;
            PrependRange(older);
            await ResolveRepliesAsync(older);
            return older.Count > 0;
        }
        catch (Exception ex)
        {
            _owner.ShowToast(TgErrors.Describe(ex), true);
            return false;
        }
        finally
        {
            IsLoadingOlder = false;
        }
    }

    private void AppendRange(IEnumerable<MessageItem> messages, bool animate)
    {
        foreach (var m in messages)
        {
            if (_byId.ContainsKey(m.Id)) continue;
            var last = Items.OfType<MessageViewModel>().LastOrDefault();
            // Части альбома склеиваются в одно сообщение.
            if (m.GroupedId != 0 && last is not null && last.Item.GroupedId == m.GroupedId)
            {
                var part = Create(m);
                last.AddToAlbum(part, _tg.Media);
                _byId[m.Id] = last;
                continue;
            }
            var vm = Create(m);
            vm.IsNew = animate;
            var day = m.DateUtc.ToLocalTime().Date;
            if (last is null || last.Item.DateUtc.ToLocalTime().Date != day) Items.Add(new DateSeparatorViewModel(day));
            Items.Add(vm);
            _byId[m.Id] = vm;
            Regroup(Items.Count - 1);
        }
    }

    private void PrependRange(IReadOnlyList<MessageItem> messages)
    {
        var fresh = messages.Where(m => !_byId.ContainsKey(m.Id)).OrderBy(m => m.Id).ToList();
        if (fresh.Count == 0) return;

        if (Items.FirstOrDefault() is DateSeparatorViewModel) Items.RemoveAt(0);

        var block = new List<object>();
        DateTime? lastDay = null;
        MessageViewModel? prev = null;
        foreach (var m in fresh)
        {
            if (m.GroupedId != 0 && prev is not null && prev.Item.GroupedId == m.GroupedId)
            {
                prev.AddToAlbum(Create(m), _tg.Media);
                _byId[m.Id] = prev;
                continue;
            }
            var day = m.DateUtc.ToLocalTime().Date;
            if (lastDay != day) block.Add(new DateSeparatorViewModel(day));
            lastDay = day;
            var vm = Create(m);
            block.Add(vm);
            _byId[m.Id] = vm;
            prev = vm;
        }
        var firstExisting = Items.OfType<MessageViewModel>().FirstOrDefault();
        if (firstExisting is not null && firstExisting.Item.DateUtc.ToLocalTime().Date != lastDay)
            block.Add(new DateSeparatorViewModel(firstExisting.Item.DateUtc.ToLocalTime().Date));

        for (var i = 0; i < block.Count; i++) Items.Insert(i, block[i]);
        for (var i = 0; i <= block.Count && i < Items.Count; i++) Regroup(i);
    }

    private MessageViewModel Create(MessageItem m)
    {
        var vm = new MessageViewModel(m, Peer) { IsRead = m.IsOut && m.Id <= _readOutboxMaxId };
        vm.EnsurePreview(_tg.Media);
        vm.EnsureSenderAvatar(_tg.Media);
        if (vm.IsVoice || vm.IsAudio) vm.BuildWave();
        VoicePlayback.Attach(vm);
        if (m.ReplyToId is { } r && _byId.TryGetValue(r, out var target)) vm.SetReply(target.Item);
        return vm;
    }

    // Серии сообщений от одного отправителя (< 5 минут) — без повторного имени/аватара.
    private void Regroup(int index)
    {
        if (index < 0 || index >= Items.Count || Items[index] is not MessageViewModel vm) return;
        var prev = index > 0 ? Items[index - 1] as MessageViewModel : null;
        var next = index + 1 < Items.Count ? Items[index + 1] as MessageViewModel : null;
        vm.IsFirstInGroup = !SameGroup(prev, vm);
        vm.IsLastInGroup = !SameGroup(vm, next);
        if (prev is not null) prev.IsLastInGroup = !SameGroup(prev, vm);
    }

    private static bool SameGroup(MessageViewModel? a, MessageViewModel? b) =>
        a is not null && b is not null && !a.IsService && !b.IsService &&
        a.Item.FromKey == b.Item.FromKey && a.IsOut == b.IsOut &&
        (b.Item.DateUtc - a.Item.DateUtc) < TimeSpan.FromMinutes(5);

    private async Task ResolveRepliesAsync(IEnumerable<MessageItem> messages)
    {
        var list = messages.ToList();
        var missing = list.Where(m => m.ReplyToId is { } r && !_byId.ContainsKey(r))
            .Select(m => m.ReplyToId!.Value).Distinct().ToArray();
        foreach (var vm in list.Select(m => _byId.GetValueOrDefault(m.Id)).OfType<MessageViewModel>())
            if (vm.Item.ReplyToId is { } r && _byId.TryGetValue(r, out var t)) vm.SetReply(t.Item);
        if (missing.Length == 0) return;
        try
        {
            var found = (await _tg.GetMessagesAsync(Peer, missing)).ToDictionary(m => m.Id);
            foreach (var vm in _byId.Values.Distinct().Where(v => v.Item.ReplyToId is { } r && missing.Contains(r)))
                vm.SetReply(found.GetValueOrDefault(vm.Item.ReplyToId!.Value));
        }
        catch { /* превью ответа не критично */ }
    }

    // ===================================================================== События

    public void OnMessageReceived(MessageItem m)
    {
        if (_byId.ContainsKey(m.Id)) return;
        if (_detached)
        {
            if (!m.IsOut) PendingNewCount++;
            return;
        }
        var atBottom = IsAtBottom?.Invoke() ?? true;
        AppendRange([m], animate: true);
        if (m.ReplyKeyboard is not null || (!m.IsOut && Peer.Kind == PeerKind.Bot)) UpdateReplyKeyboard();
        _ = ResolveRepliesAsync([m]);
        IsTypingNow = false;
        UpdateStatus();
        if (atBottom || m.IsOut)
        {
            ScrollToBottomRequested?.Invoke(true);
            MarkRead();
        }
        else if (!m.IsOut)
        {
            PendingNewCount++;
        }
    }

    public void OnMessageEdited(MessageItem m)
    {
        if (!_byId.TryGetValue(m.Id, out var vm)) return;
        if (vm.IsAlbum)
        {
            if (vm.Album.FirstOrDefault(a => a.Id == m.Id) is { } part) { part.Text = m.Text; }
            if (m.Text.Length > 0) vm.Text = m.Text;
            return;
        }
        if (m.Poll is { } poll) vm.SetPoll(poll);
        if (vm.Text == m.Text && vm.IsEdited == (m.EditDateUtc is not null)) return;
        vm.Text = m.Text;
        vm.IsEdited = m.EditDateUtc is not null;
    }

    public void OnPollUpdated(long pollId, PollInfo poll)
    {
        foreach (var vm in _byId.Values.Distinct().Where(v => v.Poll?.Id == pollId)) vm.SetPoll(poll);
    }

    public void OnMessagesDeleted(IEnumerable<int> ids)
    {
        foreach (var id in ids)
        {
            if (!_byId.Remove(id, out var vm)) continue;
            if (vm.IsAlbum && vm.Album.Count > 1)
            {
                vm.RemoveFromAlbum(id);
                if (vm.Album.Count > 0) continue;
            }
            var index = Items.IndexOf(vm);
            if (index < 0) continue;
            Items.RemoveAt(index);
            // Убираем «висящий» разделитель даты.
            if (index > 0 && Items[index - 1] is DateSeparatorViewModel && (index >= Items.Count || Items[index] is DateSeparatorViewModel))
                Items.RemoveAt(index - 1);
            Regroup(Math.Min(index, Items.Count - 1));
            Regroup(index - 1);
        }
    }

    public void OnOutboxRead(int maxId)
    {
        _readOutboxMaxId = Math.Max(_readOutboxMaxId, maxId);
        foreach (var vm in _byId.Values.Distinct().Where(v => v.IsOut && !v.IsRead && v.Id <= _readOutboxMaxId)) vm.IsRead = true;
    }

    public async void OnTyping(PeerRef who)
    {
        _typingCts?.Cancel();
        var cts = _typingCts = new CancellationTokenSource();
        IsTypingNow = true;
        Status = Peer.IsUser ? "печатает" : $"{who.DisplayTitle} печатает";
        try
        {
            await Task.Delay(5500, cts.Token);
            IsTypingNow = false;
            UpdateStatus();
        }
        catch (TaskCanceledException) { }
    }

    public void UpdateStatus()
    {
        if (IsTypingNow) return;
        Status = Peer.Kind switch
        {
            _ when Peer.IsSelf => "сохранённые сообщения",
            PeerKind.Bot => "бот",
            PeerKind.User => Fmt.LastSeen(_tg.GetPresence(Peer).Presence, _tg.GetPresence(Peer).LastSeen),
            PeerKind.Channel => Peer.MembersCount > 0
                ? $"{Fmt.N(Peer.MembersCount)} {Fmt.Plural(Peer.MembersCount, "подписчик", "подписчика", "подписчиков")}" : "канал",
            _ => Peer.MembersCount > 0
                ? $"{Fmt.N(Peer.MembersCount)} {Fmt.Plural(Peer.MembersCount, "участник", "участника", "участников")}" : "группа",
        };
    }

    public void MarkRead()
    {
        var max = LastMessageId;
        if (max <= _lastMarkedRead || max == 0) return;
        _lastMarkedRead = max;
        _owner.OnChatRead(Peer.Key);
        _ = _tg.MarkReadAsync(Peer, max);
    }

    // ===================================================================== Esc

    /// <summary>Закрыть верхний слой. false — закрывать в чате нечего (можно закрыть сам чат).</summary>
    public bool HandleEscape()
    {
        if (IsViewerOpen) { CloseViewer(); return true; }
        if (IsPickerOpen) { IsPickerOpen = false; return true; }
        if (HasAttachments) { Attachments.Clear(); OnPropertyChanged(nameof(HasAttachments)); return true; }
        if (HasComposerBar) { CancelComposerBar(); return true; }
        if (SideProfile is { IsAvatarViewerOpen: true } sp) { sp.IsAvatarViewerOpen = false; return true; }
        if (IsCommentsOpen) { Comments = null; return true; }
        if (IsSideProfileOpen) { SideBack(); return true; }
        return false;
    }

    // ===================================================================== Композер

    partial void OnDraftChanged(string value)
    {
        if (value.Length > 0 && Editing is null) _ = _tg.NotifyTypingAsync(Peer);
    }

    private bool CanSend() => (!string.IsNullOrWhiteSpace(Draft) || HasAttachments) && !IsSending;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        if (HasAttachments)
        {
            await SendAttachmentsAsync(text);
            return;
        }
        if (text.Length == 0) return;
        IsSending = true;
        try
        {
            if (Editing is { } edit)
            {
                await _tg.EditTextAsync(Peer, edit.Id, text);
                edit.Text = text;
                edit.IsEdited = true;
                Editing = null;
                Draft = "";
            }
            else
            {
                var reply = ReplyTo?.Id;
                Draft = "";
                ReplyTo = null;
                var sent = await _tg.SendTextAsync(Peer, text, reply);
                if (!_byId.ContainsKey(sent.Id))
                {
                    OnMessageReceived(sent);
                    _owner.OnMessageReceived(sent, fromConversation: true);
                }
            }
        }
        catch (Exception ex)
        {
            if (Editing is null && Draft.Length == 0) Draft = text; // вернуть текст, если не ушло
            _owner.ShowToast(TgErrors.Describe(ex), true);
        }
        finally
        {
            IsSending = false;
        }
    }

    public void AddAttachments(IEnumerable<string> paths)
    {
        foreach (var p in paths.Where(File.Exists))
            if (Attachments.All(a => a.Path != p)) Attachments.Add(new AttachmentViewModel(p));
        OnPropertyChanged(nameof(HasAttachments));
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Attach()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Прикрепить файлы" };
        if (dlg.ShowDialog() == true) AddAttachments(dlg.FileNames);
    }

    [RelayCommand]
    private void RemoveAttachment(AttachmentViewModel? a)
    {
        if (a is null) return;
        Attachments.Remove(a);
        OnPropertyChanged(nameof(HasAttachments));
        SendCommand.NotifyCanExecuteChanged();
    }

    private async Task SendAttachmentsAsync(string caption)
    {
        IsSending = true;
        var files = Attachments.ToList();
        try
        {
            await _tg.SendFilesAsync(Peer, files.Select(f => f.Path).ToList(), caption.Length > 0 ? caption : null, ReplyTo?.Id,
                SendAsFiles, new Progress<(int Index, double Fraction)>(p => files[p.Index].Progress = p.Fraction));
            Attachments.Clear();
            OnPropertyChanged(nameof(HasAttachments));
            Draft = "";
            ReplyTo = null;
            ScrollToBottomRequested?.Invoke(true);
        }
        catch (Exception ex)
        {
            _owner.ShowToast(TgErrors.Describe(ex), true);
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private void TogglePicker() => IsPickerOpen = !IsPickerOpen;

    partial void OnIsPickerOpenChanged(bool value)
    {
        if (value) _ = Picker.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void InsertEmoji(string? emoji)
    {
        if (string.IsNullOrEmpty(emoji)) return;
        Picker.RememberEmoji(emoji);
        InsertTextRequested?.Invoke(emoji);
    }

    [RelayCommand]
    private async Task SendSticker(MediaHolder? media)
    {
        if (media is null) return;
        IsPickerOpen = false;
        try
        {
            var reply = ReplyTo?.Id;
            ReplyTo = null;
            var sent = await _tg.SendDocumentAsync(Peer, media, reply);
            OnMessageReceived(sent);
            _owner.OnMessageReceived(sent, fromConversation: true);
        }
        catch (Exception ex)
        {
            _owner.ShowToast(TgErrors.Describe(ex), true);
        }
    }

    [RelayCommand]
    private void Reply(MessageViewModel? m)
    {
        if (m is null || m.IsService) return;
        Editing = null;
        ReplyTo = m;
    }

    [RelayCommand]
    private void Edit(MessageViewModel? m)
    {
        if (m is null || !m.IsOut || m.IsService) return;
        ReplyTo = null;
        Editing = m;
        Draft = m.Text;
    }

    [RelayCommand]
    private void CancelComposerBar()
    {
        if (Editing is not null) Draft = "";
        Editing = null;
        ReplyTo = null;
    }

    [RelayCommand]
    private void Copy(MessageViewModel? m)
    {
        if (m is null) return;
        try
        {
            Clipboard.SetText(m.HasText ? m.Text : m.Item.Preview);
            _owner.ShowToast("Скопировано");
        }
        catch { /* буфер занят */ }
    }

    [RelayCommand]
    private void Delete(MessageViewModel? m)
    {
        if (m is null) return;
        var everyone = m.IsOut || Peer.Kind != PeerKind.User;
        var ids = m.AllIds.ToArray();
        _owner.Confirm = new ConfirmRequest(
            ids.Length > 1 ? $"Удалить {ids.Length} сообщения?" : "Удалить сообщение?",
            Peer.Kind == PeerKind.User
                ? "Сообщение будет удалено у вас и у собеседника."
                : "Сообщение будет удалено для всех участников (нужны права администратора, если оно не ваше).",
            "Удалить", true,
            async () =>
            {
                await _tg.DeleteAsync(Peer, ids, everyone);
                OnMessagesDeleted(ids);
                _owner.OnLocalDeleted(Peer.Key);
            });
    }

    [RelayCommand]
    private async Task Download(MessageViewModel? m)
    {
        if (m is null || !m.Item.CanDownload) return;
        var ext = m.Item.Media switch
        {
            MediaKind.Photo => ".jpg",
            MediaKind.Voice => ".ogg",
            MediaKind.Video or MediaKind.VideoNote or MediaKind.Gif => ".mp4",
            _ => "",
        };
        var name = m.Item.Media is MediaKind.Document or MediaKind.Audio && m.FileTitle is { } t ? t : $"telegram_{m.Id}{ext}";
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = Sanitize(name) };
        if (dlg.ShowDialog() != true) return;
        try
        {
            m.DownloadProgress = 0;
            await using (var fs = File.Create(dlg.FileName))
                await _tg.DownloadMediaAsync(m.Item, fs, new Progress<double>(p => m.DownloadProgress = p));
            _owner.ShowToast("Файл сохранён");
        }
        catch (Exception ex)
        {
            _owner.ShowToast(TgErrors.Describe(ex), true);
        }
        finally
        {
            m.DownloadProgress = -1;
        }
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // ---- голосовые и аудио
    [RelayCommand]
    private async Task PlayAudio(MessageViewModel? m)
    {
        if (m is null) return;
        try
        {
            if (AudioPlayer.Instance.Key == m.AudioKey)
            {
                AudioPlayer.Instance.Toggle(m.AudioKey, "");
                return;
            }
            m.IsLoadingAudio = true;
            var path = await _tg.Media.GetMediaFileAsync(m.Item, new Progress<double>(p => m.DownloadProgress = p));
            m.DownloadProgress = -1;
            m.IsLoadingAudio = false;
            if (path is null) throw new IOException("Не удалось загрузить аудио.");
            AudioPlayer.Instance.Play(m.AudioKey, path);
        }
        catch (Exception ex)
        {
            m.IsLoadingAudio = false;
            _owner.ShowToast(TgErrors.Describe(ex), true);
        }
    }

    [RelayCommand]
    private void SeekAudio(object? parameter)
    {
        if (parameter is (MessageViewModel m, double fraction) && AudioPlayer.Instance.Key == m.AudioKey)
            AudioPlayer.Instance.Seek(fraction);
    }

    // ---- опросы
    [RelayCommand]
    private async Task Vote(PollOptionViewModel? option)
    {
        var m = _byId.Values.Distinct().FirstOrDefault(v => v.PollOptions.Contains(option!));
        if (option is null || m?.Poll is not { } poll || poll.IsClosed || poll.ShowResults && !poll.IsMultiple) return;
        if (poll.IsMultiple)
        {
            option.IsSelected = !option.IsSelected;
            return;
        }
        await SendVoteAsync(m, [option.Option]);
    }

    [RelayCommand]
    private Task SubmitVotes(MessageViewModel? m) =>
        m is null ? Task.CompletedTask : SendVoteAsync(m, m.PollOptions.Where(o => o.IsSelected).Select(o => o.Option).ToList());

    [RelayCommand]
    private Task RetractVote(MessageViewModel? m) => m is null ? Task.CompletedTask : SendVoteAsync(m, []);

    private async Task SendVoteAsync(MessageViewModel m, IReadOnlyList<PollOption> options)
    {
        if (m.IsVoting || (options.Count == 0 && !m.CanRetractVote)) return;
        m.IsVoting = true;
        try
        {
            if (await _tg.VotePollAsync(Peer, m.Item, options) is { } updated) m.SetPoll(updated);
        }
        catch (Exception ex)
        {
            _owner.ShowToast(TgErrors.Describe(ex), true);
        }
        finally
        {
            m.IsVoting = false;
        }
    }

    // ---- навигация
    [RelayCommand]
    private void OpenSender(MessageViewModel? m)
    {
        if (m?.Item.From is { } from) _owner.Shell.OpenProfile(from);
    }

    /// <summary>Клик по шапке — мини-профиль справа.</summary>
    [RelayCommand]
    private void ToggleSideProfile()
    {
        if (SideProfile is not null) { SideProfile = null; return; }
        Comments = null;
        // Аватар уже загружен в шапке — не качаем его заново.
        var vm = new ProfileViewModel(_tg, Peer, () => Task.CompletedTask, _owner.Shell, compact: true) { Avatar = Avatar };
        SideProfile = vm;
        _ = vm.LoadAsync();
        _ = LoadSharedAsync(SharedTab);
    }

    // ===================================================================== Боты: кнопки, клавиатура, меню, мини-приложения

    /// <summary>Клавиатура бота под полем ввода (из последнего сообщения, где она пришла).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasReplyKeyboard), nameof(ShowReplyKeyboard))]
    private IReadOnlyList<IReadOnlyList<BotButton>>? _replyKeyboard;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowReplyKeyboard))] private bool _isReplyKeyboardOpen = true;
    public bool ShowReplyKeyboard => HasReplyKeyboard && IsReplyKeyboardOpen;
    public bool HasReplyKeyboard => ReplyKeyboard is { Count: > 0 };

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasBotMenu))] private BotMenu? _botMenu;
    public bool HasBotMenu => BotMenu is not null;

    private void UpdateReplyKeyboard()
    {
        var withKeyboard = Items.OfType<MessageViewModel>().LastOrDefault(m => m.Item.ReplyKeyboard is not null && !m.IsOut);
        ReplyKeyboard = withKeyboard?.Item.ReplyKeyboard;
    }

    private async Task LoadBotMenuAsync()
    {
        if (Peer.Kind != PeerKind.Bot || BotMenu is not null) return;
        BotMenu = await _tg.GetBotMenuAsync(Peer);
    }

    [RelayCommand]
    private async Task PressKeyboardButton(BotButton? button)
    {
        if (button is null) return;
        try
        {
            var sent = await _tg.SendKeyboardButtonAsync(Peer, button);
            OnMessageReceived(sent);
            _owner.OnMessageReceived(sent, fromConversation: true);
        }
        catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
    }

    [RelayCommand]
    private void ToggleReplyKeyboard() => IsReplyKeyboardOpen = !IsReplyKeyboardOpen;

    [RelayCommand]
    private async Task OpenBotMenu()
    {
        if (BotMenu is not { } menu) return;
        await OpenWebAppAsync(Peer, menu.Url, fromMenu: menu.Url is not null, title: menu.Text);
    }

    [RelayCommand]
    private async Task PressInline(InlineButtonViewModel? b)
    {
        if (b is null) return;
        var msg = b.Message;
        var button = b.Button;
        try
        {
            switch (button.Kind)
            {
                case BotButtonKind.Callback:
                    var answer = await _tg.PressCallbackAsync(Peer, msg.Id, button);
                    if (answer.Url is { } cbUrl) Shell.Open(cbUrl);
                    if (answer.Message is { Length: > 0 } text)
                    {
                        if (answer.IsAlert) MessageBox.Show(Application.Current.MainWindow!, text, Title);
                        else _owner.ShowToast(text);
                    }
                    break;
                case BotButtonKind.Url when button.Url is { } url:
                    Shell.Open(url);
                    break;
                case BotButtonKind.WebApp or BotButtonKind.SimpleWebApp:
                    var bot = msg.Item.From is { Kind: PeerKind.Bot } from ? from : Peer;
                    await OpenWebAppAsync(bot, button.Url, fromMenu: false, title: button.Text);
                    break;
                case BotButtonKind.Copy when button.CopyText is { } copy:
                    Clipboard.SetText(copy);
                    _owner.ShowToast("Скопировано");
                    break;
                case BotButtonKind.Profile:
                    _owner.Shell.OpenProfile(await _tg.ResolveAsync(button.UserId.ToString()));
                    break;
                default:
                    _owner.ShowToast("Эта кнопка пока не поддерживается");
                    break;
            }
        }
        catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
    }

    private async Task OpenWebAppAsync(PeerRef bot, string? url, bool fromMenu, string title)
    {
        try
        {
            var appUrl = await _tg.RequestWebAppAsync(Peer, bot, url, fromMenu);
            var window = new Views.WebAppWindow(title.Length > 0 ? title : bot.DisplayTitle, appUrl,
                link => _ = _owner.Shell.OpenLinkAsync(link)) { Owner = Application.Current.MainWindow };
            window.Show();
        }
        catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
    }

    // ===================================================================== Комментарии к постам

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsCommentsOpen))] private CommentsViewModel? _comments;
    public bool IsCommentsOpen => Comments is not null;

    [RelayCommand]
    private async Task OpenComments(MessageViewModel? post)
    {
        if (post is null) return;
        SideProfile = null;
        var vm = new CommentsViewModel(_tg, Peer, post, () => Comments = null, _owner.ShowToast);
        Comments = vm;
        await vm.LoadAsync();
    }

    // ===================================================================== Закреплённые и переход к сообщению

    public ObservableCollection<MessageItem> Pinned { get; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PinnedText), nameof(PinnedTitle), nameof(HasPinned))] private int _pinnedIndex;
    public bool HasPinned => Pinned.Count > 0;
    public string PinnedTitle => Pinned.Count > 1 ? $"Закреплённое сообщение {PinnedIndex + 1} из {Pinned.Count}" : "Закреплённое сообщение";
    public string PinnedText => Pinned.Count > 0 ? Pinned[Math.Min(PinnedIndex, Pinned.Count - 1)].Preview : "";

    /// <summary>View: прокрутить к сообщению и подсветить его.</summary>
    public event Action<MessageViewModel>? ScrollToMessageRequested;
    public bool IsDetached => _detached;
    private bool _detached;

    private async Task LoadPinnedAsync()
    {
        var pinned = await _tg.GetPinnedAsync(Peer);
        Pinned.Clear();
        foreach (var p in pinned) Pinned.Add(p);
        PinnedIndex = 0;
        OnPropertyChanged(nameof(HasPinned));
        OnPropertyChanged(nameof(PinnedText));
        OnPropertyChanged(nameof(PinnedTitle));
    }

    /// <summary>Клик по плашке закрепа: перейти к сообщению, следующий клик — к более старому закрепу.</summary>
    [RelayCommand]
    private async Task OpenPinned()
    {
        if (Pinned.Count == 0) return;
        var target = Pinned[Math.Min(PinnedIndex, Pinned.Count - 1)];
        PinnedIndex = (PinnedIndex + 1) % Pinned.Count;
        await JumpToMessageAsync(target.Id);
    }

    public async Task JumpToMessageAsync(int id)
    {
        if (!_byId.ContainsKey(id))
        {
            try
            {
                var around = await _tg.GetHistoryAroundAsync(Peer, id);
                ResetItems(around);
                HasMoreHistory = true;
                _detached = true;
            }
            catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); return; }
        }
        if (_byId.TryGetValue(id, out var vm))
        {
            ScrollToMessageRequested?.Invoke(vm);
            vm.IsHighlighted = true;
            await Task.Delay(1600);
            vm.IsHighlighted = false;
        }
    }

    [RelayCommand]
    private Task JumpToReply(MessageViewModel? m) => m?.Item.ReplyToId is { } r ? JumpToMessageAsync(r) : Task.CompletedTask;

    [RelayCommand]
    private void OpenMap(MessageViewModel? m)
    {
        if (m?.MapUrl is { } url) Shell.Open(url);
    }

    // ===================================================================== Мини-профиль: переходы внутри панели

    private readonly Stack<ProfileViewModel> _sideStack = new();
    public bool SideCanGoBack => _sideStack.Count > 0;
    /// <summary>В панели открыт не собеседник этого чата (участник группы) — можно написать ему.</summary>
    public bool SideCanWrite => SideProfile?.Profile?.Peer is { IsUser: true } p && p.Key != Peer.Key && !p.IsSelf;

    partial void OnSideProfileChanged(ProfileViewModel? value)
    {
        if (value is null) _sideStack.Clear();
        OnPropertyChanged(nameof(SideCanGoBack));
        OnPropertyChanged(nameof(SideCanWrite));
        if (value is not null) value.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileViewModel.Profile)) OnPropertyChanged(nameof(SideCanWrite));
        };
    }

    /// <summary>Участник группы → его мини-профиль в этой же панели.</summary>
    [RelayCommand]
    private void OpenSideMember(PeerResultViewModel? member)
    {
        if (member is null || SideProfile is null) return;
        _sideStack.Push(SideProfile);
        var vm = new ProfileViewModel(_tg, member.Peer, () => Task.CompletedTask, _owner.Shell, compact: true) { Avatar = member.Avatar };
        SideProfile = vm;
        OnPropertyChanged(nameof(SideCanGoBack));
        _ = vm.LoadAsync();
    }

    [RelayCommand]
    private void SideBack()
    {
        if (_sideStack.Count == 0) { SideProfile = null; return; }
        var prev = _sideStack.Pop();
        SideProfile = prev;
        OnPropertyChanged(nameof(SideCanGoBack));
    }

    [RelayCommand]
    private void SideWrite()
    {
        if (SideProfile?.Profile?.Peer is not { } peer) return;
        SideProfile = null;
        _owner.Shell.OpenChat(peer);
    }

    // ===================================================================== Общие медиа (вкладки в мини-профиле)

    public ObservableCollection<MessageViewModel> SharedItems { get; } = [];
    [ObservableProperty] private SharedMediaKind _sharedTab = SharedMediaKind.Media;
    [ObservableProperty] private bool _isSharedLoading;
    public bool SharedIsGrid => SharedTab == SharedMediaKind.Media;
    private CancellationTokenSource? _sharedCts;

    partial void OnSharedTabChanged(SharedMediaKind value)
    {
        OnPropertyChanged(nameof(SharedIsGrid));
        _ = LoadSharedAsync(value);
    }

    private async Task LoadSharedAsync(SharedMediaKind kind)
    {
        _sharedCts?.Cancel();
        var cts = _sharedCts = new CancellationTokenSource();
        SharedItems.Clear();
        IsSharedLoading = true;
        try
        {
            var items = await _tg.SearchMediaAsync(Peer, kind);
            if (cts.IsCancellationRequested) return;
            foreach (var m in items)
            {
                var vm = new MessageViewModel(m, Peer);
                vm.EnsurePreview(_tg.Media);
                if (vm.IsVoice || vm.IsAudio) vm.BuildWave();
                VoicePlayback.Attach(vm);
                SharedItems.Add(vm);
            }
        }
        catch (Exception ex) { TgErrors.Describe(ex); }
        finally
        {
            if (!cts.IsCancellationRequested) IsSharedLoading = false;
        }
    }

    // ===================================================================== Автопроигрывание голосовых по порядку

    private void OnAudioEnded(string key) => Anim.Ui(() =>
    {
        var all = Items.OfType<MessageViewModel>().ToList();
        var index = all.FindIndex(v => v.AudioKey == key);
        if (index < 0) return;
        var next = all.Skip(index + 1).FirstOrDefault(v => v.IsVoice);
        if (next is not null) PlayAudioCommand.Execute(next);
    });

    [RelayCommand]
    private void OpenFullProfile()
    {
        SideProfile = null;
        _owner.Shell.OpenProfile(Peer);
    }

    [RelayCommand]
    private void CloseChat() => _owner.CloseChat();

    [RelayCommand]
    private async Task JumpToBottom()
    {
        PendingNewCount = 0;
        if (_detached)
        {
            // После перехода к старому сообщению лента «оторвана» — возвращаем последние.
            _detached = false;
            try { ResetItems(await _tg.GetHistoryAsync(Peer, 0, 50)); } catch { }
            ScrollToBottomRequested?.Invoke(false);
        }
        else ScrollToBottomRequested?.Invoke(true);
        MarkRead();
    }

    // ---- просмотр фото / видео
    [RelayCommand]
    private Task OpenMedia(MessageViewModel? m)
    {
        if (m is null) return Task.CompletedTask;
        // Листаем всё медиа чата (или вкладки «Медиа» в профиле, если открыли оттуда).
        _viewerList = SharedItems.Contains(m) ? SharedItems.Where(v => v.IsVisualMedia).ToList() : ChatVisualMedia();
        if (!_viewerList.Contains(m)) _viewerList = [m];
        return ShowInViewer(m);
    }

    private List<MessageViewModel> _viewerList = [];

    /// <summary>Все фото/видео/GIF ленты по порядку (части альбомов — по отдельности).</summary>
    private List<MessageViewModel> ChatVisualMedia() =>
        Items.OfType<MessageViewModel>()
            .SelectMany(v => v.IsAlbum ? v.Album.AsEnumerable() : [v])
            .Where(v => v.IsVisualMedia)
            .ToList();

    public string ViewerCounter => ViewerMessage is { } m && _viewerList.Count > 1 ? $"{_viewerList.IndexOf(m) + 1} из {_viewerList.Count}" : "";
    public bool ViewerHasPrev => ViewerMessage is { } m && _viewerList.IndexOf(m) > 0;
    public bool ViewerHasNext => ViewerMessage is { } m && _viewerList.IndexOf(m) is var i && i >= 0 && i < _viewerList.Count - 1;

    [RelayCommand]
    private Task ViewerPrev() => ViewerMessage is { } m && _viewerList.IndexOf(m) is > 0 and var i ? ShowInViewer(_viewerList[i - 1]) : Task.CompletedTask;

    [RelayCommand]
    private Task ViewerNext() => ViewerMessage is { } m && _viewerList.IndexOf(m) is var i && i >= 0 && i < _viewerList.Count - 1
        ? ShowInViewer(_viewerList[i + 1]) : Task.CompletedTask;

    private async Task ShowInViewer(MessageViewModel m)
    {
        if (m.Item.Media is not (MediaKind.Photo or MediaKind.Video or MediaKind.Gif))
        {
            if (m.CanDownload) await Download(m);
            return;
        }
        AudioPlayer.Instance.Stop();
        ViewerMessage = m;
        OnPropertyChanged(nameof(ViewerCounter));
        OnPropertyChanged(nameof(ViewerHasPrev));
        OnPropertyChanged(nameof(ViewerHasNext));
        ViewerImage = m.Preview;
        ViewerVideoPath = null;
        ViewerLoading = true;
        ViewerProgress = 0;
        try
        {
            if (m.Item.Media is MediaKind.Photo)
            {
                var bytes = await _tg.Media.GetFullPhotoAsync(m.Item);
                if (ViewerMessage == m) ViewerImage = (bytes is null ? null : await Task.Run(() => Images.FromBytes(bytes))) ?? m.Preview;
            }
            else
            {
                var path = await _tg.Media.GetMediaFileAsync(m.Item, new Progress<double>(p => ViewerProgress = p));
                if (ViewerMessage == m) ViewerVideoPath = path;
            }
        }
        catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
        finally { if (ViewerMessage == m) ViewerLoading = false; }
        PrefetchNeighbours(m);
    }

    // Соседние фото подгружаем заранее в фоне — переход стрелками мгновенный.
    private void PrefetchNeighbours(MessageViewModel m)
    {
        var i = _viewerList.IndexOf(m);
        if (i < 0) return;
        foreach (var n in new[] { i + 1, i - 1, i + 2 })
            if (n >= 0 && n < _viewerList.Count && _viewerList[n].Item.Media is MediaKind.Photo)
                _ = _tg.Media.GetFullPhotoAsync(_viewerList[n].Item, priority: false);
    }

    [RelayCommand]
    private void CloseViewer()
    {
        ViewerMessage = null;
        ViewerImage = null;
        ViewerVideoPath = null;
    }

    [RelayCommand]
    private Task SaveViewer() => ViewerMessage is { } m ? Download(m) : Task.CompletedTask;
}
