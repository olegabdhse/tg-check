using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TgCheck.App.Helpers;

namespace TgCheck.App.ViewModels;

/// <summary>Экран «загрузка» или «ошибка» с кнопкой повтора.</summary>
public sealed partial class StatusViewModel : ObservableObject
{
    private readonly Func<Task>? _retry;

    private StatusViewModel(string title, string? message, bool isBusy, Func<Task>? retry, bool showAppFolder)
    {
        Title = title;
        Message = message;
        IsBusy = isBusy;
        _retry = retry;
        ShowAppFolder = showAppFolder;
    }

    public string Title { get; }
    public string? Message { get; }
    public bool IsBusy { get; }
    public bool CanRetry => _retry is not null;
    public bool ShowAppFolder { get; }

    public static StatusViewModel Busy(string title) => new(title, null, true, null, false);

    public static StatusViewModel Error(string title, string message, Func<Task> retry, bool showAppFolder = false) =>
        new(title, message, false, retry, showAppFolder);

    [RelayCommand]
    private Task Retry() => _retry?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    private static void OpenAppFolder() => Shell.Open(AppContext.BaseDirectory);
}
