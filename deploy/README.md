# Deployment: autonome GitHub-Action

Setup für den vollautomatischen Betrieb:

- **Öffentliches Repo `garmin-mcp`** — dieser Quellcode (enthält den Report-Generator
  `tools/GarminMcp.Report`). Keine Credentials, keine Daten.
- **Privates Repo `garmin-dashboard`** — Inhalt aus [`deploy/garmin-dashboard/`](garmin-dashboard):
  die GitHub Action (`.github/workflows/sync.yml`) + die generierten Dateien
  (`dashboard.md`, `data.json`). **Hier liegen die persönlichen Daten.**

## Sicherheitsmodell (wo liegen die Credentials?)

- **Kein Passwort auf GitHub.** Die Action nutzt nur den `GARMIN_TOKEN` (OAuth1-Bundle).
- Der Token liegt **ausschließlich als verschlüsseltes Actions-Secret** im **privaten**
  Repo — nie im Code, nie in einem Commit, in Logs maskiert.
- **Kein PAT nötig:** die Action committet in ihr eigenes Repo via `GITHUB_TOKEN`.
- Daten landen nur im privaten Repo; das öffentliche Repo bleibt datenfrei.

## Schritte

1. Öffentliches Repo `garmin-mcp` mit diesem Quellcode anlegen und pushen.
2. Privates Repo `garmin-dashboard` mit dem Inhalt von `deploy/garmin-dashboard/` anlegen
   und pushen (die Workflow-Datei nach `.github/workflows/sync.yml`).
3. Im privaten Repo das Secret `GARMIN_TOKEN` setzen (Wert aus `GarminMcp.Login`).
4. Action „Garmin dashboard" einmal manuell starten (Run workflow) → danach alle 6 h.

Details siehe [`garmin-dashboard/README.md`](garmin-dashboard/README.md).
