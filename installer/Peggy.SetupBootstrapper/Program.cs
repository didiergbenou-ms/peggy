using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Peggy.Tray;
using WixToolset.BootstrapperApplicationApi;

namespace Peggy.SetupBootstrapper;

internal static class Program
{
    private static void Main() => ManagedBootstrapperApplication.Run(new PeggyBootstrapperApplication());
}

internal sealed class PeggyBootstrapperApplication : BootstrapperApplication
{
    private BurnInstaller? _installer;

    protected override void OnCreate(CreateEventArgs args)
    {
        base.OnCreate(args);
        _installer = new BurnInstaller(this, args.Engine, args.Command);
    }

    protected override void Run()
    {
        var installer = _installer ?? throw new InvalidOperationException("Burn did not initialize setup.");
        // WiX's OnStartup calls Run on its dedicated STA UI thread.
        RunDesktop(installer);
        installer.Quit();
    }

    private static void RunDesktop(BurnInstaller installer)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        UnifiedSetupHost? host = null;
        try
        {
            if (installer.RequiresWindow)
            {
                host = new UnifiedSetupHost(installer);
                var window = new WorkIqSignInWindow(host);
                window.Loaded += (_, _) => installer.WindowReady();
                application.Run(window);
            }
            else
            {
                // Burn requires an owner HWND even when no window is displayed.
                using var owner = CreateHiddenOwner();
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                application.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        if (installer.IsLayout)
                        {
                            // Layout only caches the bundle/MSI. Do not create the app
                            // setup host, inspect account state, or take Peggy's mutex.
                            await installer.ApplyAsync(owner.Handle, CancellationToken.None);
                        }
                        else
                        {
                            host = new UnifiedSetupHost(installer);
                            await host.RunWithoutWindowAsync(owner.Handle);
                        }

                    }
                    catch (Exception ex) when (IsSetupFailure(ex)) { installer.LogFailure(ex); }
                    finally { application.Shutdown(); }
                }));
                application.Run();
            }
        }
        catch (Exception ex) when (IsSetupFailure(ex)) { installer.LogFailure(ex); }
        finally
        {
            host?.Close(launch: false);
            application.Shutdown();
        }
    }

    internal static HwndSource CreateHiddenOwner() => new(new HwndSourceParameters("Peggy setup owner")
    {
        WindowStyle = 0,
        Width = 0,
        Height = 0
    });

    private static bool IsSetupFailure(Exception exception) =>
        SetupReadinessService.IsExpectedFailure(exception) ||
        exception is OperationCanceledException or System.Runtime.InteropServices.COMException;
}
