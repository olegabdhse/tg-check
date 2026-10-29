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
    private readonly Task<string> _url;
    private readonly Action<string>? _openTgLink;

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new LightWindowAutomationPeer(this);

    // Окружение WebView2 одно на всё приложение и создаётся заранее: раньше каждое открытие мини-приложения
    // заново поднимало его (заметная часть ожидания).
    private static Task<CoreWebView2Environment>? _environment;

    private static Task<CoreWebView2Environment> SharedEnvironment() => _environment ??= CoreWebView2Environment.CreateAsync(null,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TgCheck", "webview"));

    /// <summary>Подготовить WebView2 в простое — первое мини-приложение откроется быстрее.</summary>
    public static void Prewarm() => _ = SharedEnvironment();

    /// <param name="url">Адрес приложения: окно открывается сразу, а адрес (запрос к Telegram) приходит параллельно с подготовкой WebView2.</param>
    public WebAppWindow(string title, Task<string> url, Action<string>? openTgLink = null)
    {
        _url = url;
        _openTgLink = openTgLink;
        InitializeComponent();
        Helpers.RichText.SetText(TitleText, title);
        // Размер телефона: мини-приложения рассчитаны на мобильный экран — на ПК не растягиваем на весь монитор.
        var area = SystemParameters.WorkArea;
        Height = Math.Min(800, area.Height - 60);
        Width = Math.Min(410, Height * 0.56);
        Loaded += async (_, _) => await InitAsync();
        SourceInitialized += (_, _) => WindowEffects.ApplyDarkTitleBar(this, (Color)FindResource("BgColor"));
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await SharedEnvironment();
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
            // Убираем загрузку, как только страница построена (DOM), а не после всех картинок и скриптов.
            core.DOMContentLoaded += (_, _) => Loading.Visibility = Visibility.Collapsed;
            core.NavigationCompleted += (_, _) => Loading.Visibility = Visibility.Collapsed;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                Helpers.Shell.Open(e.Uri);
            };
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;
            core.Navigate(await _url);
        }
        catch (Exception ex)
        {
            // Telegram не выдал адрес — ошибку уже показал чат (всплывашкой), окно просто закрываем.
            if (_url.IsFaulted) { Close(); return; }
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
                    if (type == "web_app_ready")
                    {
                        Send("theme_changed", $$"""{"theme_params":{{TelegramService.ThemeJson}}}""");
                        SendSafeAreas();
                    }
                    break;
                // Новые SDK ждут отступы «безопасной зоны»: у окна на ПК их нет — нули, иначе страница рисует поля по краям.
                case "web_app_request_safe_area":
                case "web_app_request_content_safe_area":
                    SendSafeAreas();
                    break;
                case "web_app_set_background_color" when data.ValueKind == JsonValueKind.Object && data.TryGetProperty("color", out var bg):
                    try
                    {
                        var c = (Color)ColorConverter.ConvertFromString(bg.GetString()!);
                        Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
                        Background = new SolidColorBrush(c);
                    }
                    catch { }
                    break;
                // Окно на ПК остаётся размером с телефон (как приложение и рассчитано) — полноэкранный режим не включаем.
                case "web_app_request_fullscreen":
                    Send("fullscreen_failed", """{"error":"UNSUPPORTED"}""");
                    break;
                case "web_app_exit_fullscreen":
                    Send("fullscreen_changed", """{"is_fullscreen":false}""");
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

    private void SendSafeAreas()
    {
        const string zero = """{"top":0,"bottom":0,"left":0,"right":0}""";
        Send("safe_area_changed", zero);
        Send("content_safe_area_changed", zero);
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
