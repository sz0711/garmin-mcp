# Garmin Connect MCP Server (C#)

Ein C#/.NET-Webservice, der Garmin-Connect-Daten als **MCP-Server** (Model Context
Protocol) **und** optional als **REST-API** bereitstellt — als Ersatz für das
Python-Projekt [`cyberjunky/python-garminconnect`](https://github.com/cyberjunky/python-garminconnect).
Er läuft als **Docker-Container** und lässt sich an **Claude Desktop** anbinden.

Read-only Fokus: Schlaf, Herzfrequenz, Schritte, Stress, Body Battery, HRV,
Körperzusammensetzung/Gewicht, Hydration, Aktivitäten, persönliche Rekorde u. a.

---

## Auth-Modell (token-first, passwortfrei)

Garmin hat keine offizielle Consumer-API; der Login emuliert den Mobile-/Web-SSO und
liefert OAuth-Tokens:

- **E-Mail/Passwort (+ MFA) brauchst du nur einmal**, um per `GarminMcp.Login` einen
  **`GARMIN_TOKEN`** zu erzeugen (enthält den langlebigen OAuth1-Token, ~1 Jahr gültig).
- Der **laufende Container bekommt nur diesen Token** — kein Passwort, kein MFA. Der
  kurzlebige OAuth2-Access-Token wird intern automatisch aus dem OAuth1-Token erneuert
  (`RefreshingTokenCache`), ohne erneuten Login.

> Die verwendete Bibliothek `Unofficial.Garmin.Connect` würde bei OAuth2-Ablauf jede
> Stunde mit Passwort neu einloggen. Dieses Projekt umgeht das mit einer eigenen,
> portierten SSO-/OAuth1-Refresh-Schicht (`GarminMcp.Core/Auth`), die den OAuth1-Token
> persistiert — daher echtes token-first.

---

## Projektstruktur

| Projekt | Zweck |
|---|---|
| `src/GarminMcp.Core` | Auth (OAuth1-Signierung, SSO-Port, Token-Refresh, Token-Bundle) + `GarminService` (read-only) |
| `src/GarminMcp.Server` | MCP-Server (stdio + HTTP) und REST, DI, Env-Konfiguration |
| `tools/GarminMcp.Login` | Einmal-Tool: Login → erzeugt `GARMIN_TOKEN` |
| `tests/GarminMcp.Tests` | Unit- + Integrations- + (optionale) Live-E2E-Tests |

---

## Voraussetzungen

- .NET SDK 9 (oder neuer)
- Docker Desktop (für den Container)

## 1) Bauen & Testen

```powershell
dotnet build
dotnet test
```

Die Tests umfassen u. a. die OAuth1-Signierung gegen den kanonischen Referenzvektor
und einen Integrationstest, der den MCP-Server real über stdio startet und alle Tools
prüft (ohne Garmin-Zugriff).

## 2) Token erzeugen

```powershell
$env:GARMIN_EMAIL = "you@example.com"
$env:GARMIN_PASSWORD = "dein-passwort"
dotnet run --project tools/GarminMcp.Login
```

Bei aktivem 2FA wirst du nach dem **MFA-Code** gefragt. Das Tool verifiziert den Token
(holt dein Profil über den passwortfreien Pfad) und gibt den **`GARMIN_TOKEN`** auf
STDOUT aus. Optional in Datei: `... GarminMcp.Login -- C:\pfad\garmin-token.json`.

> ⚠️ Der Token ist ein Geheimnis (Vollzugriff aufs Konto). Nicht committen/teilen.

## 3) Docker-Image bauen

```powershell
docker build -t garmin-mcp:latest .
```

## 4) Anbindung an Claude Desktop (Windows)

Datei: `%APPDATA%\Claude\claude_desktop_config.json`. Danach Claude Desktop komplett
beenden und neu starten.

**Variante A — Token aus dem CLI (einfachste):** Du hast Schritt 2 ausgeführt und einen
`GARMIN_TOKEN`. Kein Port, kein Volume nötig; Setup-UI aus.

```json
{
  "mcpServers": {
    "garmin": {
      "command": "docker",
      "args": ["run", "-i", "--rm", "-e", "GARMIN_TOKEN", "-e", "GARMIN_SETUP_ENABLED=false", "garmin-mcp:latest"],
      "env": { "GARMIN_TOKEN": "<dein GARMIN_TOKEN>" }
    }
  }
}
```

**Variante B — Anmeldung per Browser (ohne CLI):** Kein Token vorab nötig. Der Container
bekommt ein Volume für den Token und veröffentlicht den Setup-Port.

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

Beim ersten Start ist der MCP nicht eingeloggt. Frag Claude z. B. „Bist du mit Garmin
verbunden?" — `garmin_auth_status` (und jedes Daten-Tool) liefert dann die **Setup-URL**
`http://localhost:8765/`. Diese im Browser öffnen, E-Mail/Passwort (+ MFA) eingeben,
**speichern**. Der Token wird im Volume `garmin-tokens` abgelegt und ab dann bei jedem
Start automatisch geladen. Läuft der Token irgendwann ab (~1 Jahr), einfach erneut über
die URL anmelden — ohne die Config zu ändern.

Hinweise (Windows):

- Docker Desktop muss **laufen**, bevor Claude Desktop startet.
- MCP-Subprozesse erben den PATH nicht immer — falls `docker` nicht gefunden wird, den
  vollen Pfad eintragen, z. B. `"C:\\Program Files\\Docker\\Docker\\resources\\bin\\docker.exe"`.
- Logs zur Fehlersuche: `%APPDATA%\Claude\logs\mcp-server-garmin.log`.

---

## Optional: HTTP-Modus (Streamable HTTP + REST)

Für Betrieb als dauerhafter Server statt stdio:

```powershell
$env:GARMIN_TOKEN = "<token>"
docker compose up --build      # nutzt MCP_TRANSPORT=http, Port 8080
```

- MCP-Endpoint: `http://localhost:8080/` (Streamable HTTP)
- REST: `GET http://localhost:8080/api/garmin/...`, Health: `GET /health`

REST-Beispiele:

```
GET /api/garmin/profile
GET /api/garmin/daily-summary?date=2026-06-30
GET /api/garmin/sleep?date=2026-06-29
GET /api/garmin/activities?start=0&limit=20
GET /api/garmin/body-battery?startDate=2026-06-25&endDate=2026-06-30
```

---

## Konfiguration (Umgebungsvariablen)

| Variable | Bedeutung |
|---|---|
| `GARMIN_TOKEN` | **Empfohlen.** Base64-Token aus `GarminMcp.Login` (token-first) |
| `GARMIN_EMAIL` / `GARMIN_PASSWORD` | Alternativ: einmaliger Login im Service (mintet Token) |
| `GARMIN_MFA_CODE` | MFA-Code, falls beim Login im Service nötig (nicht interaktiv) |
| `GARMIN_TOKEN_FILE` | Pfad zum Persistieren/Laden des Tokens (z. B. Docker-Volume) |
| `GARMIN_DOMAIN` | `garmin.com` (Standard) oder `garmin.cn` |
| `MCP_TRANSPORT` | `stdio` (Standard) oder `http` |
| `GARMIN_SETUP_ENABLED` | Browser-Anmelde-UI an/aus (Standard `true`; bei Variante A `false`) |
| `GARMIN_SETUP_PORT` | Port der Anmelde-UI (Standard `8765`) |
| `GARMIN_SETUP_URL` | Angezeigte URL der Anmelde-UI (Standard `http://localhost:<port>/`) |

## Verfügbare MCP-Tools (read-only)

`garmin_auth_status` (Login-Status + Setup-URL), `garmin_get_profile`,
`garmin_get_user_settings`, `garmin_get_daily_summary`,
`garmin_get_steps`, `garmin_get_heart_rate`, `garmin_get_sleep`,
`garmin_get_body_battery`, `garmin_get_hrv`, `garmin_get_body_composition`,
`garmin_get_weight`, `garmin_get_hydration`, `garmin_get_activities`,
`garmin_get_activities_by_date`, `garmin_get_activity_details`,
`garmin_get_personal_records`. Datumsangaben im Format `yyyy-MM-dd`.

## Live-E2E-Test (optional, mit echtem Token)

```powershell
$env:GARMIN_E2E_TOKEN = "<dein GARMIN_TOKEN>"
dotnet test
```

Ohne diese Variable werden die Live-Tests übersprungen (Suite bleibt hermetisch).

## Troubleshooting

- **„authentication failed … re-mint it"**: OAuth1-Token abgelaufen/widerrufen →
  `GarminMcp.Login` erneut ausführen, neuen `GARMIN_TOKEN` setzen.
- **HTTP 429 / rate-limited**: kurz warten; nicht wiederholt neu einloggen
  (token-first vermeidet häufige Logins genau deshalb).
- **MFA bei jedem Start**: passiert nur, wenn ohne Token mit E-Mail/Passwort gestartet
  wird → token-first nutzen.
