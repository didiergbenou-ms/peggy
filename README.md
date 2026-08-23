# Peggy

Peggy is a friendly animated bird who watches Outlook email and Microsoft Teams
and alerts you only when something deserves attention, powered by GitHub
Copilot. Her original perch, listening, thinking, and new-digest animations are
preserved. Scheduled sleep and wake extend that motion set without scaling or
distorting her silhouette.

This repo contains the **v1 live loop**: a Windows tray host, an animated mascot,
and a custom Model Context Protocol (MCP) server that lets GitHub Copilot inspect
the user's Microsoft 365 messages.

## Architecture (v1)

- **GitHub Copilot** - strict monitoring classification remains isolated in
  headless `copilot -p` runs. Voice conversations use one warmed, persistent
  official .NET Copilot SDK session with streaming text deltas and the same
  bounded assistant tools.
- **Peggy MCP server** (`src/Peggy.Mcp`, this repo) - a .NET stdio MCP server
  exposing bounded read tools plus proposal-only `prepare_action`.
- **Peggy tray host** (`src/Peggy.Tray`) - hybrid 5-minute pre-filter,
  one resumable Copilot session on a pinned model, AI-credit history, speech
  bubbles, and Peggy's animated states.
- **Peggy speech host** (`src/Peggy.Speech.Host`) - isolated English-only
  sherpa-onnx process using NeMo FastConformer for live partial text, Parakeet
  TDT for the accurate final transcript, Silero VAD with 450 ms endpointing,
  Kitten TTS Nano for real-time CPU speech, and NAudio WASAPI. It stays warm throughout a conversation
  and is terminated when the conversation ends so all native memory is
  released.
- **WorkIQ provider** (`src/Peggy.Core`) - prompt-guided Teams discovery,
  structured Microsoft 365 verification, and confirmation-gated writes through
  the locally installed WorkIQ CLI/MCP server.

On first launch, set `PEGGY_WORKIQ_ACCOUNT` to the Microsoft 365 account Peggy
should use. Peggy pins that account inside DPAPI-encrypted local state and
rejects a different configured or authenticated account. Outlook monitoring
covers Inbox plus `Inbox/Cases/Active` and all descendants by default.
The assistant compares each Teams chat's last-message preview with the user's
`lastMessageReadDateTime`, then fetches bounded history only for unread chats.
Incremental monitoring enumerates the user's chats and requests messages by
`lastModifiedDateTime`, so edits to older messages are not missed. Message
activity is still based on `createdDateTime` or a real `lastEditedDateTime`;
reaction, importance, and other metadata-only changes to old messages do not
re-enter the monitoring window. Teams source links must identify a specific
`/l/message/` item, never only a generic chat.
Broad Teams channel discovery uses a strict WorkIQ prompt over the five-minute
window. Only explicitly watchlisted channels use Graph delta scans, avoiding a
full crawl of every joined channel.
Each process startup begins a fresh five-minute monitoring window and ignores
older downtime backlog while retaining known channel participation.
Live Microsoft 365 writes are executed only after the assistant shows and speaks the
exact action preview. Non-destructive actions require click or immediate voice
approval. Destructive actions require a separate exact typed phrase. Every
attempt is locally audited and the result is verified before the assistant
reports success. Every alert labels why Peggy flagged it and identifies Outlook
or Teams as its source. Prompt discovery must resolve and verify a cited source
URL before an alert can be created; unresolved candidates are withheld and
retried with a visible monitoring warning. **Open in Outlook/Teams** therefore
always launches the saved source. Context stays hidden while privacy masking is
active unless the user explicitly confirms **Reveal details**, which reveals
the card for one minute. No cloud relay or webhook is needed.

## Prerequisites

1. Microsoft Scout with WorkIQ installed and signed in.
2. GitHub Copilot CLI available on `PATH`.
3. .NET 10 SDK.
4. Set the Microsoft 365 account before first launch:

   ```powershell
   $env:PEGGY_WORKIQ_ACCOUNT = "you@example.com"
   ```

5. Local English speech models installed with:

   ```powershell
   .\scripts\Install-SpeechModels.ps1
   ```

   The installer downloads only pinned official sherpa-onnx release assets,
   verifies their SHA-256 digests, and places about 803 MiB under
   `%LOCALAPPDATA%\Peggy\models`.

