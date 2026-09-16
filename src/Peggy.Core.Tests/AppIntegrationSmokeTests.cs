using System.Reflection;
using Peggy.Core;
using Peggy.Tray;

namespace Peggy.Core.Tests;

internal static class AppIntegrationSmokeTests
{
    public static async Task RunAsync()
    {
        var clone = typeof(App).GetMethod(
            "CloneSettings", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("App settings cloner not found.");
        foreach (var outlook in Enum.GetValues<OutlookMonitoringScope>())
        {
            foreach (var teams in Enum.GetValues<TeamsMonitoringScope>())
            {
                var settings = new PeggySettings
                {
                    OutlookScope = outlook,
                    TeamsScope = teams
                };
                var copy = (PeggySettings?)clone.Invoke(null, [settings])
                    ?? throw new InvalidOperationException("App settings clone missing.");
                TestAssert.True(
                    copy.OutlookScope == outlook && copy.TeamsScope == teams,
                    "App runtime cloning lost a monitoring mode.");
                var environment = PeggyProviderEnvironment.Create(
                    settings, "synthetic@example.invalid", "synthetic-principal");
                TestAssert.True(
                    environment["PEGGY_OUTLOOK_SCOPE"] == outlook.ToString() &&
                    environment["PEGGY_TEAMS_SCOPE"] == teams.ToString() &&
                    environment[PeggyDefaults.TeamsModeEnvironmentVariable] == teams.ToString(),
                    "Copilot MCP did not receive the selected monitoring modes.");
            }
        }

        var providerCalls = 0;
        var diagnostic = new MonitoringTroubleshooter(
            new MonitoringFailureDetails(),
            new PeggySettings
            {
                OutlookScope = OutlookMonitoringScope.Off,
                TeamsScope = TeamsMonitoringScope.Off
            },
            new MonitoringCursorState(),
            "synthetic@example.invalid",
            0,
            _ => Task.FromResult<IReadOnlyList<CopilotModelOption>>([]),
            () =>
            {
                providerCalls++;
                return new MockMessageProvider();
            });
        var report = await diagnostic.RunAsync();
        TestAssert.True(
            report.Checks[0].Status == TroubleshootingCheckStatus.NotReached &&
            report.Checks[1].Status == TroubleshootingCheckStatus.NotReached &&
            providerCalls == 1,
            "Troubleshooting performed catalog reads for disabled sources.");
    }
}
