using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Strela.App.Views;

/// <summary>
/// Текст-украшение поверх поля ввода (значок, подсказка в пустом поле): экранный диктор его не видит — поле и так
/// озвучивается по AutomationProperties.Name, а «лупа» и «Поиск» отдельными элементами только мешают.
/// </summary>
public sealed class DecorText : TextBlock
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
