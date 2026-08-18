using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Pippa.Core;
using Forms = System.Windows.Forms;

namespace Pippa.Tray;

public partial class App : Application
{
    private Forms.NotifyIcon _tray = null!;
    private MascotWindow _mascot = null!;
    private SpeechBubbleWindow _bubble = null!;

    private readonly IMessageProvider _provider = new MockMessageProvider();
    private readonly CopilotRunner _copilot = new();
    private readonly HashSet<string> _seen = new();
    private DispatcherTimer _poll = null!;
    private DispatcherTimer? _idleTimer;
    private bool _checking;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        _bubble = new SpeechBubbleWindow();

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
        menu.Items.Add("Check inbox now", null, (_, _) => RunCheck(force: true));
        menu.Items.Add("Show / hide Pippa", null, (_, _) => ToggleMascot());

        var test = new Forms.ToolStripMenuItem("Test animations");
        test.DropDownItems.Add("Play all", null, (_, _) => PlayAllAnimations());
        test.DropDownItems.Add("Idle", null, (_, _) => ShowState(PippaState.Idle, "Idle - just perching"));
        test.DropDownItems.Add("Listening", null, (_, _) => ShowState(PippaState.Listening, "Listening..."));
        test.DropDownItems.Add("Thinking", null, (_, _) => ShowState(PippaState.Thinking, null));
        test.DropDownItems.Add("New digest", null, (_, _) => ShowState(PippaState.NewDigest, "New digest! Here's what needs you."));
        test.DropDownItems.Add("Fly out then in", null, (_, _) => FlyDemo());
        menu.Items.Add(test);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Pippa", null, (_, _) => QuitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleMascot();

        // Greet, then run the first inbox check shortly after she lands.
        var hello = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.3) };
        hello.Tick += (_, _) => { hello.Stop(); Say("Hi! I'm Pippa. Let me take a look at your inbox..."); };
        hello.Start();

        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        first.Tick += (_, _) => { first.Stop(); RunCheck(force: false); };
        first.Start();

        // Background poll. (5 minutes in real use; shorter here for demoing the loop.)
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        _poll.Tick += (_, _) => RunCheck(force: false);
        _poll.Start();
    }

    /// <summary>
    /// Pre-filters cheaply against the provider and only spends a Copilot call when
    /// there is genuinely new unread mail (or when the user forces a check).
    /// </summary>
    private async void RunCheck(bool force)
    {
        if (_checking) return;

        IReadOnlyList<Message> unread;
        try { unread = await _provider.ListUnreadAsync(null, 50); }
        catch { return; }

        var fresh = unread.Where(m => !_seen.Contains(m.Id)).ToList();
        if (!force && fresh.Count == 0) return;
        foreach (var m in unread) _seen.Add(m.Id);
        Log($"CHECK force={force} fresh={fresh.Count}");

        _checking = true;
        _idleTimer?.Stop();
        if (!_mascot.IsVisible) _mascot.ShowAnimated();
        _mascot.SetState(PippaState.Thinking);
        _bubble.HideNow();

        try
        {
            var text = await _copilot.RunDigestAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                Log("DIGEST " + text.Replace("\r", " ").Replace("\n", " "));
                _mascot.SetState(PippaState.NewDigest);
                _bubble.Say(text, _mascot.HeadAnchor(), seconds: 12);
                ReturnToIdleAfter(TimeSpan.FromSeconds(13));
            }
            else
            {
                Log("EMPTY");
                _mascot.SetState(PippaState.Idle);
            }
        }
        catch (Exception ex)
        {
            Log("ERROR " + ex.Message);
            _mascot.SetState(PippaState.Idle);
            _bubble.Say("I couldn't check your inbox just now. I'll try again soon.", _mascot.HeadAnchor(), seconds: 6);
            ReturnToIdleAfter(TimeSpan.FromSeconds(7));
        }
        finally
        {
            _checking = false;
        }
    }

    private void ReturnToIdleAfter(TimeSpan delay)
    {
        _idleTimer?.Stop();
        _idleTimer = new DispatcherTimer { Interval = delay };
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer!.Stop();
            if (!_checking) _mascot.SetState(PippaState.Idle);
        };
        _idleTimer.Start();
    }

    private void Say(string text)
    {
        if (!_mascot.IsVisible) _mascot.ShowAnimated();
        _bubble.Say(text, _mascot.HeadAnchor());
    }

    private void ShowState(PippaState state, string? bubble)
    {
        if (!_mascot.IsVisible) _mascot.ShowAnimated();
        _mascot.SetState(state);
        if (bubble is not null) _bubble.Say(bubble, _mascot.HeadAnchor(), 5);
        else _bubble.HideNow();
    }

    private async void FlyDemo()
    {
        _bubble.HideNow();
        if (!_mascot.IsVisible) { _mascot.ShowAnimated(); return; }
        _mascot.HideAnimated();
        await Task.Delay(1300);
        _mascot.ShowAnimated();
    }

    /// <summary>Play every state and the fly-out/fly-in in sequence, for review.</summary>
    private async void PlayAllAnimations()
    {
        if (!_mascot.IsVisible) _mascot.ShowAnimated();
        await Task.Delay(400);

        _mascot.SetState(PippaState.Idle);
        _bubble.Say("Idle - bob, blink, hop and flap", _mascot.HeadAnchor(), 3);
        await Task.Delay(3200);

        _mascot.SetState(PippaState.Listening);
        _bubble.Say("Listening - head tilt and ripples", _mascot.HeadAnchor(), 3);
        await Task.Delay(3200);

        _bubble.HideNow();
        _mascot.SetState(PippaState.Thinking);
        await Task.Delay(3500);

        _mascot.SetState(PippaState.NewDigest);
        _bubble.Say("New digest - envelope and sparkles!", _mascot.HeadAnchor(), 4);
        await Task.Delay(4200);

        _mascot.SetState(PippaState.Idle);
        _bubble.Say("Now watch me fly!", _mascot.HeadAnchor(), 2);
        await Task.Delay(1900);

        _bubble.HideNow();
        _mascot.HideAnimated();     // lift up, then peel away
        await Task.Delay(1300);
        _mascot.ShowAnimated();     // swoop in, land on the taskbar
        await Task.Delay(1400);

        _mascot.SetState(PippaState.Idle);
        _bubble.Say("That's all my moves!", _mascot.HeadAnchor(), 4);
    }

    private void ToggleMascot()
    {
        if (_mascot.IsVisible)
        {
            _bubble.HideNow();
            _mascot.HideAnimated();
        }
        else
        {
            _mascot.ShowAnimated();
        }
    }

    private void QuitApp()
    {
        _poll?.Stop();
        _idleTimer?.Stop();
        _bubble.HideNow();
        _tray.Visible = false;
        _tray.Dispose();
        Shutdown();
    }

    private static void Log(string msg)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "pippa.log"), $"{DateTime.Now:HH:mm:ss} {msg}{Environment.NewLine}"); }
        catch { /* logging is best-effort */ }
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
