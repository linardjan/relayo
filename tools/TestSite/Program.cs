using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

// Тестовый «сайт клиента». Слушает два порта — два зеркала одного сайта:
// у каждого свой localStorage и своя cookie входа, как у разных доменов.
// Вход ненастоящий: кнопка «Войти как клиент 1001».

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

string Get(string key, string fallback) => string.IsNullOrWhiteSpace(cfg[key]) ? fallback : cfg[key]!;

var port = int.Parse(Get("TEST_SITE_PORT", "5300"));
var mirrorPort = int.Parse(Get("TEST_SITE_MIRROR_PORT", "5301"));
var keyId = Get("TEST_SITE_KEY_ID", "test-key-1");
var secret = cfg["TEST_SITE_SECRET"];
if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
    throw new InvalidOperationException("TEST_SITE_SECRET must be set (at least 32 characters). See .env.example.");
var product = Get("TEST_SITE_PRODUCT", "test");
var clientIds = Get("TEST_SITE_CLIENT_IDS", "1001,1002,1003").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var tokenMinutes = int.Parse(Get("TEST_SITE_TOKEN_MINUTES", "5"));
var tokenWidget = Get("TEST_WIDGET_TOKEN_MODE_ID", "00000000-0000-0000-0000-000000000001");
var directWidget = Get("TEST_WIDGET_DIRECT_MODE_ID", "00000000-0000-0000-0000-000000000002");
// Опубликованные файлы виджетов в S3: widget_<ключ>.js (Relayo публикует тестовые виджеты сам при запуске).
var widgetFiles = Get("WIDGET_FILES_BASE_URL", "http://localhost:9000/relayo-widgets").TrimEnd('/');
var tokenWidgetKey = Get("TEST_WIDGET_TOKEN_MODE_KEY", "testtoken1");
var directWidgetKey = Get("TEST_WIDGET_DIRECT_MODE_KEY", "testdirect");
// Адрес публичного сервиса, каким его видит браузер (не контейнер).
var publicUrl = Get("PUBLIC_BASE_URL", "http://localhost:5200").TrimEnd('/');

builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(port);
    k.ListenAnyIP(mirrorPort);
});

var app = builder.Build();
var handler = new JsonWebTokenHandler();

// Cookie не различают порты, поэтому имя cookie включает порт: у зеркал раздельный вход.
string CookieName(HttpContext http) => $"testsite_user_{http.Connection.LocalPort}";

string CreateToken(string clientId, TimeSpan lifetime, string signingSecret)
{
    var now = DateTime.UtcNow;
    return handler.CreateToken(new SecurityTokenDescriptor
    {
        Claims = new Dictionary<string, object> { ["clientId"] = clientId, ["locale"] = "en" },
        IssuedAt = now.Add(lifetime < TimeSpan.Zero ? lifetime * 2 : TimeSpan.Zero),
        NotBefore = now.Add(lifetime < TimeSpan.Zero ? lifetime * 2 : TimeSpan.Zero),
        Expires = now.Add(lifetime),
        SigningCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingSecret)) { KeyId = keyId },
            SecurityAlgorithms.HmacSha256),
    });
}

