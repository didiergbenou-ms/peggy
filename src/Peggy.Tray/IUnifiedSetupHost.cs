using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Peggy.Core;

namespace Peggy.Tray;

internal sealed record SetupRequirement(string Id, string Name, bool Ready, string Detail);
internal sealed record SetupInstallProgress(string Message, int Percentage);
internal sealed record SetupInstallResult(bool Succeeded, bool Cancelled, bool RestartRequired, string Message);

internal interface IUnifiedSetupHost
{
    PeggyStateDocument InitialState { get; }
    bool RequiresOnboarding { get; }
    string ActionLabel { get; }
    event Action<SetupInstallProgress>? Progress;
    Task<IReadOnlyList<SetupRequirement>> PrepareAsync(CancellationToken ct);
    Task InstallRequirementAsync(string id, CancellationToken ct);
    Task<SetupInstallResult> ApplyAsync(IntPtr owner, CancellationToken ct);
    Task<WorkIqAccountIdentity?> VerifySavedIdentityAsync(CancellationToken ct);
    Task CompleteAsync(WorkIqAccountIdentity identity, bool voiceReady, CancellationToken ct);
    void Cancel();
    void Close(bool launch);
    void ReportFailure(Exception exception);
}
