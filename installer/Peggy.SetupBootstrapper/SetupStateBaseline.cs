using Peggy.Core;

namespace Peggy.SetupBootstrapper;

internal sealed class SetupStateBaseline(EncryptedStateStore store)
{
    internal PeggyStateDocument State { get; private set; } = new();
    internal string Identity { get; private set; } = "";

    internal async Task RefreshAsync(CancellationToken ct)
    {
        State = await store.LoadAsync(ct);
        Identity = Stamp(State);
    }

    internal bool Matches(PeggyStateDocument state) => Identity == Stamp(state);
    internal void AcceptSaved(PeggyStateDocument state) => Identity = Stamp(state);
    private static string Stamp(PeggyStateDocument state) =>
        string.Join("\n", state.PinnedAccount, state.PinnedMailbox, state.PinnedUserPrincipalName,
            state.PinnedPrincipalId, state.WorkIqEulaAcceptedVersion);
}
