namespace KobraLanMonitor;

public static class Pages
{
    private const string Style = """
        <style>
          :root {
            --bg: #f4f5f7; --card: #ffffff; --text: #1a1a1a; --muted: #666666;
            --border: #dddddd; --accent: #2563eb; --bad: #dc2626;
          }
          @media (prefers-color-scheme: dark) {
            :root {
              --bg: #16181d; --card: #21242b; --text: #f0f0f0; --muted: #9aa0aa;
              --border: #33363e; --accent: #5b9bff; --bad: #f87171;
            }
          }
          * { box-sizing: border-box; }
          body {
            margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center;
            font-family: -apple-system, Segoe UI, Roboto, Arial, sans-serif; background: var(--bg); color: var(--text);
          }
          .card { background: var(--card); border: 1px solid var(--border); border-radius: 10px; padding: 28px; width: 340px; }
          h1 { font-size: 1.2rem; margin: 0 0 4px; }
          p.sub { color: var(--muted); margin: 0 0 20px; font-size: 0.85rem; }
          label { display: block; font-size: 0.8rem; color: var(--muted); margin: 12px 0 4px; }
          input {
            width: 100%; padding: 8px 10px; border-radius: 6px; border: 1px solid var(--border);
            background: var(--bg); color: var(--text); font-size: 0.95rem;
          }
          button {
            width: 100%; margin-top: 20px; padding: 10px; border-radius: 6px; border: none;
            background: var(--accent); color: white; font-weight: 600; font-size: 0.95rem; cursor: pointer;
          }
          .error { color: var(--bad); font-size: 0.85rem; margin-top: 12px; }
        </style>
        """;

    public static string Setup(string? error = null) => $$"""
        <!DOCTYPE html><html><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Set up Kobra LAN Monitor</title>{{Style}}</head>
        <body>
          <div class="card">
            <h1>Set up Kobra LAN Monitor</h1>
            <p class="sub">One-time setup. Enter your printer's IP and choose a login for this dashboard.</p>
            <form method="post" action="/setup">
              <label>Printer IP address</label>
              <input name="printerHost" placeholder="172.16.77.13" required>
              <label>Choose a username</label>
              <input name="username" required>
              <label>Choose a password</label>
              <input name="password" type="password" required>
              <button type="submit">Save & Continue</button>
              {{(error != null ? $"<div class=\"error\">{error}</div>" : "")}}
            </form>
          </div>
        </body></html>
        """;

    public static string Login(string? error = null) => $$"""
        <!DOCTYPE html><html><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Log in - Kobra LAN Monitor</title>{{Style}}</head>
        <body>
          <div class="card">
            <h1>Kobra LAN Monitor</h1>
            <p class="sub">Log in to view the printer dashboard.</p>
            <form method="post" action="/login">
              <label>Username</label>
              <input name="username" required autofocus>
              <label>Password</label>
              <input name="password" type="password" required>
              <button type="submit">Log In</button>
              {{(error != null ? $"<div class=\"error\">{error}</div>" : "")}}
            </form>
          </div>
        </body></html>
        """;
}
