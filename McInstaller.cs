using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace StarryLauncher;

public sealed record McVersionEntry(string Id, string Type, string Url, string ReleaseTime);

public sealed record McLoaderEntry(string Version, string Date, bool Stable, string Tag = "");

public sealed record McInstallProgress(string TaskId, string VersionId, string Title, string StageKey, string File, double Percent, double SpeedMb);

public sealed class McInstallStage
{
    public string Key { get; init; } = "";
    public long Total { get; set; }
    public long Done;
    public double Percent => Total <= 0 ? (Done > 0 ? 100 : 0) : Math.Clamp(Done * 100.0 / Total, 0, 100);
}

public sealed class McInstallTask
{
    public string TaskId { get; }
    public string VersionId { get; }
    public string Title { get; }
    public string GameDir { get; }
    public CancellationTokenSource Cts { get; } = new();

    public List<McInstallStage> Stages { get; } = [];
    public string CurrentStageKey { get; private set; } = "dl.stage.meta";
    public string CurrentFile { get; private set; } = "";

    private long _done, _total = 1, _speedSample;
    private DateTime _speedAt = DateTime.Now, _lastRaise;
    private readonly object _lock = new();

    public bool Finished;
    public bool Success;

    public bool Cancelled;

    public string? Error;

    public string ErrorDetail = "";

    private readonly List<string> _tmpFiles = [];
    public int Borrowed;
    private readonly List<string> _createdDirs = [];

    public void TrackTmp(string p) { lock (_lock) _tmpFiles.Add(p); }
    public void UntrackTmp(string p) { lock (_lock) _tmpFiles.Remove(p); }

    public void EnsureVersionDir(string path)
    {
        if (Directory.Exists(path)) return;
        Directory.CreateDirectory(path);
        lock (_lock) _createdDirs.Add(path);
    }

    public void CleanupPartial()
    {
        List<string> tmps, dirs;
        lock (_lock) { tmps = [.. _tmpFiles]; dirs = [.. _createdDirs]; _tmpFiles.Clear(); _createdDirs.Clear(); }
        foreach (var f in tmps) McInstaller.TryDelete(f);
        foreach (var d in dirs)
        {
            try
            {
                if (!Directory.Exists(d)) continue;

                if (Directory.EnumerateFiles(d, "*.json", SearchOption.TopDirectoryOnly).Any())
                {
                    AppLog.Info($"[mcinstall] 清理跳过已有版本清单的目录：{d}");
                    continue;
                }
                Directory.Delete(d, true);
            }
            catch (Exception ex) { AppLog.Warn($"[mcinstall] 残留目录清理失败 {d}：{ex.Message}"); }
        }
    }

    public McInstallTask(string taskId, string versionId, string title, string gameDir)
    {
        TaskId = taskId; VersionId = versionId; Title = title; GameDir = gameDir;
    }

    public double Percent => Math.Clamp(Interlocked.Read(ref _done) * 100.0 / Math.Max(1, Interlocked.Read(ref _total)), 0, 100);
    public double SpeedMb => _speed;
    public long TotalBytes => Interlocked.Read(ref _total);

    private double _speed;
    private long Done => Interlocked.Read(ref _done);
    private long Total => Interlocked.Read(ref _total);

    public McInstallStage Stage(string key)
    {
        lock (_lock)
        {
            var s = Stages.FirstOrDefault(x => x.Key == key);
            if (s is null) { s = new McInstallStage { Key = key }; Stages.Add(s); }
            return s;
        }
    }

    public void SetStage(string key, string file)
    {
        CurrentStageKey = key;
        SetFile(file);
        Raise(force: true);
    }

    public void SetFile(string file) => CurrentFile = file;

    public void AddTotal(long bytes, string stageKey)
    {
        if (bytes <= 0) return;
        Interlocked.Add(ref _total, bytes);
        Stage(stageKey).Total += bytes;
    }

    public void AddDone(long bytes, string stageKey)
    {
        Interlocked.Add(ref _done, bytes);
        Stage(stageKey).Done += bytes;
        Raise();
    }

    public void Raise(bool force = false)
    {
        McInstallProgress? snap = null;
        lock (_lock)
        {
            var now = DateTime.Now;
            if (!force && (now - _lastRaise).TotalMilliseconds < 80) return;
            var dt = (now - _speedAt).TotalSeconds;
            if (dt > 0.4)
            {
                _speed = Math.Max(0, (Interlocked.Read(ref _done) - _speedSample) / dt / 1024.0 / 1024.0);
                _speedSample = Interlocked.Read(ref _done);
                _speedAt = now;
            }
            _lastRaise = now;
            snap = new McInstallProgress(TaskId, VersionId, Title, CurrentStageKey, CurrentFile, Percent, _speed);
        }
        if (snap != null) McInstaller.RaiseProgress(snap);
    }
}

internal static class McInstaller
{
    private static readonly HttpClient Http = LauncherService.Http;
    private static readonly ConcurrentDictionary<string, McInstallTask> _tasks = new();

    public static event Action<McInstallProgress>? ProgressChanged;
    public static event Action<string>? Completed;
    public static event Action<string, string>? Failed;
    public static event Action<string>? Cancelled;

    internal static void RaiseProgress(McInstallProgress p) => ProgressChanged?.Invoke(p);

    public static List<McInstallTask> Tasks
    {
        get
        {
            var list = _tasks.Values.OrderByDescending(t => t.Finished ? 0 : 1).ToList();
            var done = list.Where(t => t.Finished).Skip(20).ToList();
            foreach (var t in done) _tasks.TryRemove(t.TaskId, out _);
            return list;
        }
    }

    public static List<McInstallTask> ActiveTasks => _tasks.Values.Where(t => !t.Finished).ToList();
    public static bool IsRunning => _tasks.Values.Any(t => !t.Finished);

    public static bool IsVersionRunning(string versionId)
        => _tasks.Values.Any(t => !t.Finished && (t.VersionId == versionId || t.Title.Contains(versionId, StringComparison.Ordinal)));

    public static void Cancel(string taskId)
    {
        if (_tasks.TryGetValue(taskId, out var t)) { try { t.Cts.Cancel(); } catch { } }
    }

    public static void CancelAll()
    {
        foreach (var t in ActiveTasks) { try { t.Cts.Cancel(); } catch { } }
    }

    public static void RemoveTask(string taskId) => _tasks.TryRemove(taskId, out _);

    public static string ResolveInstallDir()
    {
        var st = AppSettings.Shared;
        if (!string.IsNullOrWhiteSpace(st.McGameDir))
            return LauncherService.NormalizeGameRoot(st.McGameDir);

        try
        {
            string beside = Path.Combine(AppDirs.ExeDir, ".minecraft");
            if (TryEnsureWritable(beside)) return beside;
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
    }

    private static bool TryEnsureWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public static void InstallVanilla(string versionId, string gameDir, string? customName = null)
        => StartTask(new McInstallTask(versionId, versionId, versionId, gameDir),
            async (t, ct) =>
            {
                await VanillaCoreAsync(t, versionId, gameDir, ct);
                RenameInstalledVersion(t, gameDir, versionId, customName);
            });

    public static void InstallFabric(string mcVersion, string loaderVersion, string gameDir, string? customName = null,
        string? fabricApiFile = null, string[]? fabricApiUrls = null)
        => StartTask(new McInstallTask($"{mcVersion}+fabric+{loaderVersion}", mcVersion,
                $"{mcVersion} · Fabric {loaderVersion}", gameDir),
            async (t, ct) =>
            {

                t.SetStage("dl.stage.meta", "fabric-loader");
                string profileUrl = $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}/{loaderVersion}/profile/json";
                var child = await DownloadJsonAsync(new[] { profileUrl }, ct);
                string fabricId = child.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (fabricId.Length == 0) throw new InvalidOperationException("fabric profile json 缺 id");

                await VanillaCoreAsync(t, mcVersion, gameDir, ct);

                string instDir = Path.Combine(gameDir, "versions", fabricId);
                t.EnsureVersionDir(instDir);
                string jsonPath = Path.Combine(instDir, fabricId + ".json");
                await using (var fs = File.Create(jsonPath))
                    await JsonSerializer.SerializeAsync(fs, child, cancellationToken: ct);

                var merged = MergeForInstall(child, gameDir);
                await DownloadJsonJobsAsync(t, merged, gameDir, Path.Combine(gameDir, "versions", mcVersion), ct);

                if (fabricApiFile != null && fabricApiUrls is { Length: > 0 })
                {
                    t.SetStage("dl.stage.fabricapi", fabricApiFile);
                    string modsDir = Path.Combine(gameDir, "mods");
                    Directory.CreateDirectory(modsDir);
                    await DownloadWithCandidatesAsync(t, "dl.stage.fabricapi", fabricApiUrls, Path.Combine(modsDir, fabricApiFile), ct);
                }

                RenameInstalledVersion(t, gameDir, fabricId, customName);
            });

    public static void InstallForgeLike(string mcVersion, string loaderVersion, bool isNeoForge, string gameDir, string? customName = null)
    {
        string loaderName = isNeoForge ? "NeoForge" : "Forge";
        StartTask(new McInstallTask($"{mcVersion}+{loaderName.ToLower()}+{loaderVersion}", mcVersion,
                $"{mcVersion} · {loaderName} {loaderVersion}", gameDir),
            async (t, ct) =>
            {

                await VanillaCoreAsync(t, mcVersion, gameDir, ct);

                t.SetStage("dl.stage.loader", "installer.jar");
                var stage = t.Stage("dl.stage.loader");
                string installerPath;
                if (isNeoForge)
                {
                    string rel = $"net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar";
                    installerPath = await DownloadWithCandidatesAsync(t, "dl.stage.loader", OrderBySource(
                        "https://bmclapi2.bangbang93.com/maven/" + rel,
                        "https://maven.neoforged.net/releases/" + rel),
                        Path.Combine(gameDir, "installer-cache", $"neoforge-{loaderVersion}-installer.jar"), ct);
                }
                else
                {
                    string full = $"{mcVersion}-{loaderVersion}";
                    string rel = $"net/minecraftforge/forge/{full}/forge-{full}-installer.jar";
                    installerPath = await DownloadWithCandidatesAsync(t, "dl.stage.loader", OrderBySource(
                        "https://bmclapi2.bangbang93.com/maven/" + rel,
                        "https://maven.minecraftforge.net/" + rel),
                        Path.Combine(gameDir, "installer-cache", $"forge-{full}-installer.jar"), ct);
                }

                string java = FindJavaForInstaller(gameDir, mcVersion)
                    ?? throw new InvalidOperationException(S.Get("dl.nojava"));
                string versionsRoot = Path.Combine(gameDir, "versions");
                var dirsBefore = Directory.Exists(versionsRoot)
                    ? Directory.GetDirectories(versionsRoot).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : [];
                await RunInstallerAsync(t, java, installerPath, gameDir, ct);

                var created = Directory.GetDirectories(versionsRoot)
                    .Where(d => !dirsBefore.Contains(d) && Path.GetFileName(d) != mcVersion)
                    .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                    .ToList();
                if (created.Count > 0)
                    RenameInstalledVersion(t, gameDir, Path.GetFileName(created[0]), customName);
            });
    }

