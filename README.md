# Garmin Connect MCP Server (C#)

A C#/.NET web service that exposes Garmin Connect data as an **MCP server** (Model
Context Protocol) **and** optionally as a **REST API** — a replacement for the Python
project [`cyberjunky/python-garminconnect`](https://github.com/cyberjunky/python-garminconnect).
It runs as a **Docker container** and connects to **Claude Desktop**.

Read-only focus: sleep, heart rate, steps, stress, Body Battery, HRV, body
composition/weight, hydration, activities, personal records, and more.

---

## Auth model (token-first, password-free)

Garmin has no official consumer API; login emulates the mobile/web SSO and returns
OAuth tokens:

- **Email/password (+ MFA) are needed only once** to create a **`GARMIN_TOKEN`** via
  `GarminMcp.Login` (it holds the long-lived OAuth1 token, valid ~1 year).
- The **running container only ever gets that token** — no password, no MFA. The
  short-lived OAuth2 access token is refreshed internally from the OAuth1 token
  (`RefreshingTokenCache`) with no re-login.

> The underlying library `Unofficial.Garmin.Connect` would re-login with the password
> every hour when the OAuth2 token expires. This project avoids that with its own
> ported SSO / OAuth1-refresh layer (`GarminMcp.Core/Auth`) that persists the OAuth1
> token — hence true token-first.

---

## Project layout

| Project | Purpose |
|---|---|
| `src/GarminMcp.Core` | Auth (OAuth1 signing, ported SSO, token refresh, token bundle) + `GarminService` (read-only) |
| `src/GarminMcp.Server` | MCP server (stdio + HTTP) and REST, DI, env configuration |
| `tools/GarminMcp.Login` | One-time tool: login → produces `GARMIN_TOKEN` |
| `tools/GarminMcp.Report` | Dashboard generator (Markdown + self-contained HTML) |
| `tests/GarminMcp.Tests` | Unit + integration + (optional) live E2E tests |

---

## Prerequisites

- .NET SDK 9 (or newer)
- Docker Desktop (for the container)

## 1) Build & test

```powershell
dotnet build
dotnet test
```

The tests include OAuth1 signing verified against the canonical reference vector and an
integration test that actually starts the MCP server over stdio and checks all tools
(without touching Garmin).

## 2) Create a token

```powershell
$env:GARMIN_EMAIL = "you@example.com"
$env:GARMIN_PASSWORD = "your-password"
dotnet run --project tools/GarminMcp.Login
```

With 2FA enabled you'll be prompted for the **MFA code**. The tool verifies the token
(fetching your profile over the password-free path) and prints the **`GARMIN_TOKEN`** to
STDOUT. Optionally to a file: `... GarminMcp.Login -- C:\path\garmin-token.json`.

> ⚠️ The token is a secret (full account access). Do not commit or share it.

## 3) Build the Docker image

```powershell
docker build -t garmin-mcp:latest .
```

## 4) Connect to Claude Desktop (Windows)

File: `%APPDATA%\Claude\claude_desktop_config.json`. Fully quit and restart Claude
Desktop afterwards.

**Option A — token from the CLI (simplest):** you ran step 2 and have a `GARMIN_TOKEN`.
No port, no volume needed; sign-in UI off.

```json
{
  "mcpServers": {
    "garmin": {
      "command": "docker",
      "args": ["run", "-i", "--rm", "-e", "GARMIN_TOKEN", "-e", "GARMIN_SETUP_ENABLED=false", "garmin-mcp:latest"],
      "env": { "GARMIN_TOKEN": "<your GARMIN_TOKEN>" }
    }
  }
}
```

**Option B — browser sign-in (no CLI):** no token needed up front. The container gets a
volume for the token and publishes the setup port.

```json
{
  "mcpServers": {
    "garmin": {
      "command": "docker",
      "args": [
        "run", "-i", "--rm",
        "-p", "8765:8765",
        "-v", "garmin-tokens:/data",
        "-e", "GARMIN_TOKEN_FILE=/data/garmin-token.json",
        "garmin-mcp:latest"
      ]
    }
  }
}
```

On first start the MCP isn't signed in. Ask Claude e.g. "Are you connected to Garmin?" —
`garmin_auth_status` (and every data tool) then returns the **setup URL**
`http://localhost:8765/`. Open it in a browser, enter email/password (+ MFA), and
**save**. The token is stored in the `garmin-tokens` volume and loaded automatically on
every start afterwards. When the token eventually expires (~1 year), just sign in again
via the URL — no config change needed.

Notes (Windows):

- Docker Desktop must be **running** before Claude Desktop starts.
- MCP subprocesses don't always inherit the PATH — if `docker` isn't found, use the full
  path, e.g. `"C:\\Program Files\\Docker\\Docker\\resources\\bin\\docker.exe"`.
- Debug logs: `%APPDATA%\Claude\logs\mcp-server-garmin.log`.

---

## Optional: HTTP mode (Streamable HTTP + REST)

To run as a long-lived server instead of stdio:

```powershell
$env:GARMIN_TOKEN = "<token>"
docker compose up --build      # uses MCP_TRANSPORT=http, port 8080
```

- MCP endpoint: `http://localhost:8080/` (Streamable HTTP)
- REST: `GET http://localhost:8080/api/garmin/...`, health: `GET /health`

REST examples:

```
GET /api/garmin/profile
GET /api/garmin/daily-summary?date=2026-06-30
GET /api/garmin/sleep?date=2026-06-29
GET /api/garmin/activities?start=0&limit=20
GET /api/garmin/body-battery?startDate=2026-06-25&endDate=2026-06-30
```

---

## Configuration (environment variables)

| Variable | Meaning |
|---|---|
| `GARMIN_TOKEN` | **Recommended.** Base64 token from `GarminMcp.Login` (token-first) |
| `GARMIN_EMAIL` / `GARMIN_PASSWORD` | Alternative: one-time login inside the service (mints a token) |
| `GARMIN_MFA_CODE` | MFA code if a service-side login needs one (non-interactive) |
| `GARMIN_TOKEN_FILE` | Path to persist/load the token (e.g. a Docker volume) |
| `GARMIN_DOMAIN` | `garmin.com` (default) or `garmin.cn` |
| `MCP_TRANSPORT` | `stdio` (default) or `http` |
| `GARMIN_SETUP_ENABLED` | Browser sign-in UI on/off (default `true`; `false` for option A) |
| `GARMIN_SETUP_PORT` | Sign-in UI port (default `8765`) |
| `GARMIN_SETUP_URL` | Advertised sign-in UI URL (default `http://localhost:<port>/`) |

## Available MCP tools (read-only)

`garmin_auth_status` (login status + setup URL), `garmin_get_profile`,
`garmin_get_user_settings`, `garmin_get_daily_summary`,
`garmin_get_steps`, `garmin_get_heart_rate`, `garmin_get_sleep`,
`garmin_get_body_battery`, `garmin_get_hrv`, `garmin_get_body_composition`,
`garmin_get_weight`, `garmin_get_hydration`, `garmin_get_activities`,
`garmin_get_activities_by_date`, `garmin_get_activity_details`,
`garmin_get_personal_records`. Dates use the `yyyy-MM-dd` format.

**Coaching tools** (acts as a personal trainer): `garmin_daily_coaching` (readiness
green/amber/red, recommended session rest/easy/moderate/hard, planned workout reconciled
with recovery, next key workout), `garmin_training_readiness`, `garmin_training_status`
(VO₂max, ACWR/load), `garmin_race_predictions`, `garmin_scheduled_workouts` (marathon
plan).

## Live E2E test (optional, with a real token)

```powershell
$env:GARMIN_E2E_TOKEN = "<your GARMIN_TOKEN>"
dotnet test
```

Without this variable the live tests are skipped (the suite stays hermetic).

## Autonomous dashboard (GitHub Actions)

`tools/GarminMcp.Report` generates a phone-friendly dashboard (`dashboard.md` +
self-contained `index.html` + `data.json`) that **acts as a daily coach**: it reads your
recovery (HRV/RHR/sleep/Body Battery), Garmin Training Readiness/Status/load and your
marathon training-plan workouts, then produces a daily recommendation (rest / easy /
moderate / hard) reconciled with the plan, **plus a daily fuelling target** (calories +
carb/protein/fat split scaled to the day's load and your body weight) with concrete food
ideas. A natural-language insight is written by
**GitHub Models** (the workflow's built-in `GITHUB_TOKEN` with `models: read` — no
separate API key), with a deterministic rule-based fallback. It runs on a schedule via
GitHub Actions so a private repo holds your data and updates itself. Optional repo
variable `GARMIN_GOAL` (e.g. "sub 4:00") makes coaching goal-aware. See
[`deploy/`](deploy/) for the workflow and setup.

## Troubleshooting

- **"authentication failed … re-mint it"**: OAuth1 token expired/revoked → run
  `GarminMcp.Login` again and set the new `GARMIN_TOKEN`.
- **HTTP 429 / rate-limited**: wait a bit; don't repeatedly re-login (token-first avoids
  frequent logins for exactly this reason).
- **MFA on every start**: only happens when starting with email/password instead of a
  token → use token-first.
