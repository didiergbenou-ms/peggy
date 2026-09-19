using System.Runtime.Versioning;

namespace Peggy.Core;

[SupportedOSPlatform("windows")]
public static class WorkIqSetupCompletion
{
    public static async Task<bool> ApplyIdentityAsync(
        PeggyStateManager state, WorkIqAccountIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.PrincipalId) ||
            string.IsNullOrWhiteSpace(identity.EulaAcceptedVersion))
            throw new InvalidOperationException("A verified identity and exact accepted WorkIQ version are required.");
        var principalChanged = !string.IsNullOrWhiteSpace(state.State.PinnedAccount) &&
            !WorkIqSetupService.RepresentsSameAccount(
                state.State.PinnedAccount, state.State.PinnedMailbox,
                state.State.PinnedUserPrincipalName, state.State.PinnedPrincipalId, identity);
        var selectorChanged = !principalChanged && !string.IsNullOrWhiteSpace(state.State.PinnedAccount) &&
            !string.Equals(state.State.PinnedAccount, identity.Account, StringComparison.OrdinalIgnoreCase);
        if (principalChanged)
            await state.SwitchWorkIqIdentityAsync(identity, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, ct);
        else if (selectorChanged)
            await state.RebindWorkIqIdentityAsync(identity, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, ct);
        else
        {
            await state.PinWorkIqIdentityAsync(
                string.IsNullOrWhiteSpace(state.State.PinnedAccount) ? identity.Account : state.State.PinnedAccount,
                identity.Mail ?? identity.UserPrincipalName, identity.UserPrincipalName, identity.PrincipalId, ct);
            await state.RecordWorkIqEulaAcceptanceAsync(identity.EulaAcceptedVersion, ct);
        }
        return principalChanged;
    }
}
