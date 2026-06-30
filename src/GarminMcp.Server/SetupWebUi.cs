using GarminMcp.Core;

namespace GarminMcp.Server;

/// <summary>
/// A tiny browser-based sign-in UI. The user opens it (URL is surfaced by the MCP
/// tools when not signed in), enters email/password (+ MFA), and the server mints and
/// stores the Garmin token — no CLI needed. Used both as a standalone setup server
/// (stdio mode) and mounted under the main app (HTTP mode).
/// </summary>
public static class SetupWebUi
{
    public static void Map(IEndpointRouteBuilder app, string basePath = "")
    {
        basePath = basePath.TrimEnd('/');

        app.MapGet(basePath + "/", () => Results.Content(Html(basePath), "text/html; charset=utf-8"));

        app.MapGet(basePath + "/status", (IGarminConnectionProvider provider) =>
            Results.Json(new { authenticated = provider.IsAuthenticated, setupUrl = provider.SetupUrl }));

        app.MapPost(basePath + "/login", async (HttpRequest request, IGarminConnectionProvider provider) =>
        {
            var form = await request.ReadFormAsync();
            var outcome = await provider.BeginLoginAsync(form["email"].ToString(), form["password"].ToString());
            return Json(outcome);
        });

        app.MapPost(basePath + "/mfa", async (HttpRequest request, IGarminConnectionProvider provider) =>
        {
            var form = await request.ReadFormAsync();
            var outcome = await provider.SubmitMfaAsync(form["code"].ToString());
            return Json(outcome);
        });
    }

    private static IResult Json(LoginOutcome outcome) => Results.Json(new
    {
        status = outcome.Status switch
        {
            LoginStatus.Success => "success",
            LoginStatus.MfaRequired => "mfa_required",
            _ => "error",
        },
        message = outcome.Message,
    });

    private static string Html(string basePath) => $$"""
        <!doctype html>
        <html lang="de">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>Garmin MCP – Anmeldung</title>
          <style>
            :root { color-scheme: light dark; }
            body { font-family: system-ui, sans-serif; max-width: 30rem; margin: 3rem auto; padding: 0 1rem; }
            h1 { font-size: 1.4rem; }
            label { display: block; margin: .75rem 0 .25rem; font-weight: 600; }
            input { width: 100%; padding: .55rem; font-size: 1rem; box-sizing: border-box; }
            button { margin-top: 1.25rem; padding: .6rem 1.2rem; font-size: 1rem; cursor: pointer; }
            .hidden { display: none; }
            .msg { margin-top: 1rem; padding: .7rem .9rem; border-radius: .4rem; }
            .ok { background: #e6f4ea; color: #1e7d34; }
            .err { background: #fdecea; color: #b3261e; }
            .muted { color: #777; font-size: .85rem; margin-top: 1.5rem; }
          </style>
        </head>
        <body>
          <h1>Garmin Connect – Anmeldung</h1>
          <p id="status" class="muted">Status wird geladen …</p>

          <div id="loginForm">
            <label for="email">E-Mail</label>
            <input id="email" type="email" autocomplete="username">
            <label for="password">Passwort</label>
            <input id="password" type="password" autocomplete="current-password">
            <button id="loginBtn">Anmelden &amp; speichern</button>
          </div>

          <div id="mfaForm" class="hidden">
            <label for="code">MFA-Code (Authenticator/E-Mail)</label>
            <input id="code" type="text" inputmode="numeric" autocomplete="one-time-code">
            <button id="mfaBtn">Code bestätigen</button>
          </div>

          <div id="message"></div>
          <p class="muted">Die Zugangsdaten werden nur zum einmaligen Anmelden verwendet; gespeichert wird nur ein Token.</p>

          <script>
            const base = {{("\"" + basePath + "\"")}};
            const $ = (id) => document.getElementById(id);
            const show = (el, on) => el.classList.toggle('hidden', !on);
            function msg(text, ok) {
              const m = $('message');
              m.className = 'msg ' + (ok ? 'ok' : 'err');
              m.textContent = text;
            }
            async function refreshStatus() {
              try {
                const r = await fetch(base + '/status');
                const s = await r.json();
                $('status').textContent = s.authenticated
                  ? 'Verbunden mit Garmin ✓ (du kannst dich bei Bedarf erneut anmelden)'
                  : 'Nicht angemeldet – bitte unten anmelden.';
              } catch { $('status').textContent = ''; }
            }
            async function post(path, data) {
              const r = await fetch(base + path, {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                body: new URLSearchParams(data),
              });
              return r.json();
            }
            $('loginBtn').addEventListener('click', async () => {
              $('loginBtn').disabled = true;
              msg('Anmeldung läuft …', true);
              try {
                const res = await post('/login', { email: $('email').value, password: $('password').value });
                if (res.status === 'success') { msg('Erfolgreich angemeldet und Token gespeichert ✓', true); show($('mfaForm'), false); refreshStatus(); }
                else if (res.status === 'mfa_required') { msg('MFA-Code erforderlich – bitte unten eingeben.', true); show($('mfaForm'), true); }
                else { msg('Fehler: ' + (res.message || 'unbekannt'), false); }
              } catch (e) { msg('Fehler: ' + e, false); }
              $('loginBtn').disabled = false;
            });
            $('mfaBtn').addEventListener('click', async () => {
              $('mfaBtn').disabled = true;
              msg('Code wird geprüft …', true);
              try {
                const res = await post('/mfa', { code: $('code').value });
                if (res.status === 'success') { msg('Erfolgreich angemeldet und Token gespeichert ✓', true); show($('mfaForm'), false); refreshStatus(); }
                else { msg('Fehler: ' + (res.message || 'unbekannt'), false); }
              } catch (e) { msg('Fehler: ' + e, false); }
              $('mfaBtn').disabled = false;
            });
            refreshStatus();
          </script>
        </body>
        </html>
        """;
}
