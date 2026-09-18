using Peggy.Tray;

namespace Peggy.PrerequisiteCheck;

internal sealed record CopilotCandidateResolution(IReadOnlyList<string> Candidates, string? Error);

internal static class CopilotPrerequisite
{
    internal static CopilotCandidateResolution ResolveCandidates(string localAppData)
    {
        try { return new([CopilotRunner.ResolveCopilot()], null); }
        catch (Exception ex) when (SetupReadinessService.IsExpectedFailure(ex))
        {
            return new([], ex.Message);
        }
    }
}