## Build

```powershell
dotnet build src/Peggy.Tray/Peggy.Tray.csproj
```

The tray project also builds and packages the complete MCP server it launches
for Copilot and the isolated speech host. `dotnet publish` places them under
`Peggy.Mcp` and `Peggy.Speech.Host` in the publish output.

The assistant never falls back to `System.Speech`, Windows Speech Recognizer, or an
installed Windows voice. If the local models or speech host are unavailable,
voice is disabled with an explicit error while the rest of the assistant continues.
Every recognized turn is shown in an editable **I heard...** card before the
text can be sent to Copilot or WorkIQ. Ordinary turns auto-send after a visible
one-second undo window; editing or **Cancel auto-send** pauses the timer.
Action-approval transcripts always require explicit review. Live partial text
appears locally while speaking and is never sent; Parakeet's final transcript
is the only version eligible for auto-send.
The persistent Copilot session is warmed at app startup. As response deltas
arrive, complete sentences are queued to Kitten, so Peggy can begin speaking
before Copilot finishes the full answer. Voice answers default to one short
sentence. Pressing the microphone during Thinking or Speaking aborts
that turn and returns Peggy to listening.
Use **Unload voice models** from the tray at any time; ending a conversation
does the same automatically.

Automatic monitoring is enabled from 07:00 until 19:00 local time by default.
Outside that window Peggy uses a sleeping animation and
monitoring pauses without changing the manual pause setting. The wake and sleep
times can be changed or disabled in Settings. **Check now** and voice
conversations remain available while Peggy is sleeping. At wake,
monitoring resumes from a fresh five-minute lookback rather than processing the
whole overnight backlog.

For defense in depth, run the following once from an elevated PowerShell
window after building or installing the application:

```powershell
.\scripts\Protect-SpeechHost.ps1
```

This creates a verified outbound firewall block for only
`peggy-speech-host.exe`. The host has no network features, raw audio never
leaves that process, and only the approved transcript can later be sent to
Copilot or WorkIQ. A private sherpa-onnx build linked against ONNX Runtime
built with `--no_telemetry` remains the strongest way to remove ONNX Runtime
telemetry events at build time; the managed `OrtEnv` switch cannot control the
environment embedded inside sherpa-onnx.
The script detects standard, RID-specific, and publish Release layouts. Pass
`-HostPath` when protecting a host installed elsewhere.

WorkIQ is the default provider. To run with sample data instead:

```powershell
$env:PEGGY_MESSAGE_PROVIDER = "mock"
dotnet run --project src\Peggy.Tray\Peggy.Tray.csproj
```

Optional settings:

| Variable | Purpose |
| --- | --- |
| `PEGGY_MESSAGE_PROVIDER` | `workiq` (default) or `mock` |
| `PEGGY_STATE_PATH` | Optional encrypted-state path override for isolated testing |
| `PEGGY_WORKIQ_PATH` | Explicit path to `workiq.exe` or Scout's `workiq.cmd` |
| `PEGGY_WORKIQ_ACCOUNT` | Microsoft 365 account to pin on first launch; later values must match encrypted state |
| `PEGGY_OUTLOOK_FOLDERS` | Additional Inbox-relative folder paths, separated by semicolons; descendants are included |
| `PEGGY_COPILOT_MODEL` | Override the saved Copilot model for the process; defaults to `gpt-5.4` |
| `PEGGY_SPEECH_MODELS` | Override the local English speech model directory |
| `PEGGY_SPEECH_HOST_PATH` | Override the isolated speech-host executable path |
| `PEGGY_LOG_PATH` | Optional diagnostic log path override for isolated testing |

## Tools

| Tool          | Description                                            |
| ------------- | ------------------------------------------------------ |
| `list_unread` | Unread messages across email + Teams, newest first |
| `get_thread` | Bounded conversation context for a thread id |
| `list_upcoming_events` | Bounded upcoming calendar events |
| `list_tasks` | Bounded Microsoft To Do tasks |
| `ask_workiq` | Prompt-guided discovery across Microsoft 365 |
| `get_turn_context` | Private per-turn context supplied by the tray |
| `prepare_action` | Validate and stage one action for explicit approval |

`prepare_action` never performs a write. Only the tray host can execute the
staged action after the confirmation requirements above are satisfied.
