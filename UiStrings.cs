using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace StarryLauncher;

public enum UiLanguage { ZhCN = 0, ZhTW = 1, En = 2 }

public static class S
{
    public static UiLanguage Current { get; private set; } = UiLanguage.ZhCN;

    public static event Action? Changed;

    public static void Init(UiLanguage lang) => Current = lang;

    public static void Set(UiLanguage lang)
    {
        if (Current == lang) return;
        Current = lang;
        Changed?.Invoke();
    }

    public static UiLanguage Parse(string? value) => value switch
    {
        "zh-TW" => UiLanguage.ZhTW,
        "en" => UiLanguage.En,
        _ => UiLanguage.ZhCN,
    };

    public static string ToSettingValue(UiLanguage lang) => lang switch
    {
        UiLanguage.ZhTW => "zh-TW",
        UiLanguage.En => "en",
        _ => "zh-CN",
    };

    public static string Get(string key)
    {
        string zh = Zh.TryGetValue(key, out var v) ? v : key;
        return Current switch
        {
            UiLanguage.ZhCN => zh,
            UiLanguage.ZhTW => ToTraditional(zh),
            _ => En.TryGetValue(key, out var e) ? e : zh,
        };
    }

    public static string F(string key, params object?[] args)
    {
        var fmt = Get(key);
        try { return args is { Length: > 0 } ? string.Format(fmt, args) : fmt; }
        catch
        {

            string s = fmt;
            for (int i = 0; i < args.Length; i++)
                s = s.Replace("{" + i + "}", args[i]?.ToString() ?? "");
            return s;
        }
    }

    public static string MaybeConvert(string content) =>

        Current == UiLanguage.ZhTW ? ToTraditional(content, wordFix: false) : content;

    private const uint LCMAP_TRADITIONAL_CHINESE = 0x04000000;
    private static readonly ConcurrentDictionary<string, string> _twCache = new();

    private static readonly ConcurrentDictionary<string, string> _twCacheNoFix = new();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int LCMapStringEx(string lpLocaleName, uint dwMapFlags, string lpSrcStr,
        int cchSrc, IntPtr lpDestStr, int cchDest, IntPtr lpVersionInformation, IntPtr lpReserved, IntPtr sortHandle);

    private static readonly (string From, string To)[] TwWordFix =
    [
        ("復制", "複製"),
        ("游戲", "遊戲"),
        ("日志", "日誌"),
        ("里", "裡"),
        ("后", "後"),
    ];

    private const int TwCacheMaxLength = 256;

    public static string ToTraditional(string s, bool wordFix = true)
    {
        if (string.IsNullOrEmpty(s)) return s;

        if (s.Length > TwCacheMaxLength) return Convert(s, wordFix);

        return wordFix
            ? _twCache.GetOrAdd(s, static src => Convert(src, true))
            : _twCacheNoFix.GetOrAdd(s, static src => Convert(src, false));
    }

    private static string Convert(string s, bool wordFix)
    {
        string r = MapTraditional(s);
        if (wordFix)
            foreach (var (from, to) in TwWordFix) r = r.Replace(from, to);
        return r;
    }

