using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pippa.Tray;

/// <summary>
/// Runs the GitHub Copilot CLI headlessly, wired to Pippa's MCP server, and
/// returns a short digest string produced from the user's messages.
/// </summary>
public sealed class CopilotRunner
{
    private const string Prompt =
        "You are Pippa, a cheerful assistant bird. Use the pippa tools: call list_unread to get the user's " +
        "unread Outlook email and Microsoft Teams messages. Then reply with a SHORT (max 2 sentences) friendly " +
        "first-person morning digest of what deserves attention right now. Do not draft, send, or mark anything. " +
        "Output ONLY the digest text, no preamble.";

    private readonly string _copilotPath;
    private readonly string _mcpConfigArg;

    public CopilotRunner()
    {
        _copilotPath = ResolveCopilot();
        var mcpServer = ResolveMcpServer();
        var cfg = new
        {
            mcpServers = new
            {
                pippa = new
                {
                    type = "local",
                    command = mcpServer,
                    tools = new[] { "*" }
                }
            }
        };
        var json = JsonSerializer.Serialize(cfg);
        // Pass the MCP config as a file (@path). Passing it inline mangles the JSON
        // through argument escaping and Copilot then can't load the server.
        var configPath = Path.Combine(Path.GetTempPath(), "pippa-mcp-config.json");
        File.WriteAllText(configPath, json);
        _mcpConfigArg = "@" + configPath;
    }

    public async Task<string> RunDigestAsync(CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _copilotPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(Prompt);
        psi.ArgumentList.Add("--allow-tool=pippa");
        psi.ArgumentList.Add("--additional-mcp-config");
        psi.ArgumentList.Add(_mcpConfigArg);
        psi.ArgumentList.Add("--no-ask-user");
        psi.ArgumentList.Add("--no-color");
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add("--log-level");
        psi.ArgumentList.Add("error");

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, _) => { };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(150));
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* best effort */ }
            throw new TimeoutException("Copilot took too long to respond.");
        }

        return stdout.ToString().Trim();
    }

    private static string ResolveCopilot()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "copilot.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* ignore malformed PATH entries */ }
        }
        return "copilot.exe"; // let the OS resolve it
    }

    private static string ResolveMcpServer()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var config = baseDir.Contains(@"\Release\", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

        // Prefer the sibling project's own build output - a complete, runnable server.
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Pippa.Mcp", "bin", config, "net10.0", "pippa-mcp.exe");
            if (IsRunnable(candidate)) return candidate;
        }

        // Fallback: a server published side-by-side with the host.
        return Path.Combine(baseDir, "pippa-mcp.exe");
    }

    /// <summary>An apphost exe only runs if its runtimeconfig.json sits beside it.</summary>
    private static bool IsRunnable(string exePath)
    {
        if (!File.Exists(exePath)) return false;
        var dir = Path.GetDirectoryName(exePath)!;
        var name = Path.GetFileNameWithoutExtension(exePath);
        return File.Exists(Path.Combine(dir, name + ".runtimeconfig.json"));
    }
}
