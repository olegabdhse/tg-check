using System.Windows;
using System.Windows.Automation.Peers;

namespace TgCheck.App.Helpers;

/// <summary>
/// UI Automation-пир окна без дочерних элементов.
/// Без этого WPF при каждом изменении раскладки синхронно пересылает всё дерево (тысячи элементов чата)
/// внешним слушателям службы специальных возможностей — окно «не отвечает», анимации рвутся.
/// </summary>
public sealed class LightWindowAutomationPeer(Window owner) : WindowAutomationPeer(owner)
{
    protected override List<AutomationPeer>? GetChildrenCore() => null;
}
