using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace StarryLauncher;

public record ModpackInfo(
    string Title, string Description, string Author,
    long Downloads, long Follows, string? IconUrl,
    string Slug, string[] Categories, string ProjectType = "modpack",
    string Source = ModpackService.SourceModrinth, string? PageUrlOverride = null,
    DateTime? Updated = null, string? PlatformId = null)
{

    public string PageUrl
    {
        get
        {
            if (PageUrlOverride is { Length: > 0 }) return PageUrlOverride;
            var section = ProjectType switch
            {
                "mod" => "mod",
                "shader" => "shader",
                "resourcepack" => "resourcepack",
                "datapack" => "datapack",
                _ => "modpack",
            };
            return $"https://modrinth.com/{section}/{Slug}";
        }
    }

    public string Tag => Categories.Length > 0 ? Categories[0] : "modpack";

    public string IconKey => Source + "/" + Slug;
}

public class ModpackService
{
    public static ModpackService Instance { get; } = new();

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private const string UserAgent = "StarryLauncher/1.0 (Starry Launcher)";

    public const string SourceAll = "all";
    public const string SourceModrinth = "modrinth";
    public const string SourceCurseForge = "curseforge";

    private string _source = SourceAll;
    public string Source => _source;

    private const string CfApiBase = "https://mod.mcimirror.top/curseforge/v1";

    private const int CfPageSize = 50;

    private static string CfClassId(string type) => type switch
    {
        "mod" => "6",
        "shader" => "6552",
        "resourcepack" => "12",
        "datapack" => "6945",
        "map" => "17",
        _ => "4471",
    };

    public static readonly (string Type, string Key, string En)[] ProjectTypes =
    {

        ("vanilla",      "vanilla",  "Game Versions"),
        ("mod",          "mods",     "Mods"),
        ("modpack",      "modpack",  "Modpacks"),
        ("shader",       "shader",   "Shaders"),
        ("resourcepack", "resource", "Resource Packs"),
        ("datapack",     "datapack", "Data Packs"),
        ("map",          "map",      "Maps"),
    };

    public static readonly (string? Slug, string Key, string En)[] Categories =
    {
        (null,             "all",         "All"),
        ("adventure",      "adventure",   "Adventure"),
        ("challenging",    "challenge",   "Challenge"),
        ("combat",         "combat",      "Combat"),
        ("kitchen-sink",   "kitchenSink", "Kitchen Sink"),
        ("lightweight",    "lightweight", "Lightweight"),
        ("magic",          "magic",       "Magic"),
        ("multiplayer",    "multiplayer", "Multiplayer"),
        ("optimization",   "optimization","Optimization"),
        ("quests",         "quests",      "Quests"),
        ("technology",     "technology",  "Technology"),

        ("utility",        "utility",     "Utility"),
        ("library",        "library",     "Library"),
        ("worldgen",       "worldgen",    "World Gen"),
        ("economy",        "economy",     "Economy"),
        ("equipment",      "equipment",   "Equipment"),
        ("food",           "food",        "Food"),
        ("storage",        "storage",     "Storage"),
        ("transportation", "transport",   "Transportation"),
        ("game-mechanics", "gamemech",    "Game Mechanics"),
        ("social",         "social",      "Social"),

        ("cartoon",        "cartoon",     "Cartoon"),
        ("realistic",      "realistic",   "Realistic"),
        ("semi-realistic", "semireal",    "Semi-realistic"),
        ("vanilla-like",   "vanillalike", "Vanilla-like"),
    };

    private const int PageSize = 100;

    private sealed class CategoryState
    {
        public List<ModpackInfo> Items = new();
        public int Total;
        public bool EndReached;
    }

    private readonly Dictionary<string, CategoryState> _states = new();
    private string? _category;
    public string? Category => _category;

    private string _projectType = "mod";
    public string ProjectType => _projectType;

    private string? _gameVersion;
    public string? GameVersion => _gameVersion;

    private string? _loader;
    public string? Loader => _loader;

    private static int? CfModLoaderType(string? loader) => loader switch
    {
        "forge" => 1, "neoforge" => 6, "fabric" => 4, "liteloader" => 2, "quilt" => 5,
        _ => null,
    };

    private string? _searchQuery;
    public string? SearchQuery => _searchQuery;
    public bool IsSearching => _searchQuery is { Length: > 0 };

    private string StateKey
        => _source + "|" + _projectType + "|v=" + (_gameVersion ?? "*") + "|ld=" + (_loader ?? "*") + "|"
           + (_searchQuery is { Length: > 0 } ? "q:" + _searchQuery : _category ?? "");

    private CategoryState State
        => _states.TryGetValue(StateKey, out var s) ? s : _states[StateKey] = new CategoryState();

    private volatile bool _loadingMore;

    public event Action? PicksChanged;
    public event Action? LoadFailed;

    public bool IsLoading { get; private set; }
    public event Action? LoadingChanged;

