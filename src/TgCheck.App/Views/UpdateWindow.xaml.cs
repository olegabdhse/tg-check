using System.Windows;
using System.Windows.Media;
using TgCheck.App.Helpers;
using TgCheck.Core.Services;

namespace TgCheck.App.Views;

/// <summary>Окно «Что нового»: программа обновилась в фоне, пока окна не было на экране.</summary>
public partial class UpdateWindow : Window
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new LightWindowAutomationPeer(this);

    public UpdateWindow(InstalledUpdate update)
    {
        InitializeComponent();
        TitleText.Text = "Приложение обновилось";
        SubtitleText.Text = $"Теперь у вас версия {update.Version}";
        Markdown.Render(Notes, string.IsNullOrWhiteSpace(update.Notes) ? "Описание этой версии не указано." : update.Notes);
        SourceInitialized += (_, _) => WindowEffects.ApplyDarkTitleBar(this, (Color)FindResource("BgColor"));
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
