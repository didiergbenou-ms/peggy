using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Peggy.Core;

namespace Peggy.Tray;

internal sealed class SpeechModelSetupException : InvalidOperationException
{
    public SpeechModelSetupException(string message)
        : base(message)
    {
    }

    public SpeechModelSetupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class SpeechModelSetupService
{
    public const string DownloadSizeDescription = "about 803 MiB";

    private const string InstallerName = "Install-SpeechModels.ps1";
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromHours(1);
    private static readonly string[] ArchiveNames =
    [
        "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8.tar.bz2",
        "sherpa-onnx-nemo-streaming-fast-conformer-ctc-en-80ms-int8.tar.bz2",
        "kitten-nano-en-v0_8-int8.tar.bz2"
    ];
    private static readonly string[] ModelDirectoryNames =
    [
        "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8",
        "sherpa-onnx-nemo-streaming-fast-conformer-ctc-en-80ms-int8",
        "kitten-nano-en-v0_8-int8"
    ];
    private readonly string? _installerPath;

    public SpeechModelSetupService(string? installerPath = null)
    {
        _installerPath = installerPath;
    }

    public bool AreModelsInstalled(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(
                    directory.Trim().Trim('"')));
            var asr = Path.Combine(
                root,
                "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8");
            var streaming = Path.Combine(
                root,
                "sherpa-onnx-nemo-streaming-fast-conformer-ctc-en-" +
                "80ms-int8");
            var tts = Path.Combine(root, "kitten-nano-en-v0_8-int8");
            return File.Exists(Path.Combine(root, "speech-models.json")) &&
                   File.Exists(Path.Combine(root, "silero_vad_v5.onnx")) &&
                   File.Exists(Path.Combine(asr, "encoder.int8.onnx")) &&
                   File.Exists(Path.Combine(asr, "decoder.int8.onnx")) &&
                   File.Exists(Path.Combine(asr, "joiner.int8.onnx")) &&
                   File.Exists(Path.Combine(asr, "tokens.txt")) &&
                   File.Exists(Path.Combine(streaming, "model.int8.onnx")) &&
                   File.Exists(Path.Combine(streaming, "tokens.txt")) &&
                   (File.Exists(Path.Combine(tts, "model.int8.onnx")) ||
                    File.Exists(Path.Combine(tts, "model.onnx"))) &&
                   File.Exists(Path.Combine(tts, "voices.bin")) &&
                   File.Exists(Path.Combine(tts, "tokens.txt")) &&
                   Directory.Exists(Path.Combine(tts, "espeak-ng-data"));
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return false;
        }
    }

    public async Task InstallAsync(
        string directory,
        bool force = false,
        CancellationToken ct = default)
    {
        try
        {
            await InstallCoreAsync(directory, force, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SpeechModelSetupException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            IOException or
            NotSupportedException or
            System.Security.SecurityException or
            UnauthorizedAccessException)
        {
            throw new SpeechModelSetupException(
                "Peggy could not install voice models at the selected path.",
                ex);
        }
    }

    private async Task InstallCoreAsync(
        string directory,
        bool force,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new SpeechModelSetupException(
                "Choose a speech model directory before installing voice.");
        }

        var destination = Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(
                directory.Trim().Trim('"')));
        var root = Path.GetPathRoot(destination);
        if (string.Equals(
                destination.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                root?.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new SpeechModelSetupException(
                "Speech models cannot be installed at a filesystem root.");
        }

        var installer = ResolveInstallerPath();
        Directory.CreateDirectory(destination);
        CleanupTransientArtifacts(destination);
        var powershell = Path.Combine(
            Environment.SystemDirectory,
            @"WindowsPowerShell\v1.0\powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        var arguments = new List<string>
        {
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            installer,
            "-Destination",
            destination
        };
        if (force)
        {
            arguments.Add("-Force");
        }
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            throw new SpeechModelSetupException(
                "Peggy could not start the voice model installer.",
                ex);
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(InstallTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            CleanupTransientArtifacts(destination);
            throw new SpeechModelSetupException(
                "Voice model installation did not finish within one hour.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            CleanupTransientArtifacts(destination);
            throw;
        }

        var output = await stdout;
        var error = await stderr;
        CleanupTransientArtifacts(destination);
        if (process.ExitCode != 0)
        {
            var detail = MonitoringFailureInspector.Sanitize(
                string.IsNullOrWhiteSpace(error) ? output : error);
            throw new SpeechModelSetupException(
                detail.Length == 0
                    ? $"Voice model installation failed with exit code " +
                      $"{process.ExitCode}."
                    : $"Voice model installation failed: {detail}");
        }

        if (!AreModelsInstalled(destination))
        {
            throw new SpeechModelSetupException(
                "The voice model installer completed, but one or more verified " +
                "model files are missing.");
        }
    }

    private static void CleanupTransientArtifacts(string destination)
    {
        foreach (var archiveName in ArchiveNames)
        {
            DeleteFileWithRetry(Path.Combine(destination, archiveName));
            DeleteFileWithRetry(
                Path.Combine(destination, archiveName + ".partial"));
        }
        DeleteFileWithRetry(
            Path.Combine(destination, "silero_vad_v5.onnx.partial"));

        foreach (var modelName in ModelDirectoryNames)
        {
            foreach (var staging in Directory.EnumerateDirectories(
                         destination,
                         $".staging-{modelName}-*",
                         SearchOption.TopDirectoryOnly))
            {
                DeleteDirectoryWithRetry(staging);
            }
        }
    }

    private static void DeleteFileWithRetry(string path)
    {
        for (var attempt = 0; attempt < 5 && File.Exists(path); attempt++)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        for (var attempt = 0;
             attempt < 5 && Directory.Exists(path);
             attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(200);
            }
        }
    }

    private string ResolveInstallerPath()
    {
        if (!string.IsNullOrWhiteSpace(_installerPath))
        {
            var configured = Path.GetFullPath(_installerPath);
            return File.Exists(configured)
                ? configured
                : throw new SpeechModelSetupException(
                    $"Voice model installer '{configured}' does not exist.");
        }

        var packaged = Path.Combine(
            AppContext.BaseDirectory,
            "scripts",
            InstallerName);
        if (File.Exists(packaged))
        {
            return packaged;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0;
             depth < 8 && directory is not null;
             depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "scripts",
                InstallerName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new SpeechModelSetupException(
            "Peggy's voice model installer is missing. Reinstall Peggy.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
