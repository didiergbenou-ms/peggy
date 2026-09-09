using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Peggy.Tray;

public sealed class DecorativeTextBlock : TextBlock
{
    protected override AutomationPeer? OnCreateAutomationPeer()
        => null;
}
