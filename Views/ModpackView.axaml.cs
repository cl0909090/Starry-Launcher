using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace StarryLauncher.Views;

public partial class ModpackView : UserControl
{
    private readonly ModpackService _svc = ModpackService.Instance;
    private int _rendered;

    public ModpackView()
    {
        InitializeComponent();
        BuildTabs();
        BuildFilterBar();

        SearchIcon.Source = SvgIcon.Get("ss");
        SearchBox.TextChanged += OnSearchTextChanged;

        HookServiceEvents(true);

        Loaded += async (_, _) =>
        {

            HookServiceEvents(true);
            if (!_initialTabDone)
            {

                _initialTabDone = true;
                _inVanilla = true;
                MoveTabIndicator(FindTab("vanilla"));
                EnterVanillaUi();
                await LoadVanillaVersionsAsync();
                return;
            }
            MoveTabIndicator(FindTab(_inVanilla ? "vanilla" : _svc.ProjectType));
            if (_inVanilla) return;
            await _svc.LoadFirstPageAsync();
        };
        Unloaded += (_, _) =>
        {
            HookServiceEvents(false);
            MainWindow.ThemePaletteChanged -= OnThemePaletteChanged;
        };
        MainWindow.ThemePaletteChanged += OnThemePaletteChanged;
    }

    private void BuildTabs()
    {
        TabButtons.Children.Clear();
        var first = true;
        foreach (var (type, key, _) in ModpackService.ProjectTypes)
        {

            if (!first && type == ModpackService.ProjectTypes[1].Type) TabButtons.Children.Add(new Border
            {
                Height = 1, Margin = new Thickness(12, 6), Opacity = 0.3,
                Background = GetRes("OnSurfaceVarBrush"),
            });
            var b = new Button
            {
                Classes = { "tabPill" },
                Tag = type,
                Content = S.Get("moddl.tab." + key),
            };
            b.Click += OnTabClick;
            TabButtons.Children.Add(b);
            first = false;
        }
    }

    private Button? FindTab(string type)
        => TabButtons.Children.OfType<Button>().FirstOrDefault(b => (string?)b.Tag == type);

    private bool _suppressFilter;
    private DispatcherTimer? _versionTextTimer;

    private sealed record PageFilter(string? Ver, string? Cat, string? Query, string? Loader = null);

    private readonly Dictionary<string, PageFilter> _pageFilters = new();

    private string FilterKey => _svc.Source + "|" + _svc.ProjectType;

    private string? _sourceBeforeMap;

    private bool _inVanilla;
    private bool _initialTabDone;
    private List<McVersionEntry> _vanillaVersions = [];

    private string? _vanillaDetail;
    private (string Kind, string Mc)? _loaderPick;
    private List<McLoaderEntry> _loaderVersions = [];
    private bool _loaderLoading;

    private readonly Dictionary<string, Dictionary<string, (bool Done, List<McLoaderEntry>? List)>> _loaderProbe = new();

    private bool _selVanilla = true;
    private (string Kind, string Version, string Tag)? _loaderSel;

    private string _renameText = "";
    private string _renameDefault = "";
    private bool _renameTouched;

    private bool _fabricApiOn = true;
    private string? _fabricApiSelVer;
    private bool _fabricApiExpand;
    private readonly Dictionary<string, List<McInstaller.McFabricApiEntry>> _fabricApiCache = new();
    private readonly HashSet<string> _fabricApiListLoading = [];

    private static string LoaderName(string kind)
        => kind switch { "forge" => "Forge", "neoforge" => "NeoForge", "optifine" => "OptiFine", _ => "Fabric" };

    private void SyncSourceCombo(string source)
    {
        _suppressFilter = true;
        SourceCombo.SelectedIndex = source switch
        {
            ModpackService.SourceCurseForge => 2,
            ModpackService.SourceAll => 0,
            _ => 1,
        };
        _suppressFilter = false;
    }

    private void BuildFilterBar()
    {

        SourceCombo.ItemsSource = new List<object>
        {
            new ComboBoxItem { Content = S.Get("modpack.f.allSrc"), Tag = ModpackService.SourceAll },
            new ComboBoxItem { Content = "Modrinth", Tag = ModpackService.SourceModrinth },
            new ComboBoxItem { Content = "CurseForge", Tag = ModpackService.SourceCurseForge },
        };
        SourceCombo.SelectedIndex = _svc.Source switch
        {
            ModpackService.SourceCurseForge => 2,
            ModpackService.SourceAll => 0,
            _ => 1,
        };
        SourceCombo.SelectionChanged += OnSourceChanged;

        VersionCombo.SelectionChanged += OnVersionSelectionChanged;

        VersionCombo.PropertyChanged += (_, e) =>
        {
            if (e.Property == ComboBox.TextProperty) ScheduleVersionCommit();
        };
        LoadVersionComboAsync();

        TypeCombo.SelectionChanged += OnTypeChanged;
        _ = BuildTypeComboAsync();

        LoaderCombo.ItemsSource = new List<object>
        {
            new ComboBoxItem { Content = S.Get("modpack.f.allType"), Tag = null },
            new ComboBoxItem { Content = "Forge", Tag = "forge" },
            new ComboBoxItem { Content = "NeoForge", Tag = "neoforge" },
            new ComboBoxItem { Content = "Fabric", Tag = "fabric" },
            new ComboBoxItem { Content = "LiteLoader", Tag = "liteloader" },
        };
        LoaderCombo.SelectionChanged += OnLoaderChanged;
        RestoreLoaderCombo(_svc.Loader);
        UpdateLoaderVisibility();

        VanillaCatCombo.ItemsSource = new List<object>
        {
            new ComboBoxItem { Content = S.Get("dl.cat.all"), Tag = null },
            new ComboBoxItem { Content = S.Get("dl.cat.rel"), Tag = "release" },
            new ComboBoxItem { Content = S.Get("dl.cat.snap"), Tag = "snapshot" },
            new ComboBoxItem { Content = S.Get("dl.cat.old"), Tag = "old" },
            new ComboBoxItem { Content = S.Get("dl.cat.af"), Tag = "af" },
        };
        VanillaCatCombo.SelectedIndex = 0;
        VanillaCatCombo.SelectionChanged += (_, _) => { if (_inVanilla) RebuildVanillaList(); };

        DlSrcCombo.ItemsSource = new List<object>
        {
            new ComboBoxItem { Content = S.Get("dl.src.bmclapi"), Tag = "bmclapi" },
            new ComboBoxItem { Content = S.Get("dl.src.official"), Tag = "official" },
        };
        DlSrcCombo.SelectedIndex = AppSettings.Shared.McDownloadSource == "official" ? 1 : 0;
        DlSrcCombo.SelectionChanged += (_, _) =>
        {
            if (DlSrcCombo.SelectedItem is not ComboBoxItem ci || ci.Tag is not string src) return;
            var st = AppSettings.Shared;
            if (st.McDownloadSource == src) return;
            st.McDownloadSource = src;
            st.Save();
            McInstaller.InvalidateManifestCache();
            AppLog.Info($"[mcinstall] 下载源切换为 {src}");
        };

        _suppressSearch = true;
        SearchBox.Text = _svc.SearchQuery ?? "";
        _suppressSearch = false;

        UpdateSearchPlaceholder();
        RestoreVersionCombo(_svc.GameVersion);
    }

    private void UpdateSearchPlaceholder(string? type = null)
    {
        var t = type ?? _svc.ProjectType;
        var hit = Array.Find(ModpackService.ProjectTypes, x => x.Type == t);
        SearchBox.PlaceholderText = S.Get("modpack.searchPh." + hit.Key);
    }

    private void RestoreVersionCombo(string? ver)
    {
        _suppressFilter = true;
        var items = VersionCombo.ItemsSource as List<object>;
        var idx = ver is null
            ? (items is { Count: > 0 } ? 0 : -1)
            : items?.FindIndex(x => x is ComboBoxItem ci && (ci.Tag as string) == ver) ?? -1;
        if (idx >= 0) VersionCombo.SelectedIndex = idx;
        else
        {
            VersionCombo.SelectedIndex = -1;
            VersionCombo.Text = ver ?? "";
        }
        _suppressFilter = false;
    }

