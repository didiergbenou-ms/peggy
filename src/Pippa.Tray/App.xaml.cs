using System;
using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Pippa.Tray;

public partial class App : Application
{
    private Forms.NotifyIcon _tray = null!;
    private MascotWindow _mascot = null!;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        _mascot = new MascotWindow();
        _mascot.SetState(PippaState.Idle);
        _mascot.ShowAnimated();

        _tray = new Forms.NotifyIcon
        {
            Icon = CreateIcon(),
            Visible = true,
            Text = "Pippa"
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show / hide Pippa", null, (_, _) => ToggleMascot());
        menu.Items.Add("Simulate: new digest", null, (_, _) => Digest());
        menu.Items.Add("Simulate: listening", null, (_, _) => _mascot.SetState(PippaState.Listening));
        menu.Items.Add("Simulate: thinking", null, (_, _) => _mascot.SetState(PippaState.Thinking));
        menu.Items.Add("Simulate: idle", null, (_, _) => _mascot.SetState(PippaState.Idle));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Pippa", null, (_, _) => QuitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleMascot();

        _tray.ShowBalloonTip(4000, "Pippa",
            "Hi! I'm Pippa. I'll perch here and keep an eye on your inbox.", Forms.ToolTipIcon.Info);
    }

    private void Digest()
    {
        if (!_mascot.IsVisible) _mascot.ShowAnimated();
        _mascot.SetState(PippaState.NewDigest);
        _tray.ShowBalloonTip(6000, "Pippa - morning digest",
            "3 things worth a look: Aisha needs Q3 numbers, Marco wants mascot feedback, and your password expires soon.",
            Forms.ToolTipIcon.Info);
    }

    private void ToggleMascot()
    {
        if (_mascot.IsVisible) _mascot.HideAnimated();
        else _mascot.ShowAnimated();
    }

    private void QuitApp()
    {
        _tray.Visible = false;
        _tray.Dispose();
        Shutdown();
    }

    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var body = new SolidBrush(Color.FromArgb(0x8a, 0x5a, 0x3b));
            using var belly = new SolidBrush(Color.FromArgb(0xe2, 0x69, 0x4a));
            using var white = new SolidBrush(Color.White);
            using var beak = new SolidBrush(Color.FromArgb(0xf6, 0xa9, 0x3a));
            using var eye = new SolidBrush(Color.FromArgb(0x2b, 0x2b, 0x2b));
            g.FillEllipse(body, 5, 3, 22, 26);
            g.FillEllipse(belly, 9, 14, 13, 13);
            g.FillEllipse(white, 12, 8, 7, 7);
            g.FillEllipse(eye, 14, 10, 3, 3);
            g.FillPolygon(beak, new[]
            {
                new System.Drawing.Point(21, 12),
                new System.Drawing.Point(28, 15),
                new System.Drawing.Point(21, 18)
            });
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
