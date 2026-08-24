# Peggy

Peggy is a local-first Windows desktop assistant for Microsoft 365. She watches
Outlook and Microsoft Teams, uses GitHub Copilot to decide what deserves
attention, presents source-linked alerts, and can prepare or execute a bounded
set of work actions only after explicit approval.

Peggy appears as an animated desktop bird with a tray menu, alert center, and
optional English voice conversation. Raw microphone audio and speech models
remain in a separate local process.

> **Current platform:** Windows x64, .NET 10, Microsoft Scout WorkIQ, and the
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
- Supports prompt-guided Teams channel discovery and explicit full-channel
  watchlists.
- Provides a warmed, persistent Copilot conversation with streamed responses.
- Runs English speech recognition and synthesis locally with sherpa-onnx.
- Requires an exact action preview and explicit approval before every write.
- Requires a second typed confirmation for destructive actions.
- Pins one Microsoft 365 identity and rejects a different authenticated account.
- Stores state with Windows DPAPI for the current user.
- Tracks local AI-credit usage and keeps a 30-day local action audit.

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

1. **Monitoring:** short, isolated `copilot -p` runs classify focused batches of
   messages. Private focus, context, action, and usage files are passed through
   temporary local paths rather than command-line arguments.
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
- Three consecutive monitoring failures mark monitoring as interrupted and
  surface remediation in the Alert Center.

### Outlook

Peggy monitors:

- Inbox.
- `Inbox\Cases\Active`.
- Every descendant of those folders.
- Any additional Inbox-relative folders configured in Settings.

Outlook cursors use message activity boundaries and delta links where
available. Source references are verified before they are attached to alerts.

### Teams chats

Peggy enumerates the signed-in user's chats and requests changed messages using
`lastModifiedDateTime`. Alert activity uses the message creation time or a real
`lastEditedDateTime`, so an edit to an older message is detected while
reaction-only or importance-only metadata changes do not re-enter the alert
window.

Unread views compare chat activity with the caller's
`lastMessageReadDateTime`. Teams links must identify a specific
`/l/message/` item rather than only a generic chat.

### Teams channels

- Broad channel discovery uses a constrained WorkIQ prompt for the current
  monitoring window.
- Prompt results must resolve to a cited, compatible Teams source before Peggy
  creates an alert.
- Channels selected in the full-channel watchlist use structured Graph delta
  scans.
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
desktop interaction stay on the Windows device. GitHub Copilot and Microsoft
Scout WorkIQ are cloud services, so Peggy sends them only the content needed
for the requested monitoring or conversation task.

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
- **Microsoft Scout WorkIQ** accesses Microsoft 365 under the pinned delegated
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

`PEGGY_WORKIQ_ACCOUNT` is required before first launch. Peggy stores that
identity in encrypted state and validates the authenticated WorkIQ profile
before structured reads, prompts, or writes.

The configured value may match either the mailbox address or user principal
name. Peggy resolves the canonical Outlook address after validation. A
different configured or authenticated identity is rejected.

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
2. **.NET 10 SDK** for development, or the .NET 10 Desktop Runtime for a
   framework-dependent published build.
3. **GitHub Copilot CLI** installed, authenticated, and available on `PATH`.
4. **Microsoft Scout with WorkIQ** installed and signed in.
5. Acceptance of the WorkIQ pre-release terms when required:

   ```powershell
   workiq accept-eula
   ```

6. `curl.exe` and `tar.exe` for the speech-model installer.
7. **Classic Outlook desktop** for Microsoft To Do task reads and actions.
8. A microphone and audio output device for voice features.

Peggy resolves WorkIQ in this order:

1. `PEGGY_WORKIQ_PATH`.
2. `workiq.exe` on `PATH`.
3. The Microsoft Scout installation.
4. `%USERPROFILE%\.scout\bin\workiq.cmd`.

## Quick start

Open PowerShell in the repository root.

### 1. Pin the Microsoft 365 account

Set the value for the current shell and persist it for future Windows sessions:

```powershell
$account = "you@example.com"
$env:PEGGY_WORKIQ_ACCOUNT = $account
[Environment]::SetEnvironmentVariable(
    "PEGGY_WORKIQ_ACCOUNT",
    $account,
    "User"
)
```

Use the account currently authenticated in Microsoft Scout. Do not point the
same encrypted state file at different accounts.

### 2. Install local speech models

```powershell
.\scripts\Install-SpeechModels.ps1
```

The installer downloads about 803 MiB of pinned official sherpa-onnx assets,
verifies every SHA-256 digest, and installs them under:

```text
%LOCALAPPDATA%\Peggy\models
```

Voice is optional. To run without the models, start Peggy and disable local
speech in Settings.

### 3. Build and run

```powershell
dotnet restore .\src\Peggy.Tray\Peggy.Tray.csproj
dotnet run --project .\src\Peggy.Tray\Peggy.Tray.csproj -c Release
```

The tray project automatically builds and packages the MCP server and speech
host beside the tray executable.

### 4. Add the optional speech-host firewall rule

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

Peggy runs as a single-instance tray application. Double-clicking the tray icon
opens the Alert Center.

### Tray menu

