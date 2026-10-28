using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using TgCheck.App.Helpers;
using TgCheck.Core.Services;

namespace TgCheck.App.Views;

/// <summary>
/// Мини-приложение бота (Telegram Web App) в WebView2. Мост — как в официальном Telegram Desktop:
/// страница вызывает window.TelegramWebviewProxy.postEvent(name, data), клиент отвечает через
/// window.Telegram.WebView.receiveEvent(name, data).
/// </summary>
public partial class WebAppWindow : Window
{
    private readonly string _url;
    private readonly Action<string>? _openTgLink;

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new LightWindowAutomationPeer(this);

    public WebAppWindow(string title, string url, Action<string>? openTgLink = null)
    {
        _url = url;
        _openTgLink = openTgLink;
        InitializeComponent();
        TitleText.Text = title;
        Loaded += async (_, _) => await InitAsync();
        SourceInitialized += (_, _) => WindowEffects.ApplyDarkTitleBar(this, (Color)FindResource("BgColor"));
    }

    private async Task InitAsync()
    {
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TgCheck", "webview");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;
            // Прокси до загрузки страницы: Telegram Web App SDK ищет window.TelegramWebviewProxy.
            await core.AddScriptToExecuteOnDocumentCreatedAsync("""
                window.TelegramWebviewProxy = {
                  postEvent: function (eventType, eventData) {
                    window.chrome.webview.postMessage(JSON.stringify({ eventType: eventType, eventData: eventData }));
                  }
                };
                """);
            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += (_, _) => Loading.Visibility = Visibility.Collapsed;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                Helpers.Shell.Open(e.Uri);
            };
            core.Settings.AreDevToolsEnabled = false;
            core.Navigate(_url);
        }
        catch (Exception ex)
        {
            Loading.Visibility = Visibility.Collapsed;
            MessageBox.Show(this, "Не удалось открыть мини-приложение: " + TgErrors.Describe(ex) +
                "\n\nНужен Microsoft Edge WebView2 Runtime (в Windows 11 он уже есть).", "Мини-приложение");
            Close();
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var type = doc.RootElement.GetProperty("eventType").GetString();
            var data = doc.RootElement.TryGetProperty("eventData", out var d) && d.ValueKind == JsonValueKind.String
                ? JsonDocument.Parse(d.GetString() ?? "{}").RootElement
                : default;
            switch (type)
            {
                case "web_app_close":
                    Close();
                    break;
                case "web_app_request_theme":
                    Send("theme_changed", $$"""{"theme_params":{{TelegramService.ThemeJson}}}""");
                    break;
                case "web_app_request_viewport":
                case "web_app_ready":
                case "web_app_expand":
                    SendViewport();
                    if (type == "web_app_ready") Send("theme_changed", $$"""{"theme_params":{{TelegramService.ThemeJson}}}""");
                    break;
                case "web_app_open_link" when data.ValueKind == JsonValueKind.Object && data.TryGetProperty("url", out var url):
                    Helpers.Shell.Open(url.GetString()!);
                    break;
                case "web_app_open_tg_link" when data.ValueKind == JsonValueKind.Object && data.TryGetProperty("path_full", out var path):
                    var link = "https://t.me" + path.GetString();
                    if (_openTgLink is not null) _openTgLink(link);
                    else Helpers.Shell.Open(link);
                    break;
                case "web_app_set_header_color" when data.ValueKind == JsonValueKind.Object && data.TryGetProperty("color", out var color):
                    try { Header.Background = new BrushConverter().ConvertFromString(color.GetString()!) as Brush; } catch { }
                    break;
                case "web_app_setup_back_button" when data.ValueKind == JsonValueKind.Object && data.TryGetProperty("is_visible", out var vis):
                    BackButton.Visibility = vis.GetBoolean() ? Visibility.Visible : Visibility.Collapsed;
                    break;
            }
        }
        catch (Exception ex)
        {
            Logging("webapp event: " + ex.Message);
        }
    }

    private void SendViewport() =>
        Send("viewport_changed", $$"""{"height":{{(int)Web.ActualHeight}},"is_expanded":true,"is_state_stable":true}""");

    private void Send(string eventType, string json) =>
        _ = Web.CoreWebView2?.ExecuteScriptAsync(
            $"window.Telegram && window.Telegram.WebView && window.Telegram.WebView.receiveEvent({JsonSerializer.Serialize(eventType)}, {json});");

    private void Back_Click(object sender, RoutedEventArgs e) => Send("back_button_pressed", "null");

    private void Reload_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.Reload();

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (Web.CoreWebView2 is not null) SendViewport();
    }

    private static void Logging(string message) => System.Diagnostics.Debug.WriteLine(message);
}