    private static string MapTraditional(string src)
    {
        try
        {
            int len = LCMapStringEx("zh-CN", LCMAP_TRADITIONAL_CHINESE, src, src.Length, IntPtr.Zero, 0,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (len <= 0) return src;

            IntPtr buf = Marshal.AllocHGlobal((len + 2) * 2);
            try
            {
                for (int i = 0; i < (len + 2) * 2; i++) Marshal.WriteByte(buf, i, 0);
                int written = LCMapStringEx("zh-CN", LCMAP_TRADITIONAL_CHINESE, src, src.Length, buf, len,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (written <= 0) return src;
                return Marshal.PtrToStringUni(buf) ?? src;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return src; }
    }

    private static readonly Dictionary<string, string> Zh = new()
    {

        ["nav.settings"] = "设置",
        ["nav.modpackTip"] = "资源下载",
        ["moddl.tab.modpack"] = "整合包",
        ["moddl.tab.mods"] = "模组",
        ["moddl.tab.shader"] = "光影包",
        ["moddl.tab.resource"] = "资源包",
        ["moddl.tab.datapack"] = "数据包",
        ["moddl.tab.map"] = "地图",
        ["moddl.tab.vanilla"] = "游戏版本",


        ["nav.launcherTip"] = "启动器",
        ["launcher.title"] = "启动器",
        ["launcher.loginType.microsoft"] = "微软账号",
        ["launcher.login"] = "登录",
        ["launcher.logout"] = "退出登录",
        ["launcher.ms.start"] = "微软登录",
        ["launcher.ms.openpage"] = "打开登录页",
        ["launcher.ms.oauthHint"] = "浏览器已自动打开微软官方登录页面，请在浏览器中完成登录，完成后会自动返回启动器",
        ["launcher.ms.waiting"] = "等待浏览器完成登录…（最多 10 分钟）",
        ["launcher.ms.refreshing"] = "正在刷新微软登录状态…",
        ["launcher.ms.winTitle"] = "微软账号登录",
        ["launcher.ms.cancel"] = "取消",
        ["launcher.ms.canceled"] = "已取消微软登录",
        ["launcher.role.title"] = "选择角色",
        ["launcher.role.hint"] = "此账号有多个 Minecraft 角色，请选择要使用的角色。",
        ["launcher.role.canceled"] = "已取消角色选择",
        ["launcher.role.none"] = "该账号还没有 Minecraft 角色",
        ["launcher.account.switch"] = "切换账号",
        ["launcher.account.hint"] = "点账号直接切换；添加新账号会回到登录表单。",
        ["launcher.account.add"] = "添加新账号",
        ["launcher.account.current"] = "当前",
        ["launcher.account.delete"] = "删除",
        ["launcher.account.empty"] = "还没有记住任何账号",
        ["launcher.status.notlogin"] = "未登录",
        ["launcher.status.launching"] = "正在启动游戏…",
        ["launcher.login.ok"] = "登录成功",
        ["launcher.login.failed"] = "登录失败",
        ["launcher.logout.ok"] = "已退出登录",
        ["launcher.play"] = "启动游戏",
        ["launcher.stop"] = "关闭游戏",
        ["launcher.noinstance"] = "未选择实例",
        ["launcher.instanceCard"] = "实例管理",
        ["launcher.instance.refresh"] = "刷新",
        ["launcher.instance.refreshed"] = "实例列表已刷新",

        ["dl.title"] = "下载游戏",
        ["dl.pickTitle"] = "安装新版本",
        ["dl.cat.all"] = "全部",
        ["dl.cat.rel"] = "正式版",
        ["dl.cat.snap"] = "快照",
        ["dl.cat.old"] = "远古版本",
        ["dl.cat.af"] = "愚人节版",
        ["modpack.f.dlsrc"] = "下载源",
        ["dl.src.bmclapi"] = "社区源（推荐）",
        ["dl.src.official"] = "官方源",
        ["dl.task.mgr"] = "任务管理",
        ["dl.task.summary"] = "{0} 个任务 · 平均 {1:F0}% · {2:F1} MB/s",
        ["dl.task.none"] = "全部任务已结束",
        ["dl.task.empty"] = "暂无下载任务",
        ["dl.task.hint"] = "支持多版本同时下载安装；点击右上角下载横幅随时回到这里。",
        ["dl.task.clickHint"] = "多任务进行中 — 点击查看任务详情",
        ["dl.task.already"] = "该版本已在下载队列中",
        ["dl.nojava"] = "未找到可用的 Java（javaw.exe），无法运行加载器安装器",
        ["dl.installerFail"] = "加载器安装器运行失败",
        ["dl.vanillaRow"] = "仅原版（不加 Mod 加载器）",
        ["dl.canAdd"] = "可以添加",
        ["dl.unsupported"] = "暂不支持",
        ["dl.back"] = "返回",
        ["dl.loader.none"] = "该版本没有可用的加载器",
        ["dl.loader.na"] = "不可添加",
        ["dl.start"] = "开始下载",
        ["dl.stable"] = "稳定版",
        ["dl.beta"] = "测试版",
        ["dl.loader.checking"] = "检查中",
        ["dl.search"] = "搜索版本号…",
        ["dl.loading"] = "正在获取版本列表…",
        ["dl.empty"] = "没有匹配的版本",
        ["dl.listFail"] = "版本列表拉取失败，请检查网络",
        ["dl.installing"] = "正在下载 {0}",
        ["dl.done"] = "下载完成",
        ["dl.failed"] = "下载失败",
        ["dl.cancel"] = "取消下载",
        ["dl.cancelled"] = "已取消",
        ["dl.err.dns"] = "无法连接下载源（域名解析失败）。请检查网络后再试，或切换下载源",
        ["dl.err.net"] = "网络请求失败。你的网络环境不佳，请稍后再试，或使用 VPN 改善网络环境",
        ["dl.err.timeout"] = "连接超时。请稍后再试，或在下载源里换个源",
        ["dl.err.io"] = "文件写入失败。请检查磁盘剩余空间和游戏目录权限",
        ["dl.err.generic"] = "下载失败",
        ["dl.detail"] = "详细信息",
        ["dl.detail.hide"] = "收起详细信息",
        ["dl.task.remove"] = "删除",
        ["dl.stage.meta"] = "获取版本信息",
        ["dl.stage.jar"] = "下载游戏本体",
        ["dl.stage.lib"] = "下载依赖库",
        ["dl.stage.assetsidx"] = "下载资源索引",
        ["dl.stage.assets"] = "下载游戏资源",
        ["dl.stage.loader"] = "下载加载器",
        ["dl.stage.fabricapi"] = "下载 Fabric API",
        ["dl.stage.optifine"] = "下载 OptiFine",
        ["dl.fapi.desc"] = "Fabric 官方前置，大量 Mod 依赖 · 可选",
        ["dl.fapi.on"] = "已选",
        ["dl.fapi.off"] = "可选",
        ["dl.fapi.latest"] = "最新",
        ["dl.fapi.none"] = "不安装",
        ["dl.fapi.pub"] = "发布于",
        ["dl.fapi.fail"] = "Fabric API 版本列表获取失败，请检查网络后重试",
        ["dl.stage.res"] = "下载资源文件",
        ["dl.stage.resmap"] = "解压地图",
        ["dl.res.root"] = "游戏根目录",
        ["dl.res.alpha"] = "测试版",
        ["dl.res.vanilla"] = "原版",
        ["dl.res.pickTarget"] = "安装到…",
        ["dl.res.auto"] = "自动选择（推荐）",
        ["dl.res.none"] = "该资源没有可用版本",
        ["dl.res.noresult"] = "没有找到你需要的内容",
        ["dl.res.servertag"] = "服务端包",
        ["dl.res.grother"] = "其他",
        ["dl.res.serverno"] = "服务端整合包无法安装为游戏实例，请选择客户端文件",
        ["dl.res.loadFail"] = "版本列表获取失败，请检查网络后重试",
        ["dl.res.nodir"] = "没有可用的安装位置，请先在设置里配置游戏目录",
        ["dl.stage.packzip"] = "下载整合包",
        ["dl.stage.packcore"] = "安装游戏核心",
        ["dl.stage.packover"] = "复制整合包文件",
        ["dl.stage.packfiles"] = "下载整合包内容",
        ["dl.pack.unsupported"] = "不支持该整合包格式（暂只支持 CurseForge / Modrinth 整合包）",
        ["dl.pack.notfound"] = "整合包压缩包损坏：找不到清单文件",
        ["dl.pack.server"] = "这是服务端整合包（含启动脚本），无法安装为游戏实例，请选择不含 Server 的客户端文件",
        ["dl.pack.quilt"] = "暂不支持 Quilt 整合包",

        ["dl.packchoice.title"] = "选择下载方式",
        ["dl.packchoice.hint"] = "整合包有两种用法，选一种继续",
        ["dl.packchoice.install"] = "直接安装",
        ["dl.packchoice.install.desc"] = "下载整合包并安装到游戏目录，装完就能玩",
        ["dl.packchoice.export"] = "导出 mrpack 文件",
        ["dl.packchoice.export.desc"] = "导出可分享的 .mrpack，拖给其他启动器即可安装",
        ["dl.packchoice.cancel"] = "取消",
        ["dl.pack.exporting"] = "正在导出 mrpack",
        ["dl.pack.exportok"] = "已导出 mrpack：",
        ["dl.pack.exportfail"] = "导出 mrpack 失败：",
        ["modpack.openweb"] = "在浏览器打开",
        ["modpack.updated"] = "更新于",
        ["launcher.game.started"] = "游戏已启动",
        ["launcher.game.exited"] = "游戏已退出",
        ["launcher.game.exitedCrash"] = "游戏异常退出（可能已崩溃）",
        ["launcher.game.crashLog"] = "检测到游戏崩溃报告，详见游戏日志",
        ["launcher.game.stopping"] = "正在关闭游戏…",
        ["launcher.game.closedByUser"] = "游戏已关闭",
        ["launcher.game.notRunning"] = "当前没有正在运行的游戏",
        ["launcher.game.running"] = "有游戏正在运行，请先关闭再启动新的",
        ["launcher.game.reattach"] = "检测到正在运行的游戏，已接管状态跟踪",
        ["launcher.game.foreground"] = "游戏窗口已置前",
        ["launcher.game.noinstance"] = "请先在右侧选择一个游戏实例",
        ["launcher.game.nojava"] = "未找到 Java（javaw.exe），可把 Java 路径告知我后续加设置项",
        ["launcher.game.noaccount"] = "请先登录账号",
        ["launcher.download.premiumRequired"] = "请先登录微软正版账号，登录后才能下载游戏和模组",
        ["launcher.game.noDir"] = "未找到 .minecraft 目录，点右侧文件夹图标选择",
        ["launcher.game.failed"] = "启动失败，详见日志",
        ["launcher.game.dir"] = "游戏目录",
        ["launcher.game.noinherit"] = "暂不支持 Forge/Fabric 等整合实例（需要版本继承合并），先选原版实例",
        ["launcher.game.javaMismatch"] = "本机没找到该实例需要的 Java 版本，请安装对应 JDK（要求见启动日志）",
        ["launcher.chooseDir"] = "选择 .minecraft 文件夹",
        ["launcher.err.emptyField"] = "请填写用户名和密码",
        ["launcher.err.wrongPass"] = "用户名或密码错误",
        ["launcher.err.authFailed"] = "认证失败，请稍后再试",
        ["launcher.err.msaRequest"] = "微软登录请求失败，请检查网络",
        ["launcher.err.msaDenied"] = "你取消了微软登录授权",
        ["launcher.err.msaExpired"] = "微软登录已超时，请重新登录",
        ["launcher.err.msaAuth"] = "微软授权失败，请重试",
        ["launcher.err.noXbox"] = "该微软账号没有 Xbox/Minecraft 关联",
        ["launcher.err.noMc"] = "该账号没有购买 Minecraft Java 版",

        ["modpack.title"] = "整合包",
        ["modpack.cat.all"] = "全部",
        ["modpack.f.search"] = "搜索",
        ["modpack.f.source"] = "来源",
        ["modpack.f.version"] = "版本",
        ["modpack.f.loader"] = "加载器",
        ["modpack.f.type"] = "类别",
        ["modpack.f.all"] = "全部",
        ["modpack.f.allType"] = "全部",
        ["modpack.f.allSrc"] = "全部",
        ["modpack.cat.adventure"] = "冒险",
        ["modpack.cat.challenge"] = "挑战",
        ["modpack.cat.combat"] = "战斗",
        ["modpack.cat.kitchenSink"] = "厨房水槽",
        ["modpack.cat.lightweight"] = "轻量化",
        ["modpack.cat.magic"] = "魔法",
        ["modpack.cat.multiplayer"] = "多人游戏",
        ["modpack.cat.optimization"] = "优化",
        ["modpack.cat.quests"] = "任务",
        ["modpack.cat.technology"] = "技术",
        ["modpack.cat.utility"] = "实用",
        ["modpack.cat.library"] = "前置库",
        ["modpack.cat.worldgen"] = "世界生成",
        ["modpack.cat.economy"] = "经济",
        ["modpack.cat.equipment"] = "装备",
        ["modpack.cat.food"] = "食物",
        ["modpack.cat.storage"] = "存储",
        ["modpack.cat.transport"] = "运输",
        ["modpack.cat.gamemech"] = "游戏机制",
        ["modpack.cat.social"] = "社交",
        ["modpack.cat.cartoon"] = "卡通",
        ["modpack.cat.realistic"] = "写实",
        ["modpack.cat.semireal"] = "半写实",
        ["modpack.cat.vanillalike"] = "原版风",
        ["modpack.error"] = "加载失败，请检查网络",
        ["modpack.retry"] = "重试",
        ["modpack.searchPh"] = "搜索整合包…",
        ["modpack.searchPh.modpack"] = "搜索整合包…",
        ["modpack.searchPh.mods"] = "搜索模组…",
        ["modpack.searchPh.shader"] = "搜索光影包…",
        ["modpack.searchPh.resource"] = "搜索资源包…",
        ["modpack.searchPh.datapack"] = "搜索数据包…",
        ["modpack.searchPh.map"] = "搜索地图…",
        ["modpack.searchPh.vanilla"] = "搜索版本号…",

        ["log.title"] = "运行日志",
        ["btn.copyLog"] = "复制日志",
        ["btn.copied"] = "已复制",

        ["st.idle"] = "空闲",

        ["settings.title"] = "设置",
        ["settings.subtitle"] = "配置 Starry Launcher 的运行行为",
        ["settings.system"] = "系统",
        ["settings.language"] = "选择语言",
        ["btn.openLogs"] = "打开日志",
        ["btn.redownload"] = "重新下载",
        ["settings.general"] = "常规",
        ["settings.subtitleGeneric"] = "常规设置与组件维护。",
        ["settings.trayTitle"] = "最小化托盘图标",
        ["settings.trayDesc"] = "",
        ["settings.uiLanguage"] = "页面语言",
        ["settings.uiLanguageHint"] = "",
        ["settings.font"] = "界面字体",
        ["settings.fontDefault"] = "默认字体",
        ["settings.fontHint"] = "有些字体仅支持英文，suk并不能把这些字体强制套用在中文上",
        ["settings.theme"] = "主题颜色",
        ["settings.themeDark"] = "深色",
        ["settings.themeLight"] = "浅色",
        ["settings.themeSystem"] = "跟随系统",
        ["lang.zhCN"] = "简体中文",
        ["lang.zhTW"] = "繁體中文",
        ["lang.en"] = "English",
        ["tray.on"] = "最小化到托盘：开启",
        ["tray.off"] = "最小化到托盘：关闭",
        ["tray.open"] = "打开",
        ["tray.exit"] = "退出",

        ["set.tab.appearance"] = "外观",
        ["set.tab.general"] = "常规",
        ["set.tab.storage"] = "存储位置",
        ["settings.mcPath"] = "导入Minecraft路径",
        ["settings.mcPathHint"] = "选任意文件夹作为根目录，游戏会自动下载到其中的 .minecraft；下载源可在「资源下载 → 游戏版本」页切换（社区源/官方源）。",

        ["set.path.current"] = "默认安装位置",
        ["set.path.dropHint"] = "点击选择默认安装位置；下拉里可删除已添加的路径",
        ["set.path.add"] = "添加路径",
        ["set.path.noPaths"] = "暂无路径，请添加或等待自动探测",
        ["set.path.deleted"] = "已删除并屏蔽该路径（自动探测不再添加）",
        ["set.path.autoDetect"] = "自动探测路径",
        ["set.path.autoDetected"] = "自动探测完成，新增 {0} 个路径",
        ["set.path.labelFixed"] = "添加或删除 Minecraft 文件夹",
        ["set.path.scanStarting"] = "正在准备扫描…",
        ["set.path.scanning"] = "正在扫描文件夹 {0}/{1} …",
        ["set.path.scanDone"] = "扫描完成，新增 {0} 个路径",
        ["set.path.scanFail"] = "扫描失败",
        ["set.path.rename"] = "重命名（仅软件显示名，不改文件夹）",
        ["set.path.renameTitle"] = "重命名游戏路径",
        ["set.path.renameHint"] = "只改软件里显示的名字，磁盘上的文件夹名不变",
        ["launcher.pathPick"] = "选择 Minecraft 文件夹",
        ["launcher.pathPickEmpty"] = "未发现 Minecraft 路径，请先在设置 → 存储位置添加",

        ["set.adv.dlUp"] = "下载 {0} Mbps · 上传 {1} Mbps",
        ["set.identity.hint"] = "点击头像或昵称修改",
        ["set.identity.saved"] = "头像已更新",
        ["set.name.title"] = "修改昵称",
        ["set.name.hint"] = "最多 4 个字",
        ["set.bg.page"] = "页面背景",
        ["set.bg.chat"] = "聊天背景",
        ["set.bg.pick"] = "选择图片",
        ["set.bg.clear"] = "清除",
        ["set.bg.blur"] = "模糊",
        ["set.bg.visibility"] = "可见度",
        ["set.bg.components"] = "组件不透明度",
        ["dlg.pickPageBg"] = "选择页面背景图片",
        ["set.identity.mcface"] = "minecraft头像",
        ["set.identity.mcfaceFail"] = "获取失败",
        ["greet.morning"] = "早上好",
        ["greet.forenoon"] = "上午好",
        ["greet.afternoon"] = "下午好",
        ["greet.evening"] = "晚上好",
        ["set.accent.title"] = "控件颜色",
        ["set.accent.gradient"] = "渐变色",
        ["set.accent.hint"] = "按钮图标满色跟随，导航胶囊、滑块、小球等透明组件呈同色浅淡效果",
        ["set.accent.custom"] = "自定义",
        ["set.accent.new"] = "新的",
        ["set.accent.current"] = "当前",


        ["crash.title"] = "Starry Launcher",
        ["crash.msg"] = "Starry Launcher 异常退出：",
        ["crash.hint"] = "详情见 logs 目录。",


        ["hist.rename"] = "重命名",
        ["hist.renameTip"] = "输入新的对话标题，回车保存，Esc 取消",

        ["btn.skip"] = "跳过",


        ["avatar.cropTitle"] = "调整头像",
        ["avatar.cropHint"] = "拖动选位，滚轮缩放；圆框里就是最终头像效果。",
        ["btn.ok"] = "确定",
        ["btn.cancel"] = "取消",
        ["btn.close"] = "关闭",

        ["traymenu.show"] = "显示主界面",
        ["traymenu.exit"] = "退出 Starry Launcher",
        ["tray.balloonTitle"] = "Starry Launcher 还在后台运行",
        ["tray.balloonText"] = "双击托盘图标可重新打开窗口，右键可退出。",


        ["st.cancelling"] = "正在取消...",
        ["st.preparing"] = "正在准备...",
        ["st.retry"] = "失败，{0}/{1} 次重试中...",
        ["st.busyWait"] = "上一个操作还没结束，请稍候…",

        ["log.args"] = "启动参数: {0}",
        ["log.attempt"] = "第 {0} 次尝试（服务器端口 {1}）...",
        ["log.runDir"] = "使用可写运行目录: {0}",
        ["log.mcHint"] = "在游戏里「扫描局域网」或直接连接上面的地址",
        ["log.ready"] = "准备就绪",

    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["nav.settings"] = "Settings",
        ["nav.modpackTip"] = "Downloads",
        ["moddl.tab.modpack"] = "Modpacks",
        ["moddl.tab.mods"] = "Mods",
        ["moddl.tab.shader"] = "Shaders",
        ["moddl.tab.resource"] = "Resource Packs",
        ["moddl.tab.datapack"] = "Data Packs",
        ["moddl.tab.map"] = "Maps",
        ["moddl.tab.vanilla"] = "Game Versions",


        ["nav.launcherTip"] = "Launcher",
        ["launcher.title"] = "Launcher",
        ["launcher.loginType.microsoft"] = "Microsoft account",
        ["launcher.login"] = "Sign in",
        ["launcher.logout"] = "Sign out",
        ["launcher.ms.start"] = "Microsoft sign-in",
        ["launcher.ms.openpage"] = "Open sign-in page",
        ["launcher.ms.oauthHint"] = "Your browser has opened the official Microsoft sign-in page. Finish signing in there and the launcher will return automatically.",
        ["launcher.ms.waiting"] = "Waiting for browser sign-in… (up to 10 min)",
        ["launcher.ms.refreshing"] = "Refreshing Microsoft session…",
        ["launcher.ms.winTitle"] = "Microsoft account sign-in",
        ["launcher.ms.cancel"] = "Cancel",
        ["launcher.ms.canceled"] = "Microsoft sign-in cancelled",
        ["launcher.role.title"] = "Select Role",
        ["launcher.role.hint"] = "This account owns multiple Minecraft profiles. Choose one to use.",
        ["launcher.role.canceled"] = "Role selection cancelled",
        ["launcher.role.none"] = "This account has no Minecraft profile yet",
        ["launcher.account.switch"] = "Switch Account",
        ["launcher.account.hint"] = "Click an account to switch; Add New returns to the sign-in form.",
        ["launcher.account.add"] = "Add New Account",
        ["launcher.account.current"] = "current",
        ["launcher.account.delete"] = "Remove",
        ["launcher.account.empty"] = "No saved accounts yet",
        ["launcher.status.notlogin"] = "Not signed in",
        ["launcher.status.launching"] = "Launching…",
        ["launcher.login.ok"] = "Signed in",
        ["launcher.login.failed"] = "Sign-in failed",
        ["launcher.logout.ok"] = "Signed out",
        ["launcher.play"] = "Launch game",
        ["launcher.stop"] = "Close game",
        ["launcher.noinstance"] = "No instance selected",
        ["launcher.instanceCard"] = "Instances",
        ["launcher.instance.refresh"] = "Refresh",
        ["launcher.instance.refreshed"] = "Instances refreshed",

        ["dl.title"] = "Download Game",
        ["dl.pickTitle"] = "Install a New Version",
        ["dl.cat.all"] = "All",
        ["dl.cat.rel"] = "Releases",
        ["dl.cat.snap"] = "Snapshots",
        ["dl.cat.old"] = "Legacy",
        ["dl.cat.af"] = "April Fools",
        ["modpack.f.dlsrc"] = "Download Source",
        ["dl.src.bmclapi"] = "Community (Recommended)",
        ["dl.src.official"] = "Official",
        ["dl.task.mgr"] = "Task Manager",
        ["dl.task.summary"] = "{0} tasks · avg {1:F0}% · {2:F1} MB/s",
        ["dl.task.none"] = "All tasks finished",
        ["dl.task.empty"] = "No download tasks",
        ["dl.task.hint"] = "Multiple versions can download and install at once; click the banner anytime to come back here.",
        ["dl.task.clickHint"] = "Multiple tasks running — click for details",
        ["dl.task.already"] = "This version is already downloading",
        ["dl.nojava"] = "No usable Java (javaw.exe) found; cannot run the mod loader installer",
        ["dl.installerFail"] = "Mod loader installer failed",
        ["dl.vanillaRow"] = "Vanilla only (no mod loader)",
        ["dl.canAdd"] = "Available",
        ["dl.unsupported"] = "Not supported yet",
        ["dl.back"] = "Back",
        ["dl.loader.none"] = "No loader available for this version",
        ["dl.loader.na"] = "Not available",
        ["dl.start"] = "Download",
        ["dl.stable"] = "Stable",
        ["dl.beta"] = "Beta",
        ["dl.loader.checking"] = "Checking",
        ["dl.search"] = "Search versions…",
        ["dl.loading"] = "Fetching version list…",
        ["dl.empty"] = "No matching versions",
        ["dl.listFail"] = "Failed to fetch version list. Check your network.",
        ["dl.installing"] = "Downloading {0}",
        ["dl.done"] = "Download completed",
        ["dl.failed"] = "Download failed",
        ["dl.cancel"] = "Cancel",
        ["dl.cancelled"] = "Cancelled",
        ["dl.err.dns"] = "Cannot reach the download source (DNS resolution failed). Check your network or switch the download source",
        ["dl.err.net"] = "Network request failed. Your connection looks unstable — try again later or use a VPN",
        ["dl.err.timeout"] = "Connection timed out. Try again later or pick another download source",
        ["dl.err.io"] = "Failed to write file. Check free disk space and permissions of the game folder",
        ["dl.err.generic"] = "Download failed",
        ["dl.detail"] = "Details",
        ["dl.detail.hide"] = "Hide details",
        ["dl.task.remove"] = "Remove",
        ["dl.stage.meta"] = "Fetching version info",
        ["dl.stage.jar"] = "Downloading game",
        ["dl.stage.lib"] = "Downloading libraries",
        ["dl.stage.assetsidx"] = "Downloading asset index",
        ["dl.stage.assets"] = "Downloading game assets",
        ["dl.stage.loader"] = "Downloading loader",
        ["dl.stage.fabricapi"] = "Downloading Fabric API",
        ["dl.stage.optifine"] = "Downloading OptiFine",
        ["dl.fapi.desc"] = "Fabric's official API, required by many mods · optional",
        ["dl.fapi.on"] = "Selected",
        ["dl.fapi.off"] = "Optional",
        ["dl.fapi.latest"] = "Latest",
        ["dl.fapi.none"] = "Don't install",
        ["dl.fapi.pub"] = "Released",
        ["dl.fapi.fail"] = "Failed to fetch Fabric API version list, please check your network and retry",
        ["dl.stage.res"] = "Download resource",
        ["dl.stage.resmap"] = "Extract map",
        ["dl.res.root"] = "Game root",
        ["dl.res.alpha"] = "Beta",
        ["dl.res.vanilla"] = "Vanilla",
        ["dl.res.pickTarget"] = "Install to…",
        ["dl.res.auto"] = "Auto (recommended)",
        ["dl.res.none"] = "No available versions for this project",
        ["dl.res.noresult"] = "No results found",
        ["dl.res.servertag"] = "Server pack",
        ["dl.res.grother"] = "Other",
        ["dl.res.serverno"] = "Server-side modpacks cannot be installed as a game instance, pick the client file instead",
        ["dl.res.loadFail"] = "Failed to load version list, please check your network and retry",
        ["dl.res.nodir"] = "No install target found, configure a game directory in Settings first",
        ["dl.stage.packzip"] = "Download modpack",
        ["dl.stage.packcore"] = "Install game core",
        ["dl.stage.packover"] = "Copy modpack files",
        ["dl.stage.packfiles"] = "Download modpack content",
        ["dl.pack.unsupported"] = "Unsupported modpack format (CurseForge / Modrinth only for now)",
        ["dl.pack.notfound"] = "Modpack archive is broken: manifest not found",
        ["dl.pack.server"] = "This is a server-side modpack (contains launch scripts) and cannot be installed as a game instance, pick the client file without \"Server\" in its name",
        ["dl.pack.quilt"] = "Quilt modpacks are not supported yet",
        ["dl.packchoice.title"] = "Choose download mode",
        ["dl.packchoice.hint"] = "Pick how to use this modpack",
        ["dl.packchoice.install"] = "Install directly",
        ["dl.packchoice.install.desc"] = "Download and install into the game folder, ready to play",
        ["dl.packchoice.export"] = "Export mrpack file",
        ["dl.packchoice.export.desc"] = "Export a shareable .mrpack you can drop into any launcher",
        ["dl.packchoice.cancel"] = "Cancel",
        ["dl.pack.exporting"] = "Exporting mrpack",
        ["dl.pack.exportok"] = "Exported mrpack: ",
        ["dl.pack.exportfail"] = "Failed to export mrpack: ",
        ["modpack.openweb"] = "Open in browser",
        ["modpack.updated"] = "Updated",
        ["launcher.game.started"] = "Game started",
        ["launcher.game.exited"] = "Game exited",
        ["launcher.game.exitedCrash"] = "Game exited abnormally (possible crash)",
        ["launcher.game.crashLog"] = "Crash report detected — see game log for details",
        ["launcher.game.stopping"] = "Closing game…",
        ["launcher.game.closedByUser"] = "Game closed",
        ["launcher.game.notRunning"] = "No game is currently running",
        ["launcher.game.running"] = "A game is already running — close it before starting another",
        ["launcher.game.reattach"] = "Detected a running game — now tracking it",
        ["launcher.game.foreground"] = "Game window focused",
        ["launcher.game.noinstance"] = "Select an instance first",
        ["launcher.game.nojava"] = "Java (javaw.exe) not found",
        ["launcher.game.noaccount"] = "Sign in first",
        ["launcher.download.premiumRequired"] = "Sign in with a Microsoft account before downloading games or mods",
        ["launcher.game.noDir"] = ".minecraft folder not found — pick one via the folder button",
        ["launcher.game.failed"] = "Launch failed, see log",
        ["launcher.game.dir"] = "Game directory",
        ["launcher.game.noinherit"] = "Modded instances (Forge/Fabric, inheritsFrom) not supported yet — pick a vanilla instance",
        ["launcher.game.javaMismatch"] = "No matching Java version installed for this instance — install the required JDK (see launch log)",
        ["launcher.chooseDir"] = "Choose the .minecraft folder",
        ["launcher.err.emptyField"] = "Please fill in username and password",
        ["launcher.err.wrongPass"] = "Wrong username or password",
        ["launcher.err.authFailed"] = "Authentication failed, try again later",
        ["launcher.err.msaRequest"] = "Microsoft request failed, check network",
        ["launcher.err.msaDenied"] = "You declined the Microsoft authorization",
        ["launcher.err.msaExpired"] = "Microsoft sign-in timed out, try again",
        ["launcher.err.msaAuth"] = "Microsoft authorization failed, retry",
        ["launcher.err.noXbox"] = "This Microsoft account has no Xbox/Minecraft link",
        ["launcher.err.noMc"] = "This account does not own Minecraft: Java Edition",
        ["modpack.title"] = "Modpacks",
        ["modpack.cat.all"] = "All",
        ["modpack.f.search"] = "Search",
        ["modpack.f.source"] = "Source",
        ["modpack.f.version"] = "Game Version",
        ["modpack.f.loader"] = "Loader",
        ["modpack.f.type"] = "Category",
        ["modpack.f.all"] = "All",
        ["modpack.f.allType"] = "All",
        ["modpack.f.allSrc"] = "All",
        ["modpack.cat.adventure"] = "Adventure",
        ["modpack.cat.challenge"] = "Challenge",
        ["modpack.cat.combat"] = "Combat",
        ["modpack.cat.kitchenSink"] = "Kitchen Sink",
        ["modpack.cat.lightweight"] = "Lightweight",
        ["modpack.cat.magic"] = "Magic",
        ["modpack.cat.multiplayer"] = "Multiplayer",
        ["modpack.cat.optimization"] = "Optimization",
        ["modpack.cat.quests"] = "Quests",
        ["modpack.cat.technology"] = "Technology",
        ["modpack.cat.utility"] = "Utility",
        ["modpack.cat.library"] = "Library",
        ["modpack.cat.worldgen"] = "World Gen",
        ["modpack.cat.economy"] = "Economy",
        ["modpack.cat.equipment"] = "Equipment",
        ["modpack.cat.food"] = "Food",
        ["modpack.cat.storage"] = "Storage",
        ["modpack.cat.transport"] = "Transportation",
        ["modpack.cat.gamemech"] = "Game Mechanics",
        ["modpack.cat.social"] = "Social",
        ["modpack.cat.cartoon"] = "Cartoon",
        ["modpack.cat.realistic"] = "Realistic",
        ["modpack.cat.semireal"] = "Semi-realistic",
        ["modpack.cat.vanillalike"] = "Vanilla-like",
        ["modpack.error"] = "Failed to load, check your network",
        ["modpack.retry"] = "Retry",
        ["modpack.searchPh"] = "Search modpacks…",
        ["modpack.searchPh.modpack"] = "Search modpacks…",
        ["modpack.searchPh.mods"] = "Search mods…",
        ["modpack.searchPh.shader"] = "Search shaders…",
        ["modpack.searchPh.resource"] = "Search resource packs…",
        ["modpack.searchPh.datapack"] = "Search data packs…",
        ["modpack.searchPh.map"] = "Search maps…",
        ["modpack.searchPh.vanilla"] = "Search versions…",

        ["log.title"] = "Log",
        ["btn.copyLog"] = "Copy Log",
        ["btn.copied"] = "Copied",

        ["st.idle"] = "Idle",

        ["settings.title"] = "Settings",
        ["settings.subtitle"] = "Configure Starry Launcher behavior",
        ["settings.system"] = "System",
        ["settings.language"] = "Language",
        ["btn.openLogs"] = "Open Logs",
        ["btn.redownload"] = "Redownload",
        ["settings.general"] = "General",
        ["settings.subtitleGeneric"] = "General settings and component maintenance.",
        ["settings.trayTitle"] = "Tray icon",
        ["settings.uiLanguage"] = "Language",
        ["settings.font"] = "UI font",
        ["settings.fontDefault"] = "Default",
        ["settings.fontHint"] = "Some fonts are English-only; suk cannot force them onto Chinese text.",
        ["settings.theme"] = "Theme color",
        ["settings.themeDark"] = "Dark",
        ["settings.themeLight"] = "Light",
        ["settings.themeSystem"] = "Follow system",
        ["lang.zhCN"] = "Simplified Chinese",
        ["lang.zhTW"] = "Traditional Chinese",
        ["lang.en"] = "English",
        ["tray.on"] = "Minimize to tray: On",
        ["tray.off"] = "Minimize to tray: Off",
        ["tray.open"] = "Open",
        ["tray.exit"] = "Exit",

        ["set.tab.appearance"] = "Appearance",
        ["set.tab.general"] = "General",
        ["set.tab.storage"] = "Storage",
        ["settings.mcPath"] = "Import Minecraft path",
        ["settings.mcPathHint"] = "Pick any folder as the root — the game will be installed into its .minecraft subfolder. Switch the download source (community/official) on the Versions tab of the Downloads page.",

        ["set.path.current"] = "Default install location",
        ["set.path.dropHint"] = "Click to pick the default install location; remove added paths from the dropdown",
        ["set.path.add"] = "Add path",
        ["set.path.noPaths"] = "No paths yet — add one or wait for auto-detection",
        ["set.path.deleted"] = "Path removed and blocked (auto-detection will skip it)",
        ["set.path.autoDetect"] = "Auto-detect paths",
        ["set.path.autoDetected"] = "Auto-detect finished, {0} path(s) added",
        ["set.path.labelFixed"] = "Add or remove Minecraft folders",
        ["set.path.scanStarting"] = "Preparing to scan…",
        ["set.path.scanning"] = "Scanning folders {0}/{1} …",
        ["set.path.scanDone"] = "Scan complete, {0} path(s) added",
        ["set.path.scanFail"] = "Scan failed",
        ["set.path.rename"] = "Rename (display name only, folder unchanged)",
        ["set.path.renameTitle"] = "Rename game path",
        ["set.path.renameHint"] = "Only the name shown in the app changes; the folder on disk stays the same",
        ["launcher.pathPick"] = "Select Minecraft folders",
        ["launcher.pathPickEmpty"] = "No Minecraft paths found — add one first in Settings → Storage",

        ["set.adv.dlUp"] = "Down {0} Mbps · Up {1} Mbps",
        ["set.identity.hint"] = "Click avatar or name to edit",
        ["set.identity.saved"] = "Avatar updated",
        ["set.name.title"] = "Edit Nickname",
        ["set.name.hint"] = "Up to 4 characters",
        ["set.bg.page"] = "Page Background",
        ["set.bg.chat"] = "Chat Background",
        ["set.bg.pick"] = "Pick Image",
        ["set.bg.clear"] = "Clear",
        ["set.bg.blur"] = "Blur",
        ["set.bg.visibility"] = "Visibility",
        ["set.bg.components"] = "Component Opacity",
        ["dlg.pickPageBg"] = "Pick page background image",
        ["greet.morning"] = "Good morning",
        ["greet.forenoon"] = "Good morning",
        ["greet.afternoon"] = "Good afternoon",
        ["greet.evening"] = "Good evening",
        ["set.accent.title"] = "Control Colors",
        ["set.accent.gradient"] = "Gradients",
        ["set.accent.hint"] = "Icons follow the color at full strength; nav pill, sliders and the morphing ball use a light tint of the same hue",
        ["set.accent.custom"] = "Custom",
        ["set.accent.new"] = "New",
        ["set.accent.current"] = "Current",


        ["crash.title"] = "Starry Launcher",
        ["crash.msg"] = "Starry Launcher crashed: ",
        ["crash.hint"] = "See the logs folder for details.",


        ["hist.rename"] = "Rename",
        ["hist.renameTip"] = "Type a new title. Enter to save, Esc to cancel",

        ["btn.skip"] = "Skip",


        ["avatar.cropTitle"] = "Adjust Avatar",
        ["avatar.cropHint"] = "Drag to position, scroll to zoom — what's inside the circle is the final avatar.",
        ["btn.ok"] = "OK",
        ["btn.cancel"] = "Cancel",
        ["btn.close"] = "Close",

        ["traymenu.show"] = "Show Window",
        ["traymenu.exit"] = "Exit Starry Launcher",
        ["tray.balloonTitle"] = "Starry Launcher is still running",
        ["tray.balloonText"] = "Double-click the tray icon to reopen; right-click to exit.",


        ["st.cancelling"] = "Cancelling...",
        ["set.identity.mcface"] = "minecraft avatar",
        ["set.identity.mcfaceFail"] = "Failed",
        ["st.preparing"] = "Preparing...",
        ["st.retry"] = "Failed, retrying {0}/{1}...",
        ["st.busyWait"] = "Previous operation is still running, please wait…",

        ["log.args"] = "Args: {0}",
        ["log.attempt"] = "Attempt {0} (server port {1})...",
        ["log.runDir"] = "Writable run dir: {0}",
        ["log.mcHint"] = "Scan LAN in-game or connect to the address above",
        ["log.ready"] = "Ready",

    };
}
