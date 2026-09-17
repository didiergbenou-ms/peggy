using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Peggy.Tray;

internal sealed class CopilotDeviceCodeProcess : ICopilotDeviceCodeProcess
{
    private const uint KillOnJobClose = 0x2000;
    private const uint SuspendedHiddenExtendedStartup = 0x00000004 | 0x08000000 | 0x00080000;
    private SafeFileHandle? _job;
    private Process? _process;
    private StreamReader? _output;
    private StreamReader? _error;
    public TextReader StandardOutput => _output ?? throw new ObjectDisposedException(nameof(CopilotDeviceCodeProcess));
    public TextReader StandardError => _error ?? throw new ObjectDisposedException(nameof(CopilotDeviceCodeProcess));

    internal static ProcessStartInfo CreateStartInfo(string executable, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    public CopilotDeviceCodeProcess(string executable, IReadOnlyList<string> arguments)
    {
        var info = CreateStartInfo(executable, arguments);
        try
        {
            _job = Native.CreateJobObjectW(IntPtr.Zero, null);
            Check(!_job.IsInvalid);
            // No breakaway flags, and the sole job handle is never inherited.
            var limits = new JobLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
            Check(Native.SetInformationJobObject(_job, 9, ref limits, Marshal.SizeOf<JobLimits>()));
            using var stdout = new Pipe();
            using var stderr = new Pipe();
            using var stdin = new Pipe();
            Check(Native.SetHandleInformation(stdout.Read, 1, 0));
            Check(Native.SetHandleInformation(stderr.Read, 1, 0));
            Check(Native.SetHandleInformation(stdin.Write, 1, 0));
            using var attributes = new ProcessAttributes(
                [stdin.Read.DangerousGetHandle(), stdout.Write.DangerousGetHandle(), stderr.Write.DangerousGetHandle()],
                _job.DangerousGetHandle());
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = 0x100,
                    StandardInput = stdin.Read.DangerousGetHandle(),
                    StandardOutput = stdout.Write.DangerousGetHandle(),
                    StandardError = stderr.Write.DangerousGetHandle()
                },
                Attributes = attributes.Pointer
            };
            // JOB_LIST assigns the private non-breakaway job atomically at creation.
            // Suspended startup also lets us acquire handles and set stdin EOF before
            // any CLI code runs. Only these three pipe handles are inherited.
            // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute
            Check(Native.CreateProcessW(info.FileName, BuildCommandLine(info), IntPtr.Zero, IntPtr.Zero,
                true, SuspendedHiddenExtendedStartup, IntPtr.Zero, info.WorkingDirectory, ref startup, out var created));
            using var processHandle = new SafeFileHandle(created.Process, true);
            using var threadHandle = new SafeFileHandle(created.Thread, true);
            _process = Process.GetProcessById(checked((int)created.ProcessId));
            _ = _process.Handle;
            _output = stdout.CreateReader();
            _error = stderr.CreateReader();
            stdin.Write.Dispose();
            Check(Native.ResumeThread(threadHandle) != uint.MaxValue);
        }
        catch
        {
            // Closing the job also terminates a child that failed setup while suspended.
            try { Dispose(); }
            catch (Exception) { }
            throw new InvalidOperationException("Could not start GitHub Copilot in an isolated process job. Check Windows and the CLI installation.");
        }
    }

    internal static StringBuilder BuildCommandLine(ProcessStartInfo info)
    {
        var command = new StringBuilder();
        AppendArgument(info.FileName);
        foreach (var argument in info.ArgumentList) { command.Append(' '); AppendArgument(argument); }
        return command;

        void AppendArgument(string value)
        {
            if (value.IndexOf('\0') >= 0) throw new ArgumentException("A process argument contains a null character.");
            command.Append('"');
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                command.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                command.Append(character);
                slashes = 0;
            }
            command.Append('\\', slashes * 2);
            command.Append('"');
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        var process = _process ?? throw new ObjectDisposedException(nameof(CopilotDeviceCodeProcess));
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode;
    }

    public void Kill()
    {
        var job = _job;
        if (job is null || job.IsClosed) return;
        try { Check(Native.TerminateJobObject(job, 1)); }
        finally { job.Dispose(); }
    }

    public void CloseStreams()
    {
        try { _output?.Dispose(); }
        finally { _error?.Dispose(); }
    }

    public void Dispose()
    {
        try { _job?.Dispose(); }
        finally
        {
            try { CloseStreams(); }
            finally { _process?.Dispose(); }
        }
    }

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private sealed class Pipe : IDisposable
    {
        public SafeFileHandle Read { get; }
        public SafeFileHandle Write { get; }
        private bool _readTransferred;

        public Pipe()
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };
            var success = Native.CreatePipe(out var read, out var write, ref security, 0);
            Read = read;
            Write = write;
            if (!success)
            {
                var error = Marshal.GetLastWin32Error();
                Dispose();
                throw new Win32Exception(error);
            }
        }

        public StreamReader CreateReader()
        {
            var stream = new FileStream(Read, FileAccess.Read, 4096, isAsync: false);
            _readTransferred = true;
            try { return new StreamReader(stream, new UTF8Encoding(false), true, 1024); }
            catch { stream.Dispose(); throw; }
        }

        public void Dispose()
        {
            if (!_readTransferred) Read.Dispose();
            Write.Dispose();
        }
    }

    private sealed class ProcessAttributes : IDisposable
    {
        public IntPtr Pointer { get; private set; }
        private IntPtr _handles;
        private IntPtr _job;
        private bool _initialized;

        public ProcessAttributes(IntPtr[] handles, IntPtr job)
        {
            try
            {
                nuint bytes = 0;
                Native.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref bytes);
                Pointer = Marshal.AllocHGlobal(checked((nint)bytes));
                Check(Native.InitializeProcThreadAttributeList(Pointer, 2, 0, ref bytes));
                _initialized = true;
                _handles = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
                Marshal.Copy(handles, 0, _handles, handles.Length);
                _job = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(_job, job);
                Check(Native.UpdateProcThreadAttribute(Pointer, 0, 0x20002, _handles,
                    (nuint)(handles.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
                Check(Native.UpdateProcThreadAttribute(Pointer, 0, 0x2000D, _job,
                    (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (_initialized) { Native.DeleteProcThreadAttributeList(Pointer); _initialized = false; }
            Marshal.FreeHGlobal(Pointer);
            Marshal.FreeHGlobal(_handles);
            Marshal.FreeHGlobal(_job);
            Pointer = _handles = _job = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobLimits limits, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, int flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, nuint attribute,
            IntPtr value, nuint size, IntPtr previousValue, IntPtr returnSize);
        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(IntPtr attributes);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(string application, StringBuilder commandLine,
            IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(SafeFileHandle thread);
    }
}
