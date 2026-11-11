using Microsoft.Extensions.Configuration;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ActionstepGet;

public sealed class ActionstepClient : IDisposable
{
    private readonly IConfiguration _cfg;
    private readonly HttpClient _http = new();

    private sealed class Session
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTimeOffset ExpiresAt { get; set; }
        public string? ApiEndpoint { get; set; }
    }

    private Session? _session;


    public bool HasSession => _session != null && !string.IsNullOrWhiteSpace(_session.ApiEndpoint);

    private string EnvironmentName => (_cfg["Actionstep:Environment"] ?? "Production").Trim();

    public bool IsStagingEnvironment =>
        string.Equals(EnvironmentName, "Staging", StringComparison.OrdinalIgnoreCase);

    private string ClientId => _cfg[$"Actionstep:{EnvironmentName}:ClientId"] ?? "";
    private string ClientSecret => _cfg[$"Actionstep:{EnvironmentName}:ClientSecret"] ?? "";
    private string Scopes => _cfg["Actionstep:Scopes"] ?? "all";

    private string RedirectHost => _cfg["Actionstep:RedirectHost"] ?? "127.0.0.1";
    private int RedirectPort => int.TryParse(_cfg["Actionstep:RedirectPort"], out var p) ? p : 7890;

    private string VaultPath
    {
        get
        {
            var identity = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(EnvironmentName + ":" + ClientId)))[..16];
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ActionstepGet", identity + ".auth.dat");
        }
    }

    public string EnvironmentLabel => EnvironmentName;

    public ActionstepClient(IConfiguration cfg)
    {
        _cfg = cfg;
        _http.Timeout = TimeSpan.FromMinutes(2);
        TryLoadSession();
    }

    public async Task<bool> TryResumeAsync(CancellationToken ct = default)
    {
        try
        {
            if (_session == null) return false;

            if (string.IsNullOrWhiteSpace(_session.AccessToken) ||
                string.IsNullOrWhiteSpace(_session.RefreshToken) ||
                string.IsNullOrWhiteSpace(_session.ApiEndpoint))
                return false;

            if (_session.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
                await RefreshAsync(ct);

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            Logout();
            return false;
        }
    }

    public async Task<bool> LoginInteractiveAsync(CancellationToken ct = default)
    {
        await InteractiveLoginAsync(ct);
        return HasSession;
    }

    public void Logout()
    {
        _session = null;
        try
        {
            if (File.Exists(VaultPath))
                File.Delete(VaultPath);
        }
        catch
        {
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_session == null)
            throw new InvalidOperationException("Not signed in.");

        if (_session.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            await RefreshAsync(ct);

        return _session.AccessToken;
    }

    private Uri GetRestBase()
    {
        if (_session == null || string.IsNullOrWhiteSpace(_session.ApiEndpoint))
            throw new InvalidOperationException("Actionstep API endpoint is unknown. Sign in first.");

        var raw = _session.ApiEndpoint.Trim();
        var trimmed = raw.TrimEnd('/');

        if (trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            return new Uri(trimmed + "/rest/");

        return new Uri(trimmed + "/api/rest/");
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(
        HttpMethod method,
        string pathAndQuery,
        string? body,
        CancellationToken ct)
    {
        var uri = ResolveRequestUri(pathAndQuery);
        var token = await GetAccessTokenAsync(ct);

        var req = new HttpRequestMessage(method, uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.api+json"));

        if (body != null)
        {
            req.Content = new StringContent(
                body,
                Encoding.UTF8,
                "application/vnd.api+json");
        }

        return req;
    }

    public sealed record GetResponse(int StatusCode, string Reason, string Body)
    {
        public bool IsSuccess => StatusCode is >= 200 and <= 299;
    }

    public Uri ResolveRequestUri(string input)
    {
        var text = input.Trim();
        if (text.StartsWith("GET ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("POST ", StringComparison.OrdinalIgnoreCase))
        {
            text = text[(text.IndexOf(' ') + 1)..].Trim();
        }

        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsWhiteSpace))
            throw new ArgumentException("Enter an endpoint. Encode spaces in the query as %20 or +.");

        var restBase = GetRestBase();
        Uri uri;
        if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            uri = new Uri(text, UriKind.Absolute);
        else
        {
            text = text.TrimStart('/');
            if (text.StartsWith("api/rest/", StringComparison.OrdinalIgnoreCase)) text = text[9..];
            uri = new Uri(restBase, text);
        }

        // Only send the session token to its own REST endpoint.
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Authority, restBase.Authority, StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith(restBase.AbsolutePath, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Use an Actionstep REST path for the signed-in API endpoint.");
        return uri;
    }

    public async Task<GetResponse> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        using var req = await BuildRequestAsync(
            HttpMethod.Get,
            pathAndQuery,
            null,
            ct);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        return new GetResponse((int)res.StatusCode, res.ReasonPhrase ?? "", body);
    }

    public async Task<GetResponse> PostAsync(
        string pathAndQuery,
        string body,
        CancellationToken ct)
    {
        using var req = await BuildRequestAsync(
            HttpMethod.Post,
            pathAndQuery,
            body,
            ct);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var responseBody = await res.Content.ReadAsStringAsync(ct);
        return new GetResponse((int)res.StatusCode, res.ReasonPhrase ?? "", responseBody);
    }

    public void Dispose() => _http.Dispose();

    private (string Authority, string TokenEndpoint) GetOAuthEndpoints()
    {
        var section = _cfg.GetSection($"Actionstep:{EnvironmentName}");

        var authority = section["Authority"];
        var token = section["TokenEndpoint"];

        if (!string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(token))
            return (authority!, token!);

        throw new InvalidOperationException(
            $"Missing Authority or TokenEndpoint for Actionstep environment '{EnvironmentName}'.");
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        if (_session == null || string.IsNullOrWhiteSpace(_session.RefreshToken))
            throw new InvalidOperationException("Session expired.");

        var (_, tokenEndpoint) = GetOAuthEndpoints();

        using var req = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _session.RefreshToken
            })
        };

        req.Headers.Authorization = BuildBasicAuth();

        using var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(body);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        _session = new Session
        {
            AccessToken = root.GetProperty("access_token").GetString() ?? "",
            RefreshToken = root.TryGetProperty("refresh_token", out var rt)
                ? rt.GetString() ?? _session.RefreshToken
                : _session.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(
                root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() - 30 : 3600),
            ApiEndpoint = root.TryGetProperty("api_endpoint", out var api)
                ? api.GetString()
                : _session.ApiEndpoint
        };

        SaveSession();
    }

    private async Task InteractiveLoginAsync(CancellationToken ct)
    {
        var (authority, tokenEndpoint) = GetOAuthEndpoints();
        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
            throw new InvalidOperationException("Add your credentials to appsettings.actionstep.json first.");

        var redirectUri = $"http://{RedirectHost}:{RedirectPort}/";
        var state = Base64Url(RandomBytes(32));

        var authorizeUrl =
            $"{authority.TrimEnd('/')}/api/oauth/authorize" +
            $"?response_type=code" +
            $"&client_id={Uri.EscapeDataString(ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(Scopes)}" +
            $"&state={state}";

        using var listener = new LoopbackHttpListener(redirectUri);
        OpenBrowser(authorizeUrl);

        var callbackUrl = await listener.WaitForCallbackAsync(ct);
        var callback = new Uri(callbackUrl);
        var qs = ParseQuery(callback.Query);

        if (!qs.TryGetValue("code", out var code))
            throw new InvalidOperationException("Missing authorization code.");

        if (!qs.TryGetValue("state", out var cbState) || cbState != state)
            throw new InvalidOperationException("OAuth state mismatch.");

        using var tokenReq = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri
            })
        };

        tokenReq.Headers.Authorization = BuildBasicAuth();

        using var res = await _http.SendAsync(tokenReq, ct);
        var payload = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(payload);

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        _session = new Session
        {
            AccessToken = root.GetProperty("access_token").GetString() ?? "",
            RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(
                root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() - 30 : 3600),
            ApiEndpoint = root.TryGetProperty("api_endpoint", out var api)
                ? api.GetString()
                : null
        };

        SaveSession();
    }

    private void TryLoadSession()
    {
        try
        {
            if (!File.Exists(VaultPath)) return;

            var enc = File.ReadAllBytes(VaultPath);
            var bytes = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            _session = JsonSerializer.Deserialize<Session>(bytes);

            if (_session == null ||
                string.IsNullOrWhiteSpace(_session.AccessToken) ||
                string.IsNullOrWhiteSpace(_session.RefreshToken) ||
                string.IsNullOrWhiteSpace(_session.ApiEndpoint))
                _session = null;
        }
        catch
        {
            _session = null;
        }
    }

    private void SaveSession()
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_session);
            var enc = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(VaultPath)!);
            File.WriteAllBytes(VaultPath, enc);
        }
        catch
        {
        }
    }

    private AuthenticationHeaderValue BuildBasicAuth()
    {
        var raw = $"{ClientId}:{ClientSecret}";
        var b64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(raw));
        return new AuthenticationHeaderValue("Basic", b64);
    }

    private static void OpenBrowser(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] RandomBytes(int len)
    {
        var buf = new byte[len];
        RandomNumberGenerator.Fill(buf);
        return buf;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(query))
            return result;

        if (query.StartsWith("?"))
            query = query[1..];

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0]);
            var val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            result[key] = val;
        }

        return result;
    }

    private sealed class LoopbackHttpListener : IDisposable
    {
        private readonly HttpListener _listener;

        public LoopbackHttpListener(string prefix)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            _listener.Start();
        }

        public async Task<string> WaitForCallbackAsync(CancellationToken ct)
        {
            using var reg = ct.Register(() => { try { _listener.Stop(); } catch { } });

            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) when (ct.IsCancellationRequested)
            { throw new OperationCanceledException(ct); }
            var url = ctx.Request.Url?.ToString() ?? "";

            const string html = "<html><body>Signed in. You can close this window.</body></html>";
            var buf = Encoding.UTF8.GetBytes(html);

            ctx.Response.ContentType = "text/html";
            ctx.Response.ContentLength64 = buf.Length;
            await ctx.Response.OutputStream.WriteAsync(buf, 0, buf.Length, ct);
            ctx.Response.OutputStream.Close();

            return url;
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }

}
