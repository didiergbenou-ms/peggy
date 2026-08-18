# Pippa

Pippa is a friendly desktop assistant (a little bird) that watches your Outlook
email and Microsoft Teams and gives you a curated morning digest, powered by
GitHub Copilot.

This repo currently contains the **v1 vertical slice**: a custom Model Context
Protocol (MCP) server that exposes Pippa's message tools to the GitHub Copilot
CLI, so Copilot can pull messages and take actions on the user's behalf.

## Architecture (v1)

- **GitHub Copilot CLI (programmatic)** - the brain. Invoked headless via
  `copilot -p "..."`, wired to Pippa's tools.
- **Pippa MCP server** (`src/Pippa.Mcp`, this repo) - a .NET stdio MCP server
  exposing action tools: `list_unread`, `get_thread`, `mark_read`,
  `draft_reply`.
- **.NET tray host** (later phase) - 5-minute poll, pre-filter, Windows
  notifications, push-to-talk, and Pippa's animated mascot.

Live data will be pulled via Microsoft Graph delta queries (no cloud relay, no
webhooks in v1). The current build uses an in-memory **mock provider** so the
whole pipeline runs with zero setup.

## Build

```powershell
dotnet build src/Pippa.Mcp/Pippa.Mcp.csproj
```

## Tools

| Tool          | Description                                            |
| ------------- | ------------------------------------------------------ |
| `list_unread` | Unread messages across email + Teams, newest first     |
| `get_thread`  | Full conversation for a thread id                      |
| `mark_read`   | Mark a message as read                                 |
| `draft_reply` | Draft a reply (never sends)                            |
