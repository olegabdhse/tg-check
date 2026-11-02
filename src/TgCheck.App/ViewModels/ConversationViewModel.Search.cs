using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.App.Helpers;
using TgCheck.Core.Services;

namespace TgCheck.App.ViewModels;

/// <summary>Найденное сообщение в поиске по чату.</summary>
public sealed record ChatSearchResult(int Id, string Sender, string Text, string DateText);

// Поиск по сообщениям чата (лупа в шапке).
public sealed partial class ConversationViewModel
{
    private const int ChatSearchPage = 50;

    [ObservableProperty] private bool _isChatSearchOpen;
    [ObservableProperty] private string _chatSearchQuery = "";
    [ObservableProperty] private string? _chatSearchStatus;
    [ObservableProperty] private bool _showChatSearchResults;
    [ObservableProperty] private bool _isChatSearching;

    public ObservableCollection<ChatSearchResult> ChatSearchResults { get; } = [];

    private CancellationTokenSource? _chatSearchCts;
    private int _chatSearchTotal, _chatSearchIndex = -1;

    [RelayCommand]
    private void OpenChatSearch() => IsChatSearchOpen = true;

    [RelayCommand]
    private void CloseChatSearch()
    {
        _chatSearchCts?.Cancel();
        IsChatSearchOpen = false;
        ChatSearchQuery = "";
        ClearChatSearch();
    }

    private void ClearChatSearch()
    {
        ChatSearchResults.Clear();
        ChatSearchStatus = null;
        ShowChatSearchResults = false;
        IsChatSearching = false;
        _chatSearchTotal = 0;
        _chatSearchIndex = -1;
    }

    partial void OnChatSearchQueryChanged(string value) => _ = RunChatSearchAsync(value.Trim());

    // Когда перестали печатать (0,35 с); новый запрос отменяет прежний.
    private async Task RunChatSearchAsync(string query)
    {
        _chatSearchCts?.Cancel();
        ClearChatSearch();
        if (query.Length == 0) return;
        var cts = _chatSearchCts = new CancellationTokenSource();
        IsChatSearching = true;
        try
        {
            await Task.Delay(350, cts.Token);
            var (found, total) = await _tg.SearchChatMessagesAsync(Peer, query, 0, ChatSearchPage);
            if (cts.IsCancellationRequested) return;
            foreach (var m in found) ChatSearchResults.Add(ToResult(m));
            _chatSearchTotal = total;
            ChatSearchStatus = found.Count == 0 ? "Ничего не нашлось" : $"{total} {Fmt.Plural(total, "сообщение", "сообщения", "сообщений")}";
            ShowChatSearchResults = found.Count > 0;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) ChatSearchStatus = TgErrors.Describe(ex);
        }
        if (!cts.IsCancellationRequested) IsChatSearching = false;
    }

    private static ChatSearchResult ToResult(Core.Models.MessageItem m)
    {
        var text = m.Text.ReplaceLineEndings(" ").Trim();
        return new ChatSearchResult(m.Id, m.From?.Title ?? "", text.Length > 160 ? text[..160] + "…" : text, m.DateUtc.ToLocalTime().ToString("d MMM yyyy, HH:mm"));
    }

    [RelayCommand]
    private Task OpenChatSearchResult(ChatSearchResult? result)
    {
        if (result is null) return Task.CompletedTask;
        _chatSearchIndex = ChatSearchResults.IndexOf(result);
        return GoToChatSearchIndexAsync();
    }

    /// <summary>К следующему найденному (вверх по чату — результаты идут от новых к старым).</summary>
    [RelayCommand]
    private async Task ChatSearchOlder()
    {
        if (ChatSearchResults.Count == 0) return;
        if (_chatSearchIndex + 1 >= ChatSearchResults.Count && ChatSearchResults.Count < _chatSearchTotal)
        {
            try
            {
                var (more, _) = await _tg.SearchChatMessagesAsync(Peer, ChatSearchQuery.Trim(), ChatSearchResults[^1].Id, ChatSearchPage);
                foreach (var m in more) ChatSearchResults.Add(ToResult(m));
            }
            catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
        }
        if (_chatSearchIndex + 1 >= ChatSearchResults.Count) return;
        _chatSearchIndex++;
        await GoToChatSearchIndexAsync();
    }

    [RelayCommand]
    private Task ChatSearchNewer()
    {
        if (_chatSearchIndex <= 0) return Task.CompletedTask;
        _chatSearchIndex--;
        return GoToChatSearchIndexAsync();
    }

    private async Task GoToChatSearchIndexAsync()
    {
        ShowChatSearchResults = false; // список не закрывает сообщение
        ChatSearchStatus = $"{_chatSearchIndex + 1} из {_chatSearchTotal}";
        await JumpToMessageAsync(ChatSearchResults[_chatSearchIndex].Id);
    }

    [RelayCommand]
    private void ToggleChatSearchResults() => ShowChatSearchResults = !ShowChatSearchResults && ChatSearchResults.Count > 0;
}