    private async void OnLoaderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilter) return;
        var ld = LoaderCombo.SelectedItem is ComboBoxItem it ? it.Tag as string : null;
        CardList.Children.Clear();
        _rendered = 0;
        try { await _svc.SetLoaderAsync(ld); } catch {   }
    }

    private void RestoreLoaderCombo(string? ld)
    {
        _suppressFilter = true;
        var items = LoaderCombo.ItemsSource as List<object>;
        var idx = ld is null ? 0 : items?.FindIndex(x => x is ComboBoxItem ci && (ci.Tag as string) == ld) ?? -1;
        LoaderCombo.SelectedIndex = idx < 0 ? 0 : idx;
        _suppressFilter = false;
    }

    private void UpdateLoaderVisibility(string? type = null)
    {
        var show = (type ?? _svc.ProjectType) == "mod";
        LoaderLabel.IsVisible = show;
        LoaderCombo.IsVisible = show;
    }

    private async void OnSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilter) return;

        if (_svc.ProjectType == "map") { SyncSourceCombo(ModpackService.SourceCurseForge); return; }
        if (SourceCombo.SelectedItem is not ComboBoxItem it || it.Tag is not string source) return;
        if (source == _svc.Source && !_svc.IsSearching) return;

        _pageFilters[FilterKey] = new PageFilter(_svc.GameVersion, _svc.Category, _svc.SearchQuery, _svc.Loader);

        var saved = _pageFilters.TryGetValue(source + "|" + _svc.ProjectType, out var f)
            ? f : new PageFilter(_svc.GameVersion, null, null);

        _suppressSearch = true;
        SearchBox.Text = saved.Query ?? "";
        _suppressSearch = false;
        RestoreVersionCombo(saved.Ver);
        RestoreLoaderCombo(saved.Loader);
        CardList.Children.Clear();

        var carried = await ResolveCategoryAcrossSources(source, _svc.Category);

        _rendered = 0;
        try { await _svc.SetSourcePageAsync(source, saved.Ver, carried, saved.Query, saved.Loader); } catch {   }
        _ = BuildTypeComboAsync();
    }

    private async Task<string?> ResolveCategoryAcrossSources(string targetSource, string? curCat)
    {
        if (string.IsNullOrWhiteSpace(curCat)) return null;
        var slash = curCat.IndexOf('/');
        var cfPart = slash >= 0 ? curCat[..slash] : "";
        var mrSlug = slash >= 0 ? curCat[(slash + 1)..] : curCat;
        var cfId = cfPart.Length > 0 && int.TryParse(cfPart, out _) ? cfPart : null;

        if (targetSource == ModpackService.SourceModrinth)
        {
            var cats = await ModpackService.GetModrinthCategoriesAsync(_svc.ProjectType);
            return mrSlug is { Length: > 0 } && cats.Contains(mrSlug) ? "/" + mrSlug : null;
        }
        if (targetSource == ModpackService.SourceCurseForge)
        {
            var cats = await ModpackService.GetCfCategoriesAsync(_svc.ProjectType);
            if (cfId is not null) return cfId + "/";
            if (mrSlug is { Length: > 0 })
            {

                var name = ModpackService.MatchCfCategoryName(mrSlug, cats);
                var id = name is null ? null : ModpackService.GetCfCategoryId(_svc.ProjectType, name);
                if (id is not null) return id + "/";
            }
            return null;
        }

        var mrCats = await ModpackService.GetModrinthCategoriesAsync(_svc.ProjectType);
        if (mrSlug is { Length: > 0 } && mrCats.Contains(mrSlug))
        {
            var cfCats = await ModpackService.GetCfCategoriesAsync(_svc.ProjectType);
            var name = ModpackService.MatchCfCategoryName(mrSlug, cfCats);
            var id = name is null ? null : ModpackService.GetCfCategoryId(_svc.ProjectType, name);
            return $"{(id is null ? "" : id)}/{mrSlug}";
        }
        return cfId is not null ? cfId + "/" : null;
    }

    private async void LoadVersionComboAsync()
    {

        var items = new List<object> { new ComboBoxItem { Content = S.Get("modpack.f.all"), Tag = null } };
        var versions = await ModpackService.GetMcReleaseVersionsAsync();
        foreach (var v in versions) items.Add(new ComboBoxItem { Content = v, Tag = v });
        _suppressFilter = true;
        VersionCombo.ItemsSource = items;
        VersionCombo.SelectedIndex = _svc.GameVersion is null ? 0 : -1;
        if (_svc.GameVersion is not null) VersionCombo.Text = _svc.GameVersion;
        _suppressFilter = false;
    }

    private async void OnVersionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilter) return;

        var v = VersionCombo.SelectedItem is ComboBoxItem it ? it.Tag as string : null;
        await ApplyGameVersionAsync(v);
    }

    private void ScheduleVersionCommit()
    {
        if (_suppressFilter) return;
        _versionTextTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _versionTextTimer.Stop();
        _versionTextTimer.Tick -= OnVersionTextCommit;
        _versionTextTimer.Tick += OnVersionTextCommit;
        _versionTextTimer.Start();
    }

    private async void OnVersionTextCommit(object? sender, EventArgs e)
    {
        _versionTextTimer?.Stop();
        if (_suppressFilter) return;

        var text = VersionCombo.Text?.Trim();
        if (VersionCombo.SelectedItem is ComboBoxItem it && (it.Tag as string ?? "") == (text ?? "")) return;
        var v = string.IsNullOrEmpty(text) || text == S.Get("modpack.f.all") ? null : text;
        await ApplyGameVersionAsync(v);
    }

    private async Task ApplyGameVersionAsync(string? version)
    {
        CardList.Children.Clear();
        _rendered = 0;
        try { await _svc.SetGameVersionAsync(version); } catch {   }
    }

    private async Task BuildTypeComboAsync()
    {
        var list = new List<object> { new ComboBoxItem { Content = S.Get("modpack.f.allType"), Tag = "" } };

        if (_svc.Source == ModpackService.SourceAll)
        {

            var mrCats = await ModpackService.GetModrinthCategoriesAsync(_svc.ProjectType);
            var cfCats = await ModpackService.GetCfCategoriesAsync(_svc.ProjectType);
            var cfUsed = new HashSet<string>();
            foreach (var slug in mrCats)
            {
                var cfName = ModpackService.MatchCfCategoryName(slug, cfCats);
                if (cfName is not null) cfUsed.Add(cfName);
                var cid = cfName is null ? null : ModpackService.GetCfCategoryId(_svc.ProjectType, cfName);
                list.Add(new ComboBoxItem { Content = LocalizeTag(slug), Tag = $"{(cid is null ? "" : cid)}/{slug}" });
            }

            foreach (var c in cfCats.Where(c => !cfUsed.Contains(c)))
            {
                var cid = ModpackService.GetCfCategoryId(_svc.ProjectType, c);
                if (cid is null) continue;
                list.Add(new ComboBoxItem { Content = c, Tag = $"{cid}/" });
            }
        }
        else if (_svc.Source == ModpackService.SourceCurseForge)
        {
            var cats = await ModpackService.GetCfCategoriesAsync(_svc.ProjectType);
            foreach (var c in cats)
            {
                var cid = ModpackService.GetCfCategoryId(_svc.ProjectType, c);
                if (cid is null) continue;
                list.Add(new ComboBoxItem { Content = LocalizeTag(c), Tag = $"{cid}/" });
            }
        }
        else
        {
            var cats = await ModpackService.GetModrinthCategoriesAsync(_svc.ProjectType);
            foreach (var slug in cats)
                list.Add(new ComboBoxItem { Content = LocalizeTag(slug), Tag = "/" + slug });
        }

        _suppressFilter = true;
        TypeCombo.ItemsSource = list;
        var idx = string.IsNullOrEmpty(_svc.Category)
            ? 0
            : list.FindIndex(x => x is ComboBoxItem ci && (ci.Tag as string) == _svc.Category);
        TypeCombo.SelectedIndex = idx < 0 ? 0 : idx;
        _suppressFilter = false;
    }

    private async void OnTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilter) return;
        var cat = TypeCombo.SelectedItem is ComboBoxItem it ? it.Tag as string : null;
        CardList.Children.Clear();
        _rendered = 0;
        try { await _svc.SetCategoryAsync(cat); } catch {   }
    }

    private void MoveTabIndicator(Button? target)
    {
        if (target is null) return;
        var top = target.TranslatePoint(new Point(0, 0), TabPanel);
        if (top is null || target.Bounds.Height <= 0) return;
        TabIndicator.Height = target.Bounds.Height;
        TabIndicator.IsVisible = true;
        TabIndicator.RenderTransform = TransformOperations.Parse($"translateY({top.Value.Y:0.#}px)");
    }

    private Transitions? _pageFx;
    private int _fxPending;

    private Avalonia.Controls.Control[] FxTargets => [FilterCard, CardList];

    private void FxSetStart(Avalonia.Controls.Control c, int dir)
    {
        c.Transitions = null;
        c.Opacity = 0;

        c.RenderTransform = TransformOperations.Parse($"translateX({dir * 72}px)");
    }

    private void FxPlayIn(Avalonia.Controls.Control c)
    {
        c.Transitions = _pageFx;
        Dispatcher.UIThread.Post(() =>
        {
            c.RenderTransform = TransformOperations.Parse("translateX(0px)");
            c.Opacity = 1;
        }, DispatcherPriority.Background);
    }

    private void ConsumePageFx()
    {
        if (_fxPending == 0) return;
        _fxPending = 0;
        FxPlayIn(CardList);
    }

    private void SwapPage(Action rebuild, int dir = 1)
    {
        if (_pageFx is null)
            _pageFx = new Transitions
            {
                new Avalonia.Animation.DoubleTransition { Property = Avalonia.Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(240), Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
                new Avalonia.Animation.TransformOperationsTransition { Property = Avalonia.Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(320), Easing = new Avalonia.Animation.Easings.QuadraticEaseInOut() },
            };
        foreach (var t in FxTargets) FxSetStart(t, dir);
        FxPlayIn(FilterCard);
        rebuild();
        if (CardList.Children.Count > 0 || StatusCard.IsVisible)
        {
            _fxPending = 0;
            FxPlayIn(CardList);
        }
        else _fxPending = dir;
    }

    private async void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string type) return;

        if (type == "vanilla")
        {
            if (_inVanilla) return;
            _inVanilla = true;
            MoveTabIndicator(b);
            SwapPage(() => { EnterVanillaUi(); CardList.Children.Clear(); _rendered = 0; });
            await LoadVanillaVersionsAsync();
            return;
        }

        if (_inVanilla)
        {
            _inVanilla = false;
            ExitVanillaUi();

            if (type == _svc.ProjectType && _svc.CurrentItems().Count > 0)
            {

                MoveTabIndicator(b);
                SwapPage(() => { UpdateSearchPlaceholder(type); RenderAll(); });
                return;
            }

        }

        if (type == _svc.ProjectType && !_svc.IsSearching && (_svc.CurrentItems().Count > 0 || _svc.IsLoading)) return;

        var toMap = type == "map";
        var fromMap = _svc.ProjectType == "map";
        var targetSource = _svc.Source;
        if (toMap && !fromMap) { _sourceBeforeMap = _svc.Source; targetSource = ModpackService.SourceCurseForge; }
        else if (fromMap && !toMap) targetSource = _sourceBeforeMap ?? ModpackService.SourceAll;

        MoveTabIndicator(b);
        UpdateSearchPlaceholder(type);
        UpdateLoaderVisibility(type);
        SourceCombo.IsEnabled = !toMap;

        _pageFilters[FilterKey] = new PageFilter(_svc.GameVersion, _svc.Category, _svc.SearchQuery, _svc.Loader);
        var saved = _pageFilters.TryGetValue(targetSource + "|" + type, out var f)
            ? f : new PageFilter(_svc.GameVersion, null, null);

        _suppressSearch = true;
        SearchBox.Text = saved.Query ?? "";
        _suppressSearch = false;
        RestoreVersionCombo(saved.Ver);
        RestoreLoaderCombo(type == "mod" ? saved.Loader : null);
        SyncSourceCombo(targetSource);

        SwapPage(() => { CardList.Children.Clear(); _rendered = 0; });
        try
        {
            if (targetSource != _svc.Source)
                await _svc.SetSourcePageAsync(targetSource, saved.Ver, saved.Cat, saved.Query, saved.Loader);
            else
                await _svc.SetPageAsync(type, saved.Ver, saved.Cat, saved.Query, saved.Loader);
        }
        catch {   }
        _ = BuildTypeComboAsync();
    }

    private void SetVanillaRow2(bool vanilla)
    {
        if (vanilla)
        {
            Row2Label.Text = S.Get("modpack.f.dlsrc");
            VersionCombo.IsVisible = false;
            LoaderLabel.IsVisible = false;
            LoaderCombo.IsVisible = false;
            TypeLabel.IsVisible = false;
            TypeCombo.IsVisible = false;
            DlSrcCombo.IsVisible = true;
        }
        else
        {
            Row2Label.Text = S.Get("modpack.f.version");
            VersionCombo.IsVisible = true;
            TypeLabel.IsVisible = true;
            TypeCombo.IsVisible = true;
            DlSrcCombo.IsVisible = false;
            UpdateLoaderVisibility();
        }
    }

    private void EnterVanillaUi()
    {
        SourceCombo.IsVisible = false;
        SourceLabel.Text = S.Get("modpack.f.type");
        VanillaCatCombo.IsVisible = true;
        FilterRow2.IsVisible = true;
        SetVanillaRow2(true);
        _suppressSearch = true;
        SearchBox.Text = "";
        _suppressSearch = false;
        UpdateSearchPlaceholder("vanilla");
    }

    private void ExitVanillaUi()
    {
        SourceCombo.IsVisible = true;
        SourceLabel.IsVisible = true;
        SourceLabel.Text = S.Get("modpack.f.source");
        VanillaCatCombo.IsVisible = false;
        FilterRow2.IsVisible = true;
        SetVanillaRow2(false);
        _suppressSearch = true;
        SearchBox.Text = _svc.SearchQuery ?? "";
        _suppressSearch = false;
    }

    private void HideVanillaFilterControls()
    {
        SourceCombo.IsVisible = false;
        SourceLabel.IsVisible = false;
        VanillaCatCombo.IsVisible = false;
        FilterRow2.IsVisible = false;
    }

    private void RestoreVanillaFilterControls()
    {
        SourceCombo.IsVisible = false;
        SourceLabel.IsVisible = true;
        SourceLabel.Text = S.Get("modpack.f.type");
        VanillaCatCombo.IsVisible = true;
        FilterRow2.IsVisible = true;
        SetVanillaRow2(true);
    }

    private async Task LoadVanillaVersionsAsync()
    {
        ShowVanillaStatus(S.Get("dl.loading"), retryable: false);
        ShowLoadBar(true);
        try
        {
            _vanillaVersions = await McInstaller.FetchVersionListAsync();
            RebuildVanillaList();
        }
        catch (Exception ex)
        {
            AppLog.Warn("[modpack] 游戏版本清单拉取失败：" + ex.Message);
            ShowVanillaStatus(S.Get("dl.listFail"), retryable: true);
        }
        finally { ShowLoadBar(false); }
    }

    private void ShowVanillaStatus(string text, bool retryable)
    {
        if (!_inVanilla) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => ShowVanillaStatus(text, retryable)); return; }
        CardList.Children.Clear();
        _rendered = 0;
        StatusText.Text = text;
        RetryBtn.IsVisible = retryable;
        StatusCard.IsVisible = true;
        ConsumePageFx();
    }

    private void ShowLoadBar(bool show)
        => Dispatcher.UIThread.Post(() =>
        {
            if (show)
            {
                LoadBar.IsVisible = true;
                LoadBar.IsIndeterminate = false;
                Dispatcher.UIThread.Post(() => LoadBar.IsIndeterminate = true, DispatcherPriority.Background);
            }
            else
            {
                LoadBar.IsVisible = false;
                LoadBar.IsIndeterminate = false;
            }
        });

    private void RebuildVanillaList()
    {
        if (!_inVanilla) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(RebuildVanillaList); return; }

        if (_vanillaDetail is not null) { RenderVanillaDetail(); return; }

        ShowError(false);
        CardList.Children.Clear();
        _rendered = 0;

        var cat = VanillaCatCombo.SelectedItem is ComboBoxItem ci ? ci.Tag as string : null;
        var q = (SearchBox.Text ?? "").Trim();
        var hits = _vanillaVersions
            .Where(v => cat switch
            {

                "release" => v.Type == "release" && !McInstaller.IsAprilFools(v),
                "snapshot" => v.Type == "snapshot" && !McInstaller.IsAprilFools(v) && !McInstaller.IsLegacy(v),
                "old" => McInstaller.IsLegacy(v) && !McInstaller.IsAprilFools(v),
                "af" => McInstaller.IsAprilFools(v),
                _ => true,
            })
            .Where(v => q.Length == 0 || v.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (hits.Count == 0)
        {
            ShowVanillaStatus(_vanillaVersions.Count == 0 ? S.Get("dl.loading") : S.Get("dl.empty"), retryable: false);
            return;
        }
        StatusCard.IsVisible = false;
        foreach (var v in hits) CardList.Children.Add(BuildVanillaRow(v));
        ConsumePageFx();
    }

    private void OpenVanillaDetail(McVersionEntry v)
    {
        _vanillaDetail = v.Id;
        _loaderPick = null;
        _selVanilla = true;
        _loaderSel = null;
        _renameText = v.Id;
        _renameDefault = v.Id;
        _renameTouched = false;
        _fabricApiOn = true;
        _fabricApiSelVer = null;
        _fabricApiExpand = false;
        SwapPage(() => { HideVanillaFilterControls(); RebuildVanillaList(); }, 1);
        _ = ProbeLoadersAsync(v.Id);
    }

    private async Task ProbeLoadersAsync(string mc)
    {
        if (_loaderProbe.TryGetValue(mc, out var cached))
        {
            var failed = cached.Where(kv => kv.Value.Done && kv.Value.List is null).Select(kv => kv.Key).ToList();
            if (failed.Count == 0) return;
            cached = _loaderProbe[mc];
            foreach (var k in failed) cached[k] = (false, null);
            if (_vanillaDetail == mc) Dispatcher.UIThread.Post(RebuildVanillaList, DispatcherPriority.Background);
            await Task.WhenAll(failed.Select(kind => ProbeOne(kind, mc, () => FetchByKind(kind, mc))));
            return;
        }
        var dict = new Dictionary<string, (bool, List<McLoaderEntry>?)>
        {
            ["forge"] = (false, null), ["neoforge"] = (false, null), ["fabric"] = (false, null), ["optifine"] = (false, null),
        };
        _loaderProbe[mc] = dict;
        if (_vanillaDetail == mc) Dispatcher.UIThread.Post(RebuildVanillaList, DispatcherPriority.Background);
        await Task.WhenAll(
            ProbeOne("forge", mc, () => McInstaller.FetchForgeVersionsAsync(mc)),
            ProbeOne("neoforge", mc, () => McInstaller.FetchNeoForgeVersionsAsync(mc)),
            ProbeOne("fabric", mc, () => McInstaller.FetchFabricVersionsAsync(mc)),
            ProbeOne("optifine", mc, () => McInstaller.FetchOptiFineVersionsAsync(mc)));
    }

    private static Task<List<McLoaderEntry>> FetchByKind(string kind, string mc) => kind switch
    {
        "forge" => McInstaller.FetchForgeVersionsAsync(mc),
        "neoforge" => McInstaller.FetchNeoForgeVersionsAsync(mc),
        "optifine" => McInstaller.FetchOptiFineVersionsAsync(mc),
        _ => McInstaller.FetchFabricVersionsAsync(mc),
    };

    private async Task ProbeOne(string kind, string mc, Func<Task<List<McLoaderEntry>>> fetch)
    {
        var dict = _loaderProbe[mc];
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                dict[kind] = (true, await fetch());
                break;
            }
            catch (Exception ex)
            {
                if (attempt < 3)
                {
                    await Task.Delay(500 * attempt);
                    continue;
                }
                AppLog.Warn($"[modpack] {kind} 可用性探测 3 次均失败（{mc}）：" + ex.Message);
                dict[kind] = (true, null);
            }
        }
        if (_vanillaDetail == mc) Dispatcher.UIThread.Post(RebuildVanillaList, DispatcherPriority.Background);
    }

    private void RenderVanillaDetail()
    {
        var v = _vanillaVersions.FirstOrDefault(x => x.Id == _vanillaDetail);
        if (v is null) { _vanillaDetail = null; RebuildVanillaList(); return; }
        CardList.Children.Clear();
        _rendered = 0;
        StatusCard.IsVisible = false;

        string title = v.Id;
        string? iconFile = VanillaCatIconFile(v);
        if (_loaderSel is { } sel)
        {
            title = $"{v.Id}-{LoaderName(sel.Kind)} {sel.Version}";
            iconFile = sel.Kind switch
            {
                "forge" => "forge.png",
                "neoforge" => "NeoForge.png",
                "fabric" => "Fabric.png",
                _ => "OptiFine.png",
            };
        }

        _renameDefault = title;
        if (!_renameTouched || string.IsNullOrWhiteSpace(_renameText)) _renameText = title;
        _renameTouched = _renameText != _renameDefault;

        CardList.Children.Add(BuildDetailHeaderRow(title,
            v.ReleaseTime.Length >= 10 ? v.ReleaseTime[..10] : v.ReleaseTime, BackToVanillaList,
            iconFile, renameable: true));

        var vanilla = BuildDetailRow(S.Get("dl.vanillaRow"), _selVanilla ? "✓" : "",
            () => { _selVanilla = true; _loaderSel = null; RebuildVanillaList(); },
            VanillaCatIconFile(v));
        CardList.Children.Add(vanilla);
        AddLoaderRow("Forge", "forge", v.Id);
        AddLoaderRow("NeoForge", "neoforge", v.Id);
        AddLoaderRow("Fabric", "fabric", v.Id);

        if (_loaderSel is { } fsel && fsel.Kind == "fabric")
        {
            _ = EnsureFabricApiListAsync(v.Id);
            CardList.Children.Add(BuildFabricApiRow());
            if (_fabricApiExpand) AppendFabricApiVersions(v.Id);
        }
        AddLoaderRow("OptiFine", "optifine", v.Id);
        CardList.Children.Add(BuildStartDownloadButton(v.Id));
    }

    private Border BuildStartDownloadButton(string mc)
    {
        var btn = new Border
        {
            Background = GetRes("PrimaryBrush"),
            CornerRadius = new CornerRadius(24),
            Padding = new Thickness(30, 12),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Thickness(0, 20, 0, 6),
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "↓", FontSize = 16, FontWeight = FontWeight.Bold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Foreground = Brushes.White },
                    new TextBlock { Text = S.Get("dl.start"), FontSize = 14.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Foreground = Brushes.White },
                },
            },
        };
        btn.Tapped += (_, _) => _ = StartDetailDownloadAsync(mc);
        return btn;
    }

    private Border BuildFabricApiRow()
    {
        var mc = _vanillaDetail ?? "";
        bool listLoading = _fabricApiListLoading.Contains(mc);
        bool noneAvailable = _fabricApiCache.TryGetValue(mc, out var fl) && fl.Count == 0;
        var row = new Border { Classes = { "card", "mpCard" }, Padding = new Thickness(14, 12), Margin = new Thickness(16, 0, 4, 8) };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        var img = new Image { Source = VanillaCatIcon("Fabric.png"), Width = 26, Height = 26 };
        Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(img, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        var iconHost = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(6),
            ClipToBounds = true, Child = img,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        g.Children.Add(iconHost);
        var text = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        text.Children.Add(new TextBlock
        {
            Text = "Fabric API", FontSize = 13.5, FontWeight = FontWeight.SemiBold,
            Foreground = GetRes("OnSurfaceBrush"),
        });
        text.Children.Add(new TextBlock
        {
            Text = S.Get("dl.fapi.desc"), FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = GetRes("OnSurfaceVarBrush"),
        });
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        string status = _fabricApiExpand ? "▴"
            : listLoading ? S.Get("dl.loader.checking")
            : noneAvailable ? S.Get("dl.fapi.none")
            : _fabricApiOn ? (_fabricApiSelVer ?? S.Get("dl.fapi.latest"))
            : S.Get("dl.fapi.none");
        var statusEl = new TextBlock
        {
            Text = status, FontSize = 12.5, Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = _fabricApiOn || _fabricApiExpand ? GetRes("OnSurfaceBrush") : GetRes("OnSurfaceVarBrush"),
        };
        Grid.SetColumn(statusEl, 3);
        g.Children.Add(statusEl);
        row.Child = g;
        if (noneAvailable || listLoading)
        {
            row.Opacity = listLoading ? 1.0 : 0.55;
            if (!listLoading) row.Classes.Remove("mpCard");
            return row;
        }
        row.Tapped += (_, _) =>
        {
            _fabricApiExpand = !_fabricApiExpand;
            if (_fabricApiExpand) _ = EnsureFabricApiListAsync(_vanillaDetail ?? "");
            RebuildVanillaList();
        };
        return row;
    }

    private async Task StartDetailDownloadAsync(string mc)
    {

        if (!LauncherService.IsPremiumLoggedIn)
        {
            ShowVanillaStatus(S.Get("launcher.download.premiumRequired"), false);
            return;
        }
        string? custom = _renameTouched ? _renameText.Trim() : null;
        if (_loaderSel is { } sel)
        {
            string? fapiFile = null;
            string[]? fapiUrls = null;
            if (sel.Kind == "fabric" && _fabricApiOn)
            {
                await EnsureFabricApiListAsync(mc);
                var entry = _fabricApiCache.TryGetValue(mc, out var l)
                    ? l.FirstOrDefault(v => v.Version == _fabricApiSelVer)
                      ?? l.FirstOrDefault(v => v.Stable) ?? l.FirstOrDefault()
                    : null;
                if (entry is null)
                {
                    ShowVanillaStatus(S.Get("dl.fapi.fail"), false);
                    return;
                }
                fapiFile = entry.FileName;
                fapiUrls = entry.Urls;
            }
            InstallLoaderFromPick(sel.Kind, mc, sel.Version, custom, fapiFile, fapiUrls, sel.Tag);
            return;
        }
        InstallFromDetail(mc, custom);
    }

    private void AppendFabricApiVersions(string mc)
    {
        var rows = new List<Control>();
        var off = BuildDetailRow(S.Get("dl.fapi.none"), "✕",
            () => { _fabricApiOn = false; _fabricApiExpand = false; RebuildVanillaList(); }, "Fabric.png");
        off.Margin = new Thickness(16, 0, 4, 8);
        rows.Add(off);
        if (_fabricApiCache.TryGetValue(mc, out var list) && list.Count > 0)
        {
            foreach (var e in list)
            {
                var row = new Border { Classes = { "card", "mpCard" }, Padding = new Thickness(14, 12), Margin = new Thickness(16, 0, 4, 8) };
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto") };
                var img = new Image { Source = VanillaCatIcon("Fabric.png"), Width = 26, Height = 26 };
                Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(img, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
                var iconHost = new Border
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(6),
                    ClipToBounds = true, Child = img,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                };
                g.Children.Add(iconHost);
                var text = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                text.Children.Add(new TextBlock
                {
                    Text = e.Version, FontSize = 13.5, FontWeight = FontWeight.SemiBold,
                    Foreground = GetRes("OnSurfaceBrush"),
                });
                text.Children.Add(new TextBlock
                {
                    Text = S.Get(e.Stable ? "dl.stable" : "dl.beta"), FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
                    Foreground = e.Stable ? GetRes("OnSurfaceVarBrush") : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E0700A")),
                });
                Grid.SetColumn(text, 1);
                g.Children.Add(text);
                if (e.Date.Length > 0)
                {
                    var d = new TextBlock
                    {
                        Text = S.Get("dl.fapi.pub") + " " + e.Date, FontSize = 11.5,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Foreground = GetRes("OnSurfaceVarBrush"),
                    };
                    Grid.SetColumn(d, 3);
                    g.Children.Add(d);
                }
                var pick = new TextBlock
                {
                    Text = _fabricApiOn && _fabricApiSelVer == e.Version ? "✓" : S.Get("dl.fapi.off"),
                    FontSize = 11.5, Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = _fabricApiOn && _fabricApiSelVer == e.Version ? Brushes.SeaGreen : GetRes("OnSurfaceVarBrush"),
                };
                Grid.SetColumn(pick, 4);
                g.Children.Add(pick);
                row.Child = g;
                var picked = e;
                row.Tapped += (_, _) =>
                {
                    _fabricApiSelVer = picked.Version;
                    _fabricApiOn = true;
                    _fabricApiExpand = false;
                    RebuildVanillaList();
                };
                rows.Add(row);
            }
        }
        else if (!_fabricApiListLoading.Contains(mc))
        {
            rows.Add(new TextBlock
            {
                Text = S.Get("dl.fapi.fail"), FontSize = 12.5, Margin = new Thickness(18, 6, 0, 4),
                Foreground = GetRes("OnSurfaceVarBrush"),
            });
        }

        foreach (var r in rows)
        {
            r.Opacity = 0;
            r.RenderTransform = TransformOperations.Parse("translateY(-12px)");
            CardList.Children.Add(r);
        }
        _ = AnimateDropdownIn(rows);
    }

    private async Task EnsureFabricApiListAsync(string mc)
    {
        if (mc.Length == 0 || _fabricApiCache.ContainsKey(mc) || _fabricApiListLoading.Contains(mc)) return;
        _fabricApiListLoading.Add(mc);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                _fabricApiCache[mc] = await McInstaller.FetchFabricApiListAsync(mc);

                if (_vanillaDetail == mc && _loaderSel is { Kind: "fabric" } && _fabricApiSelVer == null)
                {
                    var stable = _fabricApiCache[mc].FirstOrDefault(e => e.Stable);
                    if (stable != null) _fabricApiSelVer = stable.Version;
                    else if (_fabricApiCache[mc].Count > 0) _fabricApiSelVer = _fabricApiCache[mc][0].Version;
                    else _fabricApiOn = false;
                }
                break;
            }
            catch (Exception ex)
            {
                if (attempt < 3) { await Task.Delay(500 * attempt); continue; }
                AppLog.Warn($"[modpack] Fabric API 版本列表 3 次拉取失败（{mc}）：" + ex.Message);
            }
        }
        _fabricApiListLoading.Remove(mc);
        if (_vanillaDetail != null) Dispatcher.UIThread.Post(RebuildVanillaList, DispatcherPriority.Background);
    }

    private void InstallFromDetail(string versionId, string? customName = null)
    {
        if (McInstaller.IsVersionRunning(versionId)) { ShowVanillaStatus(S.Get("dl.task.already"), false); return; }
        var dir = McInstaller.ResolveInstallDir();
        try { System.IO.Directory.CreateDirectory(dir); } catch { }
        AppLog.Info($"[mcinstall] 用户选择版本 {versionId}（仅原版），目标目录 {dir}");
        McInstaller.InstallVanilla(versionId, dir, customName);
        BackToVanillaList();
    }

    private void BackToVanillaList()
    {
        _vanillaDetail = null;
        _loaderPick = null;
        SwapPage(() => { RestoreVanillaFilterControls(); RebuildVanillaList(); }, -1);
    }

    private ModpackInfo? _resDetail;
    private bool _resLoading;
    private bool _resFailed;
    private List<ModpackService.ResourceVersionEntry> _resVersions = [];
    private string? _resPickId;
    private List<LauncherService.ResTarget> _resTargets = [];
    private int _resTargetSel = -1;
    private readonly HashSet<string> _resGroupsOpen = new();
    private string? _resGroupJustOpened;
    private bool _resGroupByMajor;
    private bool _resGroupFoldOld;

    private void OpenResourceDetail(ModpackInfo m)
    {
        AppLog.Info($"[modpack] 卡片点击进详情：{m.Title}（{m.ProjectType}）");
        _resDetail = m;
        _resPickId = null;
        _resVersions = [];
        _resLoading = true;
        _resFailed = false;
        _resTargetSel = -1;
        _resGroupsOpen.Clear();
        SwapPage(() =>
        {
            HideVanillaFilterControls();
            _suppressSearch = true;
            SearchBox.Text = "";
            _suppressSearch = false;
            CardList.Children.Clear();
            _rendered = 0;
            StatusCard.IsVisible = false;
            RenderResourceDetail();
        }, 1);
        _ = LoadResVersionsAsync();
    }

    private async Task LoadResVersionsAsync()
    {
        var m = _resDetail;
        if (m is null) return;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var list = await _svc.FetchResourceVersionsAsync(m);
                if (_resDetail != m) return;
                AppLog.Info($"[modpack] 版本列表到达：{m.Title}，{list.Count} 条（{list.Select(v => v.GameVersions).SelectMany(g => g).Distinct().Count()} 个游戏版本）");
                _resVersions = list;
                _resLoading = false;
                _resFailed = false;
                Dispatcher.UIThread.Post(RebuildResourceDetail, DispatcherPriority.Background);
                return;
            }
            catch (Exception ex)
            {
                if (_resDetail != m) return;
                AppLog.Warn($"[modpack] {m.Title} 版本列表第 {attempt} 次拉取失败：" + ex.Message);
                if (attempt < 3) { await Task.Delay(500 * attempt); continue; }
                _resLoading = false;
                _resFailed = true;
                Dispatcher.UIThread.Post(RebuildResourceDetail, DispatcherPriority.Background);
            }
        }
    }

    private void RebuildResourceDetail()
    {
        if (_resDetail is null || _inVanilla) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(RebuildResourceDetail); return; }
        CardList.Children.Clear();
        _rendered = 0;
        StatusCard.IsVisible = false;
        RenderResourceDetail();
    }

    private void RenderResourceDetail()
    {
        var m = _resDetail;
        if (m is null) return;

        IImage? iconImg = null;
        var iconBytes = _svc.GetIconBytes(m.IconKey);
        if (iconBytes is not null)
        {
            try { iconImg = new Bitmap(new MemoryStream(iconBytes)); } catch { }
        }
        CardList.Children.Add(BuildDetailHeaderRow(m.Title, m.Author, BackToResourceList, iconImage: iconImg));

        var updated = m.Updated is { } u ? u.ToLocalTime().ToString("yyyy-MM-dd") : "";
        CardList.Children.Add(new Border
        {
            Classes = { "card" },
            Padding = new Thickness(14, 12),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = m.Description, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxLines = 3,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Foreground = GetRes("OnSurfaceBrush"),
                    },
                    new TextBlock
                    {
                        Text = $"↓ {ModpackService.FormatDownloads(m.Downloads)}"
                               + (updated.Length > 0 ? $" · {S.Get("modpack.updated")} {updated}" : "")
                               + (m.Categories.Length > 0 ? $" · {LocalizeTag(m.Categories[0])}" : ""),
                        FontSize = 11, Margin = new Thickness(0, 5, 0, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Foreground = GetRes("OnSurfaceVarBrush"),
                    },
                },
            },
        });

        var web = new Button
        {
            Classes = { "mpGhost" },
            MinHeight = 30,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 8),
            Content = new TextBlock { Text = "🌐 " + S.Get("modpack.openweb"), FontSize = 12 },
        };
        web.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(m.PageUrl) { UseShellExecute = true }); }
            catch (Exception ex) { AppLog.Warn($"[modpack] 打开详情页失败：{ex.Message}"); }
        };
        CardList.Children.Add(web);

        if (_resLoading) { ShowVanillaStatus(S.Get("dl.loader.checking"), false); return; }
        if (_resFailed) { ShowVanillaStatus(S.Get("dl.res.loadFail"), true); return; }

        (_resGroupByMajor, _resGroupFoldOld) = PickGroupStrategy(_resVersions);
        var allGroups = BuildVersionGroups(_resVersions);
        var q = (SearchBox.Text ?? "").Trim();
        IEnumerable<ModpackService.ResourceVersionEntry> hitEnum =
            q.Length == 0
                ? _resVersions
                : _resVersions.Where(v =>
                    v.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    v.FileName.Contains(q, StringComparison.OrdinalIgnoreCase));
        var hits = hitEnum.ToList();

        bool hideServer = false;
        if (m.ProjectType == "modpack" && hits.Count > 0)
        {
            var client = hits.Where(v => !ModpackService.IsServerPackFile(v)).ToList();
            if (client.Count > 0) { hits = client; hideServer = true; }
        }
        if (hits.Count == 0)
        {
            ShowVanillaStatus(_resVersions.Count == 0 ? S.Get("dl.res.none") : S.Get("dl.res.noresult"), false);
            return;
        }
        if (q.Length > 0 || allGroups.Count < 2)
        {

            foreach (var e in hits) _ = AddResVersionWithTargets(m, e, hideServer);
        }
        else
        {

            List<Control>? justOpenedRows = _resGroupJustOpened is null ? null : [];
            foreach (var grp in allGroups)
            {
                var items = hits.Where(v => v.GameVersions.Any(g => VersionGroup(g) == grp)).ToList();
                if (items.Count == 0) continue;
                bool open = _resGroupsOpen.Contains(grp);
                CardList.Children.Add(BuildVersionGroupHeader(grp, items.Count, open));
                if (!open) continue;
                foreach (var e in items)
                {
                    var rows = AddResVersionWithTargets(m, e, hideServer);
                    justOpenedRows?.AddRange(rows);
                }
            }

            if (justOpenedRows is { Count: > 0 })
            {
                foreach (var r in justOpenedRows)
                {
                    r.Opacity = 0;
                    r.RenderTransform = TransformOperations.Parse("translateY(-12px)");
                }
                _ = AnimateDropdownIn(justOpenedRows);
            }
            _resGroupJustOpened = null;
        }
        ConsumePageFx();
    }

    private List<Control> AddResVersionWithTargets(ModpackInfo m, ModpackService.ResourceVersionEntry e, bool hideServer)
    {
        var added = new List<Control>();
        var row = BuildResVersionRow(e, serverOnly: !hideServer && ModpackService.IsServerPackFile(e));
        CardList.Children.Add(row);
        added.Add(row);
        if (e.Id == _resPickId)
        {
            AppLog.Info($"[modpack] 渲染选中版本目标卡：{e.DisplayName}（{_resTargets.Count} 个目标）");
            foreach (var r in BuildResTargetRows(m, e)) { CardList.Children.Add(r); added.Add(r); }
        }
        return added;
    }

    private Border BuildResVersionRow(ModpackService.ResourceVersionEntry e, bool serverOnly = false)
    {
        var row = new Border { Classes = { "card", "mpCard" }, Padding = new Thickness(14, 12) };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };

        var img = new Image { Source = VanillaCatIcon(e.ReleaseType == "release" ? "Release.png" : "Beta.png"), Width = 28, Height = 28 };
        Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(img, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        var iconHost = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(7),
            ClipToBounds = true, Child = img,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        g.Children.Add(iconHost);

        var text = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        text.Children.Add(new TextBlock
        {
            Text = e.DisplayName, FontSize = 13.5, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = GetRes("OnSurfaceBrush"),
        });

        var parts = new List<string>();
        if (e.ReleaseType != "release") parts.Add(S.Get(e.ReleaseType == "alpha" ? "dl.res.alpha" : "dl.beta"));
        if (e.Date != default) parts.Add(e.Date.ToLocalTime().ToString("yyyy-MM-dd"));
        if (e.GameVersions.Length > 0)
            parts.Add(S.Get("modpack.f.version") + " " +
                      (e.GameVersions.Length <= 2 ? string.Join("、", e.GameVersions)
                        : $"{e.GameVersions[0]}、{e.GameVersions[1]} +{e.GameVersions.Length - 2}"));
        if (e.Loaders.Length > 0) parts.Add(string.Join("、", e.Loaders.Select(l => l.Length > 0 ? char.ToUpperInvariant(l[0]) + l[1..] : l)));
        if (e.Size > 0) parts.Add(FormatSize(e.Size));
        if (serverOnly) parts.Add(S.Get("dl.res.servertag"));
        text.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", parts), FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = e.ReleaseType == "release" && !serverOnly ? GetRes("OnSurfaceVarBrush")
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E0700A")),
        });
        Grid.SetColumn(text, 1);
        g.Children.Add(text);

        var pick = new TextBlock
        {
            Text = _resPickId == e.Id ? "✓" : "",
            FontSize = 13, FontWeight = FontWeight.Bold, Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = Brushes.SeaGreen,
        };
        Grid.SetColumn(pick, 3);
        g.Children.Add(pick);
        row.Child = g;

        row.Tapped += (_, _) =>
        {
            if (serverOnly)
            {
                ShowVanillaStatus(S.Get("dl.res.serverno"), false);
                return;
            }
            AppLog.Info($"[modpack] 版本行点击：{e.DisplayName}（当前选中 {(e.Id == _resPickId ? "→取消" : "→选中")}）");
            if (_resPickId == e.Id) { _resPickId = null; _resTargetSel = -1; }
            else
            {
                _resPickId = e.Id;
                _resTargetSel = 0;
                var m = _resDetail;
                if (m is not null)
                {

                    _resTargets = m.ProjectType == "modpack"
                        ? [new LauncherService.ResTarget(S.Get("dl.res.root"), McInstaller.ResolveInstallDir(),
                            McInstaller.ResolveInstallDir(), true, "vanilla")]
                        : LauncherService.FindResourceTargets(m.ProjectType, e.GameVersions, e.Loaders);
                }
            }
            RebuildResourceDetail();
        };
        return row;
    }

    private string VersionGroup(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return S.Get("dl.res.grother");
        if (v.Contains('w')) return S.Get("dl.cat.snap");
        var m = System.Text.RegularExpressions.Regex.Match(v, @"^1\.(\d+)(?:\.(\d+))?$");
        if (!m.Success) return S.Get("dl.cat.old");
        int minor = int.Parse(m.Groups[1].Value);
        if (_resGroupFoldOld && minor < 6) return S.Get("dl.cat.old");
        return _resGroupByMajor ? $"1.{minor}" : v;
    }

    private static (bool ByMajor, bool FoldOld) PickGroupStrategy(
        List<ModpackService.ResourceVersionEntry> versions)
    {
        var names = versions.SelectMany(e => e.GameVersions)
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
        foreach (var (byMajor, foldOld) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var probe = new HashSet<string>();
            bool byMaj = byMajor, fold = foldOld;
            foreach (var n in names)
            {
                if (n.Contains('w') || !n.StartsWith("1.")) { probe.Add("s"); continue; }
                var m = System.Text.RegularExpressions.Regex.Match(n, @"^1\.(\d+)(?:\.(\d+))?$");
                if (!m.Success) { probe.Add("s"); continue; }
                if (fold && int.Parse(m.Groups[1].Value) < 6) probe.Add("s");
                else probe.Add(byMaj ? "1." + m.Groups[1].Value : n);
            }
            if (probe.Count < 9) return (byMajor, foldOld);
        }
        return (true, true);
    }

    private List<string> BuildVersionGroups(List<ModpackService.ResourceVersionEntry> versions)
    {
        int Rank(string g) => g == S.Get("dl.res.grother") ? 3
            : g == S.Get("dl.cat.old") ? 2
            : g == S.Get("dl.cat.snap") ? 1 : 0;
        return versions.SelectMany(e => e.GameVersions)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(VersionGroup).Distinct()
            .OrderBy(g => Rank(g))
            .ThenByDescending(g =>
            {
                var p = g.Split('.');
                return (p.Length >= 2 && p[0] == "1" && int.TryParse(p[1], out var a) ? a : -1) * 1000
                     + (p.Length >= 3 && int.TryParse(p[2], out var b) ? b : 0);
            })
            .ToList();
    }

    private Border BuildVersionGroupHeader(string group, int count, bool open)
    {
        string title = group.StartsWith("1.") ? $"Minecraft {group}" : group;
        var row = new Border { Classes = { "card", "mpCard" }, Padding = new Thickness(14, 11) };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        g.Children.Add(new TextBlock
        {
            Text = title, FontSize = 13.5, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = GetRes("OnSurfaceBrush"),
        });
        var cnt = new TextBlock
        {
            Text = count.ToString(), FontSize = 11.5,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = GetRes("OnSurfaceVarBrush"),
        };
        Grid.SetColumn(cnt, 1);
        g.Children.Add(cnt);
        var chev = new TextBlock
        {
            Text = open ? "▾" : "▸", FontSize = 13, Margin = new Thickness(8, 0, 2, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = GetRes("OnSurfaceVarBrush"),
        };
        Grid.SetColumn(chev, 2);
        g.Children.Add(chev);
        row.Child = g;
        row.Tapped += (_, _) =>
        {

            AppLog.Info($"[modpack] 分组标题点击：{group}（当前 {(open ? "展开" : "收起")}）");
            if (_resGroupsOpen.Remove(group))
                _resGroupJustOpened = null;
            else
            {
                _resGroupsOpen.Add(group);
                _resGroupJustOpened = group;
            }
            RebuildResourceDetail();
        };
        return row;
    }

    private IEnumerable<Control> BuildResTargetRows(ModpackInfo m, ModpackService.ResourceVersionEntry e)
    {
        var rows = new List<Control>();
        var head = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(14, 8),
            Margin = new Thickness(16, 0, 4, 6),
            Opacity = 0.8,
            Child = new TextBlock
            {
                Text = S.Get("dl.res.pickTarget"), FontSize = 12, FontWeight = FontWeight.SemiBold,
                Foreground = GetRes("OnSurfaceVarBrush"),
            },
        };
        rows.Add(head);

        if (_resTargets.Count == 0)
        {
            rows.Add(new TextBlock
            {
                Text = S.Get("dl.res.nodir"), FontSize = 12, Margin = new Thickness(18, 4, 0, 6),
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#C0392B")),
            });
            return rows;
        }

        for (int i = 0; i < _resTargets.Count && i < 12; i++)
        {
            var t = _resTargets[i];
            var idx = i;
            var row = new Border { Classes = { "card", "mpCard" }, Padding = new Thickness(14, 10), Margin = new Thickness(16, 0, 4, 6) };
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            g.Children.Add(new TextBlock
            {
                Text = i == 0 ? "★ " + S.Get("dl.res.auto") : "›",
                FontSize = 12.5, FontWeight = FontWeight.SemiBold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = i == 0 ? GetRes("PrimaryBrush") : GetRes("OnSurfaceVarBrush"),
            });
            var text = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            text.Children.Add(new TextBlock
            {
                Text = t.Title, FontSize = 13, FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = GetRes("OnSurfaceBrush"),
            });
            if (t.Sub.Length > 0)
                text.Children.Add(new TextBlock
                {
                    Text = t.Sub, FontSize = 11, Margin = new Thickness(0, 1, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = GetRes("OnSurfaceVarBrush"),
                });
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            var mark = new TextBlock
            {
                Text = _resTargetSel == idx ? "✓" : "", FontSize = 13, FontWeight = FontWeight.Bold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = t.Compat ? Brushes.SeaGreen : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E0700A")),
            };
            Grid.SetColumn(mark, 2);
            g.Children.Add(mark);
            row.Child = g;
            row.Tapped += (_, _) =>
            {
                _resTargetSel = idx;
                InstallResourceTo(m, e, t);
            };
            rows.Add(row);
        }
        return rows;
    }

    private async void InstallResourceTo(ModpackInfo m, ModpackService.ResourceVersionEntry e, LauncherService.ResTarget target)
    {

        if (!LauncherService.IsPremiumLoggedIn)
        {
            ShowVanillaStatus(S.Get("launcher.download.premiumRequired"), false);
            return;
        }
        string type = m.ProjectType;

        if (type == "modpack")
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is not Window owner) return;
            var choiceWin = new ModpackChoiceWindow($"{m.Title} · {e.DisplayName}");

            var pick = choiceWin.Pick();
            await choiceWin.ShowDialog(owner);
            var choice = await pick;
            if (choice == ModpackChoiceWindow.Choice.None) return;

            if (choice == ModpackChoiceWindow.Choice.Export)
            {

                string safe = string.Concat(m.Title.Split(Path.GetInvalidFileNameChars()));
                var save = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = S.Get("dl.packchoice.export"),
                    SuggestedFileName = (safe.Length > 0 ? safe : "modpack") + ".mrpack",
                    FileTypeChoices = [new FilePickerFileType("mrpack") { Patterns = ["*.mrpack"] }],
                });
                if (save == null) return;
                string outPath = save.Path.LocalPath;
                if (!outPath.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)) outPath += ".mrpack";
                McInstaller.ExportMrpack($"{m.Title} · {e.DisplayName}", m.Title, e.Urls, e.Sha1, McInstaller.ResolveInstallDir(), outPath);
                AppLog.Info($"[modpack] 导出 mrpack {m.Title} → {outPath}");
            }
            else
            {
                McInstaller.InstallModpack($"{m.Title} · {e.DisplayName}", m.Title, e.Urls, e.Sha1, McInstaller.ResolveInstallDir());
                AppLog.Info($"[modpack] 安装整合包 {m.Title} → {McInstaller.ResolveInstallDir()}");
            }
            BackToResourceList();
            return;
        }
        string subDir = type switch
        {
            "mod" => "mods",
            "shader" => "shaderpacks",
            "resourcepack" => "resourcepacks",
            "datapack" => "",
            "map" => "saves",
            _ => "",
        };
        string dir = subDir.Length > 0 ? Path.Combine(target.BaseDir, subDir) : target.BaseDir;
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex)
        {
            AppLog.Warn($"[modpack] 创建目录失败 {dir}：{ex.Message}");
            return;
        }
        string title = $"{m.Title} · {e.DisplayName}";
        if (type == "map")
            McInstaller.DownloadResourceMap(title, e.FileName, e.Urls, dir, e.Sha1);
        else
            McInstaller.DownloadResourceFile(title, e.FileName, e.Urls, UniqueDestPath(Path.Combine(dir, e.FileName)), e.Sha1);
        LauncherService.RememberResourceTarget(type, target.BaseDir);
        AppLog.Info($"[modpack] 安装资源 {e.FileName} → {dir}");
        BackToResourceList();
    }

    private static string UniqueDestPath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var i = 2;
        while (File.Exists(Path.Combine(dir, $"{name} ({i}){ext}"))) i++;
        return Path.Combine(dir, $"{name} ({i}){ext}");
    }

    private static string FormatSize(long bytes)
        => bytes switch
        {
            >= 1 << 20 => $"{bytes / 1048576.0:F1} MB",
            >= 1 << 10 => $"{bytes / 1024.0:F0} KB",
            _ => $"{bytes} B",
        };

    private void BackToResourceList()
    {
        _resDetail = null;
        _resPickId = null;
        _resVersions = [];
        _resTargetSel = -1;
        _resGroupsOpen.Clear();
        SwapPage(() => { ExitVanillaUi(); UpdateSearchPlaceholder(_svc.ProjectType); RenderAll(); }, -1);
    }

    private Border BuildDetailHeaderRow(string title, string? subtitle, Action onBack, string? iconFile = null,
        bool renameable = false, IImage? iconImage = null)
    {
        var card = new Border { Classes = { "card" }, Padding = new Thickness(14, 10) };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        var back = new Button
        {
            Classes = { "mpGhost" }, MinHeight = 34, MinWidth = 86,
            Content = new TextBlock { Text = "‹ " + S.Get("dl.back"), FontSize = 13 },
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        back.Click += (_, _) => onBack();
        g.Children.Add(back);

        if (iconFile != null || iconImage != null)
        {
            var catIconImg = new Image
            {
                Source = iconImage ?? VanillaCatIcon(iconFile),
                Width = 44, Height = 44,
            };
            if (iconImage == null)
                Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(catIconImg, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
            var catIcon = new Border
            {
                Width = 44, Height = 44, CornerRadius = new CornerRadius(10),
                ClipToBounds = true, Child = catIconImg,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };
            Grid.SetColumn(catIcon, 1);
            g.Children.Add(catIcon);
        }

        Control name;
        if (renameable)
        {
            var box = new TextBox
            {
                Text = _renameText, Watermark = title,
                FontSize = 16, FontWeight = FontWeight.SemiBold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 8, 0),
                Background = GetRes("FieldSurfaceBrush"),
                BorderBrush = GetRes("OutlineVarBrush"),
                CornerRadius = new CornerRadius(8),
                MinHeight = 38, Padding = new Thickness(10, 5, 10, 5),
            };
            box.TextChanged += (_, _) =>
            {
                _renameText = box.Text ?? "";
                _renameTouched = !string.IsNullOrWhiteSpace(_renameText) && _renameText != _renameDefault;
            };
            name = box;
        }
        else
        {
            name = new TextBlock
            {
                Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                Foreground = GetRes("OnSurfaceBrush"),
            };
        }
        Grid.SetColumn(name, iconFile != null || iconImage != null ? 2 : 1);
        g.Children.Add(name);

        if (!string.IsNullOrEmpty(subtitle))
        {
            var sub = new TextBlock
            {
                Text = subtitle, FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Foreground = GetRes("OnSurfaceVarBrush"),
            };
            Grid.SetColumn(sub, 3);
            g.Children.Add(sub);
        }
        card.Child = g;
        return card;
    }

    private Border BuildDetailRow(string name, string status, Action? onTap, string? iconFile = null)
    {
        var card = new Border
        {
            Classes = { "card", "mpCard" },
            Padding = new Thickness(14, 15),
        };
        if (onTap is null) card.Classes.Remove("mpCard");
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        int nameCol = 0;
        if (iconFile != null)
        {

            var img = new Image
            {
                Source = VanillaCatIcon(iconFile),
                Width = 28, Height = 28,
            };
            Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(img, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
            var iconHost = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(7),
                ClipToBounds = true, Child = img,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            g.Children.Add(iconHost);
            nameCol = 1;
        }
        var nameEl = new TextBlock
        {
            Text = name, FontSize = 14, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(iconFile != null ? 10 : 0, 0, 0, 0),
            Foreground = GetRes("OnSurfaceBrush"),
        };
        Grid.SetColumn(nameEl, nameCol);
        g.Children.Add(nameEl);
        var statusEl = new TextBlock
        {
            Text = status, FontSize = 12.5,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Foreground = status == "✓" ? Brushes.SeaGreen : GetRes("OnSurfaceVarBrush"),
        };
        Grid.SetColumn(statusEl, 3);
        g.Children.Add(statusEl);
        card.Child = g;
        if (onTap is not null) card.Tapped += (_, _) => onTap();
        return card;
    }

    private async Task LoadLoaderPickAsync(string kind, string mc)
    {
        if (McInstaller.IsVersionRunning(mc)) { ShowVanillaStatus(S.Get("dl.task.already"), false); return; }
        if (_loaderPick is { } cur && cur.Kind == kind && cur.Mc == mc)
        {
            _loaderPick = null;
            RebuildVanillaList();
            return;
        }
        _loaderPick = (kind, mc);
        RebuildVanillaList();
        try
        {

            if (_loaderProbe.TryGetValue(mc, out var d) && d.TryGetValue(kind, out var st) && st.List is { Count: > 0 })
            {
                _loaderVersions = st.List;
                _loaderLoading = false;
                RebuildVanillaList();
                return;
            }
            _loaderLoading = true;
            _loaderVersions = [];
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    _loaderVersions = kind switch
                    {
                        "forge" => await McInstaller.FetchForgeVersionsAsync(mc),
                        "neoforge" => await McInstaller.FetchNeoForgeVersionsAsync(mc),
                        "optifine" => await McInstaller.FetchOptiFineVersionsAsync(mc),
                        _ => await McInstaller.FetchFabricVersionsAsync(mc),
                    };
                    break;
                }
                catch (Exception ex) when (attempt < 3)
                {
                    AppLog.Warn($"[modpack] 加载器版本列表第 {attempt} 次拉取失败，重试：{ex.Message}");
                    await Task.Delay(500 * attempt);
                }
            }
            _loaderLoading = false;

            if (_loaderProbe.TryGetValue(mc, out var dW) && dW.ContainsKey(kind)) dW[kind] = (true, _loaderVersions);
            RebuildVanillaList();
        }
        catch (Exception ex)
        {
            AppLog.Warn("[modpack] 加载器版本列表拉取失败：" + ex.Message);
            _loaderLoading = false;
            _loaderPick = null;
            RebuildVanillaList();
        }
    }

    private void AddLoaderRow(string name, string kind, string mc)
    {
        var expanded = _loaderPick is { } lp && lp.Kind == kind && lp.Mc == mc;
        bool selHere = _loaderSel is { } s && s.Kind == kind;
        string status = expanded ? "▾" : selHere ? "✓" : S.Get("dl.canAdd");
        Action? onTap = () => _ = LoadLoaderPickAsync(kind, mc);
        string icon = kind switch
        {
            "forge" => "forge.png",
            "neoforge" => "NeoForge.png",
            "fabric" => "Fabric.png",
            _ => "OptiFine.png",
        };
        if (!expanded && !selHere && _loaderProbe.TryGetValue(mc, out var d) && d.TryGetValue(kind, out var st))
        {
            if (!st.Done) { status = S.Get("dl.loader.checking"); onTap = null; }
            else if (st.List is { } list && list.Count == 0)
            {
                status = S.Get("dl.loader.na");
                onTap = null;
                var row0 = BuildDetailRow(name, status, null, icon);
                row0.Opacity = 0.55;
                CardList.Children.Add(row0);
                return;
            }
        }
        CardList.Children.Add(BuildDetailRow(name, status, onTap, icon));
        if (expanded) AppendLoaderVersions();
    }

    private void AppendLoaderVersions()
    {
        var rows = new List<Control>();
        if (_loaderLoading || _loaderVersions.Count == 0)
        {
            rows.Add(new TextBlock
            {
                Text = _loaderLoading ? S.Get("dl.loading") : S.Get("dl.loader.none"),
                FontSize = 12.5, Margin = new Thickness(18, 6, 0, 4),
                Foreground = GetRes("OnSurfaceVarBrush"),
            });
        }
        else
        {
            var (kind, mc) = _loaderPick!.Value;
            foreach (var e in _loaderVersions)
                rows.Add(BuildLoaderVersionRow(kind, mc, e));
        }

        foreach (var r in rows)
        {
            r.Opacity = 0;
            r.RenderTransform = TransformOperations.Parse("translateY(-12px)");
            CardList.Children.Add(r);
        }
        _ = AnimateDropdownIn(rows);
    }

    private Border BuildLoaderVersionRow(string kind, string mc, McLoaderEntry e)
    {

        var row = new Border { Classes = { "card", "mpCard" }, Padding = new Thickness(14, 15), Margin = new Thickness(16, 0, 4, 8) };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto") };
        var img = new Image
        {
            Source = VanillaCatIcon(kind switch
            {
                "forge" => "forge.png",
                "neoforge" => "NeoForge.png",
                "fabric" => "Fabric.png",
                _ => "OptiFine.png",
            }),
            Width = 26, Height = 26,
        };
        Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(img, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        var iconHost = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(6),
            ClipToBounds = true, Child = img,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        g.Children.Add(iconHost);
        var namePanel = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        namePanel.Children.Add(new TextBlock
        {
            Text = e.Version, FontSize = 13.5, FontWeight = FontWeight.SemiBold,
            Foreground = GetRes("OnSurfaceBrush"),
        });

        namePanel.Children.Add(new TextBlock
        {
            Text = S.Get(e.Stable ? "dl.stable" : "dl.beta"),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = e.Stable ? GetRes("OnSurfaceVarBrush") : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E0700A")),
        });
        Grid.SetColumn(namePanel, 1);
        g.Children.Add(namePanel);
        if (e.Date.Length > 0)
        {
            var d = new TextBlock
            {
                Text = e.Date, FontSize = 11.5, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = GetRes("OnSurfaceVarBrush"),
            };
            Grid.SetColumn(d, 3);
            g.Children.Add(d);
        }
        var tag = new TextBlock
        {
            Text = S.Get("dl.canAdd"), FontSize = 11.5, Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = GetRes("OnSurfaceVarBrush"),
        };
        Grid.SetColumn(tag, 4);
        g.Children.Add(tag);
        row.Child = g;
        var picked = e.Version;

        row.Tapped += (_, _) =>
        {
            _loaderSel = (kind, picked, e.Tag);
            _selVanilla = false;
            _loaderPick = null;
            if (kind == "fabric")
            {
                _fabricApiOn = true;
                _fabricApiSelVer = null;
                _fabricApiExpand = false;

                if (_fabricApiCache.TryGetValue(mc, out var fc))
                {
                    var stable = fc.FirstOrDefault(x => x.Stable);
                    if (stable != null) _fabricApiSelVer = stable.Version;
                    else if (fc.Count > 0) _fabricApiSelVer = fc[0].Version;
                    else _fabricApiOn = false;
                }
                else _ = EnsureFabricApiListAsync(mc);
            }
            RebuildVanillaList();
        };
        return row;
    }

    private static async Task AnimateDropdownIn(List<Control> rows)
    {
        var step = rows.Count <= 12 ? 35 : Math.Max(8, 420 / rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (i > 0) await Task.Delay(step);
            Dispatcher.UIThread.Post(() =>
            {
                r.Transitions = new Transitions
                {
                    new Avalonia.Animation.DoubleTransition { Property = Avalonia.Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(200), Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
                    new Avalonia.Animation.TransformOperationsTransition { Property = Avalonia.Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(200), Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
                };
                r.Opacity = 1;
                r.RenderTransform = TransformOperations.Parse("translateY(0px)");
            }, DispatcherPriority.Background);
        }
    }

    private void InstallLoaderFromPick(string kind, string mc, string version, string? customName = null,
        string? fabricApiFile = null, string[]? fabricApiUrls = null, string? tag = null)
    {
        if (McInstaller.IsVersionRunning(mc)) { ShowVanillaStatus(S.Get("dl.task.already"), false); return; }
        var dir = McInstaller.ResolveInstallDir();
        try { System.IO.Directory.CreateDirectory(dir); } catch { }
        AppLog.Info($"[mcinstall] 用户选择 {mc} + {LoaderName(kind)} {version}" +
                    (fabricApiFile != null ? $"（Fabric API：{fabricApiFile}）" : "") + $"，目标目录 {dir}");
        if (kind == "forge") McInstaller.InstallForgeLike(mc, version, false, dir, customName);
        else if (kind == "neoforge") McInstaller.InstallForgeLike(mc, version, true, dir, customName);
        else if (kind == "optifine") McInstaller.InstallOptiFine(mc, version, tag ?? "", dir, customName);
        else McInstaller.InstallFabric(mc, version, dir, customName, fabricApiFile, fabricApiUrls);
        BackToVanillaList();
    }

    private static readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap?> _catIconCache = new();

    private static Avalonia.Media.Imaging.Bitmap? VanillaCatIcon(string file)
    {
        if (_catIconCache.TryGetValue(file, out var b)) return b;
        try { using var s = Avalonia.Platform.AssetLoader.Open(new Uri($"avares://StarryLauncher/Assets/{file}")); b = new Avalonia.Media.Imaging.Bitmap(s); }
        catch { b = null; }
        _catIconCache[file] = b;
        return b;
    }

    private static string VanillaCatIconFile(McVersionEntry v)
    {
        bool af = McInstaller.IsAprilFools(v);
        return af ? "yrj.png"
             : McInstaller.IsLegacy(v) ? "yg.png"
             : v.Type == "snapshot" ? "kz.png"
             : "zsb.png";
    }

    private Border BuildVanillaRow(McVersionEntry v)
    {

        var card = new Border
        {
            Classes = { "card", "mpCard" },
            Padding = new Thickness(14, 6),
        };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var img = new Image
        {
            Source = VanillaCatIcon(VanillaCatIconFile(v)),
            Width = 44, Height = 44,
        };
        Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(img, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        var iconHost = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(9),
            ClipToBounds = true, Child = img,
        };
        g.Children.Add(iconHost);

        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = v.Id, FontSize = 13, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = GetRes("OnSurfaceBrush"),
        });
        var meta = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 2, 0, 0),
        };
        bool af = McInstaller.IsAprilFools(v);
        bool legacy = !af && McInstaller.IsLegacy(v);
        bool snap = !af && !legacy && v.Type == "snapshot";
        if (af || legacy || snap)
        {
            meta.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 2),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.Parse(af ? "#E11D48" : snap ? "#E0700A" : "#8B5CF6")),
                Child = new TextBlock { Text = S.Get(af ? "dl.cat.af" : snap ? "dl.cat.snap" : "dl.cat.old"), FontSize = 10.5, Foreground = Brushes.White },
            });
        }
        meta.Children.Add(new TextBlock
        {
            Text = v.ReleaseTime.Length >= 10 ? v.ReleaseTime[..10] : v.ReleaseTime,
            FontSize = 11.5,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = GetRes("OnSurfaceVarBrush"),
        });
        text.Children.Add(meta);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);

        card.Child = g;
        card.Tapped += (_, _) => OpenVanillaDetail(v);
        return card;
    }

    private void HookServiceEvents(bool attach)
    {
        _svc.PicksChanged -= OnPicksChanged;
        _svc.LoadFailed -= OnLoadFailed;
        _svc.LoadingChanged -= OnLoadingChanged;
        if (attach)
        {
            _svc.PicksChanged += OnPicksChanged;
            _svc.LoadFailed += OnLoadFailed;
            _svc.LoadingChanged += OnLoadingChanged;
            OnLoadingChanged();
        }
    }

    private void OnLoadingChanged()
    {
        if (_inVanilla) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_svc.IsLoading)
            {
                LoadBar.IsVisible = true;
                LoadBar.IsIndeterminate = false;
                Dispatcher.UIThread.Post(() => LoadBar.IsIndeterminate = true, DispatcherPriority.Background);
            }
            else
            {
                LoadBar.IsVisible = false;
                LoadBar.IsIndeterminate = false;
            }
        });
    }

    private void OnThemePaletteChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!LoadBar.IsVisible) return;
            LoadBar.IsIndeterminate = false;
            Dispatcher.UIThread.Post(() => LoadBar.IsIndeterminate = true, DispatcherPriority.Background);
        });
    }

    private bool _suppressSearch;
    private DispatcherTimer? _searchTimer;

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressSearch) return;

        _searchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _searchTimer.Tick -= OnSearchDebounced;
        _searchTimer.Tick += OnSearchDebounced;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async void OnSearchDebounced(object? sender, EventArgs e)
    {
        _searchTimer!.Stop();
        if (_inVanilla)
        {
            if (_vanillaDetail is not null || _loaderPick is not null) return;
            RebuildVanillaList();
            return;
        }

        if (_resDetail is not null) { RebuildResourceDetail(); return; }

        CardList.Children.Clear();
        _rendered = 0;
        try { await _svc.SetSearchAsync(SearchBox.Text ?? ""); } catch {   }
    }

    private async void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        if (_resDetail is not null) { await LoadResVersionsAsync(); return; }
        if (_inVanilla) { await LoadVanillaVersionsAsync(); return; }
        try { await _svc.LoadFirstPageAsync(); } catch {   }
    }

    private async void OnListScroll(object? sender, ScrollChangedEventArgs e)
    {
        if (_inVanilla) return;

        double bottomGap = ListScroll.Extent.Height - ListScroll.Offset.Y - ListScroll.Viewport.Height;
        if (bottomGap > 60) return;

        for (var i = 0; i < 3; i++)
        {
            var before = _svc.CurrentItems().Count;
            try { await _svc.LoadMoreAsync(); } catch {   }
            var after = _svc.CurrentItems().Count;
            if (after > before || after == 0 || _svc.EndReached) return;
            await Task.Delay(1500);
        }
    }

    private void OnPicksChanged()
    {
        if (_inVanilla || _resDetail is not null) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(OnPicksChanged); return; }
        var items = _svc.CurrentItems();

        if (_rendered > 0 && items.Count > _rendered && StatusCard.IsVisible == false)
        {
            for (int i = _rendered; i < items.Count; i++)
                CardList.Children.Add(BuildCard(items[i]));
            _rendered = items.Count;
            PatchIcons();
            return;
        }

        if (_rendered > 0 && items.Count == _rendered && StatusCard.IsVisible == false)
        {
            PatchIcons();
            return;
        }
        RenderAll();
    }

    private void PatchIcons()
    {
        var items = _svc.CurrentItems();
        for (int i = 0; i < CardList.Children.Count && i < items.Count; i++)
        {
            if (CardList.Children[i] is not Border card || card.Child is not Grid g || g.Children.Count == 0) continue;
            if (g.Children[0] is not Border iconHost) continue;

            if (iconHost.Child is not Border avatar || avatar.Child is not TextBlock) continue;
            var bytes = _svc.GetIconBytes(items[i].IconKey);
            if (bytes is null) continue;
            try
            {
                iconHost.Child = new Image { Width = 64, Height = 64, Source = new Bitmap(new MemoryStream(bytes)) };
            }
            catch {   }
        }
    }

    private void OnLoadFailed()
    {
        if (_inVanilla) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => ShowError()); return; }
        ShowError();
    }

    private void RenderAll()
    {
        ShowError(false);
        CardList.Children.Clear();
        var items = _svc.CurrentItems();
        foreach (var m in items)
            CardList.Children.Add(BuildCard(m));
        _rendered = items.Count;
        ConsumePageFx();
    }

    private void ShowError(bool show = true)
    {
        StatusCard.IsVisible = show;
        if (show)
        {
            StatusText.Text = S.Get("modpack.error");
            RetryBtn.IsVisible = true;
            CardList.Children.Clear();
            _rendered = 0;
            ConsumePageFx();
        }
    }

    private Border BuildCard(ModpackInfo m)
    {
        var card = new Border
        {
            Classes = { "card", "mpCard" },
            Padding = new Thickness(14, 10),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

        object iconContent;
        var iconBytes = _svc.GetIconBytes(m.IconKey);
        if (iconBytes is not null)
        {
            try { iconContent = new Image { Width = 64, Height = 64, Source = new Bitmap(new MemoryStream(iconBytes)) }; }
            catch { iconContent = LetterAvatar(m); }
        }
        else iconContent = LetterAvatar(m);

        var iconHost = new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(12),
            ClipToBounds = true, Child = (Control)iconContent,
        };
        Grid.SetColumn(iconHost, 0);
        grid.Children.Add(iconHost);

        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };

        text.Children.Add(new TextBlock
        {
            Text = m.Title, FontSize = 14, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = GetRes("OnSurfaceBrush"),
        });

        text.Children.Add(new TextBlock
        {
            Text = m.Description, FontSize = 12, Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap,
            Foreground = GetRes("OnSurfaceVarBrush"),
        });

        text.Children.Add(new TextBlock
        {
            FontSize = 11, Margin = new Thickness(0, 4, 0, 0),
            Text = $"↓ {ModpackService.FormatDownloads(m.Downloads)} · {LocalizeTag(m.Tag)}{(string.IsNullOrEmpty(m.Author) ? "" : " · " + m.Author)}",
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = GetRes("OnSurfaceVarBrush"),
        });

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        card.Child = grid;

        card.Tapped += (_, _) => OpenResourceDetail(m);
        return card;
    }

    private static string LocalizeTag(string slug)
    {
        var hit = Array.Find(ModpackService.Categories, c => c.Slug == slug);
        return hit.Slug is null ? slug : S.Get("modpack.cat." + hit.Key);
    }

    private static Border LetterAvatar(ModpackInfo m)
    {
        var ch = string.IsNullOrEmpty(m.Title) ? "?" : m.Title.Trim()[0].ToString();
        return new Border
        {
            Background = GetRes("PrimaryBrush"),
            Child = new TextBlock
            {
                Text = ch, FontSize = 26, FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
        };
    }

    private static IBrush GetRes(string key)
        => (IBrush)(App.Current!.Resources[key] ?? Brushes.Gray);
}