| Command | Behavior |
| --- | --- |
| **Open alerts** | Opens active Critical, Attention, and Awareness alerts |
| **Talk to Peggy** | Starts a local voice conversation |
| **Unload voice models** | Ends voice activity and releases native model memory |
| **Check now** | Runs a monitoring check even while scheduled sleep is active |
| **Pause monitoring** | Toggles manual monitoring pause |
| **AI credits today** | Shows recent local Copilot credit entries |
| **Settings** | Opens model, source, voice, schedule, startup, and watchlist settings |
| **Show or hide Peggy** | Toggles the desktop mascot |
| **Test animations** | Plays individual or all mascot states |
| **Quit Peggy** | Stops monitoring, closes Copilot, unloads speech, and exits |

### Alert Center

The Alert Center shows:

- The alert tier and source.
- A concise summary.
- Why Peggy flagged the item.
- The verified **Open in Outlook** or **Open in Teams** action.
- Resolve, dismiss, snooze, feedback, tier correction, context, and
  conversation controls.

When privacy masking is active, details remain hidden until
**Reveal details** is explicitly confirmed. A reveal expires after one minute.

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
| Pinned Copilot model | Defaults to `gpt-5.4`; changing it starts a fresh Copilot session |
| Additional Outlook folders | One Inbox-relative path per line; descendants are included |
| Enable local speech | Enables or disables the isolated English speech host |
| Speech model directory | Defaults to `%LOCALAPPDATA%\Peggy\models` |
| Voice | Bella, Luna, Rosie, or Kiki |
| CPU threads | 1, 2, 4, or 8 |
| Microphone gain | 4x, 8x, 12x, or 16x |
| Monitoring schedule | Local 24-hour wake and sleep times |
| Start at Windows sign-in | Manages the current user's `Run` registry entry |
| Pause monitoring | Persists a manual pause independently of scheduled sleep |
| Full-channel watchlist | Selects joined Teams channels for structured delta monitoring |

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
| `--live-copilot` | Exercises a live Copilot conversation |

Example:

```powershell
dotnet run --project .\src\Peggy.Core.Tests\Peggy.Core.Tests.csproj `
    -c Release `
    -- --live-unread
```

The automated action suite uses a fake WorkIQ transport and does not perform
external writes.

## Configuration reference

### User-facing overrides

| Variable | Purpose | Default |
| --- | --- | --- |
| `PEGGY_WORKIQ_ACCOUNT` | Required Microsoft 365 mailbox or UPN to pin | None |
| `PEGGY_WORKIQ_PATH` | Explicit `workiq.exe` or compatible wrapper path | Auto-detected |
| `PEGGY_MESSAGE_PROVIDER` | `workiq` or `mock` | `workiq` |
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
| `PEGGY_TEAMS_WATCHLIST` | JSON array of team/channel watch objects |

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
$env:PEGGY_WORKIQ_ACCOUNT = "test-account@example.invalid"
$env:PEGGY_MESSAGE_PROVIDER = "mock"
dotnet run --project .\src\Peggy.Tray\Peggy.Tray.csproj -c Release
```

Remove the process override to return to WorkIQ:

```powershell
Remove-Item Env:\PEGGY_MESSAGE_PROVIDER
```

## Troubleshooting

| Symptom | Resolution |
| --- | --- |
| `Set PEGGY_WORKIQ_ACCOUNT...` | Set the account before first launch and restart Peggy |
| Authenticated account does not match | Sign Microsoft Scout into the pinned identity; do not reuse the same state file for another account |
| WorkIQ not found | Install/sign in to Microsoft Scout or set `PEGGY_WORKIQ_PATH` |
| WorkIQ terms error | Review the terms and run `workiq accept-eula` |
| Monitoring interrupted | Open the Alert Center for remediation; Peggy keeps retrying after three consecutive failures |
| Speech models missing | Run `.\scripts\Install-SpeechModels.ps1` |
| Incomplete model directory | Rerun the installer with `-Force` |
| Speech host missing | Rebuild/publish `Peggy.Tray` or set `PEGGY_SPEECH_HOST_PATH` |
| Voice input is too quiet | Increase **Mic gain** in Settings |
| Voice is unavailable but monitoring works | Verify the model directory and packaged `Peggy.Speech.Host` output |
| Firewall script cannot find the host | Build in Release mode or pass the exact executable with `-HostPath` |
| Build reports a locked Peggy assembly | Use **Quit Peggy**, then rebuild |
| Settings are overridden | Remove the corresponding process environment override, especially `PEGGY_COPILOT_MODEL` |
| Need isolated test state | Set `PEGGY_STATE_PATH` to a new file before launch |
| Need diagnostics | Inspect `%TEMP%\peggy.log` or the path in `PEGGY_LOG_PATH` |

## Current limitations

- Windows x64 only.
- English-only local speech.
- Framework-dependent publish by default.
- Microsoft To Do actions target only the default `Tasks` list.
- Monitoring is polling-based, not real-time.
- Broad channel discovery depends on grounded WorkIQ prompt results.
- The outbound speech-host firewall rule is an optional administrator step.
- ONNX Runtime telemetry behavior ultimately depends on the native runtime
  packaged by sherpa-onnx. A private native build using `--no_telemetry` is the
  strongest removal option.

## Third-party software

Dependency and model attribution is documented in
[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt).
