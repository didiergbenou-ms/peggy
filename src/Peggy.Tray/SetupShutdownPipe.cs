using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Peggy.Tray;

internal sealed class SetupShutdownPipe : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<Task> _shutdown;
    private readonly Action<Exception> _failure;
    private readonly string _name;
    private readonly int _session;
    internal Task Completion { get; }

    internal SetupShutdownPipe(string name, int session, Func<Task> shutdown, Action<Exception> failure)
    {
        _name = name;
        _session = session;
        _shutdown = shutdown;
        _failure = failure;
        Completion = ServeAsync();
    }

    internal static string Name(int pid, long startFileTime) => $"Peggy.SetupShutdown.v1.{pid}.{startFileTime}";

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var accepted = false;
            try
            {
                using var pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                request.CancelAfter(TimeSpan.FromSeconds(5));
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
                    throw new SetupShutdownRequestRejectedException("client-process-unavailable");
                using var client = Process.GetProcessById(checked((int)pid));
                if (client.SessionId != _session)
                    throw new SetupShutdownRequestRejectedException("client-session-mismatch");
                var message = new byte[1];
                await pipe.ReadExactlyAsync(message, request.Token).ConfigureAwait(false);
                if (message[0] != 1)
                    throw new SetupShutdownRequestRejectedException("unsupported-protocol");
                await pipe.WriteAsync(new byte[] { 1 }, request.Token).ConfigureAwait(false);
                await pipe.FlushAsync(request.Token).ConfigureAwait(false);
                accepted = true;
                // An accepted request is not cancellation of Peggy's final save.
                // Setup may stop waiting, but Peggy must finish draining and saving.
                await _shutdown().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (OperationCanceledException ex)
            {
                _failure(accepted ? ex : new SetupShutdownRequestRejectedException("request-timeout"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                _failure(ex);
                try { await Task.Delay(100, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }

        }
    }

    internal static async Task<bool> RequestAsync(string name, int expectedPid,
        Action validateProcess, CancellationToken ct, int connectTimeoutMilliseconds = 1500)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(connectTimeoutMilliseconds, ct).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actualPid) || actualPid != expectedPid)
            throw new InvalidOperationException("Peggy shutdown endpoint could not be verified. No shutdown request was sent.");
        validateProcess();
        await pipe.WriteAsync(new byte[] { 1 }, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
        var response = new byte[1];
        await pipe.ReadExactlyAsync(response, ct).ConfigureAwait(false);
        if (response[0] != 1) throw new InvalidOperationException("Peggy did not accept the safe shutdown request.");
        return true;
    }

    public void Dispose() => _stop.Cancel();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}

internal sealed class SetupShutdownRequestRejectedException(string reason) : InvalidOperationException(reason)
{
    internal string Reason { get; } = reason;
}
