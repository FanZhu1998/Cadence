# Cadence

An AI quota monitor for the Windows notification area, for Claude and Codex.

It answers one question the providers' own dashboards do not: **not "how much have I used" but
"am I going to run out, and when".**

```
┌──────────────────────────────────────────┐
│  Cadence                              ⟳  │
├──────────────────────────────────────────┤
│  CL  Claude   Max 5x            just now │
│                          you@example.com │
│  Session (5h)                        10% │
│  ▓▓░░╱╱╱╱░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  │
│  resets in 4 hours              29–38%   │
│  Well inside your limit — on track for   │
│  about 33% by the reset.                 │
│                                          │
│  Fable (7d)                          76% │
│  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱  │
│  resets Wed 11:00              81–100%   │
│  Roughly a 44% chance of running out     │
│  before Wed 11:00.                       │
└──────────────────────────────────────────┘
```

---

## Privacy, first

Cadence reads your AI credentials. That deserves a plain statement before anything else.

- **It never asks you to sign in.** It reuses the sign-ins Claude Code and Codex already saved on
  your machine, and asks a running Antigravity over loopback. If a tool is not signed in, its card
  says which tool to run: you sign in there, with the provider, never in Cadence.
- **It stores no credentials of its own.** Tokens are read fresh on each refresh, used, and dropped.
  Cadence never copies them into its settings, never refreshes them, and never writes to the
  provider tools' files, so it cannot break your Claude Code or Codex sign-in. It does not even keep
  the refresh tokens those files hold. Settings has no field for a password, key or token.
- **Each token goes only to its own provider**, over HTTPS: Claude's to `api.anthropic.com`,
  Codex's to `chatgpt.com`, and Antigravity's never leaves `127.0.0.1`. Beyond those, Cadence
  contacts only the providers' public status pages and, for installed copies, this repository's
  GitHub releases to look for an update (Settings → General → Updates turns that off). There is no
  telemetry, no crash reporting, no analytics.
- Every string bound for a log file, or for the diagnostics bundle, passes through a redaction
  pass first. There are tests asserting that each token shape is caught.
- **Nothing secret lives in this repository.** Test fixtures use obviously fake tokens, `.gitignore`
  excludes credential files, and `RepositoryHygieneTests` fails the build if a real-looking key,
  token or credential file appears.

Verify all of that yourself: `Cost/` and `Providers/` are the only code that touches your data, and
`Diagnostics/Redaction.cs` is forty lines.

---

## What it does

**Three surfaces, layered.** Windows has no menu bar — one 16×16 tray icon with no text label — so
the information is spread across:

| Surface | What it carries |
|---|---|
| **Tray icon** | One glance: two bars, a number, or a ring. Colour tracks pressure, and the tooltip carries the long-form text. |
| **Flyout** | The real UI. One card per provider, a bar per quota window, and one plain sentence of forecast under each. |
| **HUD** | An optional always-on-top strip, one line per provider for its current session. This is where the menu-bar vocabulary lives. |
| **CLI** | `cadence usage --json` for your own status bar, plus cost and backtest reporting. |

### The HUD

```
● CLD  76%  ↑14  →89%
● DEX  54%  ↑44  out 20:25
```

For each provider's current session: percentage, pace against even consumption, and the outlook — an exhaustion time once that is the
likelier outcome, otherwise the projection. Weekly and per-model limits stay in the
panel, where there is room to explain them. Turn it on in Settings → General.

Drag it anywhere; it snaps to work-area edges and remembers where you put it **per monitor
arrangement**, so docking and undocking does not mean dragging it back twice a day. A remembered
position on a monitor that has since been unplugged is detected and reset rather than leaving the
strip stranded off-screen.

It is click-through, so it never swallows a click meant for the window behind it, and becomes
interactive while your cursor is over it. That is genuinely circular — a window with
`WS_EX_TRANSPARENT` receives no mouse messages, so it cannot be told it is hovered — and the way
out is a 120 ms cursor poll rather than a low-level mouse hook, which would look like a keylogger
to antivirus software. If you would rather it never intercept anything, **Lock it in place** keeps
it permanently transparent at the cost of being able to drag it.

