using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace Peggy.Tray;

public partial class AboutWindow : Window
{
    private readonly Action<string> _copyText;

    public AboutWindow() : this(Clipboard.SetText)
    {
    }

    internal AboutWindow(Action<string> copyText)
    {
        ArgumentNullException.ThrowIfNull(copyText);
        InitializeComponent();
        _copyText = copyText;
        VersionText.Text = PeggyReleaseVersion.Display;
        Loaded += (_, _) => PeggyWindowLayout.ConstrainToWorkArea(this);
    }

    private void CopyVersion_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _copyText(VersionText.Text);
            CopyStatusText.Text = "Version copied.";
        }
        catch (ExternalException)
        {
            CopyStatusText.Text =
                "Could not copy the version. Select it and press Ctrl+C.";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
