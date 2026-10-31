using System.Windows;
using System.Windows.Media;
using TgCheck.App.Helpers;
using TgCheck.Core.Config;
using TgCheck.Core.Services;

namespace TgCheck.App.Views;

/// <summary>Окно «Что нового» перед обновлением. true — пользователь нажал «Начать обновление».</summary>
public partial class UpdateWindow : Window
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new LightWindowAutomationPeer(this);

    public UpdateWindow(UpdateInfo update)
    {
        InitializeComponent();
        var version = update.Version.ToString(3);
        TitleText.Text = $"Что нового в {version}";
        SubtitleText.Text = $"Сейчас у вас {BuildInfo.VersionText} · после обновления приложение перезапустится само";
        Markdown.Render(Notes, string.IsNullOrWhiteSpace(update.Notes) ? "Описание этой версии не указано." : update.Notes);
        SourceInitialized += (_, _) => WindowEffects.ApplyDarkTitleBar(this, (Color)FindResource("BgColor"));
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true; // окно закрывается само
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();
}
