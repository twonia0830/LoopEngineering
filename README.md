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

## Backends: GitHub Copilot, Claude Code or Antigravity

Each role (worker, evaluator, AGENTS.md writer) runs on its own backend, for example Copilot builds
and Claude reviews. That spreads usage over two subscriptions and gives the evaluator a different
model family.

| | GitHub Copilot | Claude Code | Antigravity (Google) |
|---|---|---|---|
| Runs through | Copilot SDK (`GitHub.Copilot.SDK`) | Claude Code CLI, headless (`claude -p`) | Antigravity CLI, headless (`agy`, stream-json in and out) |
| Billed to | Copilot plan (AI credits / premium requests) | Claude subscription (reported cost shown as an API-equivalent estimate) | Google AI Pro / Ultra plan (tokens shown, no price) |
| Credential | token from the variable named in the form (default `COPILOT_LOOP_TOKEN`) | `claude setup-token` token from the variable named in the form (default `CLAUDE_LOOP_TOKEN`) | agy's own saved Google sign-in (agy has no token option; run `agy` once to sign in) |
| Models | listed live from your Copilot account | Opus 5.5, Sonnet 5.5, Haiku 5.5, Fable 5.1 (if your plan has it) | listed live with `agy models`; the effort is part of the variant (e.g. `gemini-4-argon-high`) |
| Edits blocked for evaluator / AGENTS.md writer | prompt only | yes (edit tools disabled) | prompt only |

Claude Code gets the token through `CLAUDE_CODE_OAUTH_TOKEN` only; `ANTHROPIC_API_KEY` and other
credentials are removed from its environment, and `CLAUDE_CONFIG_DIR` points at an app-owned folder
(`%LOCALAPPDATA%\LoopEngineering\claude-config`), so your own Claude Code login, global CLAUDE.md,
plugins, hooks and MCP servers stay out of the loop. The evaluator and the AGENTS.md writer run with
the edit tools disabled. Custom agents and skill folders are Copilot features and do not apply to
Claude Code roles. An empty evaluator model only inherits the worker's model when both use the same
backend.

Antigravity runs with an app-owned home folder (`%LOCALAPPDATA%\LoopEngineering\antigravity-home`) so
your `~/.gemini` setup (GEMINI.md, plugins, MCP servers, settings) stays out of the loop; the saved
sign-in lives in the Windows credential store and keeps working. The NuGet cache, dotnet home and
global git config still point at your real profile so commands the agent runs behave normally. A
`GEMINI_API_KEY` is removed from its environment so runs use the plan, not API billing. Isolation can
be turned off in the form.

To create the Claude token, run `claude setup-token`, copy the `sk-ant-oat01-…` value and store it
without leaving it in your shell history:

```powershell
[Environment]::SetEnvironmentVariable("CLAUDE_LOOP_TOKEN", (Get-Clipboard).Trim(), "User")
```

## Model

The **Model** dropdown lists the models your account can use (from the SDK's `ListModelsAsync`,
cached 15 minutes, ↻ to reload), each with its price as *input / output AI credits per 1M tokens*.
Models blocked by policy are shown but disabled. **Reasoning effort** only offers the levels the
chosen model supports. A saved model that is no longer available is flagged and blocks Start. If the
list can't be loaded (for example the token variable isn't set), the field falls back to free text.

### Worker and evaluator models

The worker and the evaluator each have their own model and reasoning effort. The evaluator defaults
to **Same as worker**; picking a different model, ideally from another provider, makes it less likely
to share the worker's blind spots or favour its own style of output. When the evaluator uses its own
model and its effort is left empty, that model's default effort applies. Each round records which
models served the worker and the evaluator and what each cost, in the UI and in the progress file.

### Context length

There is no context-length setting: Copilot sessions compact automatically (in the background from
80% of the model's context window, blocking at 95%). Each round also starts fresh sessions, so a
prompt only carries the goal, criteria and the tail of the progress file.

## Repo guide (AGENTS.md)

Most work happens in existing repos, and every round starts fresh sessions, so each working folder
gets an `AGENTS.md`: a short guide to the repo (purpose, layout, build/run/test commands, conventions,
gotchas) for AI agents and people alike.

1. **Before round 1**, every working folder without an `AGENTS.md` gets one. A read-only session
   explores the folder and replies with the guide; the app writes the file (the session never edits
   the repo). An existing `AGENTS.md` is used as it is.
2. **Every round**, the worker and evaluator prompts include each folder's `AGENTS.md` (and
   `.github/copilot-instructions.md` if present), re-read from disk. The worker is told not to edit it.
3. **When the criteria are met** (optional), a short session per folder adds durable knowledge learned
   during the run, such as new commands, projects or gotchas, or replies `NO_CHANGES`. Task history
   stays in the progress file. Updates that would push the guide past 12,000 characters are skipped.

**Starting point file (optional):** point it at a short file such as `flow.md` that names the entry
points and main flows. The guide writer reads it first and traces the code from there instead of
guessing where to start, which helps most with large or multi-service systems. **Regenerate** rewrites
existing guides (for example after adding a starting point file); the old guide is passed along so
correct facts survive. It applies to the next run only and switches itself off after Start.

The guide is written by the evaluator's model unless you pick another. Its cost shows in the run
total, the log and the progress file. `AGENTS.md` is an ordinary repo file: review and commit it so
your team, and tools such as Copilot in VS Code, benefit too. A failed guide step is logged and the
run continues without it.

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
