using System.Windows;
using System.Windows.Controls;
using TgCheck.App.ViewModels;

namespace TgCheck.App.Views;

/// <summary>Разделитель дат / служебное / обычное сообщение — у каждого свой лёгкий шаблон.</summary>
public sealed class MessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Date { get; set; }
    public DataTemplate? Service { get; set; }
    public DataTemplate? Bubble { get; set; }
    public DataTemplate? Upload { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        DateSeparatorViewModel => Date,
        MessageViewModel { IsService: true } => Service,
        MessageViewModel => Bubble,
        UploadViewModel => Upload,
        _ => base.SelectTemplate(item, container),
    };
}
