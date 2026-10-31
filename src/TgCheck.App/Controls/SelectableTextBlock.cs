using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using TgCheck.Core.Services;

namespace TgCheck.App.Controls;

/// <summary>
/// Текст сообщения, который можно выделить мышью и скопировать (Ctrl+C), как в Telegram Desktop. Обычный TextBlock
/// с подключённым к нему встроенным «редактором» WPF в режиме только чтения — тот же, что у TextBox: выделение,
/// двойной клик по слову, Ctrl+A, Ctrl+C, а всё оформление (ссылки, эмодзи, жирный, код) остаётся как есть.
/// Своё меню по правому клику он не показывает — открывается обычное меню сообщения.
/// </summary>
public sealed class SelectableTextBlock : TextBlock
{
    private static readonly bool Available;
    private static readonly Type? EditorType;
    private static readonly PropertyInfo? IsReadOnly, TextView, ContainerTextView, Container;

    static SelectableTextBlock()
    {
        FocusableProperty.OverrideMetadata(typeof(SelectableTextBlock), new FrameworkPropertyMetadata(true));
        FocusVisualStyleProperty.OverrideMetadata(typeof(SelectableTextBlock), new FrameworkPropertyMetadata(null));
        try
        {
            var asm = typeof(TextBlock).Assembly;
            EditorType = asm.GetType("System.Windows.Documents.TextEditor", true)!;
            var containerType = asm.GetType("System.Windows.Documents.ITextContainer", true)!;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic;
            IsReadOnly = EditorType.GetProperty("IsReadOnly", inst);
            TextView = EditorType.GetProperty("TextView", inst);
            ContainerTextView = containerType.GetProperty("TextView");
            Container = typeof(TextBlock).GetProperty("TextContainer", inst);
            var register = EditorType.GetMethod("RegisterCommandHandlers", BindingFlags.Static | BindingFlags.NonPublic, null,
                [typeof(Type), typeof(bool), typeof(bool), typeof(bool)], null);
            register!.Invoke(null, [typeof(SelectableTextBlock), true, true, true]);
            Available = IsReadOnly is not null && TextView is not null && ContainerTextView is not null && Container is not null;
        }
        catch (Exception ex) { Logging.Write("selectable text: " + ex.Message); }
    }

    // Держим ссылку: редактор живёт, пока жив блок текста.
    private readonly object? _editor;

    public SelectableTextBlock()
    {
        // Явный null: встроенное меню «Копировать» не появляется, правый клик открывает меню сообщения.
        ContextMenu = null;
        if (!Available) return;
        try
        {
            var container = Container!.GetValue(this);
            _editor = Activator.CreateInstance(EditorType!, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.CreateInstance,
                null, [container, this, false], null);
            IsReadOnly!.SetValue(_editor, true);
            TextView!.SetValue(_editor, ContainerTextView!.GetValue(container));
        }
        catch (Exception ex) { Logging.Write("selectable text: " + ex.Message); }
    }
}
