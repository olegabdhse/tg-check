using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.App.Helpers;
using TgCheck.Core.Models;
using TgCheck.Core.Services;

namespace TgCheck.App.ViewModels;

/// <summary>Версия сообщения в окне истории правок.</summary>
public sealed record EditVersion(string When, string Text);

// Удалённые сообщения и история правок.
public sealed partial class ConversationViewModel
{
    [ObservableProperty] private bool _isDeletedListOpen;
    [ObservableProperty] private bool _isEditHistoryOpen;
    [ObservableProperty] private string _deletedListStatus = "";

    public ObservableCollection<ChatSearchResult> DeletedMessages { get; } = [];
    public ObservableCollection<EditVersion> EditVersions { get; } = [];

    /// <summary>Номер для обычного ответа: на удалённое сообщение отвечают цитатой.</summary>
    private int? LiveReplyId => ReplyTo is { IsDeleted: true } ? null : ReplyTo?.Id;

    private void MarkShownDeleted(MessageViewModel vm, int id)
    {
        var now = DateTime.UtcNow;
        if (!vm.IsAlbum)
        {
            vm.MarkDeleted(now);
            return;
        }
        vm.Album.FirstOrDefault(a => a.Id == id)?.MarkDeleted(now);
        if (vm.Album.All(a => a.IsDeleted)) vm.MarkDeleted(now);
    }

    /// <summary>Удалённые этого чата очистили в настройках: они исчезают и из открытой ленты.</summary>
    public void DropDeleted()
    {
        foreach (var vm in _byId.Values.Distinct().Where(v => v.IsDeleted).ToList())
            foreach (var id in vm.AllIds.ToList()) RemoveShown(id);
        foreach (var vm in _byId.Values.Distinct()) vm.HasEditHistory = false;
    }

    [RelayCommand]
    private async Task OpenDeletedList()
    {
        DeletedMessages.Clear();
        DeletedListStatus = "";
        IsDeletedListOpen = true;
        try
        {
            var found = await _tg.GetDeletedMessagesAsync(Peer);
            foreach (var m in found)
            {
                var text = (m.Text.Length > 0 ? m.Text : m.Preview).ReplaceLineEndings(" ").Trim();
                var when = m.DeletedAtUtc is { } at ? "удалено " + at.ToLocalTime().ToString("d MMM, HH:mm") : "";
                DeletedMessages.Add(new ChatSearchResult(m.Id, m.IsOut ? "Вы" : m.From?.Title ?? Peer.DisplayTitle, text.Length > 160 ? text[..160] + "…" : text, when));
            }
            DeletedListStatus = found.Count == 0 ? "В этом чате удалённых сообщений нет" : "";
        }
        catch (Exception ex) { DeletedListStatus = TgErrors.Describe(ex); }
    }

    [RelayCommand]
    private void CloseDeletedList() => IsDeletedListOpen = false;

    [RelayCommand]
    private async Task OpenDeletedMessage(ChatSearchResult? message)
    {
        if (message is null) return;
        IsDeletedListOpen = false;
        await JumpToMessageAsync(message.Id);
    }

    [RelayCommand]
    private async Task ShowEditHistory(MessageViewModel? m)
    {
        if (m is null) return;
        EditVersions.Clear();
        EditVersions.Add(new EditVersion("Сейчас", m.Text));
        IsEditHistoryOpen = true;
        try
        {
            var edits = await _tg.GetEditHistoryAsync(Peer, m.Id);
            foreach (var e in edits) EditVersions.Add(new EditVersion("До правки " + e.AtUtc.ToLocalTime().ToString("d MMM, HH:mm"), e.Text));
        }
        catch (Exception ex) { _owner.ShowToast(TgErrors.Describe(ex), true); }
    }

    [RelayCommand]
    private void CloseEditHistory() => IsEditHistoryOpen = false;

    // Текст изменился — появилась ли сохранённая прежняя версия.
    private async Task CheckEditHistoryAsync(MessageViewModel vm)
    {
        try
        {
            if ((await _tg.GetEditHistoryAsync(Peer, vm.Id)).Count > 0) vm.HasEditHistory = true;
        }
        catch (Exception ex) { Logging.Write("vault: " + ex.Message); }
    }
}