app.MapGet("/", (HttpContext http) =>
{
    var direct = http.Request.Query["mode"] == "direct";
    var user = http.Request.Cookies[CookieName(http)];
    var local = http.Connection.LocalPort;
    var other = local == port ? mirrorPort : port;
    var html = Page
        .Replace("{{PUBLIC_URL}}", publicUrl)
        .Replace("{{WIDGET_ID}}", direct ? directWidget : tokenWidget)
        .Replace("{{WIDGET_FILE}}", $"{widgetFiles}/widget_{(direct ? directWidgetKey : tokenWidgetKey)}.js")
        .Replace("{{MODE}}", direct ? "direct" : "token")
        .Replace("{{PRODUCT}}", product)
        .Replace("{{USER}}", user ?? "")
        .Replace("{{CLIENT_IDS}}", string.Join(",", clientIds))
        .Replace("{{MIRROR_NAME}}", local == port ? "main site" : "mirror")
        .Replace("{{OTHER_PORT}}", other.ToString())
        .Replace("{{OTHER_NAME}}", local == port ? "mirror" : "main site");
    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapPost("/login", (HttpContext http, LoginRequest request) =>
{
    if (!clientIds.Contains(request.ClientId))
        return Results.BadRequest();
    http.Response.Cookies.Append(CookieName(http), request.ClientId, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax });
    return Results.NoContent();
});

app.MapPost("/logout", (HttpContext http) =>
{
    http.Response.Cookies.Delete(CookieName(http));
    return Results.NoContent();
});

// Так настоящий сайт выдаёт токен клиента: только авторизованному пользователю и только со своего backend.
app.MapGet("/token", (HttpContext http, string? variant) =>
{
    var user = http.Request.Cookies[CookieName(http)];
    if (user is null)
        return Results.Unauthorized();

    var token = variant switch
    {
        "expired" => CreateToken(user, TimeSpan.FromMinutes(-5), secret),
        "wrong-key" => CreateToken(user, TimeSpan.FromMinutes(tokenMinutes),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
        _ => CreateToken(user, TimeSpan.FromMinutes(tokenMinutes), secret),
    };
    return Results.Ok(new { token });
});

app.Run();

internal sealed record LoginRequest(string ClientId);

internal partial class Program
{
    private const string Page = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Relayo test site</title>
<style>
  body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; margin: 0; background: #fafafa; color: #1f1f1f; }
  header { background: #222; color: #fff; padding: 14px 24px; display: flex; justify-content: space-between; align-items: center; }
  header a { color: #9cf; }
  main { max-width: 900px; margin: 24px auto; padding: 0 24px; display: grid; gap: 16px; }
  section { background: #fff; border: 1px solid #eee; border-radius: 10px; padding: 16px 20px; }
  h2 { font-size: 16px; margin: 0 0 12px; }
  button { padding: 6px 12px; border-radius: 6px; border: 1px solid #ccc; background: #fff; cursor: pointer; margin: 0 6px 6px 0; }
  button.primary { background: #1677ff; color: #fff; border-color: #1677ff; }
  .muted { color: #888; font-size: 13px; }
  .tag { display: inline-block; padding: 2px 8px; border-radius: 10px; background: #e6f4ff; color: #0958d9; font-size: 12px; }
  .warn { background: #fff7e6; color: #d46b08; }
  pre { background: #111; color: #9f9; padding: 12px; border-radius: 8px; height: 220px; overflow: auto; font-size: 12px; margin: 0; }
</style>
</head>
<body>
<header>
  <strong>Relayo test site · <span id="where">{{MIRROR_NAME}}</span></strong>
  <span><a id="other">Open the {{OTHER_NAME}}</a></span>
</header>
<main>
  <section>
    <h2>Identification mode</h2>
    <p>
      <span class="tag" id="modeTag"></span>
      <a id="switchMode" href="#"></a>
    </p>
    <p class="muted" id="modeHelp"></p>
  </section>

  <section>
    <h2>Site account</h2>
    <p id="who"></p>
    <div id="loginButtons"></div>
    <button id="logout">Sign out</button>
  </section>

  <section id="badTokens">
    <h2>Negative checks (signed token mode)</h2>
    <p class="muted">Relayo must reject these tokens. See the browser console and the log below.</p>
    <button data-variant="expired">Send expired token</button>
    <button data-variant="wrong-key">Send token signed with a wrong key</button>
  </section>

  <section>
    <h2>Widget</h2>
    <button onclick="Relayo.open()">Relayo.open()</button>
    <button onclick="Relayo.close()">Relayo.close()</button>
    <button onclick="Relayo.open('tg')">Relayo.open('tg')</button>
  </section>

  <section>
    <h2>Log</h2>
    <pre id="log"></pre>
  </section>
</main>

<!-- Код вставки виджета: одна строка. -->
<script async src="{{WIDGET_FILE}}"></script>

<script>
  const MODE = '{{MODE}}', PRODUCT = '{{PRODUCT}}', CLIENT_IDS = '{{CLIENT_IDS}}'.split(',');
  let user = '{{USER}}' || null;

  // Методы Relayo.* появляются после загрузки виджета (событие relayo:ready).
  function withRelayo(fn) {
    if (window.Relayo && window.Relayo.open) fn(window.Relayo);
    else window.addEventListener('relayo:ready', () => fn(window.Relayo), { once: true });
  }

  const logEl = document.getElementById('log');
  const log = (msg) => { logEl.textContent += new Date().toLocaleTimeString() + '  ' + msg + '\n'; logEl.scrollTop = logEl.scrollHeight; };

  document.getElementById('other').href = location.protocol + '//' + location.hostname + ':{{OTHER_PORT}}/' + location.search;
  document.getElementById('modeTag').textContent = MODE === 'direct' ? 'Direct clientId (unsafe)' : 'Signed token';
  if (MODE === 'direct') document.getElementById('modeTag').classList.add('warn');
  const switchLink = document.getElementById('switchMode');
  switchLink.textContent = MODE === 'direct' ? 'Switch to signed token' : 'Switch to direct clientId';
  switchLink.href = MODE === 'direct' ? '/' : '/?mode=direct';
  document.getElementById('modeHelp').textContent = MODE === 'direct'
    ? 'The page calls Relayo.setProduct() and Relayo.setClientId(). Anyone can do the same from the console, so the widget shows only this browser\'s messages.'
    : 'The page gets a signed token from its own backend (/token) and calls Relayo.identify(token).';
  document.getElementById('badTokens').style.display = MODE === 'direct' ? 'none' : '';

  async function identify(variant) {
    if (!user) return;
    if (MODE === 'direct') {
      withRelayo((r) => { r.setProduct(PRODUCT); r.setClientId(user); });
      log('Relayo.setProduct("' + PRODUCT + '"); Relayo.setClientId("' + user + '")');
      return;
    }
    const res = await fetch('/token' + (variant ? '?variant=' + variant : ''));
    if (!res.ok) { log('/token failed: ' + res.status); return; }
    const { token } = await res.json();
    withRelayo((r) => r.identify(token));
    log('Relayo.identify(<' + (variant || 'valid') + ' token for ' + user + '>)');
  }

  function renderAccount() {
    document.getElementById('who').innerHTML = user
      ? 'Signed in as client <b>' + user + '</b>'
      : '<span class="muted">Not signed in. The widget works anonymously.</span>';
    document.getElementById('logout').style.display = user ? '' : 'none';
    const box = document.getElementById('loginButtons');
    box.innerHTML = '';
    if (!user) CLIENT_IDS.forEach((id) => {
      const b = document.createElement('button');
      b.className = 'primary';
      b.textContent = 'Sign in as client ' + id;
      b.onclick = async () => {
        await fetch('/login', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ clientId: id }) });
        user = id; renderAccount(); log('Signed in as ' + id);
        await identify();
      };
      box.appendChild(b);
    });
  }

  document.getElementById('logout').onclick = async () => {
    await fetch('/logout', { method: 'POST' });
    Relayo.logout();
    log('Signed out, Relayo.logout()');
    user = null; renderAccount();
  };
  document.querySelectorAll('#badTokens button').forEach((b) => b.onclick = () => identify(b.dataset.variant));

  withRelayo((r) => ['ready', 'open', 'close', 'message', 'unreadChanged'].forEach((e) =>
    r.on(e, (payload) => log('event ' + e + (payload !== undefined ? ': ' + JSON.stringify(payload).slice(0, 120) : '')))));

  renderAccount();
  identify();
</script>
</body>
</html>
""";
}
