# Loop Engineering

A Blazor (Razor components) web app that drives the **GitHub Copilot SDK** in a loop toward a goal:
each round a *worker* session does the work, then a fresh *evaluator* session checks the result
criteria, and everything is recorded in `{GoalName}_progress.md`.

## Solution layout

| Project | What it holds |
|---|---|
| `src/LoopEngineering.Core` | Loop engine: `LoopRunner`, Copilot SDK agent, prompts, evaluator parsing, progress file |
| `src/LoopEngineering.Web` | Razor UI: setup form, Start/Stop, live output, sub-agents, round review, progress file viewer |
| `tests/LoopEngineering.Tests` | xUnit tests for the loop engine (fake agent, no Copilot calls) |

Open `LoopEngineering.sln` in Visual Studio 2022 (17.12+, .NET 9 SDK), set **LoopEngineering.Web**
as startup project and press F5. The app runs at http://localhost:5173.

## Token

The token comes from **one environment variable you name** in the UI (default `COPILOT_LOOP_TOKEN`).

```powershell
[Environment]::SetEnvironmentVariable("COPILOT_LOOP_TOKEN", "<github token with Copilot access>", "User")
```

The SDK is never allowed to look for credentials itself:

- the token is passed explicitly via `CopilotClientOptions.GitHubToken`;
- `UseLoggedInUser = false`, so stored OAuth / `gh` CLI logins are ignored;
- `GH_TOKEN`, `GITHUB_TOKEN`, `COPILOT_GITHUB_TOKEN` (and the enterprise variants) are removed from
  the environment of the spawned Copilot runtime;
- config discovery (`~/.copilot`, repo instructions, MCP) is off unless you tick the checkbox.

The token value is never shown in the UI or saved to `App_Data/last-config.json`.

## Goal and working folders

- **Goal description** has three sources:
  - **Text**: typed in the page.
  - **File**: a file picked with *Browse…*, re-read every round so you can refine it mid-run.
  - **GitHub issues**: one link per line (`https://github.com/owner/repo/issues/123` or `owner/repo#123`,
    GitHub Enterprise links work too). Title, description, labels and the 30 most recent comments are
    fetched through the GitHub REST API with the same token when the run starts. *Check issues* tests
    the links before you start. Private repos need the token to have repository access to them.
- **Working folders**: *+ Add Working Folder* opens a folder browser (it shows which folders are git
  repos). Add several when a change spans repos. The first is the **primary** folder (the session's
  working directory); the others are granted to the agent as additional directories, and every
  prompt lists them all. Use ↑ to make another folder primary.
- **Progress file folder** (optional) keeps `{GoalName}_progress.md` out of your repos; by default it
  goes into the primary folder.

## Model

The **Model** dropdown lists the models your account can use (from the SDK's `ListModelsAsync`,
cached 15 minutes, ↻ to reload), each with its price as *input / output AI credits per 1M tokens*.
Models blocked by policy are shown but disabled. **Reasoning effort** only offers the levels the
chosen model supports. A saved model that is no longer available is flagged and blocks Start. If the
list can't be loaded (for example the token variable isn't set), the field falls back to free text.

## How a round works

1. Worker prompt = goal (text, file or fetched issues) + working folders + result criteria + the progress file so far (+ extra instructions).
   It runs in a new session, optionally as a chosen custom agent, with your skill / agent directories.
2. Evaluator prompt = goal + criteria + worker report. A new session inspects the working directory
   and replies with JSON `{criteriaMet, score, reasoning, nextSteps}`.
3. The round is appended to `{GoalName}_progress.md` in the working directory.
4. The loop stops when criteria are met, max loops is reached, the user presses **Stop**,
   or too many rounds fail in a row.

The criteria file (if you use one) is re-read every round, so you can tighten it while the loop runs.

## Skills and agents

- **Skill directories**: folders whose sub folders each contain a `SKILL.md`.
- **Agent directories**: folders with `*.agent.md` agent definitions.
- **Inline custom agents**: name, description, prompt and skills to preload, entered in the UI.
- **Run worker as agent**: activates one of those agents for the worker turn. The evaluator always
  runs as the default agent.

Sub-agents the model launches show up live under *Running sub-agents* and per round in the *Rounds* tab.

## Usage and quota

- **Account quota (header):** premium requests used / included, % left and reset date, read via the
  SDK's account RPC (`client.Rpc.Account.GetQuotaAsync`). Hover for all buckets (chat, completions…).
  It loads on page open, refreshes after each round (at most every 20 s), or on ↻. A quota query is
  not an LLM call and costs nothing. Note that Copilot accounts are metered in *premium requests*,
  not tokens, so this is what the account-level number shows.
- **Token counts (per round / per run):** summed from the SDK's `assistant.usage` events, including
  sub-agent calls: input, output, cache-read and reasoning tokens and API calls. Premium requests are
  counted the way GitHub bills them, per user prompt × model multiplier (tool-loop follow-ups and
  sub-agent calls are not billed separately), read from the runtime's per-session usage metrics. Shown in the *Tokens (run)* card, the round list, the round
  details, and written into the progress file.

- **Credit-based accounts:** for accounts on token-based billing the header shows **Credits** (read
  through a session's quota RPC, which reports the budget in `ai_credits`) and each run records the
  AI credits it was charged (`TotalNanoAiu` from the session usage metrics ÷ 10⁹).
- **Reporting delay:** GitHub's account-level credit figure can lag actual usage by about an hour and
  read 0 in the meantime. While it reads 0, the header shows **Credits (this app)**: the credits this
  app has spent this month, kept in `App_Data/credit-ledger.json` (resets each UTC month). As soon as
  GitHub reports a real number, the account-wide figure is shown again.
- **Background refresh:** the header quota is re-read every `Quota:RefreshSeconds` (default 60) in
  `appsettings.json`, in addition to after each round and on ↻.

Both rely on SDK members marked experimental (`GHCP001`), so they may change in future SDK versions.