**A real forecasting engine**, which is the point of the app:

- projected usage at reset, with a P10–P90 band
- probability of running out before the window resets
- "runs out at ~17:05", once that becomes the likelier outcome
- pace against even consumption, drawn as a tick on the bar
- a sustainable rate: what you could spend and still land exactly at 100%

**Local cost accounting** from the JSONL transcripts Claude Code and Codex already write, so token
and spend figures keep working even when a provider endpoint is down.

---

## Install

**Run `CadenceApp-win-x64-Setup.exe`.** It installs Cadence for your Windows account only, so there
is no admin prompt, and starts it when it finishes. You get:

- Cadence in `%LOCALAPPDATA%\CadenceApp`, at a path that stays the same across updates
- Start menu and desktop shortcuts
- an uninstall entry in **Settings → Apps → Installed apps**
- updates: new versions download in the background from this repository's GitHub releases and
  install the next time Cadence starts. **Settings → General → Updates** has **Check now**, and
  **Restart now** once a version is waiting.

Uninstalling removes the app, its shortcuts and its start-at-sign-in entry. Your settings
(`%APPDATA%\Cadence`) and usage history (`%LOCALAPPDATA%\Cadence`) stay, so a reinstall picks up
where you left off.

Rather not install? `CadenceApp-win-x64-Portable.zip` runs from any folder and updates itself the
same way. A bare `Cadence.exe` straight from a build also runs, but never updates.

## Launch

After Setup, and the first time you run Cadence at all, a welcome window opens in the middle of the
screen. Cadence itself lives in the system tray beside the clock, and Windows 11 files new tray icons
under the `^` overflow arrow, so without the welcome it would look like nothing happened. It offers
**Start Cadence when I sign in**, ticked so the usual path is a single click. A copy that was not
installed also offers **Add Cadence to the Start menu**, since no Setup made one.

Nothing is written to your machine until you press **Get started**, and **Skip** changes nothing. To
keep the icon visible afterwards, open the `^` arrow and drag Cadence onto the taskbar.

Launching Cadence again, from the Start menu, the desktop or the exe, brings the running copy's
panel forward instead of starting a second one. One Cadence runs per Windows sign-in.

### Building it

With the .NET 10 SDK:

```powershell
.\build\publish.ps1                  # dist\win-x64\Cadence.exe, plus the installer in dist\releases
.\build\publish.ps1 -Arch win-arm64
.\build\publish.ps1 -Version 0.2.0   # each release needs a higher version than the last
.\build\publish.ps1 -SkipInstaller   # the exe only, faster
```

Packaging uses Velopack's `vpk` tool, pinned in `.config/dotnet-tools.json`; the script restores it.
The CLI is published beside the app as `dist\<arch>\cli\cadence.exe`, in its own folder because
Windows filenames are case-insensitive and `Cadence.exe` and `cadence.exe` would overwrite each other. It is not part of the
installer, which carries only the tray app: the CLI brings its own copy of the runtime, and would
double the download for a tool most people never run.

For development, `dotnet run --project src/Cadence.App`. Since only one Cadence runs per sign-in, a
copy already in the tray treats that as a relaunch and just shows its panel, so quit it first.

### Shipping a release

Installed copies look for updates in this repository's GitHub releases, which must be public for
them to see without a token. Each architecture is its own channel.

```powershell
# Fetch the previous release first, so the new one includes a small delta update.
dotnet vpk download github --repoUrl https://github.com/FanZhu1998/Cadence --channel win-x64 -o dist\releases
.\build\publish.ps1 -Version 0.2.0

# Read the token without echoing it or saving it in PowerShell's history. Use a fine-grained token
# limited to this repository, with Contents: read and write, and a short expiry.
$env:GITHUB_TOKEN = [Net.NetworkCredential]::new('', (Read-Host 'GitHub token' -AsSecureString)).Password
dotnet vpk upload github --repoUrl https://github.com/FanZhu1998/Cadence --channel win-x64 -o dist\releases `
    --publish --releaseName "Cadence 0.2.0" --tag v0.2.0 --token $env:GITHUB_TOKEN
