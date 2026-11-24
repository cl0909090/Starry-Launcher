using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StarryLauncher;

public static class LauncherService
{

    public const string MsaClientId = "71a92802-6c97-4de2-809d-281aa57ada96";
    private const string MsaScope = "XboxLive.signin offline_access";

    internal static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 64,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        EnableMultipleHttp2Connections = true,
    })
    {
        Timeout = TimeSpan.FromSeconds(45),

    };
    static LauncherService() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("StarryLauncher/1.0 (Starry Launcher; mc-installer)");

    public static event Action<string>? Log;

    private static readonly List<string> _logBacklog = new();

    private static long GetAutoMemoryMb()
    {
        try
        {
            var st = new NativeMemoryStatus { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref st)) return 4096;
            long totalMb = (long)(st.ullTotalPhys / (1024 * 1024));
            return Math.Max(2048, totalMb);
        }
        catch { return 4096; }
    }

    private static List<string> ReadPclAdvanceJvm(McInstance inst)
    {
        var list = new List<string>();
        try
        {
            string ini = Path.Combine(inst.Dir, "PCL", "Setup.ini");
            if (!File.Exists(ini)) return list;
            foreach (var line in File.ReadAllLines(ini))
            {
                const string key = "VersionAdvanceJvm:";
                if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
                string v = line[key.Length..].Trim();
                if (string.IsNullOrWhiteSpace(v)) continue;

                var sb = new StringBuilder();
                bool inQuote = false;
                foreach (char c in v)
                {
                    if (c == '"') { inQuote = !inQuote; continue; }
                    if (c == ' ' && !inQuote)
                    {
                        if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                    }
                    else sb.Append(c);
                }
                if (sb.Length > 0) list.Add(sb.ToString());
            }
        }
        catch { }
        return list;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeMemoryStatus
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref NativeMemoryStatus buffer);

    private static void Say(string msg)
    {
        lock (_logBacklog)
        {
            _logBacklog.Add(msg);
            if (_logBacklog.Count > 500) _logBacklog.RemoveRange(0, _logBacklog.Count - 500);
        }
        Log?.Invoke(msg);
        try { AppLog.Info("[启动] " + msg); } catch { }
    }

    public static string BacklogSnapshot()
    {
        lock (_logBacklog) return string.Join(Environment.NewLine, _logBacklog);
    }

    public static bool HasAccount => IsPremiumLoggedIn;

    public static bool IsPremiumLoggedIn
    {
        get
        {
            var st = AppSettings.Shared;
            return string.Equals(st.McLoginType, "microsoft", StringComparison.OrdinalIgnoreCase)
                   && !string.IsNullOrWhiteSpace(st.McUserName)
                   && !string.IsNullOrWhiteSpace(st.McAccessToken)
                   && !string.IsNullOrWhiteSpace(st.McRefreshToken);
        }
    }

    public static string ResolvePlayerName()
    {
        var st = AppSettings.Shared;
        if (!string.IsNullOrWhiteSpace(st.McUserName)) return st.McUserName.Trim();
        var dn = st.DisplayName;
        return string.IsNullOrWhiteSpace(dn) ? "Player" : dn.Trim();
    }

    public sealed record MsaLoginSession(string AuthorizeUrl, int Port, string State, string Verifier);

    private static readonly Dictionary<int, System.Net.Sockets.TcpListener> _msaListeners = new();

    public static MsaLoginSession BeginMicrosoftLogin()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        string redirectUri = $"http://localhost:{port}/";
        string verifier = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(48));
        string challenge = System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        string authorize =
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize" +
            $"?client_id={MsaClientId}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(MsaScope)}" +
            $"&code_challenge={challenge}&code_challenge_method=S256" +
            $"&state={state}&prompt=select_account";
        _msaListeners[port] = listener;
        return new MsaLoginSession(authorize, port, state, verifier);
    }

    public static async Task CompleteMicrosoftLoginAsync(MsaLoginSession s, CancellationToken ct)
    {
        if (!_msaListeners.Remove(s.Port, out var listener))
            throw new InvalidOperationException("launcher.err.msaAuth");
        try
        {
            using (listener)
            {
                string code = await WaitForAuthCodeAsync(listener, s.State, ct);
                string msaToken = "", refresh = "";
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var pairs = new Dictionary<string, string>
                    {
                        ["client_id"] = MsaClientId,
                        ["grant_type"] = "authorization_code",
                        ["code"] = code,
                        ["redirect_uri"] = $"http://localhost:{s.Port}/",
                        ["code_verifier"] = s.Verifier,
                    };
                    using (var content = new FormUrlEncodedContent(pairs))
                    using (var resp = await Http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", content, ct))
                    {
                        Say($"M令牌交换响应: HTTP {(int)resp.StatusCode}");
                        var json = await resp.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        var r = doc.RootElement;
                        if (r.TryGetProperty("error", out var err))
                        {
                            string ecode = err.GetString() ?? "";
                            string edesc = r.TryGetProperty("error_description", out var ed) ? ed.GetString() ?? "" : "";
                            Say($"M令牌交换失败: {ecode} {TruncLog(edesc)}");
                            if (ecode == "invalid_grant" && attempt == 0 && !string.IsNullOrEmpty(_msaAltCode) && _msaAltCode != code)
                            {
                                Say("M使用备用 code 解码形式重试");
                                code = _msaAltCode;
                                continue;
                            }
                            throw new InvalidOperationException(
                                ecode == "invalid_grant" ? "launcher.err.msaExpired" : "launcher.err.msaAuth");
                        }
                        msaToken = r.GetProperty("access_token").GetString()!;
                        refresh = r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
                        Say("M令牌交换成功");
                    }
                    break;
                }
                _msaAltCode = null;
                await FinishMicrosoftLoginAsync(msaToken, refresh);
            }
        }
        catch (Exception ex)
        {
            Say($"M登录流程异常: {ex.GetType().Name}: {TruncLog(ex.Message)}");
            throw;
        }
    }

    private static string? _msaAltCode;

    private static string TruncLog(string s) => s.Length <= 220 ? s : s[..220] + $"…(共{s.Length}字符)";

    private static async Task<string> WaitForAuthCodeAsync(System.Net.Sockets.TcpListener listener, string expectedState, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var client = await listener.AcceptTcpClientAsync(ct);
            string code = "", codeAlt = "", error = "", state = "";
            try
            {
                using var stream = client.GetStream();
                using var readCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(15000);
                var buf = new MemoryStream();
                var tmp = new byte[8192];
                while (buf.Length < 64 * 1024)
                {
                    int n = await stream.ReadAsync(tmp, readCts.Token);
                    if (n <= 0) break;
                    await buf.WriteAsync(tmp.AsMemory(0, n), ct);
                    if (Encoding.UTF8.GetString(buf.ToArray()).Contains("\r\n\r\n")) break;
                }
                string raw = Encoding.UTF8.GetString(buf.ToArray());
                int lineEnd = raw.IndexOf("\r\n", StringComparison.Ordinal);
                string requestLine = lineEnd < 0 ? raw : raw[..lineEnd];
                if (requestLine.Length > 0) Say($"M登录回跳: {TruncLog(requestLine)}");
                int qStart = requestLine.IndexOf('?');
                if (qStart >= 0)
                {
                    int qEnd = requestLine.LastIndexOf(" HTTP/", StringComparison.Ordinal);
                    string query = qEnd < 0 ? requestLine[(qStart + 1)..] : requestLine[(qStart + 1)..qEnd];
                    foreach (var part in query.Split('&'))
                    {
                        int eq = part.IndexOf('=');
                        if (eq < 0) continue;
                        string key = part[..eq];
                        string val = part[(eq + 1)..];
                        try
                        {
                            if (key == "code")
                            {
                                code = Uri.UnescapeDataString(val.Replace('+', ' '));
                                codeAlt = Uri.UnescapeDataString(val);
                            }
                            else if (key == "error") error = Uri.UnescapeDataString(val);
                            else if (key == "state") state = Uri.UnescapeDataString(val.Replace('+', ' '));
                        }
                        catch (Exception ex) { Say($"M回跳参数解析失败: {TruncLog(key)} ({ex.GetType().Name}: {ex.Message})"); }
                    }
                }
                Say($"M回跳解析: code={(code == "" ? "无" : "有")}, error={error}, state命中={(state == expectedState ? "是" : $"否({TruncLog(state)})")}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Say("M回跳读取超时（浏览器预连接），继续等待");
                continue;
            }
            catch { }
            if (code != "" && state != "" && state != expectedState)
            {
                Say("M回跳 state 不匹配，拒绝本次回跳");
                WriteMsaHtml(client, "<html><body style='font-family:sans-serif;text-align:center;padding-top:60px'><h3>安全校验未通过 / Security check failed</h3><p>请回到启动器重新点击登录 / Retry sign-in from the launcher</p></body></html>");
                continue;
            }
            if (error == "access_denied")
            {
                WriteMsaHtml(client, "<html><body style='font-family:sans-serif;text-align:center;padding-top:60px'><h3>授权已取消 / Authorization declined</h3><p>请回到启动器重试 / Retry from the launcher</p></body></html>");
                throw new InvalidOperationException("launcher.err.msaDenied");
            }
            if (code == "") continue;
            _msaAltCode = code != codeAlt ? codeAlt : "";
            WriteMsaHtml(client, "<html><body style='font-family:sans-serif;text-align:center;padding-top:60px'><h3>登录成功 / Signed in</h3><p>请返回启动器 / Return to the launcher</p></body></html>");
            return code;
        }
    }

    private static void WriteMsaHtml(System.Net.Sockets.TcpClient client, string html)
    {
        try
        {
            var stream = client.GetStream();
            byte[] body = Encoding.UTF8.GetBytes(html);
            string head = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            stream.Write(Encoding.UTF8.GetBytes(head));
            stream.Write(body);
            stream.Flush();
        }
        catch { }
    }

    public static async Task<bool> TryRefreshMicrosoftAsync()
    {
        var st = AppSettings.Shared;
        if (st.McLoginType != "microsoft" || string.IsNullOrWhiteSpace(st.McRefreshToken)) return false;

        if (st.McTokenExpiresAt > 0 && st.McTokenExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300) return true;
        try
        {
            var pairs = new Dictionary<string, string>
            {
                ["client_id"] = MsaClientId,
                ["refresh_token"] = st.McRefreshToken,
                ["grant_type"] = "refresh_token",
                ["scope"] = MsaScope,
            };
            using var content = new FormUrlEncodedContent(pairs);
            using var resp = await Http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", content);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty("access_token", out var at)) return false;
            await FinishMicrosoftLoginAsync(at.GetString()!,
                r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? st.McRefreshToken : st.McRefreshToken);
            return true;
        }
        catch { return false; }
    }

    private static async Task FinishMicrosoftLoginAsync(string msaToken, string refreshToken)
    {

        var xbl = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate",
            $$"""{"Properties":{"AuthMethod":"RPS","SiteName":"user.auth.xboxlive.com","RpsTicket":"d={{msaToken}}"},"RelyingParty":"http://auth.xboxlive.com","TokenType":"JWT"}""");
        string xblToken = xbl.GetProperty("Token").GetString()!;
        string uhs = xbl.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!;
        Say("M已获取 XBL 用户令牌");

        var xsts = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize",
            $$"""{"Properties":{"SandboxId":"RETAIL","UserTokens":["{{xblToken}}"]},"RelyingParty":"rp://api.minecraftservices.com/","TokenType":"JWT"}""");
        if (!xsts.TryGetProperty("Token", out var xstsTokenEl))
            throw new InvalidOperationException("launcher.err.noXbox");
        string xstsToken = xstsTokenEl.GetString()!;
        uhs = xsts.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!;
        Say("M已获取 XSTS 令牌");

        var mcLogin = await PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox",
            $$"""{"identityToken":"XBL3.0 x={{uhs}};{{xstsToken}}"}""");
        string mcToken = mcLogin.GetProperty("access_token").GetString()!;
        long expiresIn = mcLogin.TryGetProperty("expires_in", out var ei) ? ei.GetInt64() : 86400;
        Say("M已获取 Minecraft 访问令牌");

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);
        using var pResp = await Http.SendAsync(req);
        if (!pResp.IsSuccessStatusCode)
        {
            Say($"M获取档案失败: HTTP {(int)pResp.StatusCode}");
            throw new InvalidOperationException("launcher.err.noMc");
        }
        var profile = await JsonDocument.ParseAsync(await pResp.Content.ReadAsStreamAsync());
        string name = profile.RootElement.GetProperty("name").GetString()!;
        string id = profile.RootElement.GetProperty("id").GetString()!;
        Say($"M登录完成: {name}");

        var st = AppSettings.Shared;
        st.McLoginType = "microsoft";
        st.McUserName = name;
        st.McUuid = id;
        st.McAccessToken = mcToken;
        st.McRefreshToken = refreshToken;
        st.McTokenExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn;
        RememberCurrentAccount();
        st.Save();
        Say(S.Get("launcher.login.ok") + " · " + name);
    }

    public static void RememberCurrentAccount()
    {
        var st = AppSettings.Shared;
        if (string.IsNullOrWhiteSpace(st.McUserName)) return;
        var cur = new AppSettings.AccountEntry
        {
            Type = st.McLoginType,
            Name = st.McUserName,
            Uuid = st.McUuid,
            AccessToken = st.McAccessToken,
            RefreshToken = st.McRefreshToken,
            ExpiresAt = st.McTokenExpiresAt,
        };
        var old = st.McAccounts.FirstOrDefault(a => a.Key == cur.Key);
        if (old != null) st.McAccounts.Remove(old);
        st.McAccounts.Insert(0, cur);
        if (st.McAccounts.Count > 20) st.McAccounts.RemoveAt(st.McAccounts.Count - 1);
    }

    public static void SwitchToAccount(AppSettings.AccountEntry acc)
    {
        var st = AppSettings.Shared;
        st.McLoginType = acc.Type;
        st.McUserName = acc.Name;
        st.McUuid = acc.Uuid;
        st.McAccessToken = acc.AccessToken;
        st.McRefreshToken = acc.RefreshToken;
        st.McTokenExpiresAt = acc.ExpiresAt;
        RememberCurrentAccount();
        st.Save();
        Say(S.Get("launcher.login.ok") + " · " + acc.Name);
    }

    public static void RemoveAccount(AppSettings.AccountEntry acc)
    {
        var st = AppSettings.Shared;
        st.McAccounts.RemoveAll(a => a.Key == acc.Key);
        st.Save();
    }

    public static void Logout(bool keepLoginType = false)
    {
        var st = AppSettings.Shared;
        RememberCurrentAccount();
        if (!keepLoginType) st.McLoginType = "offline";
        st.McUserName = "";
        st.McUuid = "";
        st.McAccessToken = "";
        st.McRefreshToken = "";
        st.McTokenExpiresAt = 0;
        st.Save();
    }

    private static async Task DownloadToFileAsync(string url, string dest)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst);
    }

    internal static string MirrorOf(string url)
    {
        if (UseOfficialSource) return url;
        return url
            .Replace("https://libraries.minecraft.net", "https://bmclapi2.bangbang93.com/maven")
            .Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com")
            .Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com")

            .Replace("https://launcher.mojang.com", "https://bmclapi2.bangbang93.com")
            .Replace("https://launchermeta.mojang.com", "https://bmclapi2.bangbang93.com")
            .Replace("https://resources.download.minecraft.net", "https://bmclapi2.bangbang93.com/assets");
    }

    internal static bool UseOfficialSource
        => string.Equals(AppSettings.Shared.McDownloadSource, "official", StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeGameRoot(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "";
        string d;
        try { d = Path.GetFullPath(dir.Trim()); } catch { return dir; }
        string name = Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Equals(name, ".minecraft", StringComparison.OrdinalIgnoreCase) ? d : Path.Combine(d, ".minecraft");
    }

    private static async Task DownloadMirrorAsync(string url, string dest)
    {
        string mirror = MirrorOf(url);
        Exception? last = null;
        foreach (var u in mirror == url ? new[] { url } : new[] { mirror, url })
        {
            try { await DownloadToFileAsync(u, dest); return; }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new InvalidOperationException("download failed: " + url);
    }

    private static string ParseAuthError(string json, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("errorMessage", out var m))
                return m.GetString() ?? $"launcher.err.authFailed({status})";
        }
        catch { }
        return status == 403 ? "launcher.err.wrongPass" : "launcher.err.authFailed";
    }

    private static async Task<JsonElement> PostJsonAsync(string url, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(url, content);
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {

            Say($"auth fail: {(int)resp.StatusCode} {url} body={json}");
            throw new InvalidOperationException("launcher.err.authFailed");
        }
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public sealed record McInstance(string Name, string Dir, string JarPath, string JsonPath, DateTime LastWrite,
        string Root = "", string RootLabel = "", string Loader = "vanilla")
    {

        public string PlayedText =>
            (string.IsNullOrEmpty(RootLabel) ? "" : RootLabel + " · ") + LastWrite.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    public static List<string> FindGameDirs()
    {
        var st = AppSettings.Shared;
        var dirs = new List<string>();
        void Add(string? d)
        {
            if (string.IsNullOrWhiteSpace(d)) return;
            try { d = Path.GetFullPath(d); } catch { return; }

            if (st.McGamePathsExcluded.Any(x =>
                    string.Equals(x, d, StringComparison.OrdinalIgnoreCase))) return;
            if (Directory.Exists(d) && !dirs.Contains(d, StringComparer.OrdinalIgnoreCase)) dirs.Add(d);
        }
        foreach (var p in st.McGamePaths) Add(NormalizeGameRoot(p));
        Add(NormalizeGameRoot(st.McGameDir));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"));
        Add(Path.Combine(AppDirs.ExeDir, ".minecraft"));

        foreach (var baseDir in PclSearchBases())
        {
            try
            {
                foreach (var d in Directory.GetDirectories(baseDir, "*pcl*", SearchOption.TopDirectoryOnly))
                    Add(Path.Combine(d, ".minecraft"));
                foreach (var d in Directory.GetDirectories(baseDir, ".minecraft", SearchOption.TopDirectoryOnly))
                    Add(d);
                foreach (var mid in Directory.GetDirectories(baseDir, "*", SearchOption.TopDirectoryOnly))
                {
                    foreach (var d in Directory.GetDirectories(mid, ".minecraft", SearchOption.TopDirectoryOnly))
                        Add(d);

                    if (IsValidGameRoot(mid)) Add(mid);
                    else
                    {
                        foreach (var d in Directory.GetDirectories(mid, "*", SearchOption.TopDirectoryOnly))
                            if (IsValidGameRoot(d)) Add(d);
                    }
                }
            }
            catch { }
        }
        return dirs;
    }

    public static List<string> VisibleGameDirs()
    {
        var all = FindGameDirs();
        var enabled = AppSettings.Shared.McGamePathsEnabled;
        if (enabled is null) return all;
        if (enabled.Count == 0) return [];
        return all.Where(d => enabled.Any(e =>
            string.Equals(e, d, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public static bool IsGamePathEnabled(string dir)
    {
        var enabled = AppSettings.Shared.McGamePathsEnabled;
        if (enabled is null) return true;
        if (enabled.Count == 0) return false;
        return enabled.Any(e => string.Equals(e, dir, StringComparison.OrdinalIgnoreCase));
    }

    public static void SetEnabledGamePaths(List<string>? dirs)
    {
        var st = AppSettings.Shared;
        st.McGamePathsEnabled = dirs?.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        st.Save();
    }

    public static string AddGamePath(string dir)
    {
        var st = AppSettings.Shared;
        var norm = NormalizeGameRoot(dir);
        if (!string.IsNullOrWhiteSpace(norm))
        {
            if (!st.McGamePaths.Any(p => string.Equals(p, norm, StringComparison.OrdinalIgnoreCase)))
                st.McGamePaths.Add(norm);
            st.McGamePathsExcluded.RemoveAll(x => string.Equals(x, norm, StringComparison.OrdinalIgnoreCase));

            if (st.McGamePathsEnabled is { Count: > 0 } &&
                !st.McGamePathsEnabled.Any(p => string.Equals(p, norm, StringComparison.OrdinalIgnoreCase)))
                st.McGamePathsEnabled.Add(norm);
            st.McGameDir = norm;
            st.Save();
        }
        return norm;
    }

    public static void RemoveGamePath(string dir)
    {
        var st = AppSettings.Shared;
        st.McGamePaths.RemoveAll(p => string.Equals(p, dir, StringComparison.OrdinalIgnoreCase));
        if (!st.McGamePathsExcluded.Any(x => string.Equals(x, dir, StringComparison.OrdinalIgnoreCase)))
            st.McGamePathsExcluded.Add(dir);
        st.McGamePathsEnabled?.RemoveAll(p => string.Equals(p, dir, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(st.McGameDir, dir, StringComparison.OrdinalIgnoreCase))
            st.McGameDir = st.McGamePaths.FirstOrDefault() ?? "";
        st.Save();
    }

    public static void SetDefaultGamePath(string dir)
    {
        var st = AppSettings.Shared;
        st.McGameDir = dir;
        st.Save();
    }

    public static string DefaultGamePathAlias(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "";
        var trimmed = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leaf = Path.GetFileName(trimmed);
        if (string.Equals(leaf, ".minecraft", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(trimmed);
            var name = string.IsNullOrEmpty(parent) ? "" : Path.GetFileName(parent);
            return string.IsNullOrEmpty(name) ? ".minecraft" : name;
        }
        return string.IsNullOrEmpty(leaf) ? trimmed : leaf;
    }

    public static string GetGamePathAlias(string dir)
    {
        var st = AppSettings.Shared;
        var kv = st.McGamePathAliases.FirstOrDefault(k =>
            string.Equals(k.Key, dir, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(kv.Value)) return kv.Value;
        return DefaultGamePathAlias(dir);
    }

    public static void SetGamePathAlias(string dir, string name)
    {
        var st = AppSettings.Shared;
        name = (name ?? "").Trim();
        var key = st.McGamePathAliases.Keys.FirstOrDefault(k =>
            string.Equals(k, dir, StringComparison.OrdinalIgnoreCase)) ?? dir;
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, DefaultGamePathAlias(dir), StringComparison.Ordinal))
            st.McGamePathAliases.Remove(key);
        else
            st.McGamePathAliases[key] = name;
        st.Save();
    }

    public sealed record GameFolderScanProgress(int Done, int Total, string Current);

    private static readonly HashSet<string> ScanSkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin",
        "System Volume Information", "Recovery", "node_modules", ".git", ".workbuddy",
        ".cache", ".npm", ".nuget", ".gradle", "Temp", "__pycache__", "AppData",
    };

    private static List<string> ScanDriveRoots()
    {
        var roots = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable)
                    roots.Add(drive.RootDirectory.FullName);
            }
        }
        catch { }
        return roots;
    }

    public static async Task<List<string>> DetectGameFoldersAsync(
        IProgress<GameFolderScanProgress>? progress = null, CancellationToken ct = default)
    {
        var found = new List<string>();
        void Add(string? d)
        {
            if (string.IsNullOrWhiteSpace(d)) return;
            try { d = Path.GetFullPath(d); } catch { return; }
            if (Directory.Exists(d) && !found.Contains(d, StringComparer.OrdinalIgnoreCase)) found.Add(d);
        }

        var st = AppSettings.Shared;

        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"));
        Add(Path.Combine(AppDirs.ExeDir, ".minecraft"));
        foreach (var p in st.McGamePaths) Add(p);
        Add(st.McGameDir);

        var bases = ScanDriveRoots();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && !bases.Any(b => string.Equals(b, home, StringComparison.OrdinalIgnoreCase)))
            bases.Add(home);
        bases = bases.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var queue = new Queue<(string Dir, int Depth, int MaxDepth)>();
        foreach (var b in bases)
            queue.Enqueue((b, 0, string.Equals(b, home, StringComparison.OrdinalIgnoreCase) ? 6 : 4));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            var (dir, depth, maxDepth) = queue.Dequeue();
            if (!visited.Add(dir)) continue;
            try
            {
                foreach (var sub in Directory.GetDirectories(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(sub.TrimEnd(Path.DirectorySeparatorChar));
                    if (ScanSkipDirs.Contains(name)) continue;
                    if (depth + 1 <= maxDepth) queue.Enqueue((sub, depth + 1, maxDepth));
                }
            }
            catch { }
        }
        var scanDirs = visited.ToList();

        int total = scanDirs.Count;
        await Task.Run(() =>
        {
            for (int i = 0; i < scanDirs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var dir = scanDirs[i];
                try
                {

                    var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
                    if (string.Equals(name, ".minecraft", StringComparison.OrdinalIgnoreCase)) Add(dir);
                    else
                    {
                        var mc = Path.Combine(dir, ".minecraft");
                        if (Directory.Exists(mc)) Add(mc);
                    }
                    if (IsValidGameRoot(dir)) Add(dir);
                }
                catch { }
                progress?.Report(new GameFolderScanProgress(i + 1, total, dir));
            }
        }, ct);
        return found;
    }

    public static int CommitDetectedPaths(List<string> found)
    {
        var st = AppSettings.Shared;
        int added = 0;
        foreach (var dir in found)
        {
            st.McGamePathsExcluded.RemoveAll(x => string.Equals(x, dir, StringComparison.OrdinalIgnoreCase));
            if (st.McGamePaths.Any(p => string.Equals(p, dir, StringComparison.OrdinalIgnoreCase))) continue;
            st.McGamePaths.Add(dir);
            added++;
        }
        st.Save();
        return added;
    }

    private static bool IsValidGameRoot(string dir)
    {
        try
        {
            var versionsDir = Path.Combine(dir, "versions");
            if (!Directory.Exists(versionsDir)) return false;
            foreach (var v in Directory.GetDirectories(versionsDir))
            {
                var name = Path.GetFileName(v);
                if (File.Exists(Path.Combine(v, name + ".json")) || File.Exists(Path.Combine(v, name + ".jar")))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static IEnumerable<string> PclSearchBases()
    {
        var bases = ScanDriveRoots();
        try
        {
            bases.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            bases.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            bases.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
            bases.Add(AppDirs.ExeDir);
        }
        catch { }
        return bases;
    }

    private static string RootLabelOf(string root)
    {
        try
        {
            var appdata = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
            if (string.Equals(Path.GetFullPath(root).TrimEnd('\\'), Path.GetFullPath(appdata).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return "";
            string name = Path.GetFileName(root.TrimEnd('\\'));
            if (name.Equals(".minecraft", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(root.TrimEnd('\\'));
                name = parent != null ? Path.GetFileName(parent) : "";
            }
            return name;
        }
        catch { return ""; }
    }

    public static List<McInstance> ScanInstances(string gameDir)
    {
        var list = new List<McInstance>();
        var versionsDir = Path.Combine(gameDir, "versions");
        if (!Directory.Exists(versionsDir)) return list;
        string label = RootLabelOf(gameDir);
        foreach (var dir in Directory.GetDirectories(versionsDir))
        {
            var name = Path.GetFileName(dir);
            var jar = Path.Combine(dir, name + ".jar");
            var json = Path.Combine(dir, name + ".json");
            if (!File.Exists(jar) && !File.Exists(json)) continue;
            list.Add(new McInstance(name, dir, jar, json, Directory.GetLastWriteTimeUtc(dir), gameDir, label,
                DetectLoader(name, json)));
        }
        return list.OrderByDescending(i => i.LastWrite).ToList();
    }

    private static string DetectLoader(string name, string jsonPath)
    {
        try
        {
            var text = File.ReadAllText(jsonPath);
            if (text.Contains("optifine", StringComparison.OrdinalIgnoreCase)) return "optifine";
            if (text.Contains("neoforge", StringComparison.OrdinalIgnoreCase)) return "neoforge";
            if (text.Contains("fabricloader", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("fabricmc", StringComparison.OrdinalIgnoreCase)) return "fabric";
            if (text.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("fmlloader", StringComparison.OrdinalIgnoreCase)) return "forge";
        }
        catch { }
        if (name.Contains("neoforge", StringComparison.OrdinalIgnoreCase)) return "neoforge";
        if (name.Contains("fabric", StringComparison.OrdinalIgnoreCase)) return "fabric";
        if (name.Contains("forge", StringComparison.OrdinalIgnoreCase)) return "forge";
        return "vanilla";
    }

    public sealed record ResTarget(string Title, string Sub, string BaseDir, bool Compat, string Loader);

    public static string InstanceMcVersion(McInstance inst)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(inst.JsonPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("inheritsFrom", out var ih) && ih.ValueKind == System.Text.Json.JsonValueKind.String)
                return ih.GetString() ?? "";
            return root.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    public static bool IsIsolatedInstance(McInstance inst)
        => Directory.Exists(Path.Combine(inst.Dir, "mods"))
           || Directory.Exists(Path.Combine(inst.Dir, "saves"))
           || Directory.Exists(Path.Combine(inst.Dir, "config"));

    private static string LoaderLabel(string loader) => loader switch
    {
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "fabric" => "Fabric",
        "optifine" => "OptiFine",
        _ => S.Get("dl.res.vanilla"),
    };

    public static List<ResTarget> FindResourceTargets(string type, string[] gameVersions, string[] loaders)
    {
        var result = new List<ResTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loaderSet = loaders.Select(l => l.ToLowerInvariant()).ToHashSet();
        bool needLoader = type == "mod" && loaderSet.Count > 0;

        var insts = new List<(McInstance Inst, string McVer, bool Isolated, bool Compat)>();
        foreach (var dir in FindGameDirs())
            foreach (var inst in ScanInstances(dir))
            {
                var mcVer = InstanceMcVersion(inst);
                bool verOk = gameVersions.Length == 0 ||
                             (mcVer.Length > 0 && gameVersions.Contains(mcVer, StringComparer.OrdinalIgnoreCase));

                bool loaderOk = !needLoader || inst.Loader switch
                {
                    "optifine" => loaderSet.Contains("forge"),
                    _ => loaderSet.Contains(inst.Loader),
                };
                insts.Add((inst, mcVer, IsIsolatedInstance(inst), verOk && loaderOk));
            }

        foreach (var (inst, mcVer, isolated, compat) in insts.Where(x => x.Compat)
                     .OrderByDescending(x => x.Inst.LastWrite))
        {
            string baseDir = isolated ? inst.Dir : inst.Root;
            if (!seen.Add(baseDir)) continue;
            string sub = string.Join(" · ",
                new[] { inst.RootLabel, mcVer, LoaderLabel(inst.Loader) }.Where(s => s.Length > 0));
            result.Add(new ResTarget(inst.Name, sub, baseDir, true, inst.Loader));
        }

        foreach (var dir in FindGameDirs())
            if (seen.Add(dir))
                result.Add(new ResTarget(S.Get("dl.res.root"), RootLabelOf(dir) == "" ? dir : RootLabelOf(dir), dir, false, "vanilla"));

        var mem = AppSettings.Shared.ResTargetMemory.GetValueOrDefault(type, "");
        if (mem.Length > 0)
        {
            var hit = result.FindIndex(t => t.BaseDir.Equals(mem, StringComparison.OrdinalIgnoreCase));
            if (hit > 0)
            {
                var picked = result[hit];
                result.RemoveAt(hit);
                result.Insert(0, picked);
            }
        }
        return result;
    }

    public static void RememberResourceTarget(string type, string baseDir)
    {
        try
        {
            AppSettings.Shared.ResTargetMemory[type] = baseDir;
            AppSettings.Shared.Save();
        }
        catch (Exception ex) { AppLog.Warn($"[modpack] 记忆安装位置失败：{ex.Message}"); }
    }

    public static List<(string Exe, int Major)> CollectJavas()
    {
        var paths = new List<string>();
        string? jh = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (jh != null) paths.Add(Path.Combine(jh, "bin", "javaw.exe"));
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { paths.Add(Path.Combine(p.Trim(), "javaw.exe")); } catch { }
        }
        foreach (var root in new[] {
            @"C:\Program Files\Eclipse Adoptium",
            @"C:\Program Files\Java",
            @"C:\Program Files (x86)\Java",
            @"C:\Program Files\Microsoft",
            @"C:\Program Files\Zulu",
            @"C:\Program Files\Amazon Corretto",
            @"C:\Program Files\BellSoft" })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var sub in Directory.GetDirectories(root))
                {
                    try { paths.Add(Path.Combine(sub, "bin", "javaw.exe")); } catch { }

                    foreach (var sub2 in Directory.GetDirectories(sub))
                        try { paths.Add(Path.Combine(sub2, "bin", "javaw.exe")); } catch { }
                }
            }
            catch { }
        }
        var list = new List<(string Exe, int Major)>();
        foreach (var exe in paths.Distinct())
            if (File.Exists(exe)) list.Add((exe, JavaMajor(exe)));
        return list;
    }

    private static int JavaMajor(string exe)
    {
        try
        {
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            if (vi.ProductMajorPart > 0) return vi.ProductMajorPart;
        }
        catch { }
        var m = System.Text.RegularExpressions.Regex.Match(exe, @"(?:jdk|jre)[-_ ]?(1[6-9]|2[0-9]|8)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return int.Parse(m.Groups[1].Value);
        return 0;
    }

    public static string? FindJava(int requiredMajor)
    {
        var javas = CollectJavas();
        var exact = javas.Where(j => j.Major == requiredMajor)
                         .OrderByDescending(j => j.Major).Select(j => j.Exe).FirstOrDefault();
        if (exact != null) return exact;
        if (requiredMajor >= 9)
        {
            var newer = javas.Where(j => j.Major > requiredMajor)
                             .OrderBy(j => j.Major).Select(j => j.Exe).FirstOrDefault();
            if (newer != null) return newer;
        }
        return null;
    }

    private static readonly List<System.Diagnostics.Process> _games = new();
    private static readonly object _gamesGate = new();
    private static bool _exitPollerStarted;

    private static List<System.Diagnostics.Process> GamesSnapshot()
    {
        lock (_gamesGate) return _games.ToList();
    }

    public static bool IsGameRunning
    {
        get
        {
            foreach (var p in GamesSnapshot())
            {
                try { if (!p.HasExited) return true; } catch { }
            }
            return false;
        }
    }

    public static event Action<int, bool>? GameExited;

    private static void HandleGameExit(System.Diagnostics.Process p)
    {
        int pid = 0;
        try { pid = p.Id; } catch { }
        lock (_gamesGate)
        {
            if (!_games.Remove(p)) return;
        }
        ForgetGamePid(pid);

        bool byUser;
        lock (_killGate) byUser = pid != 0 && _killedPids.Remove(pid);
        int code = 0;
        try { code = p.ExitCode; } catch { }
        lock (_pumpGate) { try { FlushRepeat(); } catch { } }
        try
        {
            Say(byUser ? S.Get("launcher.game.closedByUser")
               : code == 0 ? S.Get("launcher.game.exited")
                           : S.Get("launcher.game.exitedCrash") + $" ({code})");
        }
        catch { }
        try { GameExited?.Invoke(code, byUser); }
        catch (Exception ex) { try { AppLog.Warn("GameExited 监听者异常: " + ex.Message); } catch { } }
        try { p.Dispose(); } catch { }
    }

    private static void EnsureExitPoller()
    {
        if (_exitPollerStarted) return;
        _exitPollerStarted = true;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(1000);
                foreach (var p in GamesSnapshot())
                {
                    try { p.Refresh(); if (p.HasExited) HandleGameExit(p); }
                    catch { }
                }
            }
        });
    }

    private static void HookGameExit(System.Diagnostics.Process p)
    {
        p.EnableRaisingEvents = true;
        p.Exited += (_, _) => HandleGameExit(p);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    public static event Action? GameWindowFocused;

    private static readonly System.Text.RegularExpressions.Regex[] _noise = {
        new(@"is missing level data"),
        new(@"Resizing Chunk Sections"),
        new(@"^WARNING: (A restricted method|java\.lang\.System::load|Use --enable-native-access|Restricted methods will be blocked|A terminally deprecated method|sun\.misc\.Unsafe|Please consider reporting)"),
    };

    private static readonly object _pumpGate = new();
    private static string? _lastLine;
    private static int _lastCount;

    private static void FlushRepeat()
    {
        if (_lastLine != null && _lastCount > 1)
            Say($"{_lastLine}  (×{_lastCount})");
        _lastLine = null;
        _lastCount = 0;
    }

    private static void PumpGameLine(string line)
    {
        foreach (var re in _noise)
            if (re.IsMatch(line)) return;

        if (line.Contains("Crash report saved to") || line.Contains("This crash report has been saved to:") ||
            line.Contains("Could not save crash report to") || line.Contains("/ERROR]: Unable to launch"))
        {
            Say(S.Get("launcher.game.crashLog"));
            AppLog.Warn("[崩溃特征] " + line);
        }
        lock (_pumpGate)
        {
            if (line == _lastLine) { _lastCount++; return; }
            FlushRepeat();
            _lastLine = line;
            _lastCount = 1;
            Say(line);
        }
    }

    private static async Task WatchGameWindowAsync(System.Diagnostics.Process p)
    {
        for (int i = 0; i < 360; i++)
        {
            await Task.Delay(500);
            if (p.HasExited) return;
            try
            {
                p.Refresh();
                var h = p.MainWindowHandle;
                if (h == IntPtr.Zero) continue;

                GameWindowFocused?.Invoke();
                await Task.Delay(200);

                keybd_event(0xA4, 0, 0, IntPtr.Zero);
                keybd_event(0xA4, 0, 2, IntPtr.Zero);
                ShowWindow(h, 5);
                SetForegroundWindow(h);
                Say(S.Get("launcher.game.foreground"));
                return;
            }
            catch { return; }
        }
    }

    private static readonly object _killGate = new();
    private static readonly HashSet<int> _killedPids = new();

    private static readonly object _pidFileGate = new();
    private static string GamePidsFile => Path.Combine(AppDirs.Root, "running_games.json");

    private static HashSet<int> LoadKnownGamePids()
    {
        var set = new HashSet<int>();
        try
        {
            if (File.Exists(GamePidsFile))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(GamePidsFile));
                foreach (var e in doc.RootElement.EnumerateArray())
                    if (e.TryGetInt32(out var pid)) set.Add(pid);
            }
        }
        catch { }
        return set;
    }

    private static void SaveKnownGamePids(HashSet<int> set)
    {
        try
        {
            Directory.CreateDirectory(AppDirs.Root);
            File.WriteAllText(GamePidsFile,
                System.Text.Json.JsonSerializer.Serialize(set.OrderBy(x => x).ToList()));
        }
        catch { }
    }

    private static void RememberGamePid(int pid)
    {
        if (pid <= 0) return;
        lock (_pidFileGate)
        {
            var set = LoadKnownGamePids();
            set.Add(pid);
            SaveKnownGamePids(set);
        }
    }

    private static void ForgetGamePid(int pid)
    {
        if (pid <= 0) return;
        lock (_pidFileGate)
        {
            var set = LoadKnownGamePids();
            if (set.Remove(pid)) SaveKnownGamePids(set);
        }
    }

    private static List<System.Diagnostics.Process> FindOwnMinecraftProcesses()
    {
        var list = new List<System.Diagnostics.Process>();
        var known = LoadKnownGamePids();
        if (known.Count == 0) return list;
        foreach (var pid in known)
        {
            try
            {
                var p = System.Diagnostics.Process.GetProcessById(pid);
                p.Refresh();
                if (p.HasExited) { try { p.Dispose(); } catch { } continue; }
                if (p.MainWindowHandle == IntPtr.Zero) { try { p.Dispose(); } catch { } continue; }
                string title = p.MainWindowTitle ?? "";
                if (!title.Contains("Minecraft", StringComparison.OrdinalIgnoreCase)) { try { p.Dispose(); } catch { } continue; }
                list.Add(p);
            }
            catch {   }
        }

        var alive = list.Select(p => { try { return p.Id; } catch { return 0; } }).ToHashSet();
        lock (_pidFileGate)
        {
            var set = LoadKnownGamePids();
            set.RemoveWhere(x => !alive.Contains(x));
            if (set.Count != known.Count) SaveKnownGamePids(set);
        }
        return list;
    }

    public static void StopGame()
    {
        var victims = new Dictionary<int, System.Diagnostics.Process>();
        foreach (var p in GamesSnapshot())
            try { if (!p.HasExited) victims[p.Id] = p; } catch { }
        foreach (var p in FindOwnMinecraftProcesses())
            if (p.Id != 0 && !victims.ContainsKey(p.Id)) victims[p.Id] = p;

        if (victims.Count == 0)
        {
            Say(S.Get("launcher.game.notRunning"));
            return;
        }
        foreach (var p in victims.Values)
        {
            try
            {
                lock (_killGate) _killedPids.Add(p.Id);
                p.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                lock (_killGate) _killedPids.Remove(p.Id);
                Say("stop: " + ex.Message);
            }
        }
        Say(S.Get("launcher.game.stopping") + (victims.Count > 1 ? $" ×{victims.Count}" : ""));

        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 3; i++)
            {
                await Task.Delay(2000);
                var left = FindOwnMinecraftProcesses();
                if (left.Count == 0) break;
                foreach (var p in left)
                {
                    try
                    {
                        lock (_killGate) _killedPids.Add(p.Id);
                        p.Kill(entireProcessTree: true);
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
        });
    }

    public static void ReattachRunningGame()
    {
        EnsureExitPoller();
        foreach (var p in FindOwnMinecraftProcesses())
        {
            try
            {
                string title = p.MainWindowTitle ?? "";
                lock (_gamesGate)
                {
                    bool dup = _games.Any(g => { try { return g.Id == p.Id; } catch { return false; } });
                    if (dup) { p.Dispose(); continue; }
                    _games.Add(p);
                }
                HookGameExit(p);
                Say(S.Get("launcher.game.reattach") + $" · {title}");
            }
            catch { try { p.Dispose(); } catch { } }
        }
    }

    public static async Task<string?> LaunchAsync(McInstance inst, Action? onStarted)
    {
        if (!HasAccount) return "launcher.game.noaccount";
        if (string.IsNullOrEmpty(inst.Root) || !Directory.Exists(inst.Root)) return "launcher.game.noDir";
        string gameDir = inst.Root;

        try
        {
            var st = AppSettings.Shared;
            var (classpath, mainClass, gameArgs, jvmArgs, requiredJava, finalGameDir) =
                await PrepareAsync(inst, gameDir);

            string? java;
            if (!string.IsNullOrWhiteSpace(st.McJavaPath) && File.Exists(st.McJavaPath))
            {
                java = st.McJavaPath;
            }
            else
            {
                java = FindJava(requiredJava);
                if (java == null)
                {
                    Say($"需要 Java {requiredJava}，本机没找到匹配的 javaw.exe");
                    return "launcher.game.javaMismatch";
                }
            }

            long xmxMb = GetAutoMemoryMb();
            var args = new List<string>
            {
                $"-Xmx{xmxMb}M",
                "-Dfile.encoding=UTF-8",

                "-XX:-OmitStackTraceInFastThrow",
                "-Djdk.lang.Process.allowAmbiguousCommands=True",
                "-Dfml.ignoreInvalidMinecraftCertificates=True",
                "-Dfml.ignorePatchDiscrepancies=True",
                "-Dlog4j2.formatMsgNoLookups=true",
            };

            if (!jvmArgs.Any(a => a.Contains("UseG1GC")))
            {
                args.AddRange(new[]
                {
                    "-XX:+UnlockExperimentalVMOptions",
                    "-XX:+UseG1GC",
                    "-XX:G1NewSizePercent=20",
                    "-XX:G1ReservePercent=20",
                    "-XX:G1HeapRegionSize=32M",
                    "-XX:MaxGCPauseMillis=50",
                    "-XX:+PerfDisableSharedMem",
                    "-XX:MinHeapFreeRatio=25",
                    "-XX:MaxHeapFreeRatio=40",
                });
            }

            foreach (var extra in ReadPclAdvanceJvm(inst)) args.Add(extra);
            args.AddRange(jvmArgs);
            if (!jvmArgs.Contains("-cp"))
            {
                args.Add("-cp");
                args.Add(classpath);
            }
            args.Add(mainClass);
            args.AddRange(gameArgs);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = java,
                WorkingDirectory = finalGameDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,

                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            try { AppLog.Info("[启动] 完整命令：\n" + java + " " + string.Join(" ", args)); } catch { }
            var proc = new System.Diagnostics.Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) PumpGameLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) PumpGameLine(e.Data); };
            HookGameExit(proc);
            lock (_gamesGate) _games.Add(proc);
            EnsureExitPoller();
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            RememberGamePid(proc.Id);
            _ = WatchGameWindowAsync(proc);
            Say(S.Get("launcher.game.started") + " · " + inst.Name);
            onStarted?.Invoke();
            return null;
        }
        catch (InvalidOperationException iox) when (iox.Message.StartsWith("launcher."))
        {
            return iox.Message;
        }
        catch (Exception ex)
        {
            Say("launch: " + ex.Message);
            return "launcher.game.failed";
        }
    }

    private static async Task<(string Classpath, string MainClass, string[] GameArgs, List<string> JvmArgs, int RequiredJava, string GameDir)>
        PrepareAsync(McInstance inst, string gameDir)
    {

        var (merged, parentVersionId) = LoadMerged(JsonDocument.Parse(File.ReadAllText(inst.JsonPath)).RootElement.Clone(),
            Path.GetDirectoryName(Path.GetDirectoryName(inst.JsonPath)));

        string mainClass = merged.TryGetProperty("mainClass", out var mcEl) ? mcEl.GetString() ?? "" : "";
        string assetsId = merged.TryGetProperty("assets", out var asEl) ? asEl.GetString() ?? "legacy" : "legacy";
        string instDir = Path.GetDirectoryName(inst.JsonPath)!;
        int requiredJava = merged.TryGetProperty("javaVersion", out var jvEl) && jvEl.TryGetProperty("majorVersion", out var mjEl)
            ? mjEl.GetInt32() : 8;

        bool isolated = Directory.Exists(Path.Combine(instDir, "mods")) ||
                        Directory.Exists(Path.Combine(instDir, "saves")) ||
                        Directory.Exists(Path.Combine(instDir, "config"));
        string finalGameDir = isolated ? instDir : inst.Root;
        if (isolated) Say("版本隔离: " + instDir);

        var libPaths = new List<string>();
        string nativesDir = Path.Combine(instDir, "natives");
        Directory.CreateDirectory(nativesDir);
        var nativesToExtract = new List<string>();
        if (merged.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
        {
            foreach (var lib in libs.EnumerateArray())
            {
                if (!RulesAllow(lib)) continue;
                if (!lib.TryGetProperty("name", out var nameEl)) continue;
                string name = nameEl.GetString()!;
                var parts = name.Split(':');
                if (parts.Length < 3) continue;
                string libRoot = lib.TryGetProperty("url", out var uEl) && !string.IsNullOrEmpty(uEl.GetString())
                    ? uEl.GetString()!.TrimEnd('/') + "/" : "https://libraries.minecraft.net/";

                string repoPath = $"{parts[0].Replace('.', '/')}/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}" +
                                  (parts.Length > 3 ? $"-{string.Join('-', parts.Skip(3))}" : "") + ".jar";

                if (parts.Length > 3 && parts[3].Contains("natives-windows") &&
                    ((parts[3].Contains("-x86") && Environment.Is64BitProcess) ||
                     (parts[3].Contains("-arm64") && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                         != System.Runtime.InteropServices.Architecture.Arm64)))
                    continue;

                string local = Path.Combine(inst.Root, "libraries", repoPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(local))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                        await DownloadMirrorAsync(libRoot + repoPath, local);
                        Say("lib ok: " + parts[1]);
                    }
                    catch { continue; }
                }
                libPaths.Add(local);

                if (parts.Length > 3 && parts[3].Contains("natives-windows"))
                {

                    nativesToExtract.Add(local);
                }

                else if (parts.Length == 3 && lib.TryGetProperty("natives", out var natEl) &&
                         natEl.TryGetProperty("windows", out var winEl))
                {
                    string cls = winEl.GetString()!.Replace("${arch}", Environment.Is64BitProcess ? "64" : "32");
                    string nativesRepoPath = $"{parts[0].Replace('.', '/')}/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}-{cls}.jar";
                    string localN = Path.Combine(inst.Root, "libraries", nativesRepoPath.Replace('/', Path.DirectorySeparatorChar));

                    string? clsUrl = null;
                    if (lib.TryGetProperty("downloads", out var dlEl) && dlEl.TryGetProperty("classifiers", out var clsEl) &&
                        clsEl.TryGetProperty(cls, out var artEl))
                    {
                        if (artEl.TryGetProperty("path", out var pEl) && !string.IsNullOrEmpty(pEl.GetString()))
                            localN = Path.Combine(inst.Root, "libraries", pEl.GetString()!.Replace('/', Path.DirectorySeparatorChar));
                        if (artEl.TryGetProperty("url", out var urEl)) clsUrl = urEl.GetString();
                    }
                    if (!File.Exists(localN))
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(localN)!);
                            await DownloadMirrorAsync(clsUrl ?? libRoot + nativesRepoPath, localN);
                        }
                        catch { continue; }
                    }
                    libPaths.Add(localN);
                    nativesToExtract.Add(localN);
                }
            }
        }

        Directory.CreateDirectory(nativesDir);
        string archSub = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64 ? "windows/arm64/"
            : Environment.Is64BitProcess ? "windows/x64/" : "windows/x86/";
        foreach (var jarPath in nativesToExtract)
        {
            try
            {
                using var zip = ZipFile.OpenRead(jarPath);
                foreach (var entry in zip.Entries)
                {
                    string p = entry.FullName.Replace('\\', '/');
                    if (p.StartsWith("META-INF", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

                    if (p.StartsWith("windows/x86/", StringComparison.OrdinalIgnoreCase) && archSub != "windows/x86/") continue;
                    if (p.StartsWith("windows/arm64/", StringComparison.OrdinalIgnoreCase) && archSub != "windows/arm64/") continue;
                    if (p.StartsWith("windows/x64/", StringComparison.OrdinalIgnoreCase) && archSub != "windows/x64/") continue;

                    string dest = Path.Combine(nativesDir, p.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    if (!File.Exists(dest))
                        entry.ExtractToFile(dest, overwrite: false);
                }
            }
            catch (Exception ex) { Say("natives: " + ex.Message); }
        }

        string clientJarPath = inst.JarPath;
        if (!File.Exists(clientJarPath) && parentVersionId is not null)
        {
            string parentJar = Path.Combine(Path.GetDirectoryName(instDir)!, parentVersionId, parentVersionId + ".jar");
            if (File.Exists(parentJar))
            {
                try
                {
                    Directory.CreateDirectory(instDir);
                    File.Copy(parentJar, clientJarPath, overwrite: false);
                    Say("client jar copied from parent");
                }
                catch (Exception ex)
                {
                    Say("client jar copy: " + ex.Message);
                    clientJarPath = parentJar;
                }
            }
        }
        if (!File.Exists(clientJarPath) &&
            merged.TryGetProperty("downloads", out var dls) && dls.TryGetProperty("client", out var cliEl) &&
            cliEl.TryGetProperty("url", out var jarUrlEl) && !string.IsNullOrEmpty(jarUrlEl.GetString()))
        {
            try
            {
                Say("Downloading client jar...");
                Directory.CreateDirectory(instDir);
                await DownloadMirrorAsync(jarUrlEl.GetString()!, clientJarPath);
                Say("client jar ok");
            }
            catch (Exception ex) { Say("client jar: " + ex.Message); }
        }
        var cp = libPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (File.Exists(clientJarPath) && !cp.Contains(clientJarPath, StringComparer.OrdinalIgnoreCase))
            cp.Add(clientJarPath);
        string classpath = string.Join(";", cp);

        try
        {
            if (merged.TryGetProperty("assetIndex", out var aiEl) && aiEl.ValueKind == JsonValueKind.Object)
            {
                string aiUrl = aiEl.GetProperty("url").GetString()!;
                string aiId = aiEl.GetProperty("id").GetString()!;
                string aiLocal = Path.Combine(inst.Root, "assets", "indexes", aiId + ".json");
                if (!File.Exists(aiLocal))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(aiLocal)!);
                    await DownloadMirrorAsync(aiUrl, aiLocal);
                }
            }
        }
        catch (Exception ex) { Say("assets: " + ex.Message); }

        var st = AppSettings.Shared;
        string userName = ResolvePlayerName();
        string uuid = st.McUuid;
        string token = st.McAccessToken;
        string userType = "msa";
        var replace = new Dictionary<string, string>
        {
            ["${auth_player_name}"] = userName,
            ["${auth_uuid}"] = uuid,
            ["${auth_access_token}"] = token,
            ["${auth_session}"] = $"token:{token}:{uuid}",
            ["${auth_xuid}"] = "",
            ["${clientid}"] = "",
            ["${user_type}"] = userType,
            ["${user_properties}"] = "{}",
            ["${version_name}"] = inst.Name,
            ["${version_type}"] = merged.TryGetProperty("type", out var tEl) ? tEl.GetString() ?? "" : "",
            ["${assets_index_name}"] = assetsId,
            ["${game_directory}"] = finalGameDir,
            ["${assets_root}"] = Path.Combine(inst.Root, "assets"),
            ["${game_assets}"] = Path.Combine(inst.Root, "assets", "virtual", assetsId),
            ["${launcher_name}"] = "Starry",
            ["${launcher_version}"] = "1.0",
            ["${natives_directory}"] = nativesDir,
            ["${classpath}"] = classpath,
            ["${classpath_separator}"] = ";",
            ["${library_directory}"] = Path.Combine(inst.Root, "libraries"),
            ["${resolution_width}"] = "1280",
            ["${resolution_height}"] = "720",
            ["${quickPlayPath}"] = "",
            ["${quickPlaySingleplayer}"] = "",
            ["${quickPlayMultiplayer}"] = "",
            ["${quickPlayRealms}"] = "",
            ["${quickPlayWorld}"] = "",
            ["${vmArgs}"] = "",
        };
        var gameArgs = new List<string>();
        if (merged.TryGetProperty("arguments", out var argEl) && argEl.TryGetProperty("game", out var gEl))
        {
            ExpandArgs(gEl, replace, gameArgs);
        }
        else if (merged.TryGetProperty("minecraftArguments", out var maEl))
        {
            foreach (var s in (maEl.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                gameArgs.Add(ExpandStr(s, replace));
        }

        var jvmArgs = new List<string>();
        if (argEl.ValueKind == JsonValueKind.Object && argEl.TryGetProperty("jvm", out var jmEl)
            && jmEl.ValueKind == JsonValueKind.Array)
        {
            ExpandArgs(jmEl, replace, jvmArgs);
        }
        else
        {
            jvmArgs.Add("-Djava.library.path=" + nativesDir);
        }

        return (classpath, mainClass, gameArgs.ToArray(), jvmArgs, requiredJava, finalGameDir);
    }

    private static (JsonElement Merged, string? ParentId) LoadMerged(JsonElement child, string versionsDir)
    {
        if (!child.TryGetProperty("inheritsFrom", out var p)) return (child, null);
        string parentId = p.GetString() ?? "";
        string parentPath = Path.Combine(versionsDir, parentId, parentId + ".json");
        if (!File.Exists(parentPath))
            throw new InvalidOperationException("launcher.game.noinherit");
        var parent = JsonDocument.Parse(File.ReadAllText(parentPath)).RootElement.Clone();

        var scalars = new Dictionary<string, JsonElement>();
        foreach (var key in new[] { "mainClass", "minecraftArguments", "javaVersion", "downloads", "assetIndex", "assets", "logging" })
        {
            if (child.TryGetProperty(key, out var c)) scalars[key] = c.Clone();
            else if (parent.TryGetProperty(key, out var pv)) scalars[key] = pv.Clone();
        }

        bool anyArgs = parent.TryGetProperty("arguments", out var pArgsObj) || child.TryGetProperty("arguments", out var cArgsObj);
        if (anyArgs)
        {
            var argsOut = new Dictionary<string, JsonElement>();
            foreach (var key in new[] { "game", "jvm" })
            {
                var buf = new MemoryStream();
                using (var w = new Utf8JsonWriter(buf))
                {
                    w.WriteStartArray();
                    foreach (var src in new[] { parent, child })
                    {
                        if (!src.TryGetProperty("arguments", out var argsEl) ||
                            !argsEl.TryGetProperty(key, out var arrEl) || arrEl.ValueKind != JsonValueKind.Array) continue;
                        foreach (var item in arrEl.EnumerateArray()) item.WriteTo(w);
                    }
                    w.WriteEndArray();
                }
                argsOut[key] = JsonDocument.Parse(buf.ToArray()).RootElement.Clone();
            }
            scalars["arguments"] = JsonDocument.Parse(
                JsonSerializer.SerializeToUtf8Bytes(argsOut)).RootElement.Clone();
        }

        var libBuf = new MemoryStream();
        using (var w = new Utf8JsonWriter(libBuf))
        {
            w.WriteStartArray();
            foreach (var src in new[] { child, parent })
            {
                if (!src.TryGetProperty("libraries", out var libEl) || libEl.ValueKind != JsonValueKind.Array) continue;
                foreach (var l in libEl.EnumerateArray()) l.WriteTo(w);
            }
            w.WriteEndArray();
        }
        scalars["libraries"] = JsonDocument.Parse(libBuf.ToArray()).RootElement.Clone();

        return (JsonSerializer.SerializeToElement(scalars.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)), parentId);
    }

    private static string ExpandStr(string s, Dictionary<string, string> replace)
    {
        foreach (var kv in replace.OrderByDescending(kv => kv.Key.Length))
            s = s.Replace(kv.Key, kv.Value);
        return s;
    }

    private static void ExpandArgs(JsonElement arr, Dictionary<string, string> replace, List<string> output)
    {
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                output.Add(ExpandStr(item.GetString()!, replace));
            }
            else if (item.ValueKind == JsonValueKind.Object && RulesAllow(item))
            {
                if (!item.TryGetProperty("value", out var vEl)) continue;
                if (vEl.ValueKind == JsonValueKind.String)
                {
                    output.Add(ExpandStr(vEl.GetString()!, replace));
                }
                else if (vEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in vEl.EnumerateArray())
                        output.Add(ExpandStr(s.GetString() ?? "", replace));
                }
            }
        }
    }

    internal static bool RulesAllow(JsonElement lib)
    {
        if (!lib.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array) return true;
        bool result = false;
        foreach (var rule in rules.EnumerateArray())
        {
            string action = rule.TryGetProperty("action", out var aEl) ? aEl.GetString() ?? "allow" : "allow";
            bool match = true;
            if (rule.TryGetProperty("os", out var os))
            {
                if (os.TryGetProperty("name", out var osName))
                    match &= osName.GetString() == "windows";

                if (os.TryGetProperty("arch", out var archEl))
                    match &= (archEl.GetString() == "x86") != Environment.Is64BitProcess;
            }
            if (rule.TryGetProperty("features", out var f) && f.ValueKind == JsonValueKind.Object)
            {

                bool ok = true;
                foreach (var p in f.EnumerateObject())
                    if (p.Name != "has_custom_resolution" || p.Value.ValueKind != JsonValueKind.True) { ok = false; break; }
                match &= ok;
            }
            if (match) result = action == "allow";
        }
        return result;
    }
}