    private static string[] OrderBySource(string bmcl, string official)
        => LauncherService.UseOfficialSource ? new[] { official, bmcl } : new[] { bmcl, official };

    public static void DownloadResourceFile(string title, string fileName, string[] urls, string destPath, string? sha1)
        => StartTask(new McInstallTask("res:" + fileName, fileName, title,
                Path.GetDirectoryName(destPath) ?? ""),
            async (t, ct) =>
            {
                t.SetStage("dl.stage.res", fileName);
                await DownloadWithCandidatesAsync(t, "dl.stage.res", urls, destPath, ct);
                VerifySha1(destPath, sha1);
            });

    public static void DownloadResourceMap(string title, string fileName, string[] urls, string savesDir, string? sha1)
        => StartTask(new McInstallTask("res:" + fileName, fileName, title, savesDir),
            async (t, ct) =>
            {
                Directory.CreateDirectory(savesDir);
                string zipPath = Path.Combine(Path.GetTempPath(), "starry-map-" + Guid.NewGuid().ToString("N") + ".zip");
                t.TrackTmp(zipPath);
                try
                {
                    t.SetStage("dl.stage.res", fileName);
                    await DownloadWithCandidatesAsync(t, "dl.stage.res", urls, zipPath, ct);
                    VerifySha1(zipPath, sha1);

                    t.SetStage("dl.stage.resmap", fileName);
                    using var archive = ZipFile.OpenRead(zipPath);
                    var entries = archive.Entries.Where(e => e.Length > 0 || e.FullName.EndsWith("/")).ToList();
                    if (entries.Count == 0) throw new InvalidOperationException("地图压缩包为空");

                    string? root = null;
                    var tops = entries.Select(e => e.FullName.Replace('\\', '/').Split('/')[0])
                        .Where(s => s.Length > 0).Distinct().ToList();
                    if (tops.Count == 1 && entries.Any(e => e.FullName.Replace('\\', '/').Contains('/')))
                        root = tops[0];
                    string mapName = UniqueDirName(savesDir, root ?? Path.GetFileNameWithoutExtension(fileName));
                    string mapDir = Path.Combine(savesDir, mapName);
                    Directory.CreateDirectory(mapDir);

                    foreach (var e in entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        var rel = e.FullName.Replace('\\', '/').TrimEnd('/');
                        if (rel.Length == 0) continue;
                        if (root != null && rel.StartsWith(root + "/"))
                            rel = rel[(root.Length + 1)..];
                        else if (root == null) rel = Path.GetFileName(rel);
                        if (rel.Length == 0 || rel.Contains("..")) continue;
                        string target = Path.Combine(mapDir, rel);
                        if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\"))
                            Directory.CreateDirectory(target);
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            using var src = e.Open();
                            await using var dst = File.Create(target);
                            await src.CopyToAsync(dst, ct);
                        }
                    }
                    AppLog.Info($"[mcinstall] 地图解压完成：saves/{mapName}");
                }
                finally
                {
                    TryDelete(zipPath);
                    t.UntrackTmp(zipPath);
                }
            });

    private static void VerifySha1(string path, string? sha1)
    {
        if (string.IsNullOrWhiteSpace(sha1)) return;
        using var fs = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
        if (hash != sha1.Trim().ToLowerInvariant())
            throw new InvalidOperationException($"sha1 校验失败（{hash[..8]}… ≠ {sha1.Trim()[..8]}…），文件可能被镜像站篡改或下载不完整");
    }

    private static string UniqueDirName(string parent, string name)
    {
        string candidate = name;
        for (var i = 2; Directory.Exists(Path.Combine(parent, candidate)); i++)
            candidate = $"{name}-{i}";
        return candidate;
    }

    private static string PackFolder(string? classId) => classId switch
    {
        "12" or "6945" => "resourcepacks",
        "6552" => "shaderpacks",
        _ => "mods",
    };

    private static bool TryGetProp(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        return false;
    }

    public static void ExportMrpack(string title, string packName, string[] urls, string? sha1, string gameDir, string outputPath)
        => StartTask(new McInstallTask("mrpack:" + packName, packName, title, gameDir), async (t, ct) =>
        {
            string safeName = string.Concat((packName ?? "modpack").Split(Path.GetInvalidFileNameChars()));
            if (safeName.Length == 0) safeName = "modpack";
            Directory.CreateDirectory(Path.Combine(gameDir, "installer-cache", "packs"));
            string zipPath = Path.Combine(gameDir, "installer-cache", "packs", safeName + ".zip");
            t.SetStage("dl.stage.packzip", safeName + ".zip");
            await DownloadWithCandidatesAsync(t, "dl.stage.packzip", urls, zipPath, ct);
            VerifySha1(zipPath, sha1);

            t.SetStage("dl.pack.exporting", Path.GetFileName(outputPath));
            string? outDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var idxEntry = archive.GetEntry("modrinth.index.json");
                if (idxEntry is not null)
                {

                    using var outZip = ZipFile.Open(outputPath, ZipArchiveMode.Create);
                    foreach (var e in archive.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        var ne = outZip.CreateEntry(e.FullName, CompressionLevel.Optimal);
                        await using var src = e.Open();
                        await using var dst = ne.Open();
                        await src.CopyToAsync(dst, ct);
                    }
                }
                else
                {

                    var mfEntry = archive.GetEntry("manifest.json")
                        ?? throw new InvalidOperationException(S.Get("dl.pack.notfound"));
                    string manifestJson;
                    using (var r = new StreamReader(mfEntry.Open())) manifestJson = await r.ReadToEndAsync(ct);
                    using var manifest = JsonDocument.Parse(manifestJson);
                    var mroot = manifest.RootElement;

                    string mcVersion = "";
                    if (TryGetProp(mroot, "minecraft", out var mcEl) && TryGetProp(mcEl, "version", out var vEl))
                        mcVersion = vEl.GetString() ?? "";
                    string loaderKey = "forge", loaderVer = "";
                    if (TryGetProp(mcEl, "modLoaders", out var mls) && mls.ValueKind == JsonValueKind.Array && mls.GetArrayLength() > 0)
                    {
                        var id = TryGetProp(mls[0], "id", out var idEl) ? idEl.GetString() ?? "" : "";
                        var dash = id.IndexOf('-');
                        if (dash > 0)
                        {
                            loaderKey = id[..dash].ToLowerInvariant();
                            loaderVer = id[(dash + 1)..];
                        }
                    }

                    var pairs = new List<(string Proj, string File)>();
                    if (TryGetProp(mroot, "files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
                        foreach (var f in filesEl.EnumerateArray())
                        {
                            bool required = !TryGetProp(f, "required", out var rq) || rq.ValueKind != JsonValueKind.False;
                            if (!required) continue;
                            var fid = TryGetProp(f, "fileID", out var fq) ? fq.GetRawText() : "";
                            var pid = TryGetProp(f, "projectID", out var pp) ? pp.GetRawText() : "";
                            if (fid.Length > 0) pairs.Add((pid, fid));
                        }
                    t.SetStage("dl.pack.exporting", $"{pairs.Count} 个文件");
                    var fileInfos = await ModpackService.CfBatchFilesAsync(pairs.Select(x => x.File), ct);
                    var classIds = await ModpackService.CfBatchClassIdsAsync(pairs.Select(x => x.Proj), ct);

                    var fileList = new List<Dictionary<string, object?>>();
                    foreach (var (proj, fid) in pairs)
                    {
                        if (!fileInfos.TryGetValue(fid, out var info)) continue;
                        string folder = classIds.TryGetValue(proj, out var cls) ? PackFolder(cls) : "mods";
                        string path = folder + "/" + info.FileName;
                        var hashes = new Dictionary<string, string>();
                        if (!string.IsNullOrEmpty(info.Sha1)) hashes["sha1"] = info.Sha1!;
                        var env = new Dictionary<string, string>
                        {
                            ["client"] = folder == "shaderpacks" ? "optional" : "required",
                            ["server"] = "optional",
                        };
                        fileList.Add(new Dictionary<string, object?>
                        {
                            ["path"] = path,
                            ["hashes"] = hashes,
                            ["downloads"] = new[] { info.Url },
                            ["fileSize"] = info.Size,
                            ["env"] = env,
                        });
                    }
                    var deps = new Dictionary<string, string> { ["minecraft"] = mcVersion };
                    if (loaderVer.Length > 0) deps[loaderKey == "forge" ? "forge" : loaderKey] = loaderVer;
                    var index = new Dictionary<string, object?>
                    {
                        ["formatVersion"] = 1,
                        ["game"] = "minecraft",
                        ["versionId"] = mcVersion + (loaderVer.Length > 0 ? "-" + loaderVer : ""),
                        ["name"] = packName,
                        ["summary"] = $"{packName}（CurseForge → mrpack）",
                        ["files"] = fileList,
                        ["dependencies"] = deps,
                    };
                    string indexJson = JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true });

                    using var outZip = ZipFile.Open(outputPath, ZipArchiveMode.Create);
                    var idxOut = outZip.CreateEntry("modrinth.index.json", CompressionLevel.Optimal);
                    await using (var w = new StreamWriter(idxOut.Open())) await w.WriteAsync(indexJson);
                    foreach (var e in archive.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!e.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)) continue;
                        var ne = outZip.CreateEntry(e.FullName, CompressionLevel.Optimal);
                        await using var src = e.Open();
                        await using var dst = ne.Open();
                        await src.CopyToAsync(dst, ct);
                    }
                }
            }
            AppLog.Info($"[mcinstall] {t.Title} 已导出 mrpack：{outputPath}");
            t.SetStage("dl.pack.exportok", Path.GetFileName(outputPath));
        });

    public static void InstallModpack(string title, string packName, string[] urls, string? sha1, string gameDir)
        => StartTask(new McInstallTask("pack:" + packName, packName, title, gameDir), async (t, ct) =>
        {

            string safeName = string.Concat((packName ?? "modpack").Split(Path.GetInvalidFileNameChars()));
            if (safeName.Length == 0) safeName = "modpack";
            Directory.CreateDirectory(Path.Combine(gameDir, "installer-cache", "packs"));
            string zipPath = Path.Combine(gameDir, "installer-cache", "packs", safeName + ".zip");
            t.SetStage("dl.stage.packzip", safeName + ".zip");
            await DownloadWithCandidatesAsync(t, "dl.stage.packzip", urls, zipPath, ct);
            VerifySha1(zipPath, sha1);

            t.SetStage("dl.stage.packcore", "manifest");
            string manifestJson;
            bool isCf;
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                if (archive.GetEntry("mmc-pack.json") is not null)
                    throw new InvalidOperationException(S.Get("dl.pack.unsupported"));
                var entry = archive.GetEntry("manifest.json") ?? archive.GetEntry("modrinth.index.json");
                if (entry is null)
                {

                    bool serverPack = archive.Entries.Any(z =>
                    {
                        var n = z.FullName;
                        if (n.Contains('/') || n.Contains('\\')) return false;
                        return n.Equals("run.bat", StringComparison.OrdinalIgnoreCase)
                            || n.Equals("run.sh", StringComparison.OrdinalIgnoreCase)
                            || n.Equals("start.bat", StringComparison.OrdinalIgnoreCase)
                            || n.Equals("start.sh", StringComparison.OrdinalIgnoreCase)
                            || n.Equals("server.properties", StringComparison.OrdinalIgnoreCase)
                            || n.EndsWith("-installer.jar", StringComparison.OrdinalIgnoreCase)
                            || n.Equals("user_jvm_args.txt", StringComparison.OrdinalIgnoreCase);
                    });
                    throw new InvalidOperationException(S.Get(serverPack ? "dl.pack.server" : "dl.pack.notfound"));
                }
                isCf = entry.FullName == "manifest.json";
                using var r = new StreamReader(entry.Open());
                manifestJson = await r.ReadToEndAsync(ct);
            }
            using var manifest = JsonDocument.Parse(manifestJson);
            var mroot = manifest.RootElement;

            string mcVersion = "";
            string? loaderKind = null, loaderVer = null;
            if (TryGetProp(mroot, "minecraft", out var mcEl) && TryGetProp(mcEl, "version", out var vEl))
                mcVersion = vEl.GetString() ?? "";
            if (isCf)
            {
                if (TryGetProp(mcEl, "modLoaders", out var mls) && mls.ValueKind == JsonValueKind.Array &&
                    mls.GetArrayLength() > 0)
                {
                    var id = TryGetProp(mls[0], "id", out var idEl) ? idEl.GetString() ?? "" : "";
                    var dash = id.IndexOf('-');
                    if (dash > 0) { loaderKind = id[..dash].ToLowerInvariant(); loaderVer = id[(dash + 1)..]; }
                }
            }
            else if (TryGetProp(mroot, "dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
            {
                foreach (var d in deps.EnumerateObject())
                {
                    var k = d.Name.ToLowerInvariant();
                    if (k is "fabric-loader" or "fabric") { loaderKind = "fabric"; loaderVer = d.Value.GetString(); }
                    else if (k == "forge") { loaderKind = "forge"; loaderVer = d.Value.GetString(); }
                    else if (k == "neoforge") { loaderKind = "neoforge"; loaderVer = d.Value.GetString(); }
                    else if (k == "quilt") throw new InvalidOperationException(S.Get("dl.pack.quilt"));
                }
            }
            if (mcVersion.Length == 0) throw new InvalidOperationException(S.Get("dl.pack.notfound"));
            AppLog.Info($"[mcinstall] 整合包 {packName} 清单：MC {mcVersion}" +
                (loaderKind is not null ? $" + {loaderKind} {loaderVer}" : "（无加载器）") + "；2026-11-23 修 modLoaders 大小写前 forge 判定一直失败");

            t.SetStage("dl.stage.packcore", mcVersion);
            var versionsRoot = Path.Combine(gameDir, "versions");
            var dirsBefore = Directory.Exists(versionsRoot)
                ? Directory.GetDirectories(versionsRoot).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
            await VanillaCoreAsync(t, mcVersion, gameDir, ct);
            if (loaderKind is not null && loaderVer is { Length: > 0 })
            {
                t.SetStage("dl.stage.packcore", $"{loaderKind} {loaderVer}");
                if (loaderKind == "fabric")
                {

                    string profileUrl = $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}/{loaderVer}/profile/json";
                    var child = await DownloadJsonAsync(new[] { profileUrl }, ct);
                    string fabricId = child.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    if (fabricId.Length == 0) throw new InvalidOperationException("fabric profile json 缺 id");
                    string instDir = Path.Combine(versionsRoot, fabricId);
                    t.EnsureVersionDir(instDir);
                    await using (var fs = File.Create(Path.Combine(instDir, fabricId + ".json")))
                        await JsonSerializer.SerializeAsync(fs, child, cancellationToken: ct);
                    var merged = MergeForInstall(child, gameDir);
                    await DownloadJsonJobsAsync(t, merged, gameDir, Path.Combine(versionsRoot, mcVersion), ct);
                }
                else
                {
                    bool isNeo = loaderKind == "neoforge";
                    string full = isNeo ? loaderVer : $"{mcVersion}-{loaderVer}";
                    string rel = isNeo
                        ? $"net/neoforged/neoforge/{loaderVer}/neoforge-{loaderVer}-installer.jar"
                        : $"net/minecraftforge/forge/{full}/forge-{full}-installer.jar";
                    t.SetStage("dl.stage.packcore", "installer.jar");
                    string installerPath = await DownloadWithCandidatesAsync(t, "dl.stage.packcore", OrderBySource(
                        "https://bmclapi2.bangbang93.com/maven/" + rel,
                        isNeo ? "https://maven.neoforged.net/releases/" + rel : "https://maven.minecraftforge.net/" + rel),
                        Path.Combine(gameDir, "installer-cache", $"pack-{full}-installer.jar"), ct);
                    string java = FindJavaForInstaller(gameDir, mcVersion)
                        ?? throw new InvalidOperationException(S.Get("dl.nojava"));
                    await RunInstallerAsync(t, java, installerPath, gameDir, ct);
                }
            }
            var newDirs = Directory.Exists(versionsRoot)
                ? Directory.GetDirectories(versionsRoot).Where(d => !dirsBefore.Contains(d)).ToList() : [];

            t.SetStage("dl.stage.packover", packName);
            string instPath = Path.Combine(versionsRoot, safeName);

            var forgeNew = loaderKind is "forge" or "neoforge"
                ? newDirs.FirstOrDefault(d => Path.GetFileName(d) != mcVersion)
                : null;
            if (forgeNew is not null && Path.GetFileName(forgeNew) != safeName)
                RenameInstalledVersion(t, gameDir, Path.GetFileName(forgeNew), safeName);

            if (forgeNew is not null && Directory.Exists(forgeNew) && Path.GetFileName(forgeNew) != safeName)
            {
                try
                {
                    string bakRoot = Path.Combine(gameDir, "installer-cache", "old-versions");
                    Directory.CreateDirectory(bakRoot);
                    string bak = Path.Combine(bakRoot, Path.GetFileName(forgeNew) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    Directory.Move(forgeNew, bak);
                    AppLog.Info($"[mcinstall] {t.Title} 残留的中间版本 {Path.GetFileName(forgeNew)} 已移到备份：{bak}");
                }
                catch (Exception ex) { AppLog.Warn($"[mcinstall] 移动残留中间版本失败：{ex.Message}"); }
            }
            else if (forgeNew is null)
            {
                string guess = loaderKind switch
                {
                    "fabric" => $"fabric-loader-{mcVersion}-{loaderVer}",
                    "forge" => $"{mcVersion}-forge-{loaderVer}",
                    "neoforge" => $"{mcVersion}-neoforge-{loaderVer}",
                    _ => mcVersion,
                };
                string srcDir = Path.Combine(versionsRoot, guess);
                if (!Directory.Exists(srcDir)) srcDir = Path.Combine(versionsRoot, mcVersion);
                string srcName = Path.GetFileName(srcDir);
                string srcJson = Path.Combine(srcDir, srcName + ".json");
                if (File.Exists(srcJson))
                {
                    var patched = WithJsonId(JsonDocument.Parse(File.ReadAllText(srcJson)).RootElement.Clone(), safeName);
                    await using (var fs = File.Create(Path.Combine(instPath, safeName + ".json")))
                        await JsonSerializer.SerializeAsync(fs, patched, new JsonSerializerOptions { WriteIndented = true }, ct);
                }
                string srcJar = Path.Combine(srcDir, srcName + ".jar");
                if (File.Exists(srcJar))
                    File.Copy(srcJar, Path.Combine(instPath, safeName + ".jar"), overwrite: true);
            }
            t.EnsureVersionDir(instPath);

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entries = archive.Entries
                    .Select(e => (E: e, N: e.FullName.Replace('\\', '/')))
                    .Where(x => x.N.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase) && x.N.Length > "overrides/".Length)
                    .ToList();
                t.AddTotal(entries.Sum(x => x.E.Length), "dl.stage.packover");
                foreach (var (e, full) in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = full["overrides/".Length..];
                    if (rel.Contains("..")) continue;
                    string target = Path.Combine(instPath, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (full.EndsWith('/')) Directory.CreateDirectory(target);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        using var src = e.Open();
                        await using var dst = File.Create(target);
                        await src.CopyToAsync(dst, ct);
                        t.AddDone(e.Length, "dl.stage.packover");
                    }
                }
            }

            t.SetStage("dl.stage.packfiles", "");
            var jobs = new List<(string Dest, string[] Urls, string? Sha1, long Size, string Name)>();
            if (isCf)
            {
                var pairs = new List<(string Proj, string File)>();
                int skipped = 0;
                if (TryGetProp(mroot, "files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
                    foreach (var f in filesEl.EnumerateArray())
                    {
                        var projId = TryGetProp(f, "projectID", out var p) ? p.GetRawText() : "";
                        var fileId = TryGetProp(f, "fileID", out var q) ? q.GetRawText() : "";
                        bool required = !TryGetProp(f, "required", out var rq) || rq.ValueKind != JsonValueKind.False;
                        if (fileId.Length == 0) continue;
                        if (!required) { skipped++; continue; }
                        pairs.Add((projId, fileId));
                    }
                if (skipped > 0) AppLog.Info($"[mcinstall] 整合包 {packName}：跳过 {skipped} 个可选文件");
                var fileInfos = await ModpackService.CfBatchFilesAsync(pairs.Select(x => x.File), ct);
                var classIds = await ModpackService.CfBatchClassIdsAsync(pairs.Select(x => x.Proj), ct);
                foreach (var (proj, fileId) in pairs)
                {
                    if (!fileInfos.TryGetValue(fileId, out var info)) { AppLog.Warn($"[mcinstall] 整合包文件 {fileId} 批量查询无结果，跳过"); continue; }
                    string folder = classIds.TryGetValue(proj, out var cls) ? PackFolder(cls) : "mods";

                    string back = info.Url.Replace("edge.forgecdn.net", "mediafilez.forgecdn.net");
                    var srcs = back != info.Url ? new[] { info.Url, back } : new[] { info.Url };
                    jobs.Add((Path.Combine(instPath, folder, info.FileName), srcs, info.Sha1, info.Size, info.FileName));
                }
            }
            else if (TryGetProp(mroot, "files", out var mrFiles) && mrFiles.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in mrFiles.EnumerateArray())
                {

                    if (TryGetProp(f, "env", out var env) && TryGetProp(env, "client", out var cl) &&
                        cl.ValueKind == JsonValueKind.String && cl.GetString() == "unsupported") continue;
                    var path = TryGetProp(f, "path", out var pa) ? pa.GetString() ?? "" : "";
                    if (path.Length == 0 || path.Contains("..")) continue;
                    var dl = TryGetProp(f, "downloads", out var dls) && dls.ValueKind == JsonValueKind.Array
                        ? dls.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray() : [];
                    if (dl.Length == 0) continue;
                    string? sha = TryGetProp(f, "hashes", out var hs) && hs.ValueKind == JsonValueKind.Object &&
                                  TryGetProp(hs, "sha1", out var s1) ? s1.GetString() : null;
                    long size = TryGetProp(f, "fileSize", out var sz) ? sz.GetInt64() : 0;
                    jobs.Add((Path.Combine(instPath, path.Replace('/', Path.DirectorySeparatorChar)), dl, sha, size, Path.GetFileName(path)));
                }
            }
            t.SetStage("dl.stage.packfiles", $"{jobs.Count} 个文件");
            foreach (var j in jobs) t.AddTotal(j.Size, "dl.stage.packfiles");
            Directory.CreateDirectory(Path.Combine(instPath, "mods"));

            using var gate = new SemaphoreSlim(16);
            var failures = new ConcurrentBag<string>();
            await Task.WhenAll(jobs.Select(async j =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    t.SetFile(j.Name);
                    await DownloadWithCandidatesAsync(t, "dl.stage.packfiles", j.Urls, j.Dest, ct, j.Size);
                    try { VerifySha1(j.Dest, j.Sha1); }
                    catch { TryDelete(j.Dest); throw; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppLog.Warn($"[mcinstall] 整合包文件下载失败 {j.Name}：{ex.Message}");
                    failures.Add(j.Name);
                }
                finally { gate.Release(); }
            }));
            if (failures.Count > 0)
                throw new InvalidOperationException(
                    $"{failures.Count} 个整合包文件下载失败（{failures.First()} 等），请检查网络后重试");
        });

    private static bool IsNewOptiFineMc(string mcVersion)
    {
        if (mcVersion.StartsWith("1.") && mcVersion.Split('.').Length >= 2 &&
            int.TryParse(mcVersion.Split('.')[1], out int minor))
            return minor >= 14;
        return true;
    }

    private static async Task<string?> ScrapeOptiFineOfficialUrlAsync(string fileName, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync("https://optifine.net/adloadx?f=" + fileName, ct);
            resp.EnsureSuccessStatusCode();
            string html = await resp.Content.ReadAsStringAsync(ct);
            var m = Regex.Match(html, @"downloadx\?f=[^""']+");
            return m.Success ? "https://optifine.net/" + m.Value : null;
        }
        catch { return null; }
    }

    public static void InstallOptiFine(string mcVersion, string version, string fileName, string gameDir, string? customName = null)
    {
        StartTask(new McInstallTask($"{mcVersion}+optifine+{version}", mcVersion,
                $"{mcVersion} · OptiFine {version}", gameDir),
            async (t, ct) =>
            {

                await VanillaCoreAsync(t, mcVersion, gameDir, ct);

                t.SetStage("dl.stage.optifine", fileName);

                string core = fileName.Replace(".jar", "").Replace("preview_", "");
                if (core.StartsWith(mcVersion + "_")) core = core[(mcVersion.Length + 1)..];
                var segs = core.Split('_');
                string patch = segs[^1];
                string type = string.Join('_', segs[..^1]);
                string mcUrl = mcVersion == "1.8" ? "1.8.0" : mcVersion == "1.9" ? "1.9.0" : mcVersion;
                string bmcl = $"https://bmclapi2.bangbang93.com/optifine/{mcUrl}/{type}/{patch}";
                string? offUrl = await ScrapeOptiFineOfficialUrlAsync(fileName, ct);
                string[] urls = offUrl is null ? new[] { bmcl } : OrderBySource(bmcl, offUrl);
                string target = Path.Combine(gameDir, "installer-cache", fileName);
                await DownloadWithCandidatesAsync(t, "dl.stage.optifine", urls, target, ct);

                if (IsNewOptiFineMc(mcVersion))
                {

                    int minJava = 21;
                    try
                    {
                        using var zip = System.IO.Compression.ZipFile.OpenRead(target);
                        var cls = zip.GetEntry("optifine/Installer.class")
                            ?? throw new FileNotFoundException("optifine/Installer.class not found");
                        using var er = cls.Open();
                        var head = new byte[8];
                        for (int read = 0; read < 8;)
                        {
                            int n = er.Read(head, read, 8 - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        if (head[0] == 0xCA && head[1] == 0xFE && head[2] == 0xBA && head[3] == 0xBE)
                        {
                            int ver = head[6] * 0x100 + head[7];
                            if (ver is > 48 and < 100) minJava = ver - 44;
                        }
                        AppLog.Info($"[mcinstall] OptiFine 安装器 class 版本 {head[6] * 0x100 + head[7]}，需 Java {minJava}+");
                    }
                    catch (Exception ex) { AppLog.Warn("[mcinstall] 读 OptiFine 安装器 Java 版本失败：" + ex.Message); }

                    var javas = LauncherService.CollectJavas()
                        .Where(j => j.Major >= minJava).OrderBy(j => j.Major).ToList();
                    if (javas.Count == 0) throw new InvalidOperationException(S.Get("dl.nojava"));
                    var (javaExe, javaMajor) = javas[0];

                    string home = Path.Combine(gameDir, "installer-cache", "optifine-home");
                    string homeMc = Path.Combine(home, ".minecraft");
                    try { Directory.Delete(home, true); } catch { }
                    Directory.CreateDirectory(Path.Combine(homeMc, "versions", mcVersion));
                    await File.WriteAllTextAsync(Path.Combine(homeMc, "launcher_profiles.json"), "{}", ct);
                    foreach (var ext in new[] { ".json", ".jar" })
                    {
                        string src = Path.Combine(gameDir, "versions", mcVersion, mcVersion + ext);
                        if (File.Exists(src))
                            File.Copy(src, Path.Combine(homeMc, "versions", mcVersion, mcVersion + ext), true);
                    }

                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = javaExe,
                        Arguments = (javaMajor >= 9 ? "--add-exports cpw.mods.bootstraplauncher/cpw.mods.bootstraplauncher=ALL-UNNAMED " : "") +
                                    $"-Duser.home=\"{home}\" -cp \"{target}\" optifine.Installer",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = home,
                    };
                    psi.EnvironmentVariables["appdata"] = home;
                    AppLog.Info($"[mcinstall] OptiFine 静默安装：java {javaMajor} · {psi.Arguments}");
                    string RunInstaller()
                    {
                        using var proc = System.Diagnostics.Process.Start(psi)
                            ?? throw new InvalidOperationException("安装器进程启动失败");
                        void Kill() { try { if (!proc.HasExited) proc.Kill(true); } catch { } }
                        using var reg = ct.Register(Kill);
                        var so = proc.StandardOutput.ReadToEndAsync(ct);
                        var se = proc.StandardError.ReadToEndAsync(ct);
                        proc.WaitForExitAsync(ct).GetAwaiter().GetResult();
                        string stdout = so.GetAwaiter().GetResult() ?? "";
                        string stderr = se.GetAwaiter().GetResult() ?? "";
                        if (stdout.Length > 0)
                        {
                            var tail = string.Join(" | ", stdout.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(6));
                            AppLog.Info($"[mcinstall][optifine] {tail[..Math.Min(400, tail.Length)]}");
                        }
                        if (stderr.Length > 0) AppLog.Warn($"[mcinstall][optifine-err] {stderr[..Math.Min(400, stderr.Length)]}");
                        return stdout;
                    }
                    string output = await Task.Run(RunInstaller, ct);
                    if (output.Split('\n').Length < 1000)
                        throw new InvalidOperationException(S.Get("dl.installerFail") + "（安装器输出异常偏少）");

                    var created = Directory.GetDirectories(Path.Combine(homeMc, "versions"))
                        .Where(d => Path.GetFileName(d) != mcVersion)
                        .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d)).ToList();
                    if (created.Count == 0)
                        throw new InvalidOperationException(S.Get("dl.installerFail") + "（安装器未生成版本目录）");
                    string newId = Path.GetFileName(created[0]);

                    string dstVer = Path.Combine(gameDir, "versions", newId);
                    if (Directory.Exists(dstVer)) Directory.Delete(dstVer, true);
                    Directory.Move(created[0], dstVer);
                    string srcLib = Path.Combine(homeMc, "libraries");
                    string dstLib = Path.Combine(gameDir, "libraries");
                    if (Directory.Exists(srcLib))
                        foreach (var f in Directory.EnumerateFiles(srcLib, "*", SearchOption.AllDirectories))
                        {
                            string rel = Path.GetRelativePath(srcLib, f);
                            string dst = Path.Combine(dstLib, rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                            if (!File.Exists(dst))
                                File.Copy(f, dst);
                        }
                    try { Directory.Delete(home, true); } catch { }
                    try { File.Delete(target); } catch { }
                    AppLog.Info($"[mcinstall] OptiFine 方式 A 安装完成：{newId}");
                    RenameInstalledVersion(t, gameDir, newId, customName);
                }
                else
                {

                    string libVer = core;
                    string jarName = fileName.Replace("OptiFine_", "OptiFine-").Replace("preview_", "");
                    string libDest = Path.Combine(gameDir, "libraries", "optifine", "OptiFine", libVer, jarName);
                    Directory.CreateDirectory(Path.GetDirectoryName(libDest)!);
                    File.Move(target, libDest);

                    string ofId = $"{mcVersion}-OptiFine_{type}_{patch}";
                    string verDir = Path.Combine(gameDir, "versions", ofId);
                    Directory.CreateDirectory(verDir);
                    string srcJar = Path.Combine(gameDir, "versions", mcVersion, mcVersion + ".jar");
                    if (File.Exists(srcJar)) File.Copy(srcJar, Path.Combine(verDir, ofId + ".jar"), true);

                    string relTime = DateTime.Now.ToString("yyyy-MM-dd");
                    bool oldArgs = false;
                    string vanillaArgs = "";
                    try
                    {
                        using var vd = JsonDocument.Parse(File.ReadAllText(srcJar.Replace(".jar", ".json")));
                        if (vd.RootElement.TryGetProperty("releaseTime", out var rt))
                            relTime = (rt.GetString() ?? relTime)[..10];
                        if (vd.RootElement.TryGetProperty("minecraftArguments", out var ma))
                        {
                            oldArgs = true;
                            vanillaArgs = ma.GetString() ?? "";
                        }
                    }
                    catch { }
                    var jsonObj = new Dictionary<string, object?>
                    {
                        ["id"] = ofId,
                        ["inheritsFrom"] = mcVersion,
                        ["time"] = relTime + "T23:33:33+08:00",
                        ["releaseTime"] = relTime + "T23:33:33+08:00",
                        ["type"] = "release",
                        ["libraries"] = new object[]
                        {
                            new Dictionary<string, object?> { ["name"] = $"optifine:OptiFine:{libVer}" },
                            new Dictionary<string, object?> { ["name"] = "net.minecraft:launchwrapper:1.12" },
                        },
                        ["mainClass"] = "net.minecraft.launchwrapper.Launch",
                    };
                    if (oldArgs)
                    {
                        jsonObj["minimumLauncherVersion"] = 18;
                        jsonObj["minecraftArguments"] = vanillaArgs + " --tweakClass optifine.OptiFineTweaker";
                    }
                    else
                    {
                        jsonObj["minimumLauncherVersion"] = "21";
                        jsonObj["arguments"] = new Dictionary<string, object?>
                        {
                            ["game"] = new[] { "--tweakClass", "optifine.OptiFineTweaker" },
                        };
                    }
                    string jsonPath = Path.Combine(verDir, ofId + ".json");
                    await using (var fs = File.Create(jsonPath))
                        await JsonSerializer.SerializeAsync(fs, jsonObj, new JsonSerializerOptions { WriteIndented = true }, ct);

                    var child = JsonDocument.Parse(File.ReadAllText(jsonPath)).RootElement.Clone();
                    var merged = MergeForInstall(child, gameDir);
                    await DownloadJsonJobsAsync(t, merged, gameDir, verDir, ct);
                    AppLog.Info($"[mcinstall] OptiFine 方式 B 安装完成：{ofId}");
                    RenameInstalledVersion(t, gameDir, ofId, customName);
                }
            });
    }

    private static void RenameInstalledVersion(McInstallTask task, string gameDir, string fromId, string? newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        newName = string.Concat(newName.Split(Path.GetInvalidFileNameChars()));
        if (newName.Length == 0 || newName == fromId) return;
        var src = Path.Combine(gameDir, "versions", fromId);
        if (!Directory.Exists(src)) return;
        var dst = Path.Combine(gameDir, "versions", newName);
        if (Directory.Exists(dst))
        {
            AppLog.Warn($"[mcinstall] 重命名目标 {newName} 已存在，保留原版本名 {fromId}");

            try
            {
                string srcJson = Path.Combine(src, fromId + ".json");
                string dstJson = Path.Combine(dst, newName + ".json");
                if (File.Exists(srcJson) && !File.Exists(dstJson))
                {
                    var patched = WithJsonId(JsonDocument.Parse(File.ReadAllText(srcJson)).RootElement.Clone(), newName);
                    using var fs = File.Create(dstJson);
                    JsonSerializer.Serialize(fs, patched, new JsonSerializerOptions { WriteIndented = true });
                    AppLog.Info($"[mcinstall] 已把 {fromId} 的核心 json 补入已有实例 {newName}");
                }
                string srcJar = Path.Combine(src, fromId + ".jar");
                string dstJar = Path.Combine(dst, newName + ".jar");
                if (File.Exists(srcJar) && !File.Exists(dstJar))
                    File.Copy(srcJar, dstJar, overwrite: false);
            }
            catch (Exception ex) { AppLog.Warn($"[mcinstall] 补入已有实例核心文件失败：{ex.Message}"); }
            return;
        }
        Directory.Move(src, dst);
        var oldJson = Path.Combine(dst, fromId + ".json");
        if (File.Exists(oldJson))
        {
            var patched = WithJsonId(JsonDocument.Parse(File.ReadAllText(oldJson)).RootElement.Clone(), newName);
            File.Delete(oldJson);
            using (var fs = File.Create(Path.Combine(dst, newName + ".json")))
                JsonSerializer.Serialize(fs, patched, new JsonSerializerOptions { WriteIndented = true });
        }
        var oldJar = Path.Combine(dst, fromId + ".jar");
        if (File.Exists(oldJar)) File.Move(oldJar, Path.Combine(dst, newName + ".jar"));
        AppLog.Info($"[mcinstall] {task.Title} 版本 {fromId} 已重命名为 {newName}");
    }

    private static JsonElement WithJsonId(JsonElement root, string id)
    {
        var outObj = new Dictionary<string, object?>();
        foreach (var p in root.EnumerateObject())
            outObj[p.Name] = p.Name == "id" ? JsonSerializer.SerializeToElement(id) : JsonSerializer.SerializeToElement(p.Value);
        outObj["id"] = JsonSerializer.SerializeToElement(id);
        return JsonSerializer.SerializeToElement(outObj)!;
    }

    private static void StartTask(McInstallTask task, Func<McInstallTask, CancellationToken, Task> run)
    {
        _tasks[task.TaskId] = task;
        Directory.CreateDirectory(task.GameDir);
        AppLog.Info($"[mcinstall] 开始任务 {task.Title} → {task.GameDir}");
        _ = Task.Run(async () =>
        {
            var ct = task.Cts.Token;
            try
            {
                await run(task, ct);
                task.Finished = true;
                task.Success = true;
                task.Raise(force: true);
                AppLog.Info($"[mcinstall] {task.Title} 安装完成");
                Completed?.Invoke(task.VersionId);
            }
            catch (OperationCanceledException)
            {

                task.Finished = true;
                task.Cancelled = true;
                task.Error = null;
                task.CleanupPartial();
                task.Raise(force: true);
                AppLog.Info($"[mcinstall] {task.Title} 用户取消，残留已清理");
                Cancelled?.Invoke(task.VersionId);
                ScheduleRemove(task);
            }
            catch (Exception ex)
            {

                task.Finished = true;
                task.Error = FriendlyError(ex);
                task.ErrorDetail = $"{task.Error}\n\n—— 详细错误信息 ——\n{ex}";
                task.CleanupPartial();
                task.Raise(force: true);
                AppLog.Warn($"[mcinstall] {task.Title} 失败：{ex.Message}");
                Failed?.Invoke(task.VersionId, task.Error);
            }
        });
    }

    private static void ScheduleRemove(McInstallTask task)
        => _ = Task.Delay(600).ContinueWith(_t => _tasks.TryRemove(task.TaskId, out _));

    internal static string FriendlyError(Exception ex)
    {
        if (ex is OperationCanceledException) return S.Get("dl.cancelled");
        string m = ex.Message ?? "";
        if (m.Contains("NameResolutionFailure", StringComparison.OrdinalIgnoreCase)
            || m.Contains("No such host", StringComparison.OrdinalIgnoreCase)
            || m.Contains("无法解析远程名称", StringComparison.Ordinal))
            return S.Get("dl.err.dns");
        if (m.Contains("timed out", StringComparison.OrdinalIgnoreCase) || m.Contains("超时时间已过", StringComparison.Ordinal))
            return S.Get("dl.err.timeout");
        if (ex is HttpRequestException) return S.Get("dl.err.net");
        if (ex is IOException) return S.Get("dl.err.io");
        return S.Get("dl.err.generic") + "：" + (m.Length > 120 ? m[..120] + "…" : m);
    }

    private static async Task VanillaCoreAsync(McInstallTask task, string versionId, string gameDir, CancellationToken ct)
    {

        task.SetStage("dl.stage.meta", versionId);
        var manifest = await FetchVersionListAsync();
        var entry = manifest.FirstOrDefault(v => v.Id == versionId)
            ?? throw new InvalidOperationException($"清单里没有 {versionId}");
        ct.ThrowIfCancellationRequested();

        string instDir = Path.Combine(gameDir, "versions", versionId);
        task.EnsureVersionDir(instDir);
        string jsonPath = Path.Combine(instDir, versionId + ".json");
        JsonElement root;
        if (File.Exists(jsonPath))
        {
            root = JsonDocument.Parse(File.ReadAllText(jsonPath)).RootElement.Clone();
        }
        else
        {
            root = await DownloadJsonAsync(new[] { entry.Url }, ct);
            await using (var fs = File.Create(jsonPath))
                await JsonSerializer.SerializeAsync(fs, root, cancellationToken: ct);
        }

        string? aiPath = await DownloadJsonJobsAsync(task, root, gameDir, instDir, ct);

        await AssetsPhaseAsync(task, aiPath, ct);
    }

    private static async Task<string?> DownloadJsonJobsAsync(McInstallTask task, JsonElement root, string gameDir,
        string jarInstDir, CancellationToken ct)
    {
        var jobs = new List<(string Url, string Dest, long Size, string? Sha1, string File, string Stage)>();
        string libRoot = Path.Combine(gameDir, "libraries");
        string assetRoot = Path.Combine(gameDir, "assets");

        if (root.TryGetProperty("downloads", out var dls) && dls.TryGetProperty("client", out var clientEl))
        {
            string url = clientEl.GetProperty("url").GetString() ?? "";
            long size = clientEl.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
            string? sha1 = clientEl.TryGetProperty("sha1", out var sh) ? sh.GetString() : null;
            if (url.Length > 0)
            {

                string jarName = new DirectoryInfo(jarInstDir).Name + ".jar";
                jobs.Add((url, Path.Combine(jarInstDir, jarName), size, sha1, jarName, "dl.stage.jar"));
            }
        }

        if (root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
        {
            foreach (var lib in libs.EnumerateArray())
            {
                if (!LauncherService.RulesAllow(lib)) continue;
                if (!lib.TryGetProperty("name", out var nameEl)) continue;
                var parts = nameEl.GetString()!.Split(':');
                if (parts.Length < 3) continue;
                string repoPath = $"{parts[0].Replace('.', '/')}/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}" +
                                  (parts.Length > 3 ? $"-{string.Join('-', parts.Skip(3))}" : "") + ".jar";
                string libUrlBase = lib.TryGetProperty("url", out var uEl) && !string.IsNullOrEmpty(uEl.GetString())
                    ? uEl.GetString()!.TrimEnd('/') + "/" : "https://libraries.minecraft.net/";

                if (lib.TryGetProperty("downloads", out var dl) && dl.TryGetProperty("artifact", out var art))
                {
                    string url = art.TryGetProperty("url", out var au) ? au.GetString() ?? "" : "";
                    if (url.Length == 0) url = libUrlBase + repoPath;
                    string path = art.TryGetProperty("path", out var p) && !string.IsNullOrEmpty(p.GetString())
                        ? p.GetString()! : repoPath;
                    long size = art.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                    string? sha1 = art.TryGetProperty("sha1", out var sh) ? sh.GetString() : null;
                    jobs.Add((url, Path.Combine(libRoot, path.Replace('/', Path.DirectorySeparatorChar)), size, sha1,
                        Path.GetFileName(path), "dl.stage.lib"));
                }
                else
                {
                    jobs.Add((libUrlBase + repoPath, Path.Combine(libRoot, repoPath.Replace('/', Path.DirectorySeparatorChar)),
                        0, null, Path.GetFileName(repoPath), "dl.stage.lib"));
                }

                if (parts.Length == 3 && lib.TryGetProperty("natives", out var natEl) &&
                    natEl.TryGetProperty("windows", out var winEl))
                {
                    string cls = winEl.GetString()!.Replace("${arch}", Environment.Is64BitProcess ? "64" : "32");
                    string clsPath = $"{parts[0].Replace('.', '/')}/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}-{cls}.jar";
                    if (dl.ValueKind == JsonValueKind.Object && dl.TryGetProperty("classifiers", out var clsEl) &&
                        clsEl.TryGetProperty(cls, out var cArt))
                    {
                        string url = cArt.TryGetProperty("url", out var cu) ? cu.GetString() ?? "" : "";
                        string path = cArt.TryGetProperty("path", out var cp) && !string.IsNullOrEmpty(cp.GetString())
                            ? cp.GetString()! : clsPath;
                        long size = cArt.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                        string? sha1 = cArt.TryGetProperty("sha1", out var sh) ? sh.GetString() : null;
                        if (url.Length > 0)
                            jobs.Add((url, Path.Combine(libRoot, path.Replace('/', Path.DirectorySeparatorChar)), size, sha1,
                                Path.GetFileName(path), "dl.stage.lib"));
                    }
                    else
                    {
                        jobs.Add((libUrlBase + clsPath, Path.Combine(libRoot, clsPath.Replace('/', Path.DirectorySeparatorChar)),
                            0, null, Path.GetFileName(clsPath), "dl.stage.lib"));
                    }
                }
            }
        }

        string? assetIndexPath = null;
        if (root.TryGetProperty("assetIndex", out var aiEl) && aiEl.ValueKind == JsonValueKind.Object)
        {
            string aiUrl = aiEl.GetProperty("url").GetString() ?? "";
            string aiId = aiEl.TryGetProperty("id", out var aiIdEl) ? aiIdEl.GetString() ?? "legacy" : "legacy";
            long aiSize = aiEl.TryGetProperty("size", out var asz) ? asz.GetInt64() : 0;
            string? aiSha1 = aiEl.TryGetProperty("sha1", out var ash) ? ash.GetString() : null;
            assetIndexPath = Path.Combine(assetRoot, "indexes", aiId + ".json");
            if (aiUrl.Length > 0) jobs.Add((aiUrl, assetIndexPath, aiSize, aiSha1, aiId + ".json", "dl.stage.assetsidx"));
        }

        foreach (var j in jobs) task.AddTotal(Math.Max(0, j.Size), j.Stage);
        task.SetStage("dl.stage.jar", task.CurrentFile.Length > 0 ? task.CurrentFile : task.VersionId);
        AppLog.Info($"[mcinstall] {task.Title} 共 {jobs.Count} 个文件（累计 {task.TotalBytes / 1024.0 / 1024:F1} MB）");

        using var gate = new SemaphoreSlim(10);
        var tasks = jobs.Select(async j =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (ct.IsCancellationRequested) return;
                if (TryBorrowLocal(task, j.Stage, j.Dest, j.Size, j.Sha1)) return;
                task.SetStage(j.Stage, j.File);
                await DownloadFileAsync(task, j.Stage, new[] { LauncherService.MirrorOf(j.Url), j.Url }.Distinct().ToArray(),
                    j.Dest, j.Size, j.Sha1, j.File, ct);
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        if (task.Borrowed > 0) AppLog.Info($"[mcinstall] {task.Title} jar/库阶段借用本地 {task.Borrowed} 个文件，免下载");
        return assetIndexPath;
    }

    private static List<string> _borrowRoots = [];
    private static DateTime _borrowRootsAt = DateTime.MinValue;
    private static readonly object _borrowLock = new();

    private static List<string> BorrowRoots()
    {
        lock (_borrowLock)
        {
            if (DateTime.Now - _borrowRootsAt > TimeSpan.FromMinutes(5))
            {
                try { _borrowRoots = LauncherService.FindGameDirs(); }
                catch { _borrowRoots = []; }
                _borrowRootsAt = DateTime.Now;
            }
            return _borrowRoots;
        }
    }

    private static bool SameDir(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool TryBorrowLocal(McInstallTask task, string stageKey, string dest, long expectedSize, string? sha1)
    {
        string rel;
        try { rel = Path.GetRelativePath(task.GameDir, dest); } catch { return false; }
        foreach (var root in BorrowRoots())
        {
            if (SameDir(root, task.GameDir)) continue;
            string cand;
            try { cand = Path.Combine(root, rel); } catch { continue; }
            if (!File.Exists(cand)) continue;
            long len;
            try { len = new FileInfo(cand).Length; } catch { continue; }
            if (expectedSize > 0 && len != expectedSize) continue;
            string tmp = dest + ".part";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(cand, tmp, overwrite: true);
                VerifyAndMove(tmp, dest, expectedSize, sha1);
            }
            catch { TryDelete(tmp); continue; }
            Interlocked.Increment(ref task.Borrowed);
            task.AddDone(Math.Max(0, expectedSize), stageKey);
            return true;
        }
        return false;
    }

    private static async Task AssetsPhaseAsync(McInstallTask task, string? assetIndexPath, CancellationToken ct)
    {
        if (assetIndexPath is null || !File.Exists(assetIndexPath)) return;

        task.SetStage("dl.stage.assets", "");

        string assetRoot = Path.GetDirectoryName(Path.GetDirectoryName(assetIndexPath))!;
        using var idxDoc = JsonDocument.Parse(File.ReadAllText(assetIndexPath));
        var objects = idxDoc.RootElement.GetProperty("objects");
        var assetJobs = new List<(string Url, string Dest, long Size, string Hash)>();
        foreach (var o in objects.EnumerateObject())
        {
            if (o.Value.ValueKind != JsonValueKind.Object || !o.Value.TryGetProperty("hash", out var hEl)) continue;
            string hash = hEl.GetString() ?? "";
            if (hash.Length < 2) continue;
            long size = o.Value.TryGetProperty("size", out var sEl) ? sEl.GetInt64() : 0;
            string dest = Path.Combine(assetRoot, "objects", hash[..2], hash);
            assetJobs.Add(($"/{hash[..2]}/{hash}", dest, size, hash));
        }
        var mirrorBase = "https://bmclapi2.bangbang93.com/assets";
        var officialBase = "https://resources.download.minecraft.net";

        string[] AssetBases(string path) => LauncherService.UseOfficialSource
            ? new[] { officialBase + path, mirrorBase + path }
            : new[] { mirrorBase + path, officialBase + path };

        var pend = new List<(string Url, string Dest, long Size, string Hash)>();
        foreach (var a in assetJobs)
        {
            var fi = new FileInfo(a.Dest);
            if (fi.Exists && (a.Size <= 0 || fi.Length == a.Size)) { task.AddTotal(Math.Max(0, a.Size), "dl.stage.assets"); task.AddDone(Math.Max(0, a.Size), "dl.stage.assets"); }
            else { task.AddTotal(Math.Max(0, a.Size), "dl.stage.assets"); pend.Add(a); }
        }
        AppLog.Info($"[mcinstall] {task.Title} assets 待下 {pend.Count} 个");

        using var gate = new SemaphoreSlim(24);
        var borrowedBefore = task.Borrowed;
        var tasks = pend.Select(async a =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (TryBorrowLocal(task, "dl.stage.assets", a.Dest, a.Size, a.Hash)) return;
                await DownloadFileAsync(task, "dl.stage.assets", AssetBases(a.Url),
                    a.Dest, a.Size, null, "", ct);
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        if (task.Borrowed > borrowedBefore)
            AppLog.Info($"[mcinstall] {task.Title} assets 借用本地 {task.Borrowed - borrowedBefore}/{assetJobs.Count} 个，实际下载 {pend.Count - (task.Borrowed - borrowedBefore)} 个");
    }

    private static JsonElement MergeForInstall(JsonElement child, string gameDir)
    {
        if (!child.TryGetProperty("inheritsFrom", out var p)) return child;
        string parentId = p.GetString() ?? "";
        string parentPath = Path.Combine(gameDir, "versions", parentId, parentId + ".json");
        if (!File.Exists(parentPath)) throw new InvalidOperationException($"父版本 {parentId} 未安装");
        var parent = JsonDocument.Parse(File.ReadAllText(parentPath)).RootElement.Clone();

        var merged = new Dictionary<string, JsonElement>();

        foreach (var key in new[] { "mainClass", "minecraftArguments", "downloads", "javaVersion", "arguments" })
            if (child.TryGetProperty(key, out var c)) merged[key] = c;
            else if (parent.TryGetProperty(key, out var pv)) merged[key] = pv;

        foreach (var key in new[] { "assetIndex", "assets", "logging", "mainClass", "minecraftArguments", "downloads", "javaVersion", "arguments" })
            if (!merged.ContainsKey(key) && parent.TryGetProperty(key, out var pv2)) merged[key] = pv2;

        var libArr = new List<JsonElement>();
        if (child.TryGetProperty("libraries", out var cl) && cl.ValueKind == JsonValueKind.Array)
            foreach (var l in cl.EnumerateArray()) libArr.Add(l.Clone());
        if (parent.TryGetProperty("libraries", out var pl) && pl.ValueKind == JsonValueKind.Array)
            foreach (var l in pl.EnumerateArray()) libArr.Add(l.Clone());
        var outObj = new Dictionary<string, object?>();
        foreach (var kv in merged) outObj[kv.Key] = JsonSerializer.SerializeToElement(kv.Value);
        outObj["libraries"] = JsonSerializer.SerializeToElement(libArr);
        outObj["id"] = JsonSerializer.SerializeToElement(
            child.TryGetProperty("id", out var cid) ? cid.GetString() ?? "" : "");
        return JsonSerializer.SerializeToElement(outObj)!;
    }

    public static async Task<List<McLoaderEntry>> FetchForgeVersionsAsync(string mcVersion, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"https://bmclapi2.bangbang93.com/forge/minecraft/{mcVersion}", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var list = new List<McLoaderEntry>();
        foreach (var b in doc.RootElement.EnumerateArray())
        {
            string v = b.TryGetProperty("version", out var vEl) ? vEl.GetString() ?? "" : "";
            string mod = b.TryGetProperty("modified", out var mEl) ? mEl.GetString() ?? "" : "";
            if (v.Length > 0) list.Add(new McLoaderEntry(v, mod.Length >= 10 ? mod[..10] : mod, true));
        }
        return list;
    }

    public static async Task<List<McLoaderEntry>> FetchNeoForgeVersionsAsync(string mcVersion, CancellationToken ct = default)
    {
        var prefixes = new List<string> { mcVersion + "-", mcVersion + "." };
        if (mcVersion.StartsWith("1.") && mcVersion.Count(c => c == '.') >= 2)
            prefixes.Add(mcVersion[2..] + ".");
        var xml = await FetchXmlWithCandidatesAsync(OrderBySource(
            "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/maven-metadata.xml",
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml"), ct);
        var list = new List<McLoaderEntry>();
        foreach (var v in xml.Descendants("version").Select(x => x.Value).Where(v => prefixes.Any(v.StartsWith)))
            list.Add(new McLoaderEntry(v, "", !v.Contains("beta") && !v.Contains("alpha")));
        return list;
    }

    private static async Task<XDocument> FetchXmlWithCandidatesAsync(IEnumerable<string> urls, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                using var resp = await Http.GetAsync(url, ct);
                resp.EnsureSuccessStatusCode();
                return await XDocument.LoadAsync(await resp.Content.ReadAsStreamAsync(ct), LoadOptions.None, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new InvalidOperationException("maven-metadata 全部候选源失败");
    }

    public static async Task<List<McLoaderEntry>> FetchFabricVersionsAsync(string mcVersion, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var list = new List<McLoaderEntry>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (!e.TryGetProperty("loader", out var ld)) continue;
            string v = ld.TryGetProperty("version", out var vEl) ? vEl.GetString() ?? "" : "";
            bool stable = ld.TryGetProperty("stable", out var sEl) && sEl.ValueKind == JsonValueKind.True;
            if (v.Length > 0) list.Add(new McLoaderEntry(v, "", stable));
        }
        return list.OrderByDescending(e => e.Stable).ToList();
    }

    public static async Task<List<McLoaderEntry>> FetchOptiFineVersionsAsync(string mcVersion, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"https://bmclapi2.bangbang93.com/optifine/{mcVersion}", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var list = new List<McLoaderEntry>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string file = e.TryGetProperty("filename", out var f) ? f.GetString() ?? "" : "";
            if (file.Length == 0) continue;
            bool preview = file.StartsWith("preview_");
            string type = e.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
            string patch = e.TryGetProperty("patch", out var pa) ? pa.GetString() ?? "" : "";
            string display = (type + " " + patch).Replace("_", " ").Trim();
            if (display.Length == 0) display = file;
            list.Add(new McLoaderEntry(display, "", !preview, file));
        }
        return list.OrderByDescending(e => e.Stable).ToList();
    }

    public sealed record McFabricApiEntry(string Version, string Date, bool Stable, string FileName, string[] Urls);

    public static async Task<List<McFabricApiEntry>> FetchFabricApiListAsync(string mcVersion, CancellationToken ct = default)
    {
        string api = $"https://api.modrinth.com/v2/project/fabric-api/version" +
                     $"?game_versions=%5B%22{Uri.EscapeDataString(mcVersion)}%22%5D&loaders=%5B%22fabric%22%5D";
        string mirror = api.Replace("api.modrinth.com", "mod.mcimirror.top/modrinth");
        Exception? last = null;
        foreach (var url in OrderBySource(mirror, api))
        {
            try
            {
                using var resp = await Http.GetAsync(url, ct);
                resp.EnsureSuccessStatusCode();
                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var list = new List<McFabricApiEntry>();
                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    string ver = entry.TryGetProperty("version_number", out var vEl) ? vEl.GetString() ?? "" : "";
                    if (ver.Length == 0) continue;
                    string type = entry.TryGetProperty("version_type", out var tEl) ? tEl.GetString() ?? "" : "";
                    string date = entry.TryGetProperty("date_published", out var dEl) ? dEl.GetString() ?? "" : "";
                    if (entry.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var f in files.EnumerateArray())
                        {
                            bool primary = f.TryGetProperty("primary", out var pEl) && pEl.ValueKind == JsonValueKind.True;
                            if (!primary) continue;
                            string furl = f.TryGetProperty("url", out var uEl) ? uEl.GetString() ?? "" : "";
                            string name = f.TryGetProperty("filename", out var nEl) ? nEl.GetString() ?? "" : "";
                            if (furl.Length == 0 || name.Length == 0) continue;

                            string dlMirror = furl.Replace("cdn.modrinth.com", "mod.mcimirror.top")
                                                  .Replace("cdn.modrinth.net", "mod.mcimirror.top");
                            list.Add(new McFabricApiEntry(ver,
                                date.Length >= 10 ? date[..10] : date,
                                type != "beta" && type != "alpha",
                                name, new[] { dlMirror, furl }));
                            break;
                        }
                    }
                }
                return list;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new InvalidOperationException("Fabric API 列表全部候选源失败");
    }

    private static string? FindJavaForInstaller(string gameDir, string mcVersion)
    {
        int major = 8;
        try
        {
            string p = Path.Combine(gameDir, "versions", mcVersion, mcVersion + ".json");
            if (File.Exists(p))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(p));
                if (doc.RootElement.TryGetProperty("javaVersion", out var jv) &&
                    jv.TryGetProperty("majorVersion", out var mv) && mv.ValueKind == JsonValueKind.Number)
                    major = mv.GetInt32();
            }
        }
        catch { }
        return LauncherService.FindJava(major) ?? LauncherService.FindJava(8);
    }

    private static void EnsureLauncherProfile(string gameDir)
    {
        string path = Path.Combine(gameDir, "launcher_profiles.json");
        if (File.Exists(path)) return;
        try
        {
            Directory.CreateDirectory(gameDir);
            File.WriteAllText(path,
                "{\"profiles\":{},\"selectedProfile\":\"\",\"clientToken\":\"starry-launcher-local\"}",
                new System.Text.UTF8Encoding(false));
            AppLog.Info($"[mcinstall] 已补建 launcher_profiles.json：{path}（Forge 安装器硬性要求）");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[mcinstall] 补建 launcher_profiles.json 失败：{ex.Message}");
        }
    }

    private static async Task RunInstallerAsync(McInstallTask task, string javaExe, string installerJar, string gameDir, CancellationToken ct)
    {
        EnsureLauncherProfile(gameDir);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = javaExe,
            Arguments = $"-jar \"{installerJar}\" --installClient \"{gameDir}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        AppLog.Info($"[mcinstall] 运行加载器安装器：{psi.FileName} {psi.Arguments}");
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("安装器进程启动失败");
        void Kill() { try { if (!proc.HasExited) proc.Kill(true); } catch { } }
        using var reg = ct.Register(() => Kill());
        var so = proc.StandardOutput.ReadToEndAsync(ct);
        var se = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        string stdout = (await so).Trim();
        string stderr = (await se).Trim();
        if (stdout.Length > 0)
        {
            var tail = string.Join(" | ", stdout.Split('\n').TakeLast(8)).Trim();
            AppLog.Info($"[mcinstall][installer] {tail[..Math.Min(400, tail.Length)]}");
        }
        if (stderr.Length > 0) AppLog.Warn($"[mcinstall][installer-err] {stderr[..Math.Min(400, stderr.Length)]}");
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(S.Get("dl.installerFail") + $"（exit {proc.ExitCode}）");
        try { File.Delete(installerJar); } catch { }
    }

    private static readonly Regex NumericId = new(@"^\d+(\.\d+)*$", RegexOptions.Compiled);
    private static readonly string[] AprilFoolsReleases = { "3D Shareware v1.34", "1.RV-Pre1", "2.0" };

    public static bool IsAprilFools(McVersionEntry v)
        => (v.Type != "release" && v.ReleaseTime.Length >= 10 && v.ReleaseTime.Substring(5, 5) == "04-01")
           || AprilFoolsReleases.Contains(v.Id);

    public static bool IsLegacy(McVersionEntry v)
        => v.Type is "old_beta" or "old_alpha"
           || (v.Type == "snapshot" && NumericId.IsMatch(v.Id));

    private static List<McVersionEntry>? _cachedManifest;
    private static DateTime _manifestAt;
    private static readonly SemaphoreSlim _manifestLock = new(1, 1);

    private const int SslStreakThreshold = 10;
    private static readonly TimeSpan BmclSickWindow = TimeSpan.FromSeconds(60);
    private static int _sslStreak;
    private static DateTime _bmclSickUntil = DateTime.MinValue;
    private static readonly object _srcLock = new();
    private static bool IsMirrorSick { get { lock (_srcLock) return DateTime.Now < _bmclSickUntil; } }

    private static void NoteSslFailure(Exception ex, string url)
    {
        if (!ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase)) return;

        if (!url.Contains("bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)) return;
        lock (_srcLock)
        {
            _sslStreak++;
            if (_sslStreak >= SslStreakThreshold && DateTime.Now >= _bmclSickUntil)
            {
                _bmclSickUntil = DateTime.Now + BmclSickWindow;
                _sslStreak = 0;
                AppLog.Warn($"[mcinstall] 镜像源连续拒绝 TLS 握手（限流）→ {BmclSickWindow.TotalSeconds:F0}s 内切官方源兜底，窗口结束自动回镜像。最后失败：{url}");
            }
        }
    }

    private static bool IsMirrorUrl(string u) =>
        u.Contains("bmclapi", StringComparison.OrdinalIgnoreCase);

    public static void InvalidateManifestCache() { _cachedManifest = null; _manifestAt = default; }

    public static async Task<List<McVersionEntry>> FetchVersionListAsync()
    {
        if (_cachedManifest is { Count: > 0 } && (DateTime.Now - _manifestAt).TotalMinutes < 10)
            return _cachedManifest;
        await _manifestLock.WaitAsync();
        try
        {
            if (_cachedManifest is { Count: > 0 } && (DateTime.Now - _manifestAt).TotalMinutes < 10)
                return _cachedManifest;

            foreach (var baseUrl in LauncherService.UseOfficialSource
                ? new[]
                {
                    "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",
                    "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
                }
                : new[]
                {
                    "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
                    "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",
                })
            {
                try
                {
                    using var resp = await Http.GetAsync(baseUrl, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    await using var stream = await resp.Content.ReadAsStreamAsync();
                    using var doc = await JsonDocument.ParseAsync(stream);
                    var list = new List<McVersionEntry>();
                    foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
                    {
                        string id = v.GetProperty("id").GetString() ?? "";
                        if (id.Length == 0) continue;
                        string type = v.TryGetProperty("type", out var t) ? t.GetString() ?? "release" : "release";
                        string url = v.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                        string rt = v.TryGetProperty("releaseTime", out var r) ? r.GetString() ?? "" : "";
                        list.Add(new McVersionEntry(id, type, url, rt));
                    }
                    if (list.Count == 0) continue;
                    _cachedManifest = list;
                    _manifestAt = DateTime.Now;
                    return list;
                }
                catch { continue; }
            }
            throw new InvalidOperationException("版本清单拉取失败（官方与镜像都不通）");
        }
        finally { _manifestLock.Release(); }
    }

    private static async Task<JsonElement> DownloadJsonAsync(string[] candidates, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var u in candidates)
        {
            try
            {
                using var resp = await Http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                return doc.RootElement.Clone();
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; }
        }
        throw last ?? new InvalidOperationException("json download failed: " + string.Join(",", candidates));
    }

    private static async Task DownloadFileAsync(McInstallTask task, string stageKey, string[] candidates,
        string dest, long expectedSize, string? sha1, string displayFile, CancellationToken ct)
    {

        if (!LauncherService.UseOfficialSource && IsMirrorSick && candidates.Length > 1)
            Array.Reverse(candidates);
        var fi = new FileInfo(dest);
        if (fi.Exists && (expectedSize <= 0 || fi.Length == expectedSize))
        {
            task.AddDone(Math.Max(0, fi.Length), stageKey);
            return;
        }
        if (displayFile.Length > 0) task.SetFile(displayFile);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        string tmp = dest + ".part";
        task.TrackTmp(tmp);
        Exception? last = null;

        long[] backoffs = expectedSize > 0 && expectedSize <= 1024 * 1024
            ? new long[] { 300, 800 }
            : new long[] { 400, 1600, 6400 };
        foreach (var u in candidates)
        {
            for (int attempt = 0; attempt <= backoffs.Length; attempt++)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (expectedSize > 8L * 1024 * 1024 && await TryChunkedAsync(task, stageKey, u, tmp, expectedSize, ct))
                    {
                        VerifyAndMove(tmp, dest, expectedSize, sha1);
                        task.UntrackTmp(tmp);
                        if (IsMirrorUrl(u)) Interlocked.Exchange(ref _sslStreak, 0);
                        return;
                    }
                    using (var resp = await Http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        resp.EnsureSuccessStatusCode();
                        await using var src = await resp.Content.ReadAsStreamAsync(ct);
                        await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read);
                        var buf = new byte[256 << 10];
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        int n;
                        long got = 0;

                        while ((n = await ReadIdleTimeoutAsync(src, buf, ct, idle)) > 0)
                        {
                            await dst.WriteAsync(buf.AsMemory(0, n), ct);
                            got += n;
                            task.AddDone(n, stageKey);
                        }
                        if (expectedSize > 0 && got != expectedSize)
                            throw new IOException($"size mismatch {got}/{expectedSize}");
                    }
                    VerifyAndMove(tmp, dest, expectedSize, sha1);
                    task.UntrackTmp(tmp);
                    if (IsMirrorUrl(u)) Interlocked.Exchange(ref _sslStreak, 0);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryDelete(tmp);
                    throw;
                }
                catch (Exception ex)
                {

                    TryDelete(tmp);

                    AppLog.Warn($"[mcinstall] {Path.GetFileName(dest)} ← {u} 失败：{ex.Message}");
                    NoteSslFailure(ex, u);
                    last = ex;

                    if (ex is HttpRequestException hre &&
                        (hre.StatusCode == System.Net.HttpStatusCode.NotFound ||
                         hre.StatusCode == System.Net.HttpStatusCode.Forbidden))
                        break;

                    if (ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase))
                        break;
                    if (attempt < backoffs.Length && !ct.IsCancellationRequested)
                    {

                        int delay = (int)backoffs[attempt];
                        if (ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase))
                            delay = delay * 3 + Random.Shared.Next(400);
                        await Task.Delay(delay, CancellationToken.None);
                        continue;
                    }
                    break;
                }
            }
        }
        task.UntrackTmp(tmp);
        throw WrapDest(last, dest, candidates);
    }

    private static async Task<string> DownloadWithCandidatesAsync(McInstallTask task, string stageKey,
        string[] urls, string dest, CancellationToken ct, long knownSize = 0)
    {

        long size = knownSize;
        if (size <= 0) foreach (var u in urls)
        {
            try
            {
                using var h = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, u), ct);
                if (h.IsSuccessStatusCode && h.Content.Headers.ContentLength is long len) { size = len; break; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) {   }
        }
        task.AddTotal(size, stageKey);
        string display = Path.GetFileName(dest);
        var fi = new FileInfo(dest);
        if (fi.Exists && size > 0 && fi.Length == size) { task.AddDone(size, stageKey); return dest; }

        Exception? last = null;
        var backoffs = new[] { 500, 1500 };
        if (!LauncherService.UseOfficialSource && IsMirrorSick && urls.Length > 1)
            Array.Reverse(urls);
        foreach (var u in urls)
        {
            for (var attempt = 0; attempt < backoffs.Length + 1; attempt++)
            {
                try
                {
                    await DownloadCoreAsync(task, stageKey, u, dest, size, null, display, ct);
                    if (IsMirrorUrl(u)) Interlocked.Exchange(ref _sslStreak, 0);
                    return dest;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    NoteSslFailure(ex, u);
                    AppLog.Warn($"[mcinstall] {display} ← {u} 失败（第 {attempt + 1} 次）：{ex.Message}");

                    if (ex is HttpRequestException hre2 &&
                        (hre2.StatusCode == System.Net.HttpStatusCode.NotFound ||
                         hre2.StatusCode == System.Net.HttpStatusCode.Forbidden))
                        break;

                    if (ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase))
                        break;
                    if (attempt < backoffs.Length && !ct.IsCancellationRequested)
                        await Task.Delay(backoffs[attempt], CancellationToken.None);
                }
            }
        }
        throw last ?? new InvalidOperationException("installer download failed");
    }

    private static async Task DownloadCoreAsync(McInstallTask task, string stageKey, string url,
        string dest, long expectedSize, string? sha1, string displayFile, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        string tmp = dest + ".part";

        var reuse = false;
        for (var c = 1; c <= 4 && !reuse; c++)
        {
            var cand = c == 1 ? dest + ".part" : dest + $".part{c}";
            if (expectedSize > 0 && File.Exists(cand) && new FileInfo(cand).Length == expectedSize)
            {
                tmp = cand;
                reuse = true;
            }
        }

        long got = 0;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (reuse)
            {
                got = expectedSize;
                task.AddDone(got, stageKey);
                AppLog.Info($"[mcinstall] 复用上次已下载完整的 {Path.GetFileName(tmp)}（{got / 1048576.0:F1}MB，免重下）");
            }
            else
            {

                for (var k = 1; ; k++)
                {
                    try
                    {
                        using var probe = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read);
                        break;
                    }
                    catch (IOException ex) when (k < 4 && ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
                    {
                        AppLog.Warn($"[mcinstall] {Path.GetFileName(tmp)} 被其他进程占用（多为杀软云查杀），换临时名继续");
                        tmp = dest + $".part{k + 1}";
                    }
                }
                task.TrackTmp(tmp);
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read);
                var buf = new byte[1 << 16];
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                int n;
                while ((n = await ReadIdleTimeoutAsync(src, buf, ct, idle)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    got += n;
                    task.AddDone(n, stageKey);
                }

            }
            if (expectedSize > 0 && got != expectedSize) throw new IOException($"size mismatch {got}/{expectedSize}");
            MoveWithAvRetry(tmp, dest);
            task.UntrackTmp(tmp);
        }
        catch (OperationCanceledException) { if (got != expectedSize) TryDelete(tmp); task.UntrackTmp(tmp); if (got > 0) task.AddDone(-got, stageKey); throw; }
        catch
        {

            if (got != expectedSize) TryDelete(tmp);
            task.UntrackTmp(tmp);
            if (got > 0) task.AddDone(-got, stageKey);
            throw;
        }
    }

    private static async Task<int> ReadIdleTimeoutAsync(Stream src, byte[] buf, CancellationToken ct, CancellationTokenSource idle)
    {
        idle.CancelAfter(20_000);
        try { return await src.ReadAsync(new Memory<byte>(buf), idle.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new IOException("read idle timeout (20s no data)"); }
    }

    private static async Task<bool> TryChunkedAsync(McInstallTask task, string stageKey, string url, string tmp, long total, CancellationToken ct)
    {
        try
        {

            using (var probe = new HttpRequestMessage(HttpMethod.Get, url))
            {
                probe.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
                using var resp = await Http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)resp.StatusCode != 206) return false;
                var cr = resp.Content.Headers.ContentRange;
                if (cr?.Length is not long len || len != total) return false;
            }

            int chunks = total > 64L * 1024 * 1024 ? 16 : 8;
            long per = total / chunks;
            var bounds = new (long From, long To)[chunks];
            for (int i = 0; i < chunks; i++)
                bounds[i] = (i * per, i == chunks - 1 ? total - 1 : (i + 1) * per - 1);

            var partPaths = Enumerable.Range(0, chunks).Select(i => $"{tmp}.c{i}").ToArray();

            foreach (var p in partPaths) task.TrackTmp(p);
            try
            {
            using var gate = new SemaphoreSlim(chunks);
            var tasks = Enumerable.Range(0, chunks).Select(async i =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(bounds[i].From, bounds[i].To);
                        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                        resp.EnsureSuccessStatusCode();
                        if ((int)resp.StatusCode != 206) throw new IOException("chunk not 206");
                        await using var src = await resp.Content.ReadAsStreamAsync(ct);
                        await using var dst = new FileStream(partPaths[i], FileMode.Create, FileAccess.Write, FileShare.Read);
                        var buf = new byte[256 << 10];
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        int n;

                        while ((n = await ReadIdleTimeoutAsync(src, buf, ct, idle)) > 0)
                        {
                            await dst.WriteAsync(buf.AsMemory(0, n), ct);
                            task.AddDone(n, stageKey);
                        }
                    }
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);

            {
                await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read);
                foreach (var p in partPaths)
                {
                    await using var src = File.OpenRead(p);
                    await src.CopyToAsync(dst, ct);
                }
            }
            foreach (var p in partPaths) TryDelete(p);
            foreach (var p in partPaths) task.UntrackTmp(p);
            return true;
            }
            catch (OperationCanceledException)
            {

                foreach (var p in partPaths) TryDelete(p);
                TryDelete(tmp);
                throw;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            int chunks = total > 64L * 1024 * 1024 ? 16 : 8;
            for (int i = 0; i < chunks; i++) TryDelete($"{tmp}.c{i}");

            return false;
        }
    }

    private static void VerifyAndMove(string tmp, string dest, long expectedSize, string? sha1)
    {
        if (expectedSize > 0 && new FileInfo(tmp).Length != expectedSize)
        {
            TryDelete(tmp);
            throw new IOException("size mismatch after download");
        }
        if (!string.IsNullOrEmpty(sha1))
        {
            using var fs = File.OpenRead(tmp);
            using var sha = SHA1.Create();
            string hex = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
            if (hex != sha1) { TryDelete(tmp); throw new IOException("sha1 mismatch"); }
        }
        MoveWithAvRetry(tmp, dest);
    }

    private static void MoveWithAvRetry(string tmp, string dest)
    {
        for (var mv = 1; ; mv++)
        {
            try { File.Move(tmp, dest, overwrite: true); return; }
            catch (IOException ex) when (mv <= 4 && ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn($"[mcinstall] {Path.GetFileName(dest)} 被杀软关闭时扫描锁住，{(mv * 1.2):F1}s 后重试替换（第 {mv} 次）");
                Thread.Sleep(1200 * mv);
                TryDelete(dest);
            }
        }
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static Exception WrapDest(Exception? last, string dest, string[] candidates)
        => last is null
            ? new InvalidOperationException($"下载失败：{dest}（候选源：{string.Join(" , ", candidates)}）")
            : new InvalidOperationException($"文件 {Path.GetFileName(dest)} 下载失败：{last.Message}（尝试过的源：{string.Join(" , ", candidates)}）", last);
}