Remove-Item Env:GITHUB_TOKEN
```

To try an update before publishing it, quit Cadence and start the installed copy with
`CADENCE_UPDATE_FEED` set to a local folder such as `dist\releases`.

The exe and installer are unsigned, so a copy downloaded from the internet gets a SmartScreen
warning (**More info → Run anyway**). Code signing removes it and comes before any wider release.

---

## Providers

Cadence finds credentials that already exist on disk. Each provider walks an ordered chain of
strategies and falls back gracefully; Settings → Providers → **Test connection** shows exactly which
link ran, where it stopped, and what came back — with tokens stripped.

### Claude

Reads the OAuth token Claude Code wrote to `%USERPROFILE%\.claude\.credentials.json`, then calls
`api.anthropic.com/api/oauth/usage` and `/profile`.

Cadence deliberately **does not refresh that token**. Claude Code owns the file and rewrites it on
its own schedule; racing it is a real bug class. If the token has expired, run Claude Code once.

A token minted without the `user:profile` scope authenticates and then returns 403. Cadence detects
that at read time and says "re-authenticate Claude Code" rather than surfacing a bare 403.

### Codex

Reads `%USERPROFILE%\.codex\auth.json` (or `%CODEX_HOME%`) and calls
`chatgpt.com/backend-api/wham/usage`. If `config.toml` sets `cli_auth_credentials_store = "keyring"`
the tokens are in Windows Credential Manager instead, and Cadence says so with the one-line fix
rather than reporting a working setup as broken.

Falls back to driving `codex app-server` over JSON-RPC — read-only, untrusted, every stage bounded,
and the child placed in a kill-on-close Job Object so it cannot outlive Cadence.

### Gemini

Cadence offers two modes and defaults to the one that touches no Google credentials:

| Mode | Who it is for |
|---|---|
| **Antigravity** (default) | Queries the local Antigravity language server over loopback. No Google credentials reach Cadence. |
| **API key** | Local accounting against published tier limits. Less magical; still working in two years. |

Loopback TLS accepts the language server's self-signed certificate **only for 127.0.0.1**, never as
a global handler.

---

## The forecast, and why you should distrust it

Every projection is a claim about the future, so Cadence is built to be checked.

It refuses to speak when it should not: below 3% of the window elapsed, below three samples, or
when polling gaps are large enough that any band would be fiction. Unknown usage renders as an en
dash, never as zero — a provider that reports no number is not a provider reporting zero.

### Quota consumption as a marked point process

Quota does not drain smoothly. It moves in jumps: each prompt, tool call or agent turn happens at
some moment and costs some amount. That is a *marked point process*, a sequence of event times
$t_1 < t_2 < \dots$, each carrying a mark $m_i > 0$, the share of the window that event consumed.
The percentage a provider reports is the running sum of the marks since the last reset:

```math
U(t) = \sum_{t_i \le t} m_i
```

Cadence never sees the individual events. It polls $U(t)$, rounded, every few minutes, so what it
observes is the jumps summed over each polling interval: the *increments* the engine works with.
Where a window ends up depends on how often events arrive, the intensity $\lambda(t)$, and how
large they are, the distribution of the marks. The estimators below measure the product of the
two, a burn rate in percent per hour, and when it is switched on; the band comes from how uneven
the jumps are.

Every forecast works on one *epoch*, the stretch between two resets. A reset shows up as usage
falling by more than two points, the provider naming a later reset time, or the clock passing the
reset it last named. Increments never span a boundary, because differencing across a reset would
produce a large negative "burn rate".

### Poisson counts, and why the band is a Gamma

The simplest model of events arriving independently at rate $\lambda$ is a Poisson process: the
number of events in an interval of length $T$ is Poisson-distributed.

```math
N(T) \sim \mathrm{Poisson}(\lambda T), \qquad P\big(N(T) = n\big) = \frac{(\lambda T)^n e^{-\lambda T}}{n!}
```

With independent marks on top, the usage still to come before the reset is a *compound Poisson*
sum, and both of its first two moments grow linearly with the time left:

```math
\mathbb{E}[\Delta U] = \lambda T \, \mathbb{E}[M], \qquad \mathrm{Var}[\Delta U] = \lambda T \, \mathbb{E}[M^2]
```

That is the property the engine is built on: estimate a rate and a variance per hour, then scale
both by the hours left. Cadence takes that shape from the model but not the Poisson assumption
that the variance is fixed by the mean. Real work is burstier than Poisson, because requests
cluster into sessions, so the variance is measured instead: increments are spread across
equal-length bins (15 minutes for a 5-hour session, 6 hours for a weekly window, 24 hours for a
monthly one) and the spread between bins is scaled to the horizon.

A sum of positive jumps is non-negative and right-skewed, so the band is a Gamma distribution
fitted to that mean and variance, not a Normal. A Normal hands back negative P10s on bursty work,
which render as "projected -4% at reset". The Gamma gives the P10/P50/P90 band at reset, the
probability of reaching 100% first, and the earliest plausible exhaustion time. A band never
collapses to a point: when the measured spread is tiny it keeps a 15% coefficient of variation,
and when there is no spread to measure yet it falls back to a deliberately wide 80%.

### Four estimators, because each one fails differently

Every estimate of the burn rate is wrong in a predictable way. Cadence runs four, each covering a
blind spot of the others.

**E1, even pace.** Usage so far divided by time elapsed, carried forward over the hours left:

```math
\hat r_1 = \frac{U(t)}{t - t_0}
```

It has no parameters and cannot overreact. It lands above 100% exactly when used% is ahead of
elapsed%, which is the pace check every quota invites: 30% used a quarter of the way through
finishes near 120%. Its blind spot is change. It averages the idle night into the busy morning,
and it misses someone who has just sped up or stopped.

**E2, EWMA.** An exponentially weighted moving average of the rate in each interval, so recent
behaviour counts for more. The weight depends on the time each interval covers, not on how many
samples there are, because adaptive polling makes the intervals irregular:

```math
\hat r_2 \leftarrow \alpha_i r_i + (1 - \alpha_i)\,\hat r_2, \qquad \alpha_i = 1 - e^{-\Delta t_i / \tau}
```

The time constant $\tau$ follows from a half-life matched to the window: 25 minutes for a 5-hour
session, 8 hours for a weekly window, 24 hours for a monthly one. Its blind spot is the pause:
after a quiet hour it concludes you have stopped for good.

**E3, active hours.** How fast quota burns while you are actually working: the usage in intervals
that saw usage, divided by the time those intervals cover. Only intervals of 30 minutes or less
count, because a longer gap does not say how much of it was work. The result is a rate per
*active* hour, so it is carried over the active hours left, not the wall-clock hours. Projected
over wall-clock time it would assume you never stop, and roughly triple the forecast for a normal
working day. Inside a window, the active hours left are the wall-clock hours left times the duty
cycle so far, the share of elapsed time you were working. With a steady rhythm that makes E3 agree
with E1 exactly; the two diverge only when the rhythm changes, which is the signal worth having.

**E4, the hour-of-week profile.** For weekly and monthly windows, the duty cycle so far is a poor
guide to the days ahead: a window that resets on Monday has a weekend in it. E4 learns when you
work from past epochs, in 168 buckets, one per hour of the week in your local time, each holding
the share of observed time that saw usage. The active hours left before the reset are then the
sum, over every hour left, of the chance you are working in it:

```math
H_{\text{active}} = \sum_{h \in \text{hours left}} P\big(\text{active} \mid \text{hour of week}(h)\big)
```

In point-process terms, the intensity is allowed to vary with the hour of the week instead of
staying constant. E4 does not estimate a rate; it supplies the horizon that E3's rate is
multiplied by. It switches on only after 48 hours of observation, shrinks each bucket toward your
overall activity rate until that bucket has a few hours of evidence of its own, and stays off for
5-hour sessions, where a weekly rhythm is noise. It replaces the "work days per week" setting
other tools ask you to fill in, and Settings → Forecast can still override it with fixed working
hours per day.

### Shrinking toward even pace, not toward zero

The two responsive projections, E2 and E3 on its E4 horizon, are averaged rather than maxed. On a
front-loaded window E2 says you have stopped and E3 says you burn fast when working. Both are
true, and taking the larger would fire an exhaustion alarm at someone who has already put the work
down. That average is then blended with E1 by a credibility weight that grows with the evidence:

```math
\hat U_{\text{reset}} = U(t) + \theta \, \Delta\hat U_{\text{E2,E3}} + (1 - \theta) \, \Delta\hat U_{\text{E1}}, \qquad \theta = \frac{n}{n + k}, \quad k = 8
```

Here $n$ is the number of increments in the current epoch. At eight increments the responsive
estimators carry half the weight; below that, even pace dominates. Each estimator is projected
over the horizon in its own units before blending, so a per-active-hour rate is never multiplied
by wall-clock hours.

The structural point is what the blend shrinks toward. Shrinking toward E1 rather than toward zero
means: absent evidence, assume the rest of the window looks like the part you have seen. Shrinking
toward zero would assume you are about to stop, the one assumption guaranteed to be wrong in the
cases that matter. Cold start therefore degrades to the provider's own pace logic, the even pace a
window defines by spreading its limit across its length, which is the right safe default.
Switching off the blended estimator in Settings → Forecast runs E1 alone.

### Why not ARIMA or gradient boosting

They are the obvious tools for forecasting a series. There are three reasons not to use them here.

**The effective sample size is tiny.** A 5-hour window polled every 2 minutes, already faster than
Cadence's adaptive default, gives about 150 rows. Most are flat, because the percentage is rounded
and the work comes in bursts; there may be 15 to 30 informative jumps. ARIMA would spend its
parameters on autocorrelation it cannot estimate from that, and gradient boosting needs far more
rows than exist to fit anything but noise. A few estimators with fixed, interpretable constants
have almost nothing to overfit.

**The process is non-stationary by construction.** Usage is bounded above at 100, hard-resets on a
schedule, and has structural breaks, which Cadence detects explicitly: every reset starts a new
epoch. ARIMA assumes a series that is stationary after differencing, and differencing across a
reset produces exactly the large negative jump that is not a burn rate; nothing in the model
knows about the ceiling. Tree ensembles cannot predict beyond the range of values they were
trained on, and a forecast to the reset is an extrapolation.

**The loss is asymmetric.** Being told you are fine and then hitting the limit mid-task costs far
more than an early warning that turns out cautious. Both methods minimise squared error by default
and return a single conditional mean, which is the wrong target. Cadence keeps the median honest
and puts the asymmetry where it belongs, in quantiles and a probability. It shows the P10-P90 band,
the chance of reaching 100% before the reset, and the earliest plausible exhaustion from the P90
edge, and it leads with "runs out at" as soon as exhaustion becomes more likely than not. Where it
has to guess, it guesses high: a window whose start is unknown is dated from the first sample,
which shortens the elapsed time and raises the burn rate.

### Check it against your own history

```powershell
cadence forecast backtest --window claude.weekly_all --days 30
```

That replays every stored sample, forecasting only from data available at the time, and scores the
result: MAE, pinball loss at P10/P50/P90 (the proper scoring rule for quantiles, and asymmetric by
design), band coverage against its 80% target, and a Brier score against the base rate. It prints
the blended estimator beside plain even-pace.

If the blend does not win on your data, it says so, and you should turn it off in Settings →
Forecast. A calibrated simple model beats an overfit clever one.

---

## CLI

```
cadence usage    [--provider claude|codex|gemini] [--json] [--verbose]
cadence sources  [--provider ...]        what credentials this machine offers
cadence cost     [--days 30] [--json]    local token/cost totals from session logs
cadence forecast backtest --window <id> [--provider claude] [--days 30]
cadence doctor                           paths, files and versions, redacted
```

`--verbose` prints the strategy chain and the redacted response body, which is the fastest way to
see an endpoint change shape.

---

## About the money figures

Cost is computed from your local transcripts at **public API list rates**. On a subscription plan
you are not billed any of it — the figure is "what this would have cost through the API", which is
worth knowing and would be a lie presented as a bill. The UI says so under the totals.

Prices live in `pricing.json`, never in code. Copy it to `%APPDATA%\Cadence\pricing.json` and edit
any model; your overrides win per model, so correcting one price does not mean restating the table.
Unpriced models are reported rather than silently counted as free.

---

## Known limits

- **Antigravity is built to spec but unverified against a live install** — it was not present on the
  development machine, so it is exercised against a fake loopback server in tests rather than the
  real language server. Treat the first run as a bug hunt.
- **HUD placement across mixed-DPI displays is approximate.** Work areas are converted using the
  strip's own monitor scale, which is exact on a uniform-DPI desktop. On a mixed-DPI setup snapping
  may engage slightly early or late; dragging and the saved position still work.
- **Toasts are balloon notifications, not modern Windows toasts.** `AppNotificationManager` needs
  package identity, which an unpackaged single-file exe does not have.
- **These are undocumented endpoints and they will break.** Golden-file tests catch a shape change
  immediately and each provider's parsing lives in one file, so a fix is usually a few lines.

## Footprint

An always-running tray app has to be cheap enough to forget about. Measured on the published build
after settling: **~36 MB working set**, GDI objects flat at 44, and the process drops to Windows 11
Efficiency Mode (EcoQoS, idle priority) whenever the flyout is closed, so it schedules on efficiency
cores and stays out of Task Manager's high-impact list.

Every replaced tray icon handle is destroyed explicitly. There is a soak test asserting that 10,000
icon updates leave the GDI object count flat, because leaking one handle per refresh exhausts the
10,000-object quota within a day and the only symptom is the icon silently disappearing.

---

## Design

The palette and type system are ported from [fan-zhu.com](https://fan-zhu.com): deep navy surfaces,
a single steel-blue accent in two tones, tracked monospace labels for structure, hairline rules
instead of boxes, and tabular figures throughout. Both themes are contrast-validated at source —
every ink and accent pair clears WCAG AA against its own surface.

The accent has two strengths and they swap roles between themes: the dominant tone belongs to the
forecast, the receded one to structural labels. `docs/design.md` records the tokens, the rules, and
the WPF-specific workarounds (letter-spacing, retemplated stock controls).

## Architecture

```
src/
  Cadence.Core/        no UI reference, ever — so tests and the CLI drive it headlessly
    Model/             UsageSnapshot, QuotaWindow, Forecast, settings
    Providers/         source chains for Claude, Codex, Gemini
    Credentials/       DPAPI store, well-known paths
    Forecast/          estimators, epochs, Gamma bands, backtester
    History/           SQLite repository
    Cost/              JSONL scanners and pricing
    Refresh/           store, cadence policy, coordinator
    Diagnostics/       redaction, Job Objects, support bundle
  Cadence.App/         WPF: tray renderer, flyout, settings, notifications
  Cadence.Cli/         cadence.exe
tests/                 211 tests: golden files, synthetic epochs, a GDI soak test
```

Data flows one way. The refresh coordinator is the only thing that calls a provider; everything
downstream reads from `UsageStore` and never reaches back.

---

## Contributing

`docs/recon.md` records the payload shapes captured from live installs on 2026-09-13, including
three places where they differ from published documentation. Read it before changing a parser.

Run `dotnet test` before sending anything. The golden-file tests are the point: when a provider
changes shape, add a fixture and the diff tells you exactly what moved.

---

## Credit and licence

MIT. The design owes its shape to [CodexBar](https://github.com/steipete/CodexBar) (MIT) by Peter
Steinberger, whose window-mapping rules and source-chain pattern were ported directly, and to
[ccusage](https://github.com/ryoppippi/ccusage) (MIT) for prior art on transcript cost scanning.
