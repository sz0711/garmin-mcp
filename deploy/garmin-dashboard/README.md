# Garmin Dashboard (privat)

Dieses **private** Repo enthält meine persönlichen Garmin-Daten und ein automatisch
generiertes Dashboard. Eine GitHub Action holt alle 6 Stunden die aktuellen Werte über
den (token-first) Generator aus dem öffentlichen Repo `garmin-mcp` und committet:

- **`dashboard.md`** — Übersicht; in der **GitHub-Handy-App** schön gerendert.
- **`data.json`** — vollständiger, mitwachsender Datenspeicher (für KI/Coach).

> ⚠️ **Privat halten.** Hier liegen Gesundheitsdaten. Repo niemals öffentlich schalten.

## Einrichtung (einmalig)

1. **Token erzeugen** (auf dem PC, mit dem öffentlichen Repo):
   ```powershell
   $env:GARMIN_EMAIL="you@example.com"; $env:GARMIN_PASSWORD="..."
   dotnet run --project tools/GarminMcp.Login
   ```
   Den ausgegebenen `GARMIN_TOKEN` kopieren (geheim!).

2. **Secret setzen:** in DIESEM Repo → Settings → Secrets and variables → Actions →
   **New repository secret** → Name `GARMIN_TOKEN`, Wert = der kopierte Token.
   (Der Token steht damit verschlüsselt bereit und nie im Code.)

3. **Action starten:** Tab **Actions** → „Garmin dashboard" → **Run workflow**. Danach
   läuft sie automatisch alle 6 Stunden.

## Auf dem Handy ansehen

- **GitHub-App** öffnen → dieses Repo → `dashboard.md` (wird mit Charts gerendert).

## Token erneuern (~1× pro Jahr)

Läuft der Garmin-Token ab, einfach Schritt 1 wiederholen und das Secret `GARMIN_TOKEN`
aktualisieren. Sonst ist nichts zu tun.