    private void SetLoading(bool on)
    {
        if (IsLoading == on) return;
        IsLoading = on;
        LoadingChanged?.Invoke();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public async Task SetSourceAsync(string source)
    {
        if (source == _source) return;
        _source = source;
        _searchQuery = null;
        _category = null;
        _loadingMore = false;
        await LoadFirstPageAsync().ConfigureAwait(true);
    }

    public async Task SetPageAsync(string type, string? gameVersion, string? category, string? searchQuery, string? loader = null)
    {
        _projectType = type;

        if (type == "map") _source = SourceCurseForge;
        _gameVersion = string.IsNullOrWhiteSpace(gameVersion) ? null : gameVersion.Trim();
        _category = string.IsNullOrWhiteSpace(category) ? null : category;
        _searchQuery = string.IsNullOrWhiteSpace(searchQuery) ? null : searchQuery.Trim();
        _loader = type == "mod" && !string.IsNullOrWhiteSpace(loader) ? loader.Trim().ToLowerInvariant() : null;
        _loadingMore = false;
        await LoadFirstPageAsync().ConfigureAwait(true);
    }

    public async Task SetSourcePageAsync(string source, string? gameVersion, string? category, string? searchQuery, string? loader = null)
    {
        _source = source;
        await SetPageAsync(_projectType, gameVersion, category, searchQuery, loader).ConfigureAwait(true);
    }

    public async Task SetGameVersionAsync(string? version)
    {
        version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        if (version == _gameVersion) return;
        _gameVersion = version;
        _loadingMore = false;
        await LoadFirstPageAsync().ConfigureAwait(true);
    }

    public async Task SetLoaderAsync(string? loader)
    {
        loader = string.IsNullOrWhiteSpace(loader) ? null : loader.Trim().ToLowerInvariant();
        if (loader == _loader) return;
        _loader = loader;
        _loadingMore = false;
        await LoadFirstPageAsync().ConfigureAwait(true);
    }

    public async Task SetProjectTypeAsync(string type)
    {
        if (type == _projectType) return;
        _projectType = type;
        _searchQuery = null;
        _category = null;
        _loadingMore = false;
        await LoadFirstPageAsync().ConfigureAwait(true);
    }

    public async Task SetCategoryAsync(string? category)
    {
        _category = string.IsNullOrWhiteSpace(category) ? null : category;
        await LoadFirstPageAsync().ConfigureAwait(true);
    }

    public async Task SetSearchAsync(string query)
    {
        query = (query ?? "").Trim();
        var next = query.Length > 0 ? query : null;
        if (next == _searchQuery) return;
        _searchQuery = next;
        _loadingMore = false;
        await LoadFirstPageAsync().ConfigureAwait(true);
    }

    public void ClearSearch() => _searchQuery = null;

    public async Task LoadFirstPageAsync()
    {
        var st = State;

        if (st.Items.Count > 0)
        {
            RaisePicks();
            return;
        }

        SetLoading(true);
        try
        {
            var (items, total) = await FetchPageAsync(0).ConfigureAwait(true);
            st.Items = items;
            st.Total = total;
            st.EndReached = items.Count >= total;
            AppLog.Info($"[modpack] {(_source == SourceCurseForge ? "CurseForge" : _source == SourceAll ? "全部源" : "Modrinth")} 首页拉取成功（{(_searchQuery is { Length: > 0 } ? "搜索:" + _searchQuery : _projectType + "·类别=" + (_category ?? "全部"))}，{items.Count}/{total} 条）");
            SetLoading(false);
            RaisePicks();
            if (_searchQuery is null) _ = WriteDiskAsync(items);
            _ = DownloadIconsAsync(items);
            return;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[modpack] Modrinth 拉取失败：{ex.Message}");
        }

        if (_searchQuery is not null)
        {
            AppLog.Warn("[modpack] 搜索请求失败（搜索结果无磁盘兜底）");
            SetLoading(false);
            LoadFailed?.Invoke();
            return;
        }
        var disk = await ReadDiskAsync().ConfigureAwait(true);
        if (disk is { Hits.Count: > 0 })
        {
            st.Items = disk.Hits;
            st.Total = disk.Hits.Count;
            st.EndReached = true;
            AppLog.Info($"[modpack] 断网兜底：展示磁盘缓存 {disk.Hits.Count} 条（类别={_category ?? "全部"}）");
            SetLoading(false);
            RaisePicks();
            _ = DownloadIconsAsync(st.Items);
            return;
        }

        SetLoading(false);
        LoadFailed?.Invoke();
    }

    public async Task LoadMoreAsync()
    {
        var st = State;
        if (_loadingMore || st.EndReached || st.Items.Count == 0) return;
        _loadingMore = true;
        try
        {
            AppLog.Info($"[modpack] 翻页触发（已有 {st.Items.Count}/{st.Total}，offset={st.Items.Count}）");
            var (items, total) = await FetchPageAsync(st.Items.Count).ConfigureAwait(true);

            var known = new HashSet<string>(st.Items.Select(x => x.Source + "|" + x.Slug));
            int added = 0;
            foreach (var m in items)
            {
                if (!known.Add(m.Source + "|" + m.Slug)) continue;
                if (st.Items.Any(x => IsSameProject(x, m))) continue;
                st.Items.Add(m); added++;
            }

            if (st.Items.Count >= total || items.Count == 0) st.EndReached = true;

            if (added == 0)
            {
                _emptyStreak++;
                if (_emptyStreak >= 2)
                {
                    st.EndReached = true;
                    AppLog.Warn($"[modpack] 连续 {_emptyStreak} 批无新数据，终止翻页");
                }
            }
            else _emptyStreak = 0;

            if (added > 0)
            {
                AppLog.Info($"[modpack] 翻页 +{added}（{st.Items.Count}/{total}）");
                RaisePicks();
                _ = DownloadIconsAsync(items);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[modpack] 翻页失败：{ex.Message}");
        }
        finally { _loadingMore = false; }
    }

    private int _emptyStreak;

    public List<ModpackInfo> CurrentItems() => new(State.Items);

    public bool EndReached => State.EndReached;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> IconMem = new();
    private readonly object _iconLazyLock = new();
    private readonly HashSet<string> _iconLazyFetching = new();
    private readonly HashSet<string> _iconNoIcon = new();

    static ModpackService()
    {

        try
        {
            if (Directory.Exists(AppDirs.Modpack))
                foreach (var jpg in Directory.GetFiles(AppDirs.Modpack, "*.jpg"))
                    File.Delete(jpg);
        }
        catch {   }
    }

    public byte[]? GetIconBytes(string iconKey)
    {
        if (IconMem.TryGetValue(iconKey, out var b)) return b;
        TryLazyFetchIcon(iconKey);
        return null;
    }

    private void TryLazyFetchIcon(string iconKey)
    {
        lock (_iconLazyLock)
        {
            if (_iconLazyFetching.Contains(iconKey) || _iconNoIcon.Contains(iconKey)) return;
            _iconLazyFetching.Add(iconKey);
        }
        _ = LazyFetchIconAsync(iconKey);
    }

    private async Task LazyFetchIconAsync(string iconKey)
    {
        try
        {

            var known = State.Items.FirstOrDefault(x => x.IconKey == iconKey);
            string? url = known?.IconUrl;

            if (url is not { Length: > 0 } && known is not null && known.Source != SourceModrinth) return;
            if (url is not { Length: > 0 })
            {
                var slug = iconKey.Substring(iconKey.IndexOf('/') + 1);
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.modrinth.com/v2/project/{slug}");
                req.Headers.UserAgent.ParseAdd(UserAgent);
                using var resp = await Http.SendAsync(req).ConfigureAwait(true);
                if (!resp.IsSuccessStatusCode) return;
                using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(true));
                if (doc.RootElement.TryGetProperty("icon_url", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.String)
                    url = u.GetString();
            }

            var bytes = url is { Length: > 0 } ? await FetchIconBytesAsync(url).ConfigureAwait(true) : null;
            if (bytes is null) return;
            IconMem[iconKey] = bytes;
            await RaisePicksThrottledAsync().ConfigureAwait(true);
        }
        catch {   }
        finally
        {
            lock (_iconLazyLock)
            {
                _iconLazyFetching.Remove(iconKey);
                if (!IconMem.ContainsKey(iconKey)) _iconNoIcon.Add(iconKey);
            }
        }
    }

    private async Task<byte[]?> FetchIconBytesAsync(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(UserAgent);
            using var resp = await Http.SendAsync(req).ConfigureAwait(true);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(true);
            return bytes.Length == 0 || bytes.Length > 5 * 1024 * 1024 ? null : bytes;
        }
        catch { return null; }
    }

    private static string BuildFacets(string projectType, string? category, string? gameVersion, string? loader)
    {
        var inner = $"[\"project_type:{projectType}\"]";
        if (category is not null) inner += $",[\"categories:{category}\"]";
        if (gameVersion is not null) inner += $",[\"versions:{gameVersion}\"]";
        if (loader is not null) inner += $",[\"categories:{loader}\"]";
        return Uri.EscapeDataString($"[{inner}]");
    }

    private string BuildUrl(string source, string? category, int offset)
    {

        if (source == SourceCurseForge)
        {
            var url = $"{CfApiBase}/mods/search?gameId=432&classId={CfClassId(_projectType)}" +
                      $"&sortField=2&sortOrder=desc&pageSize={CfPageSize}";
            if (_gameVersion is { Length: > 0 }) url += "&gameVersion=" + Uri.EscapeDataString(_gameVersion);
            var ml = CfModLoaderType(_loader);
            if (ml is not null) url += "&modLoaderType=" + ml;
            var cfId = ParseCfCategoryId(category);
            if (cfId is not null) url += "&categoryId=" + cfId;
            else if (category is not null && !category.Contains('/')
                     && _cfCategoryIds.TryGetValue((CfClassId(_projectType), category), out var cid))
                url += "&categoryId=" + cid;
            if (_searchQuery is { Length: > 0 }) url += "&searchFilter=" + Uri.EscapeDataString(_searchQuery);
            if (offset > 0) url += "&index=" + offset;
            return url;
        }

        var mrCat = ParseMrCategorySlug(category);
        var facets = BuildFacets(_projectType, mrCat, _gameVersion, _loader);

        var q = _searchQuery is { Length: > 0 } ? "&query=" + Uri.EscapeDataString(_searchQuery) : "";
        return $"https://api.modrinth.com/v2/search?limit={PageSize}&offset={offset}&index=follows&facets={facets}{q}";
    }

    private static string? ParseCfCategoryId(string? category)
    {
        if (category is null) return null;
        var slash = category.IndexOf('/');
        if (slash < 0) return null;
        var left = category[..slash];
        return int.TryParse(left, out _) && left.Length > 0 ? left : null;
    }

    private static string? ParseMrCategorySlug(string? category)
    {
        if (category is null || category.Length == 0) return null;
        var slash = category.IndexOf('/');
        return slash < 0 ? category : category[(slash + 1)..] is { Length: > 0 } s ? s : null;
    }

    private async Task<(List<ModpackInfo> Items, int Total)> FetchPageAsync(int offset)
    {

        if (_source == SourceAll)
        {
            var mr = FetchOneAsync(SourceModrinth, offset);
            var cf = FetchOneAsync(SourceCurseForge, offset);
            await Task.WhenAll(mr, cf).ConfigureAwait(true);
            var merged = MergeTwins(mr.Result.Items.Concat(cf.Result.Items));
            return (merged, mr.Result.Total + cf.Result.Total);
        }
        return await FetchOneAsync(_source, offset).ConfigureAwait(true);
    }

    private static List<ModpackInfo> MergeTwins(IEnumerable<ModpackInfo> items)
    {
        var result = new List<ModpackInfo>();
        foreach (var m in items)
        {
            var idx = result.FindIndex(x => IsSameProject(x, m));
            if (idx < 0) result.Add(m);
            else if (m.Downloads > result[idx].Downloads) result[idx] = m;
        }
        return result.OrderByDescending(x => x.Downloads).ToList();
    }

    private static bool IsSameProject(ModpackInfo a, ModpackInfo b)
    {
        if (a.Source == b.Source || a.ProjectType != b.ProjectType) return false;
        if (a.Updated is null || b.Updated is null) return false;
        if (Math.Abs((a.Updated.Value - b.Updated.Value).TotalDays) > 7) return false;
        return (CategoryRawKey(a.Title).Length > 0 && CategoryRawKey(a.Title) == CategoryRawKey(b.Title))
            || (CategoryRawKey(a.Slug).Length > 0 && CategoryRawKey(a.Slug) == CategoryRawKey(b.Slug));
    }

    private async Task<(List<ModpackInfo> Items, int Total)> FetchOneAsync(string source, int offset)
    {

        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                BuildUrl(source, _searchQuery is null ? _category : null, offset));
            req.Headers.UserAgent.ParseAdd(UserAgent);
            using var resp = await Http.SendAsync(req).ConfigureAwait(true);
            if ((int)resp.StatusCode == 429 && attempt <= 2)
            {
                var secs = resp.Headers.RetryAfter?.Delta?.TotalSeconds is { } d && d > 0 ? (int)d : 0;
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(secs == 0 ? 6 : secs, 3, 15))).ConfigureAwait(true);
                continue;
            }
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(true);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(true);
            return source == SourceCurseForge ? ParseCfHits(doc) : ParseHits(doc);
        }
    }

    private (List<ModpackInfo> Items, int Total) ParseCfHits(JsonDocument doc)
    {
        var root = doc.RootElement;
        var total = root.TryGetProperty("pagination", out var pg) && pg.TryGetProperty("totalCount", out var tc)
            ? tc.GetInt32() : 0;
        var list = new List<ModpackInfo>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in data.EnumerateArray())
            {
                list.Add(new ModpackInfo(
                    h.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?",
                    h.TryGetProperty("summary", out var sm) ? sm.GetString() ?? "" : "",
                    h.TryGetProperty("authors", out var au) && au.ValueKind == JsonValueKind.Array && au.GetArrayLength() > 0
                        ? au[0].TryGetProperty("name", out var an) ? an.GetString() ?? "" : "" : "",
                    h.TryGetProperty("downloadCount", out var dc) ? dc.GetInt64() : 0,
                    0,
                    h.TryGetProperty("logo", out var lg) && lg.TryGetProperty("thumbnailUrl", out var th) && th.ValueKind == JsonValueKind.String
                        ? th.GetString() : null,
                    h.TryGetProperty("slug", out var sl) ? sl.GetString() ?? "" : "",
                    h.TryGetProperty("categories", out var cs) && cs.ValueKind == JsonValueKind.Array
                        ? cs.EnumerateArray()
                            .Select(c => c.TryGetProperty("name", out var cn) ? cn.GetString() ?? "" : "")
                            .Where(s => s.Length > 0).Take(2).ToArray()
                        : Array.Empty<string>(),
                    _projectType,
                    SourceCurseForge,

                    h.TryGetProperty("links", out var lk) && lk.TryGetProperty("websiteUrl", out var wu) && wu.ValueKind == JsonValueKind.String
                        ? wu.GetString() : null,

                    h.TryGetProperty("dateModified", out var dm) && dm.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(dm.GetString(), out var cfTime) ? cfTime : null,

                    h.TryGetProperty("id", out var pid) ? pid.GetRawText() : null));
            }
        }
        return (list, total);
    }

    private static (List<ModpackInfo> Items, int Total) ParseHits(JsonDocument doc)
    {
        var total = doc.RootElement.TryGetProperty("total_hits", out var t) ? t.GetInt32() : 0;
        var hits = doc.RootElement.GetProperty("hits");

        var list = new List<ModpackInfo>();
        foreach (var h in hits.EnumerateArray())
        {

            var type = h.TryGetProperty("project_type", out var pt) && pt.ValueKind == JsonValueKind.String
                ? pt.GetString() ?? "modpack" : "modpack";
            list.Add(new ModpackInfo(
                h.GetProperty("title").GetString() ?? "?",
                h.GetProperty("description").GetString() ?? "",
                h.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "",
                h.TryGetProperty("downloads", out var d) ? d.GetInt64() : 0,
                h.TryGetProperty("follows", out var f) ? f.GetInt64() : 0,
                h.TryGetProperty("icon_url", out var ic) && ic.ValueKind == JsonValueKind.String
                    ? ic.GetString() : null,
                h.GetProperty("slug").GetString() ?? "",
                h.TryGetProperty("categories", out var cs) && cs.ValueKind == JsonValueKind.Array
                    ? cs.EnumerateArray().Select(x => x.GetString() ?? "").Take(2).ToArray()
                    : Array.Empty<string>(),
                type,
                Updated: h.TryGetProperty("date_modified", out var um) && um.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(um.GetString(), out var mrTime) ? mrTime : null,

                PlatformId: h.TryGetProperty("project_id", out var pi) && pi.ValueKind == JsonValueKind.String ? pi.GetString() : null));
        }
        return (list, total);
    }

    public sealed record ResourceVersionEntry(
        string Id, string FileName, string DisplayName, string ReleaseType, DateTime Date,
        string[] GameVersions, string[] Loaders, long Size, string[] Urls, string? Sha1);

    private static readonly HashSet<string> CfLoaderWords = new(StringComparer.OrdinalIgnoreCase)
        { "fabric", "forge", "neoforge", "quilt", "liteloader", "rift" };

    public static bool IsServerPackFile(ResourceVersionEntry e)
        => e.FileName.Contains("server", StringComparison.OrdinalIgnoreCase)
        || e.DisplayName.Contains("server", StringComparison.OrdinalIgnoreCase)
        || e.FileName.Contains("服务端", StringComparison.Ordinal)
        || e.DisplayName.Contains("服务端", StringComparison.Ordinal);

    public async Task<List<ResourceVersionEntry>> FetchResourceVersionsAsync(ModpackInfo m)
    {
        if (m.Source == SourceCurseForge)
        {
            var id = m.PlatformId;
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("CF 项目缺数字 id");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{CfApiBase}/mods/{id}/files");
            req.Headers.UserAgent.ParseAdd(UserAgent);
            using var resp = await Http.SendAsync(req).ConfigureAwait(true);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(true));
            return ParseCfFiles(doc);
        }
        using var req2 = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(m.PlatformId ?? m.Slug)}/version");
        req2.Headers.UserAgent.ParseAdd(UserAgent);
        using var resp2 = await Http.SendAsync(req2).ConfigureAwait(true);
        resp2.EnsureSuccessStatusCode();
        using var doc2 = JsonDocument.Parse(await resp2.Content.ReadAsStringAsync().ConfigureAwait(true));
        return ParseMrVersions(doc2);
    }

    private static List<ResourceVersionEntry> ParseCfFiles(JsonDocument doc)
    {
        var list = new List<ResourceVersionEntry>();
        var root = doc.RootElement;
        var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array
            ? d : default;
        if (data.ValueKind != JsonValueKind.Array) return list;
        foreach (var f in data.EnumerateArray())
        {
            var fileId = f.TryGetProperty("id", out var fi) ? fi.GetRawText() : "";
            var fileName = f.TryGetProperty("fileName", out var fn) ? fn.GetString() ?? "" : "";
            if (fileId.Length == 0 || fileName.Length == 0) continue;

            var urls = new List<string>();
            if (f.TryGetProperty("downloadUrl", out var du) && du.ValueKind == JsonValueKind.String &&
                du.GetString() is { Length: > 0 } duv)
                urls.Add(duv);
            string enc = Uri.EscapeDataString(fileName);
            if (long.TryParse(fileId, out var fid))
            {
                string direct = $"https://edge.forgecdn.net/files/{fid / 1000}/{fid % 1000}/{enc}";
                if (!urls.Contains(direct, StringComparer.OrdinalIgnoreCase)) urls.Insert(0, direct);
                urls.Add($"https://mediafilez.forgecdn.net/files/{fid / 1000}/{fid % 1000}/{enc}");
            }

            var loaders = new List<string>();
            var gvs = new List<string>();
            if (f.TryGetProperty("gameVersions", out var gv) && gv.ValueKind == JsonValueKind.Array)
                foreach (var g in gv.EnumerateArray())
                {
                    var s = g.GetString() ?? "";
                    if (s.Length == 0 || s is "Client" or "Server" or "Vanilla") continue;
                    if (CfLoaderWords.Contains(s)) loaders.Add(s.ToLowerInvariant());
                    else gvs.Add(s);
                }

            string? sha1 = null;
            if (f.TryGetProperty("hashes", out var hs) && hs.ValueKind == JsonValueKind.Array)
                foreach (var h in hs.EnumerateArray())
                    if (h.TryGetProperty("algo", out var al) && al.GetInt32() == 1 &&
                        h.TryGetProperty("value", out var vl))
                        sha1 = vl.GetString();

            var rt = f.TryGetProperty("releaseType", out var rtv) ? rtv.GetInt32() : 1;
            DateTime date = f.TryGetProperty("fileDate", out var fd) && fd.ValueKind == JsonValueKind.String &&
                            DateTime.TryParse(fd.GetString(), out var dt) ? dt : default;
            list.Add(new ResourceVersionEntry(
                fileId, fileName,
                f.TryGetProperty("displayName", out var dn) && dn.GetString() is { Length: > 0 } dnv ? dnv : fileName,
                rt switch { 1 => "release", 2 => "beta", _ => "alpha" },
                date, gvs.ToArray(), loaders.Distinct().ToArray(),
                f.TryGetProperty("fileLength", out var fl) ? fl.GetInt64() : 0,
                urls.ToArray(), sha1));
        }
        return list.OrderByDescending(x => x.Date).Take(100).ToList();
    }

    private static List<ResourceVersionEntry> ParseMrVersions(JsonDocument doc)
    {
        var list = new List<ResourceVersionEntry>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            if (!v.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array ||
                files.GetArrayLength() == 0) continue;

            string? primaryName = null, primaryUrl = null, sha1 = null;
            long size = 0;
            var extraUrls = new List<string>();
            foreach (var f in files.EnumerateArray())
            {
                var url = f.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var name = f.TryGetProperty("filename", out var fn) ? fn.GetString() ?? "" : "";
                if (url.Length == 0 || name.Length == 0) continue;
                bool isPrimary = f.TryGetProperty("primary", out var pr) && pr.ValueKind is JsonValueKind.True or JsonValueKind.False && pr.GetBoolean();
                if (isPrimary || primaryUrl is null)
                {
                    if (primaryUrl is not null && primaryName is not null) extraUrls.Add(primaryUrl);
                    primaryUrl = url; primaryName = name;
                    size = f.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                    sha1 = f.TryGetProperty("hashes", out var hs) && hs.ValueKind == JsonValueKind.Object &&
                           hs.TryGetProperty("sha1", out var s1) ? s1.GetString() : null;
                }
                else extraUrls.Add(url);
            }
            if (primaryUrl is null || primaryName is null) continue;
            var urls = new List<string> { primaryUrl };
            foreach (var u in extraUrls) if (!urls.Contains(u, StringComparer.Ordinal)) urls.Add(u);

            var gvs = new List<string>();
            if (v.TryGetProperty("game_versions", out var gv) && gv.ValueKind == JsonValueKind.Array)
                foreach (var g in gv.EnumerateArray()) { var s = g.GetString() ?? ""; if (s.Length > 0) gvs.Add(s); }
            var lds = new List<string>();
            if (v.TryGetProperty("loaders", out var ld) && ld.ValueKind == JsonValueKind.Array)
                foreach (var l in ld.EnumerateArray()) { var s = l.GetString() ?? ""; if (s.Length > 0) lds.Add(s.ToLowerInvariant()); }

            var vn = v.TryGetProperty("version_number", out var vnEl) ? vnEl.GetString() ?? "" : "";
            list.Add(new ResourceVersionEntry(
                v.TryGetProperty("id", out var vid) ? vid.GetString() ?? vn : vn,
                primaryName, vn.Length > 0 ? vn : primaryName,
                v.TryGetProperty("version_type", out var vt) && vt.ValueKind == JsonValueKind.String ? vt.GetString() ?? "release" : "release",
                v.TryGetProperty("date_published", out var dp) && dp.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(dp.GetString(), out var dt) ? dt : default,
                gvs.ToArray(), lds.ToArray(), size, urls.ToArray(), sha1));
        }
        return list;
    }

    public static async Task<Dictionary<string, (string Url, string? Sha1, long Size, string FileName)>>
        CfBatchFilesAsync(IEnumerable<string> fileIds, CancellationToken ct = default)
    {
        var result = new Dictionary<string, (string, string?, long, string)>();
        var ids = fileIds.Distinct().ToList();
        for (int off = 0; off < ids.Count; off += 100)
        {
            var chunk = ids.Skip(off).Take(100)
                .Select(x => long.TryParse(x, out var v) ? v : 0).Where(v => v > 0).ToArray();
            if (chunk.Length == 0) continue;
            using var content = new StringContent(
                JsonSerializer.Serialize(new { fileIds = chunk }),
                System.Text.Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync($"{CfApiBase}/mods/files", content, ct).ConfigureAwait(true);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(true));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) continue;
            foreach (var f in data.EnumerateArray())
            {
                var id = f.TryGetProperty("id", out var fi) ? fi.GetRawText() : "";
                var fileName = f.TryGetProperty("fileName", out var fn) ? fn.GetString() ?? "" : "";
                if (id.Length == 0 || fileName.Length == 0) continue;
                string? sha1 = null;
                if (f.TryGetProperty("hashes", out var hs) && hs.ValueKind == JsonValueKind.Array)
                    foreach (var h in hs.EnumerateArray())
                        if (h.TryGetProperty("algo", out var al) && al.GetInt32() == 1 &&
                            h.TryGetProperty("value", out var vv)) sha1 = vv.GetString();
                var url = f.TryGetProperty("downloadUrl", out var du) && du.ValueKind == JsonValueKind.String
                    ? du.GetString() : null;
                if (string.IsNullOrEmpty(url) && long.TryParse(id, out var fid))
                    url = $"https://edge.forgecdn.net/files/{fid / 1000}/{fid % 1000}/{Uri.EscapeDataString(fileName)}";
                if (url is { Length: > 0 })
                    result[id] = (url, sha1, f.TryGetProperty("fileLength", out var fl) ? fl.GetInt64() : 0, fileName);
            }
        }
        return result;
    }

    public static async Task<Dictionary<string, string>> CfBatchClassIdsAsync(IEnumerable<string> projectIds, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>();
        var ids = projectIds.Distinct().ToList();
        for (int off = 0; off < ids.Count; off += 100)
        {
            var chunk = ids.Skip(off).Take(100)
                .Select(x => long.TryParse(x, out var v) ? v : 0).Where(v => v > 0).ToArray();
            if (chunk.Length == 0) continue;
            using var content = new StringContent(
                JsonSerializer.Serialize(new { modIds = chunk }),
                System.Text.Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync($"{CfApiBase}/mods", content, ct).ConfigureAwait(true);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(true));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) continue;
            foreach (var p in data.EnumerateArray())
            {
                var id = p.TryGetProperty("id", out var pi) ? pi.GetRawText() : "";
                var cls = p.TryGetProperty("classId", out var ci) && ci.ValueKind == JsonValueKind.String
                    ? ci.GetString() : p.TryGetProperty("classId", out var ci2) && ci2.ValueKind == JsonValueKind.Number
                    ? ci2.GetRawText() : "";
                if (id.Length > 0 && cls.Length > 0) result[id] = cls;
            }
        }
        return result;
    }

    private static List<string>? _mcReleases;
    private static readonly SemaphoreSlim _verLock = new(1, 1);

    private sealed record VersionCache(DateTime FetchDate, List<string> Releases);

    public static async Task<List<string>> GetMcReleaseVersionsAsync()
    {
        if (_mcReleases is { Count: > 0 }) return _mcReleases;
        await _verLock.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_mcReleases is { Count: > 0 }) return _mcReleases;

            var cache = Path.Combine(AppDirs.Modpack, "mc_versions_source.json");
            try
            {
                if (File.Exists(cache))
                {
                    await using var fs = File.OpenRead(cache);
                    var doc = await JsonSerializer.DeserializeAsync<VersionCache>(fs, JsonOpts).ConfigureAwait(true);
                    if (doc is { Releases.Count: > 0 } && (DateTime.Now - doc.FetchDate).TotalDays < 7)
                        return _mcReleases = doc.Releases;
                }
            }
            catch {   }

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.modrinth.com/v2/tag/game_version");
            req.Headers.UserAgent.ParseAdd(UserAgent);
            using var resp = await Http.SendAsync(req).ConfigureAwait(true);
            resp.EnsureSuccessStatusCode();
            using var doc2 = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(true));
            var list = doc2.RootElement.EnumerateArray()
                .Where(v => v.TryGetProperty("version_type", out var t) && t.GetString() == "release"
                            && v.TryGetProperty("version", out _))
                .Select(v => v.GetProperty("version").GetString() ?? "")
                .Where(s => s.Length > 0).ToList();
            if (list.Count == 0) return [];
            _mcReleases = list;
            try
            {
                Directory.CreateDirectory(AppDirs.Modpack);
                await using var fs = File.Create(cache);
                await JsonSerializer.SerializeAsync(fs, new VersionCache(DateTime.Now, list), JsonOpts)
                    .ConfigureAwait(true);
            }
            catch {   }
            return list;
        }
        finally { _verLock.Release(); }
    }

    private static readonly SemaphoreSlim _catLock = new(1, 1);
    private static readonly Dictionary<string, List<string>> _mrCategoryCache = new();
    private static readonly Dictionary<string, List<string>> _cfCategoryCache = new();
    private static readonly Dictionary<(string ClassId, string Name), int> _cfCategoryIds = new();

    public static async Task<List<string>> GetModrinthCategoriesAsync(string projectType)
    {
        var type = projectType == "datapack" ? "mod" : projectType;
        if (_mrCategoryCache.TryGetValue(type, out var hit)) return hit;
        await _catLock.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_mrCategoryCache.TryGetValue(type, out var hit2)) return hit2;
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.modrinth.com/v2/tag/category");
            req.Headers.UserAgent.ParseAdd(UserAgent);
            using var resp = await Http.SendAsync(req).ConfigureAwait(true);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(true));
            var list = doc.RootElement.EnumerateArray()
                .Where(x => x.TryGetProperty("project_type", out var pt) && pt.GetString() == type
                            && x.TryGetProperty("header", out var h) && h.GetString() == "categories"
                            && x.TryGetProperty("name", out _))
                .Select(x => x.GetProperty("name").GetString() ?? "")
                .Where(s => s.Length > 0).ToList();
            return _mrCategoryCache[type] = list;
        }
        catch { return []; }
        finally { _catLock.Release(); }
    }

    public static async Task<List<string>> GetCfCategoriesAsync(string projectType)
    {
        var classId = CfClassId(projectType);
        if (_cfCategoryCache.TryGetValue(classId, out var hit)) return hit;
        await _catLock.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_cfCategoryCache.TryGetValue(classId, out var hit2)) return hit2;
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{CfApiBase}/categories?classId={classId}&gameId=432");
            req.Headers.UserAgent.ParseAdd(UserAgent);
            using var resp = await Http.SendAsync(req).ConfigureAwait(true);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(true));
            var list = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in data.EnumerateArray())
                {
                    var name = x.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (x.TryGetProperty("id", out var idNode) && idNode.TryGetInt32(out var id))
                        _cfCategoryIds[(classId, name)] = id;
                    list.Add(name);
                }
            }
            return _cfCategoryCache[classId] = list;
        }
        catch { return []; }
        finally { _catLock.Release(); }
    }

    public static int? GetCfCategoryId(string projectType, string name)
        => _cfCategoryIds.TryGetValue((CfClassId(projectType), name), out var id) ? id : null;

    private static readonly Dictionary<string, string[]> CfCategoryAlias = new()
    {
        ["technology"] = new[] { "tech" },
        ["adventure"] = new[] { "adventureandrpg" },
        ["combat"] = new[] { "combatpvp" },
        ["challenging"] = new[] { "hardcore" },
        ["lightweight"] = new[] { "smalllight" },
    };

    public static string? MatchCfCategoryName(string slug, List<string> cfNames)
    {
        var raw = CategoryRawKey(slug);
        var hit = cfNames.FirstOrDefault(c => CategoryRawKey(c) == raw);
        if (hit is null && CfCategoryAlias.TryGetValue(slug, out var aliases))
            hit = cfNames.FirstOrDefault(c => aliases.Contains(CategoryRawKey(c)));
        return hit;
    }

    public static string CategoryRawKey(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private async Task DownloadIconsAsync(List<ModpackInfo> picks)
    {
        var pending = picks
            .Where(m => !string.IsNullOrEmpty(m.IconUrl) && !IconMem.ContainsKey(m.IconKey))
            .ToList();
        if (pending.Count == 0) return;

        for (int i = 0; i < pending.Count; i += 8)
        {
            var batch = pending.Skip(i).Take(8);
            await Task.WhenAll(batch.Select(DownloadOneIconAsync)).ConfigureAwait(true);
        }
        await RaisePicksThrottledAsync().ConfigureAwait(true);
    }

    private DateTime _lastIconRaise = DateTime.MinValue;
    private int _iconRaisePending;

    private async Task RaisePicksThrottledAsync()
    {
        var sinceLast = DateTime.UtcNow - _lastIconRaise;
        if (sinceLast < TimeSpan.FromMilliseconds(500))
        {

            if (Interlocked.Exchange(ref _iconRaisePending, 1) == 1) return;
            await Task.Delay(TimeSpan.FromMilliseconds(500) - sinceLast).ConfigureAwait(true);
            _iconRaisePending = 0;
        }
        _lastIconRaise = DateTime.UtcNow;
        PicksChanged?.Invoke();
    }

    private async Task DownloadOneIconAsync(ModpackInfo m)
    {
        var bytes = await FetchIconBytesAsync(m.IconUrl!).ConfigureAwait(true);
        if (bytes is not null) IconMem[m.IconKey] = bytes;
    }

    private sealed record DiskCache(string FetchDate, List<ModpackInfo> Hits);

    private static string CacheFileFor(string source, string projectType, string? category)
        => source == SourceCurseForge
            ? Path.Combine(AppDirs.Modpack, $"cf_{projectType}{(category is null ? "" : "_" + category)}.json")
            : source == SourceAll
                ? Path.Combine(AppDirs.Modpack, $"all_{projectType}{(category is null ? "" : "_" + category)}.json")
                : Path.Combine(AppDirs.Modpack,
                    projectType == "modpack" && category is null
                        ? "modpack_cache.json"
                        : $"modrinth_{projectType}{(category is null ? "" : "_" + category)}.json");

    private async Task<DiskCache?> ReadDiskAsync()
    {
        try
        {
            var file = CacheFileFor(_source, _projectType, _category);
            if (!File.Exists(file)) return null;
            await using var fs = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<DiskCache>(fs, JsonOpts).ConfigureAwait(true);
        }
        catch { return null; }
    }

    private async Task WriteDiskAsync(List<ModpackInfo> hits)
    {
        try
        {
            Directory.CreateDirectory(AppDirs.Modpack);
            await using var fs = File.Create(CacheFileFor(_source, _projectType, _category));
            await JsonSerializer.SerializeAsync(
                fs, new DiskCache(DateTime.Now.ToString("yyyy-MM-dd"), hits), JsonOpts)
                .ConfigureAwait(true);
        }
        catch {   }
    }

    private void RaisePicks()
    {
        if (State.Items.Count > 0) PicksChanged?.Invoke();
    }

    public static string FormatDownloads(long n)
    {
        if (n >= 1_000_000) return (n / 1_000_000.0).ToString("0.#") + "M";
        if (n >= 1_000) return (n / 1_000.0).ToString("0.#") + "k";
        return n.ToString();
    }
}
