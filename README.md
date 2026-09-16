# Peggy

Peggy is a local-first Windows desktop assistant for Microsoft 365. She watches
Outlook and Microsoft Teams, uses GitHub Copilot to decide what deserves
attention, presents source-linked alerts, and can prepare or execute a bounded
set of work actions only after explicit approval.

Peggy appears as an animated desktop bird with a tray menu, alert center, and
optional English voice conversation. Raw microphone audio and speech models
remain in a separate local process.

> **Current platform:** Windows x64, .NET 10, standalone Microsoft WorkIQ, and the
> GitHub Copilot CLI.

![Animated Peggy stands at the right edge above a Windows taskbar while cycling through Idle, Listening, Thinking, New Digest, Sleeping, and Waking](docs/images/peggy-hi.gif)

## Contents

- [Highlights](#highlights)
- [Architecture](#architecture)
- [Monitoring behavior](#monitoring-behavior)
- [Privacy](#privacy)
- [Safety model](#safety-model)
- [Prerequisites](#prerequisites)
- [Quick start](#quick-start)
- [Using Peggy](#using-peggy)
- [Settings](#settings)
- [Supported actions](#supported-actions)
- [Local speech](#local-speech)
- [Build and publish](#build-and-publish)
- [Testing](#testing)
- [Configuration reference](#configuration-reference)
- [MCP tools](#mcp-tools)
- [Local files and state](#local-files-and-state)
- [Project layout](#project-layout)
- [Running with sample data](#running-with-sample-data)
- [Troubleshooting](#troubleshooting)
- [Current limitations](#current-limitations)
- [Third-party software](#third-party-software)

## Highlights

- Monitors unread Outlook mail and Teams chat activity every five minutes.
- Starts each process with a fresh five-minute lookback instead of replaying a
  large downtime backlog.
- Uses strict, source-grounded Copilot classification to create Critical,
  Attention, and Awareness alerts.
- Opens alerts in the exact Outlook or Teams source when a verified link is
  available.
- Supports All, Selected only, and Off Teams monitoring scopes, with durable
  whole-team and individual-channel selections.
- Provides a warmed, persistent Copilot conversation with streamed responses.
- Runs English speech recognition and synthesis locally with sherpa-onnx.
- Requires an exact action preview and explicit approval before every write.
- Requires a second typed confirmation for destructive actions.
- Pins one Microsoft 365 identity and rejects a different authenticated account.
- Stores state with Windows DPAPI for the current user.
- Tracks local AI-credit usage and keeps a 30-day local action audit.
- Diagnoses interrupted monitoring with deterministic account, source, model,
  and read-only retrieval replay checks.

## Architecture

The diagram intentionally shows only Peggy's main runtime boundaries and
service relationships. Implementation details remain in the sections below.

![Peggy high-level architecture showing You, Peggy Desktop, Local Services, GitHub Copilot, WorkIQ, and Microsoft 365](docs/images/peggy-architecture.png)

[Edit the architecture diagram in Excalidraw](docs/diagrams/peggy-architecture.excalidraw).

### Components

| Component | Responsibility |
| --- | --- |
| `Peggy.Tray` | WPF mascot, tray menu, alert center, conversation UI, monitoring scheduler, privacy masking, and approval flow |
| `Peggy.Core` | Microsoft 365 provider, encrypted state, monitoring cursors, action policy, source verification, and shared models |
| `Peggy.Mcp` | Bounded MCP read tools and the proposal-only `prepare_action` tool |
| `Peggy.Speech.Host` | Isolated x64 process for microphone capture, VAD, speech recognition, and text-to-speech |
| `Peggy.Core.Tests` | Executable smoke and regression suite covering core, desktop, assistant, action, and speech behavior |

### Copilot execution modes

Peggy uses two Copilot paths:

1. **Monitoring:** each focused batch receives a new `copilot -p` session, so
   classifications never resume prior monitoring history. Copilot sees short
   batch-local aliases instead of Outlook or Teams identifiers. Peggy validates
   alias ownership and maps accepted decisions back to exact local identifiers
   and verified source links.
2. **Conversation:** one warmed official .NET Copilot SDK session streams text
   deltas and uses the same bounded Peggy MCP tools.

Temporary private files include process ownership in their names. Normal
cleanup removes them after each operation, and startup cleanup removes files
whose owning process has exited.

## Monitoring behavior

### Schedule and polling

- Automatic monitoring runs every five minutes.
- The default awake window is **07:00-19:00 local time**.
- Outside that window Peggy sleeps and automatic checks pause.
- **Check now** and **Talk to Peggy** remain available while sleeping.
- Waking or restarting establishes a new five-minute lookback.
- Normal monitoring uses persisted batches of at most 20 items. Consecutive
  failures reduce retries to 10, 5, and then 1 item so one problematic item can
  be isolated without silently advancing the source cursor.
- Read-only WorkIQ calls retry bounded transient `429`, `500`, `502`, `503`,
  and `504` responses; write operations are never replayed automatically.
- Three consecutive monitoring failures mark monitoring as interrupted and
  surface remediation in the Alert Center.
- Each classifier receives only `get_turn_context` and the scoped
  `list_unread` tool. Message fields are treated as untrusted data.

### Outlook

Peggy monitors:

- Inbox.
- Folder roots selected in Settings and every descendant of those folders.

Fresh installs select no additional folder roots. Existing saved selections
remain unchanged during upgrades.

Outlook cursors use message activity boundaries and delta links where
available. Source references are verified before they are attached to alerts.
Regional HTTPS links under `*.mail.microsoft.com` are rewritten to the
equivalent `outlook.office.com` URL to avoid regional endpoint authorization
failures. When Peggy opens an Outlook link, it extracts the validated Graph
item ID and builds one modern `/mail/{account}/deeplink/read/{item}` path for
the pinned mailbox. Legacy OWA query paths, nested schemes, and duplicated
URLs are never launched.

### Teams chats

The **All Teams activity** scope uses WorkIQ's structured `/search/query`
operation for newly sent messages in a bounded UTC window. Each search hit carries its message identity,
timestamp, sender, and exact Teams source link, so Peggy does not depend on a
natural-language answer to associate content with its source. Search windows
overlap by ten minutes. Peggy enriches search hits through bounded, batched
WorkIQ entity reads when possible, including resolving channel reply roots.
Stable message identities prevent duplicate classification across search and
watchlist delta scans while allowing recently indexed messages to be recovered.
Large result sets continue from a persisted search offset instead of restarting
or silently dropping later pages.

WorkIQ exposes `/search/query` through its generic `do_action` POST transport,
but the operation is read-only. Peggy does not send or modify a Teams item
during monitoring.

The **Selected teams and channels only** scope does not run broad Teams
search. Direct and group chats can be included separately; when enabled,
Peggy reads only their recent bounded message windows. Meeting chats remain
excluded from this option. The **Off** scope performs no Teams message reads.

Explicit unread views remain bounded structured WorkIQ reads and compare chat
activity with the caller's `lastMessageReadDateTime`. Teams links must identify
a specific `/l/message/` item rather than only a generic chat.

### Teams channels

- Broad Teams discovery uses structured WorkIQ search for the current monitoring
  window and discovers messages by sent time only in the All scope.
- Search results must contain a compatible exact Teams message link before
  Peggy creates an alert.
- Selected channels use structured Graph delta scans and include edits to older
  messages. Selected-only mode bypasses broad search entirely.
- Selecting an entire team includes all accessible current and future channels.
  Individual channels can be excluded, and the team catalog refreshes every
  30 minutes.
- Known channel participation is retained across monitoring-window rebases.

### Alert lifecycle

Each alert records its source, reason, tier, activity time, message IDs, and
material-change hash. Alerts can be:

- Resolved.
- Dismissed.
- Snoozed for one hour.
- Reclassified between Critical, Attention, and Awareness.
- Used as focused context for a Peggy conversation.

Feedback such as **Less like this** and **Always alert** is retained locally and
included in later classification runs.

## Privacy

Peggy is local-first, but not fully offline. Local speech, encrypted state, and
desktop interaction stay on the Windows device. GitHub Copilot and standalone
Microsoft WorkIQ connect to cloud services, so Peggy sends them only the
content needed for the requested monitoring or conversation task.

### Data flow at a glance

| Data | Where it is handled | Can it leave the device? |
| --- | --- | --- |
| Raw microphone audio | Isolated `peggy-speech-host.exe` process | No |
| Live partial speech text | Local conversation preview | No |
| Accepted final transcript | Peggy, then Copilot or WorkIQ | Yes, after the undo window or explicit review |
| Outlook, Teams, calendar, file, and task content | WorkIQ and focused Copilot context | Yes, when needed for monitoring, discovery, or conversation |
| Settings, cursors, alerts, feedback, credits, and audit | DPAPI-encrypted local state | No |
| Focus, context, action, and usage files | PID-owned temporary files under `%TEMP%` | The files stay local; selected contents can be sent through Copilot or WorkIQ |
| Diagnostic lifecycle events | `%TEMP%\peggy.log` unless overridden | No; message bodies are not logged |

### Voice privacy

- Microphone capture, voice activity detection, speech recognition, and
  text-to-speech run in the separate local speech process.
- Raw audio is never sent to GitHub Copilot, WorkIQ, or Microsoft 365.
- Live partial transcripts remain local and are never eligible for send.
- The accurate final transcript appears in an editable preview first.
- Ordinary requests have a visible one-second undo window.
- Editing the transcript or selecting **Cancel auto-send** pauses sending.
- Action approval transcripts always require explicit review.
- Ending the conversation or selecting **Unload voice models** terminates the
  speech process and releases native model memory.

### Cloud processing

- **GitHub Copilot** receives focused message context for alert classification,
  accepted conversation text, and the bounded context needed to answer.
- **Microsoft WorkIQ** accesses Microsoft 365 under the pinned delegated
  account for structured reads, grounded discovery, and approved actions.
- **Microsoft 365** remains the system of record for Outlook, Teams, Calendar,
  files, and To Do data.
- Peggy does not use a cloud relay, custom webhook, or background data export.
- Copilot cannot write directly to Microsoft 365.

Cloud processing remains subject to the policies, retention, and compliance
controls of the connected GitHub and Microsoft 365 services.

### Local storage and retention

- Persistent state is encrypted with Windows DPAPI using
  `DataProtectionScope.CurrentUser`.
- State is bound to the current Windows user and the pinned Microsoft 365
  account.
- Action audit entries are retained locally for 30 days.
- Temporary private files are deleted after normal operations.
- Temporary file names include the owning process ID. A later Peggy process
  removes files left by a crashed owner.
- Speech models are stored separately under
  `%LOCALAPPDATA%\Peggy\models` and contain no mailbox content.

To erase Peggy's persistent local state, quit Peggy and delete:

```text
%LOCALAPPDATA%\Peggy\state.dat
```

This removes settings, account pinning, cursors, alerts, feedback, credits, and
audit history. It does not remove the separately installed speech models.

### Privacy controls

| Control | Effect |
| --- | --- |
| **Pause monitoring** | Stops automatic Microsoft 365 checks |
| Monitoring schedule | Limits automatic checks to configured local hours |
| Disable local speech | Prevents voice conversations from loading |
| **Unload voice models** | Ends speech activity and releases model memory |
| Lock Windows | Ends voice activity, hides private UI, and masks alert content |
| **Reveal details** | Temporarily reveals masked alert content for one minute |
| **Quit Peggy** | Stops monitoring, Copilot sessions, and local speech |
| Speech-host firewall rule | Optionally blocks outbound network access for only the speech process |

## Safety model

### Account pinning

On first launch, Peggy asks which Microsoft 365 account to use. It completes
WorkIQ sign-in and consent, verifies the authenticated profile, then stores the
verified identity in encrypted state before structured reads, prompts, or
writes can begin.

The entered value may match either the mailbox address or user principal name.
Peggy resolves the canonical Outlook address after validation. A different
authenticated identity is rejected. Later reauthentication is available from
Settings and the tray menu.

### Approval boundary

Copilot cannot directly write to Microsoft 365. The MCP `prepare_action` tool
only validates and stages one proposal. The tray host:

1. Normalizes and validates the proposed fields.
2. Displays and speaks the exact operation.
3. Requests approval.
4. Executes the write through WorkIQ only after approval.
5. Reads the result back and verifies it.
6. Records the outcome in the encrypted local audit.

| Action class | Required approval |
| --- | --- |
| Non-destructive writes | Click approval or an immediate natural-language voice approval |
| Cancel calendar event | Exact typed confirmation |
| Delete Outlook message | Exact typed confirmation |
| Delete Microsoft To Do task | Exact typed confirmation |

Approvals are bound to the exact staged action ID. Interrupting a turn,
switching alerts, cancelling, or replacing a proposal clears stale approval
state.

### Source trust

Message bodies, prompt results, and quoted alert details are treated as data,
not instructions. Alert links must pass source-specific validation before
Peggy opens or persists them.

## Prerequisites

1. **Windows x64.**
2. **.NET 10 SDK** for development, or the .NET 10 Desktop Runtime only for a
   framework-dependent published build. GitHub Release packages are
   self-contained.
3. **GitHub Copilot CLI 1.0.81 or later** installed, authenticated, and
   available on `PATH`. Version 1.0.81 introduced the
   `--usage-output-file` support required by Peggy's monitoring classifier.
4. **Node.js 18 or later with npm** for standalone WorkIQ installation.
5. A **Work IQ usage-based billing plan** with the user assigned.
6. **Administrative consent** for the Work IQ application in the Microsoft
   Entra tenant.
7. `curl.exe` and `tar.exe` only for the optional speech-model installer.
8. **Classic Outlook desktop** for Microsoft To Do task reads and actions.
9. A microphone and audio output device for voice features.

See the official
[Microsoft Work IQ CLI documentation](https://learn.microsoft.com/microsoft-365/copilot/extensibility/work-iq/cli)
for billing, tenant consent, support, and platform requirements.

Peggy resolves standalone WorkIQ in this order:

1. `PEGGY_WORKIQ_PATH`.
2. `workiq.exe` or the global npm wrapper on `PATH`.
3. `%APPDATA%\npm\node_modules\@microsoft\workiq\bin\win-x64\workiq.exe`.

## Quick start

### Install from a GitHub Release

The
[latest GitHub Release](https://github.com/didiergbenou-ms/peggy/releases/latest)
offers three current-user installation paths:

1. **`peggy-win-x64-setup.exe` (recommended)** checks for PowerShell 7,
   Node.js 18 with npm, and GitHub Copilot CLI 1.0.81 or later, then installs
   the embedded MSI. It never installs prerequisites or credentials silently.
   If a check fails, install the named prerequisite or use `install.ps1
   -InstallPrerequisites` explicitly.
2. **`peggy-win-x64.msi`** runs the same prerequisite check and installs
   Peggy directly when all requirements are available.
3. **`install.ps1`** retains the existing checksum-verified ZIP path, optional
   WinGet prerequisite installation, and optional speech-model download.

The bootstrapper and MSI install x64 Peggy per user at
`%LOCALAPPDATA%\Programs\Peggy`, register it in Installed Apps, and create the
Peggy Start menu shortcut. WorkIQ installation, EULA acceptance, and Microsoft
365 sign-in remain explicit first-run actions. The MSI and bootstrapper do not
download the optional speech models; run the packaged
`scripts\Install-SpeechModels.ps1` if voice is wanted.

Interactive installation:

```powershell
.\peggy-win-x64-setup.exe
# Or:
msiexec.exe /i .\peggy-win-x64.msi
```

Unattended installation:

```powershell
.\peggy-win-x64-setup.exe /quiet /norestart
# Or:
msiexec.exe /i .\peggy-win-x64.msi /qn /norestart
```

For the script path, download `install.ps1`, then run:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\install.ps1 `
    -InstallPrerequisites
```

The installer:

- Downloads the latest self-contained Windows x64 package.
- Verifies its SHA-256 checksum before extraction.
- Downloads and verifies about 803 MiB of local English voice models.
- Installs for the current user under `%LOCALAPPDATA%\Programs\Peggy`.
- Creates a Start menu shortcut and launches Peggy.
- Can install PowerShell 7, Node.js LTS, and GitHub Copilot CLI through WinGet
  when `-InstallPrerequisites` is present.
- Requires GitHub Copilot CLI 1.0.81 or later. If WinGet provides an older
  version, the installer runs `copilot update stable` before installing Peggy.
- Preserves encrypted Peggy state during upgrades because state lives outside
  the installation directory.

Add `-SkipSpeechModels` to install without the local voice models, or
`-NoLaunch` to install without starting Peggy. The former
`-InstallSpeechModels` switch remains accepted for compatibility. Use
`-Version <release-tag>` to install a specific published release.

This repository is currently private. Authenticate GitHub CLI with repository
access (`gh auth login`) or set `GH_TOKEN` before running the installer. If the
repository becomes public, neither is required. The installer does not persist
the token.

The package and checksum can also be downloaded manually:

```powershell
.\install.ps1 `
    -PackagePath .\peggy-win-x64.zip `
    -ChecksumPath .\peggy-win-x64.sha256
```

### Uninstall

Use **Installed Apps**, or use the same package type unattended:

```powershell
.\peggy-win-x64-setup.exe /uninstall /quiet /norestart
# Direct MSI installation:
msiexec.exe /x .\peggy-win-x64.msi /qn /norestart
```

Normal MSI/bootstrapper uninstall removes application files, the Start menu
shortcut, ARP registration, the Peggy startup Run entry only when it points to
this canonical installation, `%LOCALAPPDATA%\Peggy` (including encrypted state
and speech models), and the current-user `PEGGY_WORKIQ_ACCOUNT` preference.
Major upgrades preserve that data. Uninstall never removes shared PowerShell,
Node.js, GitHub Copilot CLI, or WorkIQ installations.

For the script-installed path, exit Peggy and run the uninstaller from its
installation directory:

```powershell
powershell.exe -ExecutionPolicy Bypass -File `
    "$env:LOCALAPPDATA\Programs\Peggy\uninstall.ps1"
```

The uninstaller removes Peggy's application files, Start menu shortcut, and
optional startup entry. It preserves encrypted settings and local speech models
so they remain available after a reinstall. To remove those files and Peggy's
temporary runtime data and synchronized account preference too, add
`-PurgeData`:

```powershell
powershell.exe -ExecutionPolicy Bypass -File `
    "$env:LOCALAPPDATA\Programs\Peggy\uninstall.ps1" -PurgeData
```

Use `-WhatIf` to preview the cleanup. Shared prerequisites such as PowerShell 7,
Node.js, GitHub Copilot CLI, and WorkIQ are not removed. An uninstaller run from
a custom installation directory detects that directory automatically; a
standalone copy can target it with `-InstallDirectory`.

### Build from source

Open PowerShell in the repository root.

#### 1. Install standalone WorkIQ

Peggy can run this installation from its first-run window. To install it
yourself:

```powershell
npm install -g @microsoft/workiq
workiq version
```

#### 2. Install local speech models

```powershell
.\scripts\Install-SpeechModels.ps1
```

The installer downloads about 803 MiB of pinned official sherpa-onnx assets,
verifies every SHA-256 digest, and installs them under:

```text
%LOCALAPPDATA%\Peggy\models
```

Voice is optional. The GitHub Release installer includes these models by
default; use `-SkipSpeechModels` to omit them. Source builds can run without
the models by disabling local speech in Settings.

#### 3. Build and run

```powershell
dotnet restore .\src\Peggy.Tray\Peggy.Tray.csproj
dotnet run --project .\src\Peggy.Tray\Peggy.Tray.csproj -c Release
```

The tray project automatically builds and packages the MCP server and speech
host beside the tray executable.

#### 4. Complete Peggy setup

On first launch, Peggy opens **Peggy setup**:

1. Install standalone WorkIQ if it is not already available.
2. Enter the Microsoft 365 email address Peggy should use.
3. Review and accept the WorkIQ EULA. Peggy records the exact accepted WorkIQ
   build in its encrypted state.
4. Optionally select **Install voice models** to download and SHA-256 verify
   about 803 MiB of local English speech models.
5. Select **Sign in** and complete any Microsoft sign-in or permission-consent
   prompt WorkIQ opens.
6. Peggy reads `/me`, verifies the selected identity, and pins the canonical
   account in DPAPI-encrypted state.

If the tenant has not granted the required WorkIQ permissions, an administrator
must complete tenant consent before verification can succeed.

Voice setup can be skipped. Open **Settings → Local voice (English only)** and
select **Install voice models** later; the same verified downloader is used.
Fresh installs monitor Inbox only. Additional Inbox folders, including
`Inbox/Cases/Active`, are opt-in through Settings.

Use **Microsoft 365 account** from the tray or **Change or reconnect** in
Settings to refresh authentication or enter another account. After Peggy
verifies a different identity, it clears local alerts, monitoring cursors,
account-specific Outlook folders and Teams selections, Copilot conversation
context, credit history, and action audit for the previous account. General
schedule, model, startup, and speech preferences are preserved.
The account field remains editable even when `PEGGY_WORKIQ_ACCOUNT` is already
set. After verification, Peggy synchronizes that current-user preference to the
selected account; encrypted verified identity remains authoritative.

If WorkIQ loses its local `I-accept-EULA` setting, Peggy restores it
automatically only when the installed WorkIQ build exactly matches the build
whose EULA was accepted. A WorkIQ update requires the user to review and accept
the current EULA again through **Change or reconnect**.

### 5. Add the optional speech-host firewall rule

From an elevated PowerShell window:

```powershell
.\scripts\Protect-SpeechHost.ps1
```

The script finds the newest standard, RID-specific, or publish Release host and
replaces the named outbound-block rule. If Peggy is installed elsewhere:

```powershell
.\scripts\Protect-SpeechHost.ps1 `
    -HostPath "C:\Apps\Peggy\Peggy.Speech.Host\peggy-speech-host.exe"
```

Rerun the script if the executable moves.

## Using Peggy

Peggy runs as a single-instance tray application. Right-clicking either the
tray icon or the desktop mascot opens the same menu. Double-clicking the tray
icon opens the Alert Center.

### Tray menu

| Command | Behavior |
| --- | --- |
| **Open alerts** | Opens active Critical, Attention, and Awareness alerts |
| **Talk to Peggy** | Starts a local voice conversation |
| **Unload voice models** | Ends voice activity and releases native model memory |
| **Check now** | Runs a monitoring check even while scheduled sleep is active |
| **Pause monitoring** | Toggles manual monitoring pause |
| **AI credits today** | Shows recent local Copilot credit entries |
| **Microsoft 365 account** | Reconnects the current account or switches to another verified identity |
| **Settings** | Opens model, source, voice, schedule, startup, and watchlist settings |
| **Show or hide Peggy** | Toggles the desktop mascot |
| **Test animations** | Plays individual or all mascot states |
| **Help → Troubleshooting** | Runs read-only account, Teams, Copilot, and monitoring checks on demand and can create a diagnostic ZIP |
| **Quit Peggy** | Stops monitoring, closes Copilot, unloads speech, and exits |

### Alert Center

The Alert Center shows:

- The alert tier and source.
- A concise summary.
- Why Peggy flagged the item.
- The verified **Open in Outlook** or **Open in Teams** action.
- Resolve, dismiss, dismiss-all, snooze, feedback, tier correction, context,
  and conversation controls.

When privacy masking is active, details remain hidden until
**Reveal details** is explicitly confirmed. A reveal expires after one minute.
When monitoring is interrupted, **Troubleshoot** opens the captured failure
details and targeted checks beside **Retry now**.

### Voice conversation

1. Select **Talk to Peggy**, or start a conversation from an alert.
2. Peggy loads the local models and listens.
3. Live partial text appears locally.
4. The final transcript appears in an editable **I heard...** card.
5. Ordinary requests auto-send after a visible one-second undo window.
6. Editing the text or selecting **Cancel auto-send** pauses automatic send.
7. Approval transcripts never auto-send; they require explicit review.

Pressing the microphone while Peggy is thinking or speaking interrupts the
current turn and returns to listening. Ending a conversation unloads every
speech model.

## Settings

Settings are persisted atomically with the rest of Peggy's encrypted state.
Runtime services are staged before activation so a failed configuration does
not leave a partially applied identity, model, or source scope.

| Setting | Notes |
| --- | --- |
| Microsoft 365 connection | Shows the current account and supports reconnection or verified account switching |
| Pinned Copilot model | Loaded live from the authenticated GitHub Copilot CLI; **Refresh** uses a new catalog connection so newly available models appear without restarting Peggy; changing it starts a fresh Copilot session |
| Reasoning effort | Shows only the effort levels supported by the selected model; **Model default** lets Copilot choose |
| Context tier | Offers **Default** for every model and **Long context** only when the live catalog reports support |
| Outlook folders | Monitors Inbox only by default; loads its hierarchy lazily so optional folder roots and all their descendants can be selected, while saved unavailable paths remain visible |
| Enable local speech | Enables or disables the isolated English speech host |
| Speech model directory | Defaults to `%LOCALAPPDATA%\Peggy\models` |
| Install voice models | Downloads or repairs the SHA-256-verified English model bundle; about 803 MiB |
| Voice | Bella, Luna, Rosie, or Kiki |
| CPU threads | 1, 2, 4, or 8 |
| Microphone gain | 4x, 8x, 12x, or 16x |
| Monitoring schedule | Local 24-hour wake and sleep times |
| Start at Windows sign-in | Manages the current user's `Run` registry entry |
| Pause monitoring | Persists a manual pause independently of scheduled sleep |
| Teams monitoring scope | All Teams activity, selected teams and channels only, or Off |
| Direct and group chats | Optional in selected-only mode; meeting chats stay excluded |
| Teams source picker | Loads joined teams first, then loads channels only when a team is expanded |

Selected sources are always listed at the top of Settings and can be removed
without loading their team. Selecting an individual channel enables full
structured delta monitoring for it. Selecting an entire team includes all
accessible current and future channels; clearing one channel creates a durable
exclusion. A partially selected team is shown with an indeterminate checkbox.
Changing Teams scope resets only Teams progress, not Outlook progress. Activity
accumulated while Teams monitoring is Off is not replayed when it is enabled.

## Supported actions

Peggy can prepare these bounded actions:

| Area | Actions |
| --- | --- |
| Outlook | Mark a message read, create an email draft, send email, create a reply draft, send a reply, delete a message |
| Teams | Send a chat message or channel-thread reply |
| Calendar | Create an event, update an event, cancel an event |
| Microsoft To Do | Create, update, complete, or delete a task in the default `Tasks` list |

Limits enforced before approval include:

- At most 50 normalized recipients.
- Subject length up to 300 characters.
- Body length up to 10,000 characters.
- Valid event start/end ordering.
- ISO 8601 UTC timestamps for MCP calendar proposals.
- `YYYY-MM-DD` task due dates.
- No explicit recipient override for Outlook replies.
- One action proposal per Copilot turn.

## Local speech

Peggy never falls back to `System.Speech`, Windows Speech Recognizer, or an
installed Windows voice.

| Model | Role |
| --- | --- |
| NeMo streaming FastConformer CTC | Low-latency partial transcription |
| NeMo Parakeet TDT | Accurate final transcription |
| Silero VAD v5 | Voice activity detection and endpointing |
| Kitten TTS Nano | Local real-time English text-to-speech |
| NAudio WASAPI | Windows microphone and playback transport |

The speech host:

- Runs as a separate x64 process.
- Uses 450 ms endpointing.
- Receives model, thread, voice, speed, and gain settings through its process
  environment.
- Stays warm during a conversation.
- Is terminated when the conversation ends or models are unloaded.
- Has no application network features.

### Reinstalling incomplete models

The installer refuses to treat a partial model directory as complete. Repair it
with:

```powershell
.\scripts\Install-SpeechModels.ps1 -Force
```

To install in another directory:

```powershell
.\scripts\Install-SpeechModels.ps1 `
    -Destination "D:\Peggy\models"
```

Then select that directory in Settings or set `PEGGY_SPEECH_MODELS`.

## Build and publish

Exit a running Peggy instance before rebuilding so loaded assemblies are not
locked.

### Development build

```powershell
dotnet build .\src\Peggy.Tray\Peggy.Tray.csproj -c Release
```

Output:

```text
src\Peggy.Tray\bin\Release\net10.0-windows\
```

### RID-specific x64 build

```powershell
dotnet build .\src\Peggy.Tray\Peggy.Tray.csproj `
    -c Release `
    -r win-x64
```

Output:

```text
src\Peggy.Tray\bin\Release\net10.0-windows\win-x64\
```

### Framework-dependent publish

```powershell
dotnet publish .\src\Peggy.Tray\Peggy.Tray.csproj `
    -c Release `
    --self-contained false
```

Output:

```text
src\Peggy.Tray\bin\Release\net10.0-windows\publish\
```

### RID-specific publish

```powershell
dotnet publish .\src\Peggy.Tray\Peggy.Tray.csproj `
    -c Release `
    -r win-x64 `
    --self-contained false
```

The publish directory contains:

```text
peggy-tray.exe
Peggy.Mcp\
  peggy-mcp.exe
Peggy.Speech.Host\
  peggy-speech-host.exe
```

Both helper projects are published automatically by the tray project's MSBuild
targets.

### GitHub Releases

Pushing a semantic version tag creates a GitHub Release:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The release workflow runs the regression suite, builds with pinned WiX Toolset
6.0.2 packages, performs MSI database and administrative-image validation, and
publishes:

- `peggy-win-x64.zip`, a self-contained package for Windows x64.
- `peggy-win-x64.sha256`, the package checksum used by `install.ps1`.
- `peggy-win-x64.msi` and `peggy-win-x64.msi.sha256`, the per-user MSI and its
  checksum.
- `peggy-win-x64-setup.exe` and `peggy-win-x64-setup.exe.sha256`, the WiX Burn
  prerequisite-checking bootstrapper and its checksum.
- `install.ps1`, the current-user bootstrap installer.
- `uninstall.ps1`, the current-user uninstaller.

Build all Windows artifacts locally with:

```powershell
$version = '<semver>'
.\scripts\Build-WindowsRelease.ps1 -Version $version
.\scripts\Test-PeggyWindowsInstaller.ps1 -Version $version
```

MSI uses three numeric version fields. To keep upgrade ordering correct, Peggy
maps stable `major.minor.patch` to `major.minor.(patch*100+99)`. Prereleases use
ordered slots: `alpha.1` through `alpha.29`, `beta.1` through `beta.30`, and
`rc.1` through `rc.39`. Major and minor must be at most 255 and patch at most
654. The semantic release version remains in `version.txt`, assembly metadata,
release notes, and artifact context. Unsupported prerelease forms fail the
build rather than producing an unsafe upgrade.

WiX ICE custom actions can be blocked by policy in non-elevated build
environments (the local validation attempt returned `WIX1105`), so the build
suppresses that unavailable pass.
`Test-PeggyWindowsInstaller.ps1` instead inspects the MSI database, verifies the
stable UpgradeCode and upgrade sequence, compares every File-table entry,
creates a disposable `msiexec /a` administrative image, hashes every extracted
payload file, and extracts the Burn container with the pinned WiX command.

The MSI relies on Windows Installer Restart Manager for files in use, so only
processes holding this installation's files are asked to stop. A checked,
current-user deferred custom action is used on a real uninstall (not a major
upgrade) to remove the startup Run value only when it resolves exactly to the
canonical Peggy executable. A paired rollback action restores that exact value
if MSI rolls the uninstall back. After `InstallFinalize` successfully commits a
true uninstall, a separate current-user action removes Peggy's user-data root
and synchronized account preference. The cleanup action does not run during
upgrades or failed uninstalls and does not remove shared prerequisites or
broader LocalAppData.

Release packages are currently checksum-verified but not Authenticode-signed.
Windows SmartScreen can therefore show an unknown-publisher warning. The
workflow has explicit signing boundaries: sign the MSI before building the
bundle, sign the Burn engine/bundle using WiX's detach/reattach procedure, and
only then generate checksums. No certificate or signing secret is assumed.

## Testing

The test project is an executable smoke suite rather than a test-runner package.
It covers:

- Encrypted state and retention.
- Monitoring cursors, pagination, and Teams edits.
- WorkIQ retries, identity pinning, and source verification.
- Approval policy and verified action execution.
- MCP focus and proposal boundaries.
- WPF alert, settings, conversation, privacy, and mascot behavior.
- Local speech protocol, cancellation, and unloading.
- Temporary private-file crash recovery.

Run the complete isolated suite:

```powershell
dotnet run --project .\src\Peggy.Core.Tests\Peggy.Core.Tests.csproj `
    -c Release
```

Successful output ends with:

```text
All Peggy Core smoke tests passed.
```

Optional live switches use the configured Microsoft 365 or Copilot account:

| Switch | Purpose |
| --- | --- |
| `--live` | Exercises live Microsoft 365 read surfaces |
| `--live-channels` | Exercises live Teams channel reads |
| `--live-unread` | Exercises live unread-message retrieval |
| `--live-monitoring` | Exercises bounded structured monitoring with exact source validation |
| `--live-outlook-link` | Fetches one private Outlook message, creates a real Peggy alert, and launches the production source URL when enabled |
| `--live-copilot` | Exercises a live Copilot conversation |

Example:

```powershell
dotnet run --project .\src\Peggy.Core.Tests\Peggy.Core.Tests.csproj `
    -c Release `
    -- --live-unread
```

The Outlook-link test takes the private message ID only from the environment,
so it is never committed:

```powershell
$env:PEGGY_LIVE_OUTLOOK_MESSAGE_ID = '<Graph message ID>'
$env:PEGGY_LIVE_OUTLOOK_LINK_OUTPUT = "$env:TEMP\peggy-outlook-link.txt"
$env:PEGGY_LIVE_OUTLOOK_BROWSER_RESULT = "$env:TEMP\peggy-outlook-browser.json"
dotnet run --project .\src\Peggy.Core.Tests\Peggy.Core.Tests.csproj `
    -c Release `
    -- --live-outlook-link
```

The test launches through Peggy's production Windows shell path, then waits
for browser automation to return the same per-run nonce, hashes of the
navigated URL and rendered subject, `unauthorizedFound`, and the observed
Outlook `GetItem` status codes. It does not pass from URL construction alone
or from a stale browser result.

The automated action suite uses a fake WorkIQ transport and does not perform
external writes.

## Configuration reference

### User-facing overrides

| Variable | Purpose | Default |
| --- | --- | --- |
| `PEGGY_WORKIQ_ACCOUNT` | Current-user account preference, synchronized after verified sign-in | Encrypted account selected during first-run sign-in |
| `PEGGY_WORKIQ_PATH` | Explicit `workiq.exe` or compatible wrapper path | Auto-detected |
| `PEGGY_MESSAGE_PROVIDER` | `workiq` or `mock` | `workiq` |
| `PEGGY_COPILOT_PATH` | Explicit `copilot.exe` path | `PATH`, then the current user's WinGet links |
| `PEGGY_COPILOT_MODEL` | Process-level Copilot model override | Saved setting, initially `gpt-5.4` |
| `PEGGY_STATE_PATH` | Alternate DPAPI-encrypted state path | `%LOCALAPPDATA%\Peggy\state.dat` |
| `PEGGY_SPEECH_MODELS` | Alternate local speech-model directory | `%LOCALAPPDATA%\Peggy\models` |
| `PEGGY_SPEECH_HOST_PATH` | Explicit packaged speech-host executable | Auto-detected |
| `PEGGY_LOG_PATH` | Diagnostic log path | `%TEMP%\peggy.log` |

### Source-scope overrides

The tray normally derives these values from Settings and passes them to its
child processes:

| Variable | Format |
| --- | --- |
| `PEGGY_OUTLOOK_FOLDERS` | Semicolon-separated Inbox-relative paths |
| `PEGGY_INCLUDE_ACTIVE_CASES_FOLDER` | `true` or `false`; forwarded to Peggy's scoped Copilot MCP processes |
| `PEGGY_TEAMS_WATCHLIST` | JSON array of team/channel watch objects |
| `PEGGY_TEAMS_TEAM_WATCHLIST` | JSON array of whole-team watch objects and channel exclusions |
| `PEGGY_TEAMS_SCOPE` | `AllActivity`, `SelectedTeamsAndChannels`, or `Off` |
| `PEGGY_TEAMS_MONITOR_CHATS` | `true` to include direct and group chats in selected-only mode |

### Internal bridge variables

`PEGGY_FOCUS_FILE`, `PEGGY_CONTEXT_FILE`, `PEGGY_ACTION_FILE`, and the
`PEGGY_SPEECH_*` runtime variables are managed by Peggy. They are not intended
as end-user configuration.

## MCP tools

| Tool | Purpose |
| --- | --- |
| `list_unread` | Lists bounded unread Outlook and Teams messages, newest first |
| `get_thread` | Retrieves bounded context for an opaque thread ID |
| `list_upcoming_events` | Lists up to 50 events over a 1-30 day window |
| `list_tasks` | Lists up to 50 tasks from the pinned account's default Tasks list |
| `ask_workiq` | Performs broad grounded discovery across Microsoft 365 |
| `get_turn_context` | Reads the private JSON context for the current turn |
| `prepare_action` | Validates and stages exactly one proposal; never executes it |

When monitoring provides a focused message batch, `list_unread` and
`get_thread` are constrained to that batch.

## Local files and state

| Location | Contents |
| --- | --- |
| `%LOCALAPPDATA%\Peggy\state.dat` | DPAPI-encrypted settings, cursors, alerts, feedback, seen IDs, credits, and audit |
| `%LOCALAPPDATA%\Peggy\models` | Verified local speech models and `speech-models.json` manifest |
| `%TEMP%\peggy.log` | Minimal diagnostic lifecycle log unless overridden |
| `%TEMP%\peggy-*.json` | Short-lived private Copilot/MCP scope and usage files |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | Optional `Peggy` startup entry |

Audit entries older than 30 days and stale operational markers are pruned when
state is normalized.

## Project layout

```text
peggy\
  README.md
  THIRD-PARTY-NOTICES.txt
  scripts\
    Install-SpeechModels.ps1
    Protect-SpeechHost.ps1
  src\
    Peggy.Core\
    Peggy.Core.Tests\
    Peggy.Mcp\
    Peggy.Speech.Host\
    Peggy.Tray\
```

Key implementation files:

| File | Purpose |
| --- | --- |
| `src\Peggy.Core\WorkIqMessageProvider.cs` | Microsoft 365 reads, cursors, writes, and verification |
| `src\Peggy.Core\EncryptedStateStore.cs` | DPAPI persistence and retention |
| `src\Peggy.Core\WorkActionPolicy.cs` | Proposal validation and exact approval descriptions |
| `src\Peggy.Tray\App.xaml.cs` | Application lifecycle, monitoring, tray, settings, and alerts |
| `src\Peggy.Tray\ConversationController.cs` | Voice-turn and approval state machine |
| `src\Peggy.Tray\CopilotRunner.cs` | Isolated Copilot CLI monitoring operations |
| `src\Peggy.Tray\PersistentCopilotConversation.cs` | Persistent Copilot SDK conversation |
| `src\Peggy.Tray\LocalSpeechService.cs` | Speech-host lifecycle and protocol |
| `src\Peggy.Speech.Host\SpeechRuntime.cs` | sherpa-onnx model loading and audio pipeline |

## Running with sample data

Mock mode exercises the UI without Microsoft 365 reads:

```powershell
$env:PEGGY_MESSAGE_PROVIDER = "mock"
dotnet run --project .\src\Peggy.Tray\Peggy.Tray.csproj -c Release
```

Remove the process override to return to WorkIQ:

```powershell
Remove-Item Env:\PEGGY_MESSAGE_PROVIDER
```

## Troubleshooting

### Troubleshoot interrupted monitoring

Select **Troubleshoot** in the Alert Center failure banner. Peggy preserves the
original stage, operation, status, request ID, timestamp, and exception chain,
then runs four deterministic checks:

1. Microsoft 365 account and Inbox access.
2. Configured Teams source access.
3. Access to the configured GitHub Copilot model.
4. The exact monitoring retrieval path against a cloned failure-time cursor.

The replay is read-only and never advances Peggy's monitoring cursor. It also
does not ask Copilot to diagnose Copilot. The result recommends the relevant
sign-in, Copilot settings, retry, or diagnostic action. **Copy technical
details** copies the complete report; **Create diagnostic ZIP** runs the
packaged collector with deterministic WorkIQ probes, without invoking Copilot
or sending email.

### Collect a diagnostic ZIP

The repository is private, so the affected Windows user must authenticate to
GitHub with an account that has access, clone the repository, and run the
detailed collector from that clone:

```powershell
git clone --depth 1 https://github.com/didiergbenou-ms/peggy.git
Set-Location .\peggy\scripts
.\Collect-PeggyDiagnostics.ps1 -RunConnectivityTests
```

The script creates `Peggy-Diagnostics-<timestamp>.zip` on the user's Desktop,
then displays the exact recipient and asks the user to type `SEND` before
emailing it to `didiergbenou@microsoft.com` through their configured classic
Outlook profile.

Use `-NoEmail` to create the ZIP without offering to send it. For approved
automation, use `-SendWithoutPrompt`; `-EmailTo` can override the recipient.
Use `-WorkIqAccount` only when Peggy's encrypted state cannot supply its pinned
Microsoft 365 account.
If Outlook cannot send the message, the ZIP remains on the Desktop for manual
review and sharing.

The bundle includes Peggy versions, selected installation hashes, process and
startup details, the collector script hash and source Git commit, a decrypted
operational-state summary, speech-model inventory, raw and redacted lifecycle
logs, full relevant Windows Application events, and optional deep Copilot and
WorkIQ diagnostics. WorkIQ diagnostics use Peggy's pinned account and test its
exact profile fields, Inbox access, child-folder listing, message permission,
configuration, and tenant policies while preserving full technical errors.

The ZIP can contain account identifiers, folder metadata, process command
lines, and raw CLI output. It still excludes credentials, tokens,
authentication caches, customer message bodies, files, calendar contents,
Copilot session data, temporary scope-file contents, and speech-model binaries.
Review it carefully before typing `SEND` or sharing it outside the support
team.

| Symptom | Resolution |
| --- | --- |
| Peggy setup appears | Install standalone WorkIQ if needed, optionally install voice models, review the EULA, then sign in and grant consent |
| Authenticated account does not match | Open **Microsoft 365 account** and reconnect the current identity or enter and verify another account |
| WorkIQ not found | Select **Install WorkIQ with npm**, run `npm install -g @microsoft/workiq`, or set `PEGGY_WORKIQ_PATH` |
| WorkIQ terms error | Open Settings, select **Change or reconnect**, then review and accept the current terms |
| WorkIQ consent fails | Ask a Microsoft Entra administrator to grant the Work IQ application permissions required by your tenant |
| Monitoring classification repeatedly fails after installation | Run `copilot --version` and confirm version 1.0.81 or later. Run `copilot update stable`, restart Peggy, or rerun `install.ps1 -InstallPrerequisites` |
| Monitoring interrupted | Open the Alert Center and select **Troubleshoot** to inspect the original failure and replay retrieval without changing state |
| Speech models missing | Open Settings and select **Install voice models**, or run `.\scripts\Install-SpeechModels.ps1` |
| Incomplete model directory | Rerun the installer with `-Force` |
| Speech host missing | Rebuild/publish `Peggy.Tray` or set `PEGGY_SPEECH_HOST_PATH` |
| Voice input is too quiet | Increase **Mic gain** in Settings |
| Voice is unavailable but monitoring works | Verify the model directory and packaged `Peggy.Speech.Host` output |
| Firewall script cannot find the host | Build in Release mode or pass the exact executable with `-HostPath` |
| Build reports a locked Peggy assembly | Use **Quit Peggy**, then rebuild |
| Settings are overridden | Remove the corresponding process environment override, especially `PEGGY_COPILOT_MODEL` |
| Need isolated test state | Set `PEGGY_STATE_PATH` to a new file before launch |
| Need diagnostics | Select **Create diagnostic ZIP** in the troubleshooter, or clone the repository and run the collector above; for a quick check, inspect `%TEMP%\peggy.log` or the path in `PEGGY_LOG_PATH` |

## Current limitations

- Windows x64 only.
- English-only local speech.
- Framework-dependent publish by default.
- Microsoft To Do actions target only the default `Tasks` list.
- Monitoring is polling-based, not real-time.
- The All Teams activity scope depends on the freshness and availability of
  WorkIQ structured search.
- Broad Teams search cannot discover an edit to a message sent before its
  overlap window. Microsoft exposes the all-chat modification feed only to
  application identities, which is incompatible with Peggy's independent
  delegated user sign-in. Use a channel watchlist when older channel-message
  edits must be monitored.
- The outbound speech-host firewall rule is an optional administrator step.
- ONNX Runtime telemetry behavior ultimately depends on the native runtime
  packaged by sherpa-onnx. A private native build using `--no_telemetry` is the
  strongest removal option.

## Third-party software

Dependency and model attribution is documented in
[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt).
