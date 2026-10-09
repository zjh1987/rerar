// Rerar 主窗口（Task 14）：界面形态 A（规格 §6.1）。
//
// ============================================================================================
// 【这是一层薄壳 —— 请先读这段再读代码】
// 全部业务逻辑都在 src\Core，而且已经被独立复核过：解压顺序（Sniffer → 分卷 → 索引 → 门控 →
// 预检 → 密码 → 暂存 → 解压 → 校验 → 提交 → 可选删除）、五条不变式、密码阶梯、回收站资格判定、
// 崩溃恢复日志，**一条都不在这里重做**。本文件只做三件事：
//   1) 收集输入与选项（原样映射到 RunOptions），并按 RunOptions 的既有语义配置它们；
//   2) 把 IProgressSink 的回调**防御性编组**到 UI 线程后显示（回调绝不许抛）；
//   3) 把 RunSummary / ArchiveResult 如实呈现 —— 绝不改写任何结局、绝不自己判成功。
// 「未处理」的数量只取自 RunSummary.NotAttempted（**权威清单**），绝不与 Results 相加：
// 深度触顶那一项同时出现在两个集合里，相加就是同一件事数两遍。逐项列表里也是同一个对象，
// 只显示一次（未处理剩余项的合成复用 Program.AddUnprocessedRemainder，不另写一份）。
//
// ============================================================================================
// 【本文件落地的审计裁决（UI/UX 复核；每条都写在它对应的代码旁边）】
//   * 绝不提权：app.manifest 是 asInvoker，启动时检测已提权则显示**常驻非模态横幅**（J2）。
//     理由：提权后 Windows UIPI 会**静默**拦截资源管理器的拖放 —— 用户拖了、窗口毫无反应、没有
//     任何报错。所以「选择文件夹…／选择压缩包…」两个按钮做成与拖放区同等显眼、可键盘操作，
//     始终可用的那条路不能是藏起来的那条。
//   * 删除默认关（A5 / 不变式 I3）：独立的红色「危险操作」区、措辞写明后果、二次确认里报出
//     **确切数量**、且**不默认聚焦确定按钮**（AcceptButton 为空）。
//   * 密码是**停靠式非模态面板**（J6），绝不从工作线程弹模态框（那会阻塞该线程、对话框还可能
//     落到主窗后面，整批停滞）。三个动作：仅此压缩包 / 本次运行全部记住 / 跳过此压缩包。
//     跳过的包记为「跳过（需要密码）」，「不是失败」。
//   * 两阶段进度（J1）：扫描期 Marquee + 「已发现 N 个压缩包」（数量是事实，未知工作的百分比是
//     谎话）；解压期定值，分母是**已发现的工作量**。只显示「已用时间」；ETA 标「粗略」且满 3 个
//     归档后才出现。
//   * 日志绝不冻结界面（J4）：完整日志流式写文件（可「打开日志文件」）；界面只画尾部 ~2000 行的
//     **自绘虚拟化**视图（只绘制可见行）；UI 刷新**合并到 200ms 一次**（绝不逐行 append，也绝不
//     用 TextBox/ListBox 存日志）；另有「自动滚动」开关 —— 用户往上翻的时候日志一直往下跳本身
//     就是个 bug。
//   * 逐项动作（§6.1）：强制按压缩包尝试（映射到 RunOptions.ForceTreatAsArchive）/ 输入密码 /
//     重试 / 打开输出目录。
//   * FormClosing 三选一（J7）：继续在后台运行（最小化到通知区域）/ 取消任务并退出 / 返回。
//     长任务默认「继续在后台运行」；绝不静默丢下一个还在跑的批次。
//   * .lnk 解析并**显式展示**（J8）：拖进来的快捷方式给到的是 .lnk 路径，不解析的话用户只会看到
//     「不是有效压缩包」。解析失败是**逐项告警**，不是崩溃。
//   * 空状态写明「原件默认保留」（J11）+ 一句话说清这个工具做什么；高级选项默认折叠。
//   * 长任务期间阻止睡眠（G5：SetThreadExecutionState），结束/取消时清除。
//   * 完成时通知（J14）：FlashWindowEx + 托盘气泡 + **非模态**结果面板（不抢焦点）。
//   * 窗口几何与上次目录存到 %APPDATA%，原子写（临时文件 + 替换），加载时校验目录仍然存在。
//   * 字体显式 Microsoft YaHei UI（回退 Microsoft YaHei → SimSun），AutoScaleMode.Dpi，
//     布局一律 TableLayoutPanel / FlowLayoutPanel，**没有一处绝对坐标**。
//   * **刻意不做暗色主题**：WinForms 不跟随系统主题，做一半的暗色（黑底黑字）比不做更糟。
//   * 刻意不做：向导、动画、托盘常驻、导出报告按钮（YAGNI；规格没要求，审计里那半条另记）。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。窗口布局用 TableLayoutPanel/FlowLayoutPanel，无绝对坐标。
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Rerar.Core;

namespace Rerar
{
    internal sealed class MainForm : Form
    {
        // ------------------------------------------------------------------
        // 契约常量（用例按名字/数值钉住其中的一部分）
        // ------------------------------------------------------------------

        // J4：界面里只保留尾部这么多行（完整日志在文件里）。
        internal const int LogTailLines = 2000;

        // J4：UI 刷新合并窗口 150–250ms。一次 Tick 同时刷新进度、日志与列表，于是无论 Core 回调
        // 多密集（几百个包 × 每个几十行 = 几十万行），UI 每秒最多被碰 5 次。
        internal const int UiCoalesceMs = 200;

        // J1：满 3 个归档才出现 ETA，且必须标「粗略」。
        internal const int EtaMinimumCompleted = 3;

        // J3：30 秒没有任何进度信号就明说「正在处理大文件」，免得与假死无法区分。
        private const int HeartbeatSeconds = 30;

        // 预检摘要（修复轮 Finding 2）自动收起前的秒数。摘要**不是**模态确认框：它在开始按钮上方
        // 就地出现、不抢焦点、不阻塞运行，几十秒后自动消失（详情面板与日志里都留下同一份文案）。
        internal const int SummaryVisibleSeconds = 60;

        // 逐项动作的条目数（四个动作：强制 / 输入密码 / 重试 / 打开输出目录）。
        // 按钮与右键菜单**同序**，SetItemActionsEnabled 按下标同时决定两者的可用性。
        internal const int ItemActionCount = 4;

        // 界面设置的进程级覆盖（与 TestEnv/SettingsPathVariable 同名）：测试靠它把落点挪到临时目录，
        // 于是界面用例构造真的 MainForm 时既不读也不写真实的 %APPDATA%\Rerar。
        private const string SettingsPathVariable = "RERAR_SETTINGS_PATH";

        // 界面运行日志根的进程级覆盖，与 RERAR_JOURNAL_ROOT / RERAR_SETTINGS_PATH 同一机制。
        // 未设置时生产行为一字不变（仍旧是 %LOCALAPPDATA%\Rerar\logs）。
        private const string LogRootVariable = "RERAR_LOG_ROOT";

        // J11 的空状态文案：一句话说清做什么 + **明确写出原件默认保留**（安全路径要是显而易见的那条）。
        internal const string EmptyStateText =
            "自动找出多层嵌套的压缩包并解压（含伪装后缀、分卷、常用密码字典）。\r\n" +
            "原件默认保留：不勾选下面的「解压成功后删除原包」，就一个原包都不会被处置。\r\n" +
            "把压缩包或文件夹拖到这里，或点上面的「选择文件夹…」「选择压缩包…」。";

        // ------------------------------------------------------------------
        // 控件（Name 是契约：用例按名字找 dropZone / chkDelete / grpDanger …）
        // ------------------------------------------------------------------

        private TableLayoutPanel _root;
        private Label _lblElevation;
        private Panel _dropZone;
        private Label _lblDropHint;
        private Button _btnPickFolder;
        private Button _btnPickFiles;
        private Label _lblEmptyState;
        private Label _lblInputs;
        private ListView _lstInputs;
        private Label _lblEngineInfo;

        private FlowLayoutPanel _panelOptions;
        private CheckBox _chkCamouflage;
        private CheckBox _chkVolumes;
        private CheckBox _chkDict;
        private GroupBox _grpDanger;
        private CheckBox _chkDelete;
        private Button _btnAdvanced;
        private Panel _panelAdvanced;
        private Label _lblPasswordHint;
        private TextBox _txtPassword;
        private CheckBox _chkRememberRun;
        private Button _btnLoadDict;
        private Label _lblDict;
        private NumericUpDown _numDepth;

        private FlowLayoutPanel _panelActions;
        private Button _btnStart;
        private Button _btnCancel;
        private Panel _panelProgress;
        private ProgressBar _progressBar;
        private Label _lblProgress;
        private Label _lblSummary;
        private Panel _panelStatus;
        private Label _lblStatus;
        private FlowLayoutPanel _panelCounters;
        private Label _lblCounters;
        private Label _lblSessionCounters;
        private Button _btnDetails;

        private Panel _panelPasswordAsk;
        private Label _lblPasswordAsk;
        private TextBox _txtPasswordAsk;
        private Button _btnPwThisOnly;
        private Button _btnPwRememberAll;
        private Button _btnPwSkip;

        private Panel _panelDetails;
        private FlowLayoutPanel _panelLogTools;
        private Button _btnOpenLog;
        private CheckBox _chkAutoScroll;

        // 规格 §8「许可合规」的界面半边：内嵌 7-Zip ⇒ 本程序是 7-Zip 的二进制再分发者，许可义务
        // 必须能在**程序内** discharge —— 「关于/开源许可」入口展示随包的 THIRD-PARTY-NOTICES.txt
        //（文件半边由 build.ps1 随 exe 分发，缺失即构建失败；两半缺一不可）。
        private Button _btnLicense;

        // 随包许可声明文件的固定名字（build.ps1 把它复制到 exe 旁边；验收行 A06d 断言它在）。
        private const string LicenseFileName = "THIRD-PARTY-NOTICES.txt";

        // 已打开的许可窗口（非模态）：同一时刻最多一份，再点一次把它带到前台；窗口关闭时置 null。
        private Form _licenseDialog;
        private Label _lblLogHint;
        private ListView _lstItems;
        private FlowLayoutPanel _panelItemActions;
        private Button _btnForceArchive;
        private Button _btnEnterPassword;
        private Button _btnRetry;
        private Button _btnOpenOutput;
        private ContextMenuStrip _itemMenu;
        private readonly ToolStripMenuItem[] _itemMenuItems = new ToolStripMenuItem[ItemActionCount];
        private LogTailView _logView;

        private NotifyIcon _tray;
        private System.Windows.Forms.Timer _uiTimer;

        // ------------------------------------------------------------------
        // 状态
        // ------------------------------------------------------------------

        private readonly AppSettings _settings;
        // 规范化全路径去重（J9：重复拖入同一项不该变成两行）。
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _inputs = new List<string>();          // 交给 Extractor 的候选文件
        private readonly Dictionary<string, int> _inputRowOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _retryQueue = new List<string>();
        private readonly HashSet<string> _forcedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _perItemPassword = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private string _dictPath;
        private string _pendingPasswordPath;
        private int _scanRemaining;
        private int _scanFound;
        private CancellationTokenSource _scanCts;
        private bool _nextBatchOnly;         // J10：运行中拖进来的项进「下一批」

        private bool _running;
        private CancellationTokenSource _runCts;
        private RunOptions _options;
        private LogSink _log;
        private Stopwatch _watch;
        private TimeSpan _lastElapsed;
        private int _discoveredAtStart;
        // 进度计数：由工作线程写、UI 线程读。放在一个普通对象上，而不是直接放窗体上 ——
        // Form 继承自 MarshalByRefObject，对它自己的字段取 ref（Interlocked 需要）会得到 CS0197
        //（跨应用域取地址可能抛异常）。计数对象是普通引用，Interlocked 用得干净。
        private sealed class ProgressCounters
        {
            public int Started;
            public int Finished;
        }

        private readonly ProgressCounters _counters = new ProgressCounters();
        private string _currentMember = "";
        private long _lastSignalTicks;
        private DateTime _cancelRequestedAt = DateTime.MinValue;
        private bool _exitWhenDone;
        private bool _allowClose;
        private string _runWidePassword = "";

        private readonly object _pendingGate = new object();
        private readonly List<ArchiveResult> _pendingResults = new List<ArchiveResult>();
        private List<ArchiveResult> _results = new List<ArchiveResult>();
        private int _unprocessedCount;
        private List<ArchiveResult> _rows = new List<ArchiveResult>();

        // ---- 会话级（跨轮）累计：修复轮 Finding 3 ----
        // 「本轮」与「本次会话」是两个不同的口径，界面上必须**两个都说**（只显示一个就是让用户
        // 把一轮的数字当成整场的数字）。这两个集合刻意分开存放：_session* 只累加**已经收尾**的轮，
        // 未收尾的那一轮由 _results/_unprocessedCount 现场提供。
        private readonly List<ArchiveResult> _sessionResults = new List<ArchiveResult>();
        private readonly List<string> _sessionLogPaths = new List<string>();
        private int _sessionUnprocessed;

        // 本会话真正起过几轮运行（只增不减）。给用例一个**确定性**的判据：点一次「重试」之后
        // 这个值必须真的 +1 —— 以前那一下只入队、什么都不跑（修复轮 Finding 1）。
        private int _sessionRounds;

        // 引擎定位结果的**真实值**（定位到之后由工作线程写入）。没定位到就是空串 ——
        // 预检摘要据此如实说「将在开始时定位」，绝不把静态占位文案当成定位结果（修复轮 Finding 2）。
        private string _engineResolvedText = "";

        // 预检摘要自动收起的计时（只在 UI 线程上读写）。
        private Stopwatch _summaryWatch;

        // _results 里已经“过账”到逐项列表的下标（增量追加用，见 RefreshItems）。
        private int _itemsBuilt;
        private bool _logDirty;
        private bool _itemsDirty;

        // 本会话真正起过的运行轮数（只增不减）。给用例一个**确定性**的判据：点一次「重试」之后
        // 这个值必须真的 +1 —— 以前那一下只入队、什么都不跑（修复轮 Finding 1）。
        internal int SessionRunCount { get { return _sessionRounds; } }

        public MainForm()
        {
            _uiThreadId = Thread.CurrentThread.ManagedThreadId;   // 编组判据（见 UiPost）
            _settings = new AppSettings(AppSettings.DefaultFilePath);
            _settings.Load();

            // 恢复日志根的**进程级**覆盖（RERAR_JOURNAL_ROOT）：界面模式与 CLI 是同一个进程，这条
            // 覆盖必须两种模式都认（否则「进程级」就不成立）。界面模式不往控制台打印任何东西。
            Program.TryApplyJournalRootOverride();

            BuildUi();
            SetLogHint();            // 会话还没有日志：提示与按钮的初始状态由同一处决定
            RefreshCounterLabels();
            ApplySettings();
            DetectElevation();
        }

        // 建窗体的那个线程就是 UI 线程。UiPost 靠它（而不是 Control.InvokeRequired）判断该就地执行
        // 还是 BeginInvoke —— 没有句柄时 InvokeRequired 会返回 false，那会把界面动作放到工作线程上跑。
        private readonly int _uiThreadId;

        // ==================================================================
        // 构造与静态纯函数（用例直接断言这一批）
        // ==================================================================

        // 字体：显式中文字体 + 回退链（J15）。**绝不**依赖系统默认字体（那样在非中文系统上会
        // 变成一个没有中文字形的字体，界面上全是方框）。
        internal static string PickFontFamily()
        {
            string family = PickFirstInstalledFamily(new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimSun" });
            if (family.Length > 0) { return family; }

            // 三个都没有（例如极简的系统）：退回 WinForms 的默认界面字体，并如实接受这个结果 ——
            // 宁可字体不好看，也不要一个装不上的字体名让整个窗口起不来。
            try { return Control.DefaultFont.Name; }
            catch (Exception) { return "Microsoft Sans Serif"; }
        }

        // 从候选链里挑第一个**真的装上**的字体族；一个都没有返回 ""。
        // Font 构造对不存在的族名可能是「抛异常」也可能是「静默回退到别的族」，两条路都要挡住，
        // 所以既 catch 又回读 Name 比对（静默回退正是这里要防的那种「悄悄用了错字体」）。
        internal static string PickFirstInstalledFamily(string[] wanted)
        {
            if (wanted == null) { return ""; }

            foreach (string name in wanted)
            {
                if (string.IsNullOrEmpty(name)) { continue; }
                try
                {
                    using (Font probe = new Font(name, 9F))
                    {
                        if (string.Equals(probe.Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            return probe.Name;
                        }
                    }
                }
                catch (Exception)
                {
                    // 这一族不存在（或建不出来）：试下一个。
                }
            }
            return "";
        }

        // 已用时间。只显示已用时间 —— 未知工作的百分比是谎话（J1）。
        internal static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero) { elapsed = TimeSpan.Zero; }
            long total = (long)elapsed.TotalSeconds;
            long hours = total / 3600;
            long minutes = (total % 3600) / 60;
            long seconds = total % 60;

            if (hours > 0)
            {
                return hours.ToString(CultureInfo.InvariantCulture) + ":" +
                       minutes.ToString("00", CultureInfo.InvariantCulture) + ":" +
                       seconds.ToString("00", CultureInfo.InvariantCulture);
            }
            return minutes.ToString("00", CultureInfo.InvariantCulture) + ":" +
                   seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        // 扫描期：Marquee + 「已发现 N 个候选文件」。**没有百分比** —— 扫描期还不知道分母。
        //
        // 【口径（修复轮 Minor 2）】这里数的是**候选文件**：目录枚举**不按后缀过滤**（在枚举层筛掉
        //「不像压缩包」的文件，正好会把伪装后缀的包一起扔掉，那是 §6.3 禁止的静默丢弃），所以
        //「是不是压缩包」得由 Core 的 Sniffer 逐个判。一个纯文本文件也会被算进 N，随后在结果里
        // 如实显示为「跳过（无法读取）」。以前这里写「N 个压缩包」，那是**单位与名词不符** ——
        // 用户看到 N 就该知道它是什么东西的个数。
        internal static string FormatScanningText(int discovered)
        {
            return "正在扫描…已发现 " + discovered.ToString(CultureInfo.InvariantCulture) + " 个候选文件";
        }

        // 解压期：分母是**已发现的工作量**，只报已用时间。永不报百分比（J1：分母会增长，
        // 百分比会倒退 —— 用户会以为崩了然后强杀进程）。
        //
        // 【口径（修复轮 Minor 2）】与 FormatScanningText 同一个数：交给 Core 逐个判定的**候选文件**
        // 项数，不是「压缩包」个数（枚举层不按后缀过滤，见那里的说明）。所以单位一律写「项」。
        internal static string FormatProgressText(int index, int discovered, TimeSpan elapsed)
        {
            int denominator = discovered > index ? discovered : index;
            return "第 " + index.ToString(CultureInfo.InvariantCulture) +
                   " / 已发现 " + denominator.ToString(CultureInfo.InvariantCulture) + " 项" +
                   " · 已用 " + FormatElapsed(elapsed);
        }

        // ETA：满 EtaMinimumCompleted 个归档之后才出现，且**必须**标「粗略」。
        internal static string FormatEta(TimeSpan elapsed, int completed, int remaining)
        {
            if (completed < EtaMinimumCompleted || remaining <= 0) { return ""; }

            double perArchive = elapsed.TotalSeconds / completed;
            double seconds = perArchive * remaining;
            if (seconds < 1.0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) { return ""; }

            return "剩余约 " + FormatElapsed(TimeSpan.FromSeconds(seconds)) + "（粗略）";
        }

        // 计数行（J6 / §7）：跳过（需密码）与失败**分开数** —— 把「需要密码」说成失败是最伤信任的
        // 一类谎话（用户会以为文件坏了）。未处理数量由调用方按权威清单（NotAttempted）传入。
        internal static string FormatCountersLine(List<ArchiveResult> results, int unprocessed)
        {
            int failed = 0, needsPassword = 0, skipped = 0;
            if (results != null)
            {
                foreach (ArchiveResult r in results)
                {
                    if (r == null) { continue; }
                    switch (r.Status)
                    {
                        case ArchiveStatus.Failed: failed++; break;
                        case ArchiveStatus.SkippedNeedsPassword: needsPassword++; break;
                        case ArchiveStatus.SkippedContainer:
                        case ArchiveStatus.SkippedUnreadable: skipped++; break;
                    }
                }
            }

            return "❌ " + failed.ToString(CultureInfo.InvariantCulture) + " 个失败" +
                   " · 🔒 " + needsPassword.ToString(CultureInfo.InvariantCulture) + " 个需密码" +
                   " · ⏭ " + skipped.ToString(CultureInfo.InvariantCulture) + " 个跳过" +
                   " · ⏳ " + unprocessed.ToString(CultureInfo.InvariantCulture) + " 个未处理";
        }

        // 已解出的文件数（只数真的解出过东西的两种结局）。
        internal static string FormatExtractedLine(List<ArchiveResult> results)
        {
            long files = 0;
            if (results != null)
            {
                foreach (ArchiveResult r in results)
                {
                    if (r == null) { continue; }
                    if (r.Status == ArchiveStatus.Completed || r.Status == ArchiveStatus.CompletedWithFailures)
                    {
                        files += r.Files;
                    }
                }
            }
            return "✅ 已解出 " + files.ToString(CultureInfo.InvariantCulture) + " 个文件";
        }

        // 口径标签（修复轮 Finding 3）：数字**必须**带着它自己的范围一起出现。
        // 只写「❌ 0 个失败」而不说这是哪一轮的，读者就会把它当成整场的结论 —— 而重试轮的数字
        // 只描述重试轮。两个口径分别是「本轮」（当前这一轮）与「本次会话」（含之前所有轮）。
        internal const string ScopeRoundLabel = "本轮";
        internal const string ScopeSessionLabel = "本次会话";

        // 带口径的计数行：计数器本身仍然只有 FormatCountersLine 一份实现（不复制判定）。
        internal static string FormatScopedCountersLine(string scope, List<ArchiveResult> results, int unprocessed)
        {
            return "【" + scope + "】" + FormatCountersLine(results, unprocessed);
        }

        // 本次会话的总计数 = 已收尾各轮 + 当前轮（当前轮由调用方现场传入）。
        // 精确按路径取最后一条：同一个包在重试轮里被重新处理过，**只算最后一次**的结局 ——
        // 直接相加会把同一件事数两遍（与 §16「NotAttempted 绝不与 Results 相加」同一条纪律）。
        internal static string FormatSessionCountersLine(List<ArchiveResult> closedRounds,
                                                        List<ArchiveResult> currentRound,
                                                        int closedUnprocessed, int currentUnprocessed)
        {
            Dictionary<string, ArchiveResult> lastByPath = new Dictionary<string, ArchiveResult>(StringComparer.OrdinalIgnoreCase);
            List<ArchiveResult> unknownPath = new List<ArchiveResult>();

            AddLastByPath(lastByPath, unknownPath, closedRounds);
            AddLastByPath(lastByPath, unknownPath, currentRound);

            List<ArchiveResult> merged = new List<ArchiveResult>();
            foreach (KeyValuePair<string, ArchiveResult> pair in lastByPath) { merged.Add(pair.Value); }
            merged.AddRange(unknownPath);

            int unprocessed = closedUnprocessed + currentUnprocessed;
            return FormatScopedCountersLine(ScopeSessionLabel, merged, unprocessed);
        }

        // path 为空的结果没有可去重的身份：原样保留（绝不为了去重把一条真实结局丢掉）。
        private static void AddLastByPath(Dictionary<string, ArchiveResult> map, List<ArchiveResult> unknownPath,
                                          List<ArchiveResult> results)
        {
            if (results == null) { return; }
            foreach (ArchiveResult r in results)
            {
                if (r == null) { continue; }
                if (string.IsNullOrEmpty(r.Path)) { unknownPath.Add(r); continue; }
                map[r.Path] = r;
            }
        }

        // ==================================================================
        // 预检摘要（修复轮 Finding 2）：**每次**开始解压都要有，删除关着也要有。
        //
        // 【为什么必须是纯函数】摘要的四项内容（目标项数 / 删除后果 / 输出去向 / 引擎定位结果）
        // 正是「用户按下开始之后才知道自己要同意什么」的那四件事。做成静态纯函数，用例就能直接
        // 断言**真的那一段文案**，而不是断言「某个控件存在」这种什么都证明不了的形状。
        //
        // 【绝不显示假值】引擎还没定位时如实说「将在开始时定位」，绝不把静态占位文案（那是
        //「优先用本机 7-Zip ≥ 25.00…」的说明）当成定位结果印出来。
        // ==================================================================

        // 引擎状态那一行：resolvedText 非空 = 真的定位到了（工作线程写回来的原话）。
        internal static string FormatEngineSummaryLine(string resolvedText)
        {
            if (string.IsNullOrEmpty(resolvedText)) { return "引擎：将在开始时定位"; }
            return "引擎：" + resolvedText;
        }

        // 输出去向（规格 §6.11 的**原地**语义）：每个归档解到它自己所在的目录，输出根没有全局开关。
        internal static string FormatOutputSummaryLine()
        {
            return "输出去向：每个包原地解到它所在的文件夹（第 N 层解出的包放进第 N 层的输出目录之内；" +
                   "目标同名时改用「名字 (2)」，绝不覆盖）";
        }

        // 删除后果：关着就明确写「原包一律保留」（这也是空状态的承诺，开始前再确认一次）。
        // 开着时写出**确切数量**与处置范围 —— 与二次确认框同一个口径（§6.1/A5）。
        internal static string FormatDeleteSummaryLine(bool deleteEnabled, int count)
        {
            string number = count.ToString(CultureInfo.InvariantCulture);
            if (!deleteEnabled)
            {
                return "原件处置：解压成功后删除原包「未」开启 —— 这 " + number + " 项的原包一律保留，一个都不动";
            }
            return "原件处置：已开启「解压成功后删除原包」—— 这 " + number +
                   " 项里，只有解压成功且校验通过的那一项会被移入回收站，" +
                   "失败、跳过、需要密码、未处理的一律保留";
        }

        // 整段摘要（多行）。第一行是「要处理几项」，随后是处置、去向、引擎。
        // 【面向用户的中文】用户可见文案里绝不出现 Core 这类内部术语：这里说「程序」。
        internal static string BuildSummaryText(int count, bool deleteEnabled, string engineResolvedText)
        {
            return "开始前摘要：本次将处理 " + count.ToString(CultureInfo.InvariantCulture) + " 项（候选文件，" +
                   "由程序逐个判定是不是压缩包）。\r\n" +
                   FormatDeleteSummaryLine(deleteEnabled, count) + "。\r\n" +
                   FormatOutputSummaryLine() + "。\r\n" +
                   FormatEngineSummaryLine(engineResolvedText) + "。";
        }

        // A5：二次确认的措辞。必须写出**确切数量**、说清去向，并说清什么**不会**发生 ——
        // 用户按下确定之前要知道自己同意了什么。
        //
        // 【口径（修复轮 Minor 3）】单位是**项**（交给 Core 逐个判定的候选文件），不是「压缩包」：
        // 枚举层不按后缀过滤，这一批里可能有根本不是压缩包的文件（它们会如实显示为「跳过」）。
        // 说成「N 个压缩包」就是在替 Core 提前下判定。
        internal static string DeleteConfirmText(int count)
        {
            string number = count.ToString(CultureInfo.InvariantCulture);
            return "即将处理 " + number + " 项（候选文件，由程序逐个判定是不是压缩包），" +
                   "并勾选了「解压成功后删除原包」：\r\n\r\n" +
                   "· 解压成功且校验通过的那一项，其原包会被移入回收站；\r\n" +
                   "· 失败、跳过、需要密码、未处理的项一律保留，绝不会被删除；\r\n" +
                   "· 回收站不可用或超出配额时，程序会如实报告实际处置方式。\r\n\r\n" +
                   "确定要带着删除开关开始吗？";
        }

        // J8：解析 .lnk 的目标。用 WScript.Shell 晚绑定（本项目不引 COM 接口定义，也不需要）。
        // 任何失败都只是**逐项告警**（返回 false + 中文原因），绝不抛异常把整批拖下水。
        internal static bool TryResolveShortcut(string lnkPath, out string target, out string problem)
        {
            target = null;
            problem = null;

            if (string.IsNullOrEmpty(lnkPath)) { problem = "快捷方式路径为空"; return false; }

            string full;
            try { full = Path.GetFullPath(lnkPath); }
            catch (Exception ex) { problem = "快捷方式路径无效（" + ex.GetType().Name + "）：" + lnkPath; return false; }

            if (!File.Exists(full)) { problem = "快捷方式不存在或不可读：" + full; return false; }
            if (!string.Equals(Path.GetExtension(full), ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                problem = "不是 .lnk 快捷方式：" + full;
                return false;
            }

            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) { problem = "本机没有注册 WScript.Shell，无法解析快捷方式：" + full; return false; }

                object shell = Activator.CreateInstance(shellType);
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                    new object[] { full });
                if (link == null) { problem = "无法打开快捷方式：" + full; return false; }

                object value = link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null);
                string resolved = value == null ? "" : value.ToString();
                if (resolved.Length == 0)
                {
                    problem = "快捷方式没有可用的目标路径（可能已损坏）：" + full;
                    return false;
                }

                target = resolved;
                return true;
            }
            catch (Exception ex)
            {
                problem = "解析快捷方式失败（" + ex.GetType().Name + "：" + ex.Message + "）：" + full;
                return false;
            }
        }

        // ==================================================================
        // 界面构造（一律 TableLayoutPanel / FlowLayoutPanel，无绝对坐标）
        // ==================================================================

        private void BuildUi()
        {
            SuspendLayout();

            Text = "Rerar 递归解压";
            Font = new Font(PickFontFamily(), 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            // 设计基准：96 DPI。这是设计器生成代码的既有写法，运行时按当前 DPI 由 PerformAutoScale 缩放。
            AutoScaleDimensions = new SizeF(96F, 96F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(940, 660);
            MinimumSize = new Size(780, 560);
            AllowDrop = true;

            _root = new TableLayoutPanel();
            _root.Dock = DockStyle.Fill;
            _root.ColumnCount = 1;
            _root.RowCount = 8;
            _root.Padding = new Padding(10);
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            for (int i = 0; i < 7; i++) { _root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); }
            _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 最后一行：详情（可折叠）

            // ---- 0) 提权横幅（J2；非模态、常驻、不挡按钮）----
            _lblElevation = new Label();
            _lblElevation.Name = "lblElevation";
            _lblElevation.AutoSize = true;
            _lblElevation.MaximumSize = new Size(880, 0);
            _lblElevation.BackColor = Color.FromArgb(255, 248, 220);
            _lblElevation.ForeColor = Color.FromArgb(150, 60, 0);
            _lblElevation.Padding = new Padding(8);
            _lblElevation.Margin = new Padding(0, 0, 0, 6);
            _lblElevation.Text = "⚠ 本程序正在以管理员身份运行：Windows 会「静默」拦截资源管理器的拖放" +
                "（拖了没反应、也没有任何报错）。请改用下面的「选择文件夹…」「选择压缩包…」按钮 —— " +
                "它们在任何权限下都可用。建议关闭本窗口，改用普通权限重新打开。";
            _lblElevation.AccessibleName = "提权提示";
            _lblElevation.Visible = false;
            _root.Controls.Add(_lblElevation, 0, 0);

            // ---- 1) 拖放区 + 两个同等显眼的选择按钮（J2）----
            _dropZone = new DropZonePanel();
            _dropZone.Name = "dropZone";
            _dropZone.Dock = DockStyle.Fill;
            _dropZone.AutoSize = true;
            _dropZone.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _dropZone.Padding = new Padding(12);
            _dropZone.Margin = new Padding(0, 0, 0, 8);
            _dropZone.AllowDrop = true;
            _dropZone.AccessibleName = "拖放区";
            _dropZone.DragEnter += DropZone_DragEnter;
            _dropZone.DragOver += DropZone_DragEnter;
            _dropZone.DragDrop += DropZone_DragDrop;

            TableLayoutPanel dropInner = new TableLayoutPanel();
            dropInner.Dock = DockStyle.Fill;
            dropInner.AutoSize = true;
            dropInner.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            dropInner.ColumnCount = 1;
            dropInner.RowCount = 6;
            for (int i = 0; i < 5; i++) { dropInner.RowStyles.Add(new RowStyle(SizeType.AutoSize)); }
            dropInner.RowStyles.Add(new RowStyle(SizeType.Absolute, 104F));   // 输入清单（固定高度，可滚动）

            _lblDropHint = new Label();
            _lblDropHint.AutoSize = true;
            _lblDropHint.Font = new Font(Font.FontFamily, 12F, FontStyle.Bold);
            _lblDropHint.Text = "⬇  把压缩包 / 文件夹拖到这里";
            _lblDropHint.Margin = new Padding(0, 0, 0, 6);

            FlowLayoutPanel pickRow = new FlowLayoutPanel();
            pickRow.AutoSize = true;
            pickRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            pickRow.FlowDirection = FlowDirection.LeftToRight;
            pickRow.Margin = new Padding(0, 0, 0, 6);

            _btnPickFolder = new Button();
            _btnPickFolder.Name = "btnPickFolder";
            _btnPickFolder.Text = "选择文件夹…";
            _btnPickFolder.AutoSize = true;
            _btnPickFolder.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _btnPickFolder.Padding = new Padding(10, 4, 10, 4);
            _btnPickFolder.AccessibleName = "选择文件夹（拖放失效时的备用入口）";
            _btnPickFolder.Click += BtnPickFolder_Click;

            _btnPickFiles = new Button();
            _btnPickFiles.Name = "btnPickFiles";
            _btnPickFiles.Text = "选择压缩包…";
            _btnPickFiles.AutoSize = true;
            _btnPickFiles.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _btnPickFiles.Padding = new Padding(10, 4, 10, 4);
            _btnPickFiles.AccessibleName = "选择一个或多个压缩包（拖放失效时的备用入口）";
            _btnPickFiles.Click += BtnPickFiles_Click;

            pickRow.Controls.Add(_btnPickFolder);
            pickRow.Controls.Add(_btnPickFiles);

            _lblEmptyState = new Label();
            _lblEmptyState.Name = "lblEmptyState";
            _lblEmptyState.AutoSize = true;
            _lblEmptyState.MaximumSize = new Size(860, 0);
            _lblEmptyState.ForeColor = Color.FromArgb(70, 70, 70);
            _lblEmptyState.Margin = new Padding(0, 0, 0, 6);
            _lblEmptyState.Text = EmptyStateText;

            _lblInputs = new Label();
            _lblInputs.Name = "lblInputs";
            _lblInputs.AutoSize = true;
            _lblInputs.MaximumSize = new Size(860, 0);
            _lblInputs.Text = "待处理：0 项";

            _lblEngineInfo = new Label();
            _lblEngineInfo.Name = "lblEngineInfo";
            _lblEngineInfo.AutoSize = true;
            _lblEngineInfo.MaximumSize = new Size(860, 0);
            _lblEngineInfo.ForeColor = Color.FromArgb(70, 70, 70);
            _lblEngineInfo.Text = "解压引擎：开始解压时自动定位（优先用本机 7-Zip ≥ 25.00，找不到或版本过低就用内置便携版）";

            _lstInputs = new ListView();
            _lstInputs.Name = "lstInputs";
            _lstInputs.View = View.Details;
            _lstInputs.FullRowSelect = true;
            _lstInputs.MultiSelect = false;
            _lstInputs.Height = 96;
            _lstInputs.Dock = DockStyle.Fill;
            _lstInputs.AccessibleName = "待处理的输入";
            _lstInputs.Columns.Add("来源", 420);
            _lstInputs.Columns.Add("类型", 70);
            _lstInputs.Columns.Add("数量", 70);
            _lstInputs.Columns.Add("解析结果", 320);

            dropInner.Controls.Add(_lblDropHint, 0, 0);
            dropInner.Controls.Add(pickRow, 0, 1);
            dropInner.Controls.Add(_lblEmptyState, 0, 2);
            dropInner.Controls.Add(_lblInputs, 0, 3);
            dropInner.Controls.Add(_lblEngineInfo, 0, 4);
            dropInner.Controls.Add(_lstInputs, 0, 5);
            _dropZone.Controls.Add(dropInner);
            _root.Controls.Add(_dropZone, 0, 1);

            // ---- 2) 选项：三个安全复选框 + 独立的红色「危险操作」区 + 可折叠高级项 ----
            _panelOptions = new FlowLayoutPanel();
            _panelOptions.Name = "panelOptions";
            _panelOptions.AutoSize = true;
            _panelOptions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _panelOptions.FlowDirection = FlowDirection.TopDown;
            _panelOptions.WrapContents = false;
            _panelOptions.Margin = new Padding(0, 0, 0, 8);

            FlowLayoutPanel safeRow = new FlowLayoutPanel();
            safeRow.AutoSize = true;
            safeRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            safeRow.FlowDirection = FlowDirection.LeftToRight;

            _chkCamouflage = MakeOption("chkCamouflage", "识别伪装后缀", true,
                "按内容识别改了后缀名的压缩包（.jpg 其实是 zip 这类）");
            _chkVolumes = MakeOption("chkVolumes", "拼合分卷", true,
                "把分卷集交给 7-Zip 按权威成员打开；缺卷会报出具体缺哪一个");
            _chkDict = MakeOption("chkDict", "尝试密码字典", true,
                "先试手动密码，再试内置常用字典与导入的字典，最后看同目录的密码线索");
            // 修复轮 Minor 1：这三个开关在 v1.0 里是 Core 的**固定行为**，界面没有对应的 RunOptions
            // 开关（BuildOptions 不读它们）。以前它们是可点的复选框、而取消勾选什么都不改变 ——
            // 一个点了没反应的控件就是在骗人。控制方裁定：**不加 Core 开关**，把界面改诚实 ——
            // 置灰 + 提示语写明「v1.0 固定行为，不可关闭」，同时保留给用户看见（说明工具做了什么）。
            MakeFixedOption(_chkCamouflage);
            MakeFixedOption(_chkVolumes);
            MakeFixedOption(_chkDict);
            safeRow.Controls.Add(_chkCamouflage);
            safeRow.Controls.Add(_chkVolumes);
            safeRow.Controls.Add(_chkDict);
            _panelOptions.Controls.Add(safeRow);

            // A5：危险项独立成区、红色、措辞写明后果（默认**不勾**）。
            _grpDanger = new GroupBox();
            _grpDanger.Name = "grpDanger";
            _grpDanger.Text = "⚠ 危险操作";
            _grpDanger.ForeColor = Color.FromArgb(178, 34, 34);
            _grpDanger.AutoSize = true;
            _grpDanger.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _grpDanger.Padding = new Padding(8);

            FlowLayoutPanel dangerInner = new FlowLayoutPanel();
            dangerInner.AutoSize = true;
            dangerInner.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            dangerInner.FlowDirection = FlowDirection.TopDown;
            dangerInner.WrapContents = false;

            _chkDelete = new CheckBox();
            _chkDelete.Name = "chkDelete";
            _chkDelete.Text = "解压成功后删除原包（移入回收站）";
            _chkDelete.AutoSize = true;
            _chkDelete.Checked = false;      // 不变式 I3：界面层也是默认**关**
            _chkDelete.ForeColor = Color.FromArgb(178, 34, 34);
            _chkDelete.AccessibleName = "解压成功后删除原包（默认关闭）";

            Label lblDangerNote = new Label();
            lblDangerNote.AutoSize = true;
            lblDangerNote.MaximumSize = new Size(820, 0);
            lblDangerNote.ForeColor = Color.FromArgb(178, 34, 34);
            lblDangerNote.Text = "勾选之后的后果：解压成功且校验通过的原包会被移入回收站；" +
                "失败、跳过、需要密码、未处理的包一律保留。开始前还会再确认一次。";

            dangerInner.Controls.Add(_chkDelete);
            dangerInner.Controls.Add(lblDangerNote);
            _grpDanger.Controls.Add(dangerInner);
            _panelOptions.Controls.Add(_grpDanger);

            // 高级选项（J11：默认折叠）。
            _btnAdvanced = new Button();
            _btnAdvanced.Name = "btnAdvanced";
            _btnAdvanced.Text = "▸ 密码设置（可折叠）";
            _btnAdvanced.AutoSize = true;
            _btnAdvanced.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _btnAdvanced.AccessibleName = "展开或折叠密码设置";
            _btnAdvanced.Click += BtnAdvanced_Click;
            _panelOptions.Controls.Add(_btnAdvanced);

            _panelAdvanced = new Panel();
            _panelAdvanced.Name = "panelAdvanced";
            _panelAdvanced.AutoSize = true;
            _panelAdvanced.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _panelAdvanced.Visible = false;
            _panelAdvanced.Padding = new Padding(12, 6, 0, 6);

            FlowLayoutPanel advancedInner = new FlowLayoutPanel();
            advancedInner.AutoSize = true;
            advancedInner.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            advancedInner.FlowDirection = FlowDirection.TopDown;
            advancedInner.WrapContents = false;

            _lblPasswordHint = new Label();
            _lblPasswordHint.AutoSize = true;
            _lblPasswordHint.MaximumSize = new Size(820, 0);
            _lblPasswordHint.Text = "手动密码（密码只交给解压引擎，绝不写进日志、报告或恢复记录）：";

            _txtPassword = new TextBox();
            _txtPassword.Name = "txtPassword";
            _txtPassword.UseSystemPasswordChar = true;
            _txtPassword.Width = 260;
            _txtPassword.AccessibleName = "手动密码";

            _chkRememberRun = new CheckBox();
            _chkRememberRun.Name = "chkRememberRun";
            _chkRememberRun.Text = "本次运行记住此密码";
            _chkRememberRun.AutoSize = true;

            FlowLayoutPanel dictRow = new FlowLayoutPanel();
            dictRow.AutoSize = true;
            dictRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            dictRow.FlowDirection = FlowDirection.LeftToRight;

            _btnLoadDict = new Button();
            _btnLoadDict.Name = "btnLoadDict";
            _btnLoadDict.Text = "导入密码字典…";
            _btnLoadDict.AutoSize = true;
            _btnLoadDict.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _btnLoadDict.Click += BtnLoadDict_Click;

            _lblDict = new Label();
            _lblDict.AutoSize = true;
            _lblDict.Text = "未导入字典（将使用内置常用字典）";

            dictRow.Controls.Add(_btnLoadDict);
            dictRow.Controls.Add(_lblDict);

            FlowLayoutPanel depthRow = new FlowLayoutPanel();
            depthRow.AutoSize = true;
            depthRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            depthRow.FlowDirection = FlowDirection.LeftToRight;

            Label lblDepth = new Label();
            lblDepth.AutoSize = true;
            lblDepth.Text = "递归层数上限：";
            lblDepth.Margin = new Padding(0, 6, 0, 0);

            _numDepth = new NumericUpDown();
            _numDepth.Name = "numDepth";
            _numDepth.Minimum = 1;
            _numDepth.Maximum = 64;
            _numDepth.Value = new RunOptions().MaxDepth;   // 规格 §10.1 的默认 10 层，取默认值而不是硬编码
            _numDepth.Width = 60;

            Label lblDepthNote = new Label();
            lblDepthNote.AutoSize = true;
            lblDepthNote.Text = "（触顶的项会被显式列为「未处理」，绝不静默停止）";
            lblDepthNote.ForeColor = Color.FromArgb(70, 70, 70);
            lblDepthNote.Margin = new Padding(8, 6, 0, 0);

            depthRow.Controls.Add(lblDepth);
            depthRow.Controls.Add(_numDepth);
            depthRow.Controls.Add(lblDepthNote);

            advancedInner.Controls.Add(_lblPasswordHint);
            advancedInner.Controls.Add(_txtPassword);
            advancedInner.Controls.Add(_chkRememberRun);
            advancedInner.Controls.Add(dictRow);
            advancedInner.Controls.Add(depthRow);
            _panelAdvanced.Controls.Add(advancedInner);
            _panelOptions.Controls.Add(_panelAdvanced);
            _root.Controls.Add(_panelOptions, 0, 2);

            // ---- 3) 开始 / 取消 ----
            _panelActions = new FlowLayoutPanel();
            _panelActions.AutoSize = true;
            _panelActions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _panelActions.FlowDirection = FlowDirection.LeftToRight;
            _panelActions.Margin = new Padding(0, 0, 0, 6);

            _btnStart = new Button();
            _btnStart.Name = "btnStart";
            _btnStart.Text = "开始解压";
            _btnStart.AutoSize = true;
            _btnStart.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _btnStart.Padding = new Padding(22, 8, 22, 8);
            _btnStart.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            _btnStart.Enabled = false;                 // 没有输入就不给按（空状态下按钮亮着只会误导）
            _btnStart.AccessibleName = "开始解压";
            _btnStart.Click += BtnStart_Click;

            _btnCancel = new Button();
            _btnCancel.Name = "btnCancel";
            _btnCancel.Text = "取消";
            _btnCancel.AutoSize = true;
            _btnCancel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _btnCancel.Padding = new Padding(14, 8, 14, 8);
            _btnCancel.Enabled = false;
            _btnCancel.AccessibleName = "取消本次运行";
            _btnCancel.Click += BtnCancel_Click;

            _panelActions.Controls.Add(_btnStart);
            _panelActions.Controls.Add(_btnCancel);
            _root.Controls.Add(_panelActions, 0, 3);

            // ---- 4) 进度 ----
            _panelProgress = new Panel();
            _panelProgress.AutoSize = true;
            _panelProgress.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _panelProgress.Dock = DockStyle.Fill;
            _panelProgress.Margin = new Padding(0, 0, 0, 4);

            _progressBar = new ProgressBar();
            _progressBar.Name = "progressBar";
            _progressBar.Dock = DockStyle.Top;
            _progressBar.Height = 18;
            _progressBar.Minimum = 0;
            _progressBar.Maximum = 100;
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.AccessibleName = "进度";

            // 预检摘要（修复轮 Finding 2）：**每次**开始解压都在这里就地写出「要做什么」的摘要 ——
            // 目标项数、删除开关的后果、输出去向（规格 §6.11 的原地语义）、引擎定位结果。
            // 它不是模态框：不抢焦点、不阻塞运行、不挡按钮（J7/J14 的同一原则）；几十秒后自动收起，
            // 同一份文案另有一份进了日志文件（复盘时看得见）。**绝不**显示还没定位的引擎占位值。
            _lblSummary = new Label();
            _lblSummary.Name = "lblSummary";
            _lblSummary.AutoSize = true;
            _lblSummary.MaximumSize = new Size(880, 0);
            _lblSummary.BackColor = Color.FromArgb(240, 247, 255);
            _lblSummary.ForeColor = Color.FromArgb(20, 60, 110);
            _lblSummary.Padding = new Padding(8, 6, 8, 6);
            _lblSummary.Margin = new Padding(0, 0, 0, 6);
            _lblSummary.Visible = false;
            _lblSummary.AccessibleName = "本次运行的预检摘要";

            _lblProgress = new Label();
            _lblProgress.Name = "lblProgress";
            _lblProgress.AutoSize = true;
            _lblProgress.Dock = DockStyle.Bottom;
            _lblProgress.Padding = new Padding(0, 2, 0, 2);
            _lblProgress.Text = "空闲。加入压缩包或文件夹后点「开始解压」。";

            // 停靠顺序即层序：先加的在最外侧（Top），所以摘要会出现在进度条**上方**。
            _panelProgress.Controls.Add(_lblSummary);
            _panelProgress.Controls.Add(_lblProgress);
            _panelProgress.Controls.Add(_progressBar);
            _root.Controls.Add(_panelProgress, 0, 4);

            // ---- 5) 一行状态 + 计数行 ----
            _panelStatus = new Panel();
            _panelStatus.AutoSize = true;
            _panelStatus.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _panelStatus.Dock = DockStyle.Fill;
            _panelStatus.Margin = new Padding(0, 0, 0, 4);

            _panelCounters = new FlowLayoutPanel();
            _panelCounters.AutoSize = true;
            _panelCounters.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _panelCounters.FlowDirection = FlowDirection.LeftToRight;
            _panelCounters.Dock = DockStyle.Bottom;

            _lblCounters = new Label();
            _lblCounters.Name = "lblCounters";
            _lblCounters.AutoSize = true;
            _lblCounters.Margin = new Padding(0, 4, 12, 0);
            _lblCounters.Text = "";

            // 会话口径（修复轮 Finding 3）：重试轮结束时「本轮」是 0 失败，但整场可能已经攒了 30 个
            // 失败 —— 只显示一个口径就会把用户骗过去。两个口径**并排常显**，各自带标签。
            _lblSessionCounters = new Label();
            _lblSessionCounters.Name = "lblSessionCounters";
            _lblSessionCounters.AutoSize = true;
            _lblSessionCounters.ForeColor = Color.FromArgb(70, 70, 70);
            _lblSessionCounters.Margin = new Padding(0, 4, 12, 0);
            _lblSessionCounters.Text = "";

            _btnDetails = new Button();
            _btnDetails.Name = "btnDetails";
            _btnDetails.Text = "查看详情 ▸";
            _btnDetails.AutoSize = true;
            _btnDetails.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _btnDetails.AccessibleName = "展开或折叠详情与日志";
            _btnDetails.Click += BtnDetails_Click;

            _panelCounters.Controls.Add(_lblCounters);
            _panelCounters.Controls.Add(_lblSessionCounters);
            _panelCounters.Controls.Add(_btnDetails);

            _lblStatus = new Label();
            _lblStatus.Name = "lblStatus";
            _lblStatus.AutoSize = true;
            _lblStatus.MaximumSize = new Size(880, 0);
            _lblStatus.Dock = DockStyle.Top;
            _lblStatus.Padding = new Padding(0, 2, 0, 2);
            _lblStatus.Text = "状态：等待开始。";

            _panelStatus.Controls.Add(_lblStatus);
            _panelStatus.Controls.Add(_panelCounters);
            _root.Controls.Add(_panelStatus, 0, 5);

            // ---- 6) 密码：**停靠式非模态**面板（J6）----
            _panelPasswordAsk = new Panel();
            _panelPasswordAsk.Name = "panelPasswordAsk";
            _panelPasswordAsk.AutoSize = true;
            _panelPasswordAsk.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _panelPasswordAsk.Dock = DockStyle.Fill;
            _panelPasswordAsk.BackColor = Color.FromArgb(255, 248, 220);
            _panelPasswordAsk.Padding = new Padding(10);
            _panelPasswordAsk.Margin = new Padding(0, 0, 0, 6);
            _panelPasswordAsk.Visible = false;

            TableLayoutPanel pwInner = new TableLayoutPanel();
            pwInner.Dock = DockStyle.Fill;
            pwInner.AutoSize = true;
            pwInner.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            pwInner.ColumnCount = 1;
            pwInner.RowCount = 3;

            _lblPasswordAsk = new Label();
            _lblPasswordAsk.Name = "lblPasswordAsk";
            _lblPasswordAsk.AutoSize = true;
            _lblPasswordAsk.MaximumSize = new Size(860, 0);
            _lblPasswordAsk.Text = "🔒 当前压缩包需要密码 — 还有 0 个待处理。";

            FlowLayoutPanel pwRow = new FlowLayoutPanel();
            pwRow.AutoSize = true;
            pwRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            pwRow.FlowDirection = FlowDirection.LeftToRight;

            _txtPasswordAsk = new TextBox();
            _txtPasswordAsk.Name = "txtPasswordAsk";
            _txtPasswordAsk.UseSystemPasswordChar = true;
            _txtPasswordAsk.Width = 240;
            _txtPasswordAsk.AccessibleName = "为当前压缩包输入的密码";

            _btnPwThisOnly = new Button();
            _btnPwThisOnly.Name = "btnPwThisOnly";
            _btnPwThisOnly.Text = "仅此压缩包";
            _btnPwThisOnly.AutoSize = true;
            _btnPwThisOnly.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _btnPwThisOnly.Click += BtnPwThisOnly_Click;

            _btnPwRememberAll = new Button();
            _btnPwRememberAll.Name = "btnPwRememberAll";
            _btnPwRememberAll.Text = "本次运行全部记住";
            _btnPwRememberAll.AutoSize = true;
            _btnPwRememberAll.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _btnPwRememberAll.Click += BtnPwRememberAll_Click;

            _btnPwSkip = new Button();
            _btnPwSkip.Name = "btnPwSkip";
            _btnPwSkip.Text = "跳过此压缩包";
            _btnPwSkip.AutoSize = true;
            _btnPwSkip.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _btnPwSkip.Click += BtnPwSkip_Click;

            pwRow.Controls.Add(_txtPasswordAsk);
            pwRow.Controls.Add(_btnPwThisOnly);
            pwRow.Controls.Add(_btnPwRememberAll);
            pwRow.Controls.Add(_btnPwSkip);

            Label lblPwNote = new Label();
            lblPwNote.AutoSize = true;
            lblPwNote.MaximumSize = new Size(860, 0);
            lblPwNote.Text = "本面板「不会」挡住正在运行的批次（不是模态框）：填好后选「仅此压缩包」或" +
                "「本次运行全部记住」，跳过的包会记为「跳过（需要密码）」，「不是失败」。";

            pwInner.Controls.Add(_lblPasswordAsk, 0, 0);
            pwInner.Controls.Add(pwRow, 0, 1);
            pwInner.Controls.Add(lblPwNote, 0, 2);
            _panelPasswordAsk.Controls.Add(pwInner);
            _root.Controls.Add(_panelPasswordAsk, 0, 6);

            // ---- 7) 详情：逐项列表 + 逐项动作 + 自绘日志尾部 ----
            _panelDetails = new Panel();
            _panelDetails.Name = "panelDetails";
            _panelDetails.Dock = DockStyle.Fill;
            _panelDetails.Visible = false;

            TableLayoutPanel detailsInner = new TableLayoutPanel();
            detailsInner.Dock = DockStyle.Fill;
            detailsInner.ColumnCount = 1;
            detailsInner.RowCount = 5;
            detailsInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            detailsInner.RowStyles.Add(new RowStyle(SizeType.Percent, 45F));
            detailsInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            detailsInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            detailsInner.RowStyles.Add(new RowStyle(SizeType.Percent, 55F));

            _panelLogTools = new FlowLayoutPanel();
            _panelLogTools.AutoSize = true;
            _panelLogTools.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _panelLogTools.FlowDirection = FlowDirection.LeftToRight;

            _btnOpenLog = new Button();
            _btnOpenLog.Name = "btnOpenLog";
            _btnOpenLog.Text = "打开日志文件";
            _btnOpenLog.AutoSize = true;
            _btnOpenLog.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _btnOpenLog.Enabled = false;
            _btnOpenLog.AccessibleName = "打开完整日志文件";
            _btnOpenLog.Click += BtnOpenLog_Click;

            // 规格 §8：UI「关于/开源许可」+ 随包 THIRD-PARTY-NOTICES.txt（Task 15 报告 §4 建议的
            // 接点：与「打开日志文件」同一个工具条）。文本来自 exe 旁边的许可文件；文件缺席/读不动
            // 时给指名期望路径的中文说明 —— 这扇入口绝不抛异常（BuildLicenseNotice 把一切读取
            // 异常都转成了说明文本），更不允许变成一次崩溃。
            _btnLicense = new Button();
            _btnLicense.Name = "btnLicense";
            _btnLicense.Text = "关于/开源许可";
            _btnLicense.AutoSize = true;
            _btnLicense.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _btnLicense.AccessibleName = "查看开源许可声明（THIRD-PARTY-NOTICES.txt）";
            _btnLicense.Click += BtnLicense_Click;

            // J4：暂停自动滚动 —— 用户往上翻的时候日志一直往下跳，本身就是个 bug。
            _chkAutoScroll = new CheckBox();
            _chkAutoScroll.Name = "chkAutoScroll";
            _chkAutoScroll.Text = "自动滚动";
            _chkAutoScroll.AutoSize = true;
            _chkAutoScroll.Checked = true;
            _chkAutoScroll.Margin = new Padding(12, 6, 0, 0);
            _chkAutoScroll.CheckedChanged += ChkAutoScroll_CheckedChanged;

            _panelLogTools.Controls.Add(_btnOpenLog);
            _panelLogTools.Controls.Add(_btnLicense);
            _panelLogTools.Controls.Add(_chkAutoScroll);

            _lstItems = new ListView();
            _lstItems.Name = "lstItems";
            _lstItems.View = View.Details;
            _lstItems.FullRowSelect = true;
            _lstItems.MultiSelect = false;
            _lstItems.HideSelection = false;
            _lstItems.Dock = DockStyle.Fill;
            _lstItems.AccessibleName = "逐项结果";
            _lstItems.Columns.Add("压缩包", 400);
            _lstItems.Columns.Add("结局", 130);
            _lstItems.Columns.Add("文件/失败", 90);
            _lstItems.Columns.Add("说明", 420);
            _lstItems.SelectedIndexChanged += LstItems_SelectedIndexChanged;
            // 修复轮 Minor 12：双击**不再**绑到「打开输出目录」。以前双击列表里任意一行（包括
            // 还没产出任何东西的失败行）都会去开输出目录，而且选中行的语义与双击目标无关 ——
            // 一个「打开文件夹」的动作必须有明确的按钮/菜单项，不能挂在双击上。
            // 打开输出目录由按钮 _btnOpenOutput 与菜单项「打开输出目录」承担（都对选中行生效）。

            // 右键菜单的四个项与四个按钮**逐一同序**地存进数组，于是可用性由 SetItemActionsEnabled
            // 一处决定（修复轮 Finding 1：以前菜单项从不被禁用，两个界面入口会互相矛盾）。
            _itemMenu = new ContextMenuStrip();
            _itemMenuItems[0] = new ToolStripMenuItem("强制按压缩包尝试", null, BtnForceArchive_Click);
            _itemMenuItems[1] = new ToolStripMenuItem("输入密码…", null, BtnEnterPassword_Click);
            _itemMenuItems[2] = new ToolStripMenuItem("重试", null, BtnRetry_Click);
            _itemMenuItems[3] = new ToolStripMenuItem("打开输出目录", null, BtnOpenOutput_Click);
            for (int i = 0; i < _itemMenuItems.Length; i++)
            {
                _itemMenuItems[i].Enabled = false;      // 与按钮同样的初值：没有选中项就不可用
                _itemMenu.Items.Add(_itemMenuItems[i]);
            }
            _lstItems.ContextMenuStrip = _itemMenu;

            _panelItemActions = new FlowLayoutPanel();
            _panelItemActions.AutoSize = true;
            _panelItemActions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _panelItemActions.FlowDirection = FlowDirection.LeftToRight;
            _panelItemActions.Margin = new Padding(0, 4, 0, 4);

            _btnForceArchive = MakeItemAction("btnForceArchive", "强制按压缩包尝试", BtnForceArchive_Click);
            _btnEnterPassword = MakeItemAction("btnEnterPassword", "输入密码", BtnEnterPassword_Click);
            _btnRetry = MakeItemAction("btnRetry", "重试", BtnRetry_Click);
            _btnOpenOutput = MakeItemAction("btnOpenOutput", "打开输出目录", BtnOpenOutput_Click);
            _panelItemActions.Controls.Add(_btnForceArchive);
            _panelItemActions.Controls.Add(_btnEnterPassword);
            _panelItemActions.Controls.Add(_btnRetry);
            _panelItemActions.Controls.Add(_btnOpenOutput);

            _lblLogHint = new Label();
            _lblLogHint.Name = "lblLogHint";
            _lblLogHint.AutoSize = true;
            _lblLogHint.MaximumSize = new Size(880, 0);
            _lblLogHint.ForeColor = Color.FromArgb(70, 70, 70);
            _lblLogHint.Text = "运行日志（只显示最后 " + LogTailLines +
                " 行；完整日志流式写入文件，点「打开日志文件」查看）";

            _logView = new LogTailView();
            _logView.Name = "logView";
            _logView.Dock = DockStyle.Fill;
            _logView.AccessibleName = "运行日志尾部";
            _logView.AutoScroll = true;

            detailsInner.Controls.Add(_panelLogTools, 0, 0);
            detailsInner.Controls.Add(_lstItems, 0, 1);
            detailsInner.Controls.Add(_panelItemActions, 0, 2);
            detailsInner.Controls.Add(_lblLogHint, 0, 3);
            detailsInner.Controls.Add(_logView, 0, 4);
            _panelDetails.Controls.Add(detailsInner);
            _root.Controls.Add(_panelDetails, 0, 7);

            Controls.Add(_root);

            // J9/J10：拖到窗口任何地方（不只是拖放区）都收。
            DragEnter += DropZone_DragEnter;
            DragOver += DropZone_DragEnter;
            DragDrop += DropZone_DragDrop;

            FormClosing += MainForm_FormClosing;

            _uiTimer = new System.Windows.Forms.Timer();
            _uiTimer.Interval = UiCoalesceMs;
            _uiTimer.Tick += UiTimer_Tick;

            SetDetailsVisible(false);
            RefreshInputSummary();

            ResumeLayout(true);
        }

        private CheckBox MakeOption(string name, string text, bool isChecked, string tip)
        {
            CheckBox box = new CheckBox();
            box.Name = name;
            box.Text = text;
            box.Checked = isChecked;
            box.AutoSize = true;
            box.Margin = new Padding(0, 0, 16, 0);
            box.AccessibleName = tip;

            _tooltip.SetToolTip(box, tip);
            return box;
        }

        // 一个共享的 ToolTip（每个控件都 new 一个会在窗体销毁时留下没人释放的组件）。
        private readonly ToolTip _tooltip = new ToolTip();

        // 修复轮 Minor 1：把「v1.0 固定行为、不可关闭」这件事同时写进**控件状态**（置灰）与
        // **提示语**（ToolTip + AccessibleName）。置灰是必须的那一半：一个可点的复选框被点掉却
        // 什么都不改变，就是在骗用户。文案与判定都收在这两个成员里，用例直接断言它们。
        internal const string FixedOptionNote = "（v1.0 固定行为，不可关闭）";

        internal static string FixedOptionTooltip(string behavior)
        {
            return behavior + FixedOptionNote;
        }

        private void MakeFixedOption(CheckBox box)
        {
            string behavior = box.AccessibleName == null ? box.Text : box.AccessibleName;
            if (behavior == null) { behavior = ""; }

            box.Enabled = false;
            box.AccessibleName = FixedOptionTooltip(behavior);
            _tooltip.SetToolTip(box, FixedOptionTooltip(behavior));
        }

        private Button MakeItemAction(string name, string text, EventHandler handler)
        {
            Button button = new Button();
            button.Name = name;
            button.Text = text;
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.Enabled = false;
            button.Click += handler;
            return button;
        }

        // J2：检测已提权并显示**常驻非模态横幅**。绝不用模态框 —— 那会在启动时挡住一切，
        // 而这里要传达的恰恰是「别用拖放，用按钮」，横幅正好不挡按钮。
        private void DetectElevation()
        {
            try
            {
                System.Security.Principal.WindowsIdentity identity =
                    System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal principal =
                    new System.Security.Principal.WindowsPrincipal(identity);
                if (principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                {
                    _lblElevation.Visible = true;
                    AppendLog("警告：本进程以管理员身份运行 —— Windows UIPI 会静默拦截资源管理器拖放，" +
                              "只能用「选择…」按钮。建议以普通权限重新打开。");
                }
            }
            catch (Exception)
            {
                // 查不出来就当没提权：这只是一条提示，绝不因为它挡住界面启动。
            }
        }

        private void ApplySettings()
        {
            if (_settings.HasGeometry && _settings.WindowWidth > 200 && _settings.WindowHeight > 200)
            {
                Size size = new Size(_settings.WindowWidth, _settings.WindowHeight);
                // 存下来的位置可能已经不在任何屏幕上（换了显示器 / 拔了外接屏）：只接受可见的位置。
                Rectangle wanted = new Rectangle(new Point(_settings.WindowX, _settings.WindowY), size);
                if (Screen.AllScreens != null && IsOnAnyScreen(wanted))
                {
                    StartPosition = FormStartPosition.Manual;
                    Bounds = wanted;
                }
                else
                {
                    StartPosition = FormStartPosition.CenterScreen;
                    // 用 Size（外框尺寸）而不是 ClientSize：存下来的就是 Bounds（外框），
                    // 两个量纲混用会让窗口每次启动缩掉一圈边框。
                    Size = size;
                }

                if (_settings.Maximized) { WindowState = FormWindowState.Maximized; }
            }
        }

        private static bool IsOnAnyScreen(Rectangle bounds)
        {
            try
            {
                foreach (Screen screen in Screen.AllScreens)
                {
                    if (screen.WorkingArea.IntersectsWith(bounds)) { return true; }
                }
            }
            catch (Exception) { }
            return false;
        }

        // ==================================================================
        // 输入（拖放 / 选择 / 后台枚举；J9、J10）
        // ==================================================================

        private void DropZone_DragEnter(object sender, DragEventArgs e)
        {
            // 只接受文件拖放（拖进来的文本/网址没有任何意义）。
            e.Effect = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }

        private void DropZone_DragDrop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) { return; }
                string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                AddInputs(paths);
            }
            catch (Exception ex)
            {
                // 拖进来的东西再古怪也不能掀翻界面（规格 §6.3：不崩溃、不静默丢弃）。
                Warn("无法接收拖入的内容（" + ex.GetType().Name + "：" + ex.Message + "）");
            }
        }

        private void BtnPickFolder_Click(object sender, EventArgs e)
        {
            FolderBrowserDialog dialog = new FolderBrowserDialog();
            dialog.Description = "选择包含压缩包的文件夹（会递归展开其中的文件，逐个判断是不是压缩包）";
            dialog.ShowNewFolderButton = false;
            if (!string.IsNullOrEmpty(_settings.LastInputFolder)) { dialog.SelectedPath = _settings.LastInputFolder; }

            try
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
                _settings.LastInputFolder = dialog.SelectedPath;
                AddInputs(new string[] { dialog.SelectedPath });
            }
            catch (Exception ex)
            {
                Warn("打开文件夹选择框失败（" + ex.GetType().Name + "：" + ex.Message + "）");
            }
        }

        private void BtnPickFiles_Click(object sender, EventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Title = "选择压缩包（可多选）";
            dialog.Multiselect = true;
            dialog.Filter = "压缩包|*.zip;*.rar;*.7z;*.tar;*.gz;*.tgz;*.bz2;*.xz;*.cab;*.iso;*.001;*.lnk|所有文件|*.*";
            if (!string.IsNullOrEmpty(_settings.LastInputFolder)) { dialog.InitialDirectory = _settings.LastInputFolder; }

            try
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
                if (dialog.FileNames != null && dialog.FileNames.Length > 0)
                {
                    try { _settings.LastInputFolder = Path.GetDirectoryName(dialog.FileNames[0]); }
                    catch (Exception) { }
                }
                AddInputs(dialog.FileNames);
            }
            catch (Exception ex)
            {
                Warn("打开文件选择框失败（" + ex.GetType().Name + "：" + ex.Message + "）");
            }
        }

        // 收下一批路径。目录走**后台**枚举（J9：巨型文件夹不能在 UI 线程上枚举，否则「无响应」），
        // .lnk 解析并显式展示（J8），其余按文件收下。一律按规范化路径去重。
        private void AddInputs(string[] paths)
        {
            if (paths == null || paths.Length == 0) { return; }

            if (_running || _scanRemaining > 0)
            {
                // J10：运行中拖进来的项进「待处理（下一批）」，并**明说**这件事 —— 静默忽略或污染
                // 正在跑的批次都是不可接受的行为。
                _nextBatchOnly = true;
                Warn("已加入「待处理（下一批）」：本次运行不会带上新加入的项。");
            }

            List<string> folders = new List<string>();

            foreach (string raw in paths)
            {
                if (string.IsNullOrEmpty(raw)) { continue; }

                string full;
                try { full = Path.GetFullPath(raw); }
                catch (Exception ex)
                {
                    AddInputRow(raw, "警告", "-", "路径无效（" + ex.GetType().Name + "）：" + raw);
                    continue;
                }

                // source = 用户拖/选进来的那个路径（列表第一列**原样**展示它）；
                // resolved = 真正要处理的路径（.lnk 解析之后）。两者不同时，note 里写出解析结果。
                string source = full;
                string resolved = full;
                string note = "";
                bool isShortcut = string.Equals(Path.GetExtension(full), ".lnk", StringComparison.OrdinalIgnoreCase);

                if (isShortcut)
                {
                    // J8：拖进来的是 .lnk 路径而不是它的目标。解析并**显式展示**，否则用户只会看到
                    //「不是有效压缩包」而不知道原因。解析失败只是逐项告警，绝不崩溃。
                    string target;
                    string problem;
                    if (TryResolveShortcut(full, out target, out problem))
                    {
                        resolved = target;
                        note = "→ " + target;
                    }
                    else
                    {
                        note = "⚠ 快捷方式解析失败：" + problem;
                    }
                }

                if (Directory.Exists(resolved))
                {
                    if (_seen.Contains(resolved))
                    {
                        AddInputRow(source, "文件夹", "重复", note + "（已经加过了，按规范化路径去重）");
                        continue;
                    }
                    _seen.Add(resolved);
                    folders.Add(resolved);
                    _inputRowOf[resolved] = AddInputRow(source, "文件夹", "正在统计…", note);
                    continue;
                }

                if (!File.Exists(resolved))
                {
                    // 不存在 / 解析不出来的目标：**绝不静默丢弃**。照样交给 Core，让它如实报
                    //「目标不存在或不可读」并在逐项结果里出现（用户才知道自己拖错了什么）。
                    AddInputRow(source, isShortcut ? "快捷方式" : "文件", "-",
                        note.Length > 0 ? note : "⚠ 不存在或不可读：" + resolved);
                    AddCandidate(resolved);
                    continue;
                }

                if (AddCandidate(resolved))
                {
                    AddInputRow(source, isShortcut ? "快捷方式" : "文件", "1", note);
                }
                else
                {
                    AddInputRow(source, "文件", "重复", note + "（已经加过了，按规范化路径去重）");
                }
            }

            if (folders.Count > 0) { StartEnumeration(folders); }
            RefreshInputSummary();
        }

        // 收下一个候选文件（去重）。返回 false = 已经有了。
        private bool AddCandidate(string full)
        {
            if (_seen.Contains(full)) { return false; }
            _seen.Add(full);
            _inputs.Add(full);
            return true;
        }

        private int AddInputRow(string source, string kind, string count, string note)
        {
            ListViewItem item = new ListViewItem(source);
            item.SubItems.Add(kind);
            item.SubItems.Add(count);
            item.SubItems.Add(note);
            _lstInputs.Items.Add(item);
            return _lstInputs.Items.Count - 1;
        }

        private void RefreshInputSummary()
        {
            int folders = 0;
            foreach (KeyValuePair<string, int> pair in _inputRowOf) { folders++; }

            // 单位是**项**（候选文件），不是「压缩包」：枚举层不按后缀过滤，「是不是压缩包」由 Core
            // 逐个判（修复轮 Minor 2）。文件夹数照旧写出来 —— 它是用户指过的范围，不是判定结果。
            string text = "待处理：" + _inputs.Count + " 项（候选文件，来自 " + folders + " 个文件夹）";
            if (_scanRemaining > 0) { text += " · 正在统计：" + _scanFound + " 个文件"; }
            if (_nextBatchOnly) { text += " · 新加入的项进入「下一批」"; }
            _lblInputs.Text = text;

            bool hasInputs = _inputs.Count > 0 || _scanRemaining > 0;
            _lblEmptyState.Visible = _inputs.Count == 0 && _scanRemaining == 0;
            if (!_running) { _btnStart.Enabled = hasInputs && !_nextBatchOnly; }
        }

        // 后台枚举（J9：显示「正在统计…」、可取消、按规范化路径去重、文件夹折叠为一行显示文件数）。
        //
        // 【为什么**不**按后缀过滤】在枚举层按扩展名筛掉「不像压缩包」的文件，正好会把本工具要认出来的
        // 伪装后缀（.jpg 其实是 zip）一起扔掉 —— 那是规格 §6.3 禁止的静默丢弃。格式判定是 Core 的
        // Sniffer 的职责，这里只负责把「用户指到的所有文件」原样交给它，并在界面上如实展示数量。
        private void StartEnumeration(List<string> folders)
        {
            _scanCts = new CancellationTokenSource();
            CancellationToken token = _scanCts.Token;
            _scanRemaining++;
            _scanFound = 0;

            Thread worker = new Thread(delegate()
            {
                List<string> found = new List<string>();
                List<string> notes = new List<string>();
                Dictionary<string, int> perFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (string folder in folders)
                {
                    if (token.IsCancellationRequested) { break; }
                    int before = found.Count;
                    EnumerateFilesSafe(folder, token, found, notes);
                    perFolder[folder] = found.Count - before;
                }

                if (token.IsCancellationRequested) { notes.Add("扫描被取消：已经找到的那部分仍然有效（不是零）"); }

                UiPost(delegate
                {
                    _scanFound = found.Count;
                    FinishEnumeration(folders, perFolder, found, notes);
                });
            });
            worker.IsBackground = true;
            worker.Name = "rerar-gui-scan";
            worker.Start();

            if (!_running)
            {
                _lblProgress.Text = FormatScanningText(0);
                _progressBar.Style = ProgressBarStyle.Marquee;
            }
            _uiTimer.Start();
        }

        // 手工递归（不用 Directory.GetFiles(AllDirectories)）：一，某个子目录读不了时不能把已经找到的
        // 全丢掉（GetFiles 会整体抛异常）；二，必须**跳过重解析点**，否则一个 junction 指回上层就是
        // 无限循环（研究 V17/V24 的那一族问题在枚举层也要挡）。
        private static void EnumerateFilesSafe(string root, CancellationToken token, List<string> into, List<string> notes)
        {
            Stack<string> pending = new Stack<string>();
            pending.Push(root);

            int visited = 0;
            while (pending.Count > 0)
            {
                if (token.IsCancellationRequested) { return; }
                if (++visited > 100000) { notes.Add(root + "：目录数超过 10 万个，已停止枚举"); return; }

                string dir = pending.Pop();
                string[] files;
                string[] subs;

                try
                {
                    files = Directory.GetFiles(dir);
                    subs = Directory.GetDirectories(dir);
                }
                catch (Exception ex)
                {
                    notes.Add(dir + "：无法读取（" + ex.GetType().Name + "）");
                    continue;
                }

                foreach (string file in files)
                {
                    if (token.IsCancellationRequested) { return; }
                    into.Add(file);
                }

                foreach (string sub in subs)
                {
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(sub);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            notes.Add(sub + "：是重解析点（软链/联接），已跳过以免循环");
                            continue;
                        }
                    }
                    catch (Exception)
                    {
                        // 取不到属性就照常下探：枚举层不必为此丢东西。
                    }
                    pending.Push(sub);
                }
            }
        }

        private void FinishEnumeration(List<string> folders, Dictionary<string, int> perFolder,
                                       List<string> found, List<string> notes)
        {
            if (_scanRemaining > 0) { _scanRemaining--; }

            int added = 0;
            foreach (string file in found)
            {
                if (AddCandidate(file)) { added++; }
            }

            foreach (string folder in folders)
            {
                int row;
                int count;
                if (!perFolder.TryGetValue(folder, out count)) { count = 0; }
                if (_inputRowOf.TryGetValue(folder, out row) && row < _lstInputs.Items.Count)
                {
                    _lstInputs.Items[row].SubItems[2].Text = count + " 个文件";
                }
            }

            foreach (string note in notes) { AppendLog("扫描提示：" + note); }
            if (notes.Count > 0) { Warn("扫描有 " + notes.Count + " 条提示（软链已跳过、个别目录读不了）；详见日志。"); }

            AppendLog("扫描完成：" + folders.Count + " 个文件夹共 " + found.Count + " 个文件（新增 " + added + " 个）");
            if (_scanRemaining == 0 && !_running)
            {
                _progressBar.Style = ProgressBarStyle.Continuous;
                _progressBar.Value = 0;
                _lblProgress.Text = _inputs.Count == 0
                    ? "空闲。加入压缩包或文件夹后点「开始解压」。"
                    : FormatScanningText(_inputs.Count);
            }
            RefreshInputSummary();
        }

        // ==================================================================
        // 开始运行（先弹预检摘要确认 —— J12）
        // ==================================================================

        private void BtnStart_Click(object sender, EventArgs e)
        {
            if (_running || _scanRemaining > 0) { return; }
            if (_inputs.Count == 0) { Warn("还没有可处理的文件：先拖入压缩包/文件夹，或用上面的按钮选择。"); return; }

            // 删除开启时：先弹**自定义**二次确认（A5）。它报出确切数量、不默认聚焦确定、Esc 只能返回。
            if (_chkDelete.Checked)
            {
                using (ConfirmDeleteForm confirm = new ConfirmDeleteForm(_inputs.Count))
                {
                    if (confirm.ShowDialog(this) != DialogResult.OK)
                    {
                        // 用户没确认 ⇒ **什么都不做**（绝不偷偷把删除开关关掉然后照跑：那也是替用户做主）。
                        _lblStatus.Text = "状态：已取消（删除开关仍然勾选着，但没有开始）。";
                        return;
                    }
                }
            }

            List<string> paths = new List<string>(_inputs);
            _nextBatchOnly = false;

            RunOptions options = BuildOptions();
            StartRun(paths, options, paths.Count);
        }

        private RunOptions BuildOptions()
        {
            RunOptions options = new RunOptions();
            // 【本方法**只**读真正的杠杆】修复轮 Minor 1：三个安全复选框在 v1.0 里是 Core 的**固定
            // 行为**（Core 没有「关掉伪装后缀识别」的开关，也不该有），所以它们不再可点、也不再被
            // 这里读取 —— 界面把这件事如实说出来（置灰 + 提示语），而不是假装它们能改变什么。
            // 真正的杠杆只有：删除、密码、字典、层数与逐项强制。
            options.DeleteOriginals = _chkDelete.Checked;       // I3：默认关，只有勾上才是 true
            options.MaxDepth = (int)_numDepth.Value;
            options.Password = _txtPassword.Text.Length > 0 ? _txtPassword.Text : null;
            if (_chkRememberRun.Checked && options.Password != null) { _runWidePassword = options.Password; }

            if (!string.IsNullOrEmpty(_dictPath))
            {
                try { options.DictLines = new List<string>(File.ReadAllLines(_dictPath)); }
                catch (Exception ex)
                {
                    Warn("读不出字典文件「" + _dictPath + "」（" + ex.GetType().Name + "）：本次不使用字典。");
                }
            }

            foreach (string forced in _forcedPaths) { options.ForceTreatAsArchive.Add(forced); }

            options.Cancellation = _runCts == null ? CancellationToken.None : _runCts.Token;
            return options;
        }

        // 起一次运行。paths 是这一批要处理的文件。
        private void StartRun(List<string> paths, RunOptions options, int discovered)
        {
            if (_running || paths == null || paths.Count == 0) { return; }

            // 会话累计（修复轮 Finding 3）：跨轮累加的真实计数与「本轮」分开存放。
            // 只加**已经收尾的那几轮**（未结束的那一轮由 _counters/_results 现场提供），于是
            //「本次会话」= _session* + 当前轮，绝不会把同一件事数两遍。
            if (_sessionRounds > 0)
            {
                _sessionResults.AddRange(_results);
                _sessionUnprocessed += _unprocessedCount;
            }
            _sessionRounds++;

            _runCts = new CancellationTokenSource();
            options.Cancellation = _runCts.Token;
            _options = options;
            _running = true;
            _counters.Started = 0;
            _counters.Finished = 0;
            _currentMember = "";
            _engineResolvedText = "";
            _discoveredAtStart = discovered;
            _lastSignalTicks = DateTime.Now.Ticks;
            _cancelRequestedAt = DateTime.MinValue;
            _watch = Stopwatch.StartNew();
            lock (_pendingGate) { _pendingResults.Clear(); }
            _results = new List<ArchiveResult>();
            _unprocessedCount = 0;

            string[] carryOverTail = null;

            // 修复轮 Finding 3：先**释放被丢下的日志接收器** —— 以前这里直接覆盖 _log 字段，
            // 上一轮的文件句柄一直锁到进程退出（Dispose 只收得到最后一个）。
            // 顺带把上一轮的**界面尾部**带进新的接收器：日志在界面上是连续的，而每轮的日志文件仍然
            // 只含自己那一轮的内容（carryOver 只进内存尾部，绝不写文件 —— 逐轮不互相污染）。
            if (_log != null)
            {
                try
                {
                    carryOverTail = _log.Snapshot();
                    _log.Flush();
                    _log.Dispose();
                }
                catch (Exception) { }
                _log = null;
            }

            string logPath = BuildLogPath(delegate(string wanted) { return BuildUniqueLogPath(wanted); });
            _log = new LogSink(logPath, carryOverTail);
            _sessionLogPaths.Add(logPath);
            if (!string.IsNullOrEmpty(options.Password)) { _log.RegisterSecret(options.Password); }
            if (!string.IsNullOrEmpty(_runWidePassword)) { _log.RegisterSecret(_runWidePassword); }
            foreach (KeyValuePair<string, string> pair in _perItemPassword) { _log.RegisterSecret(pair.Value); }
            if (!_log.FileOk) { Warn(_log.FileProblem); }     // 写不了文件要**看得见**（绝不静默）
            SetLogHint();

            AppendLog("==== 本次运行开始：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                      "，目标 " + paths.Count + " 项，本次会话第 " + _sessionRounds + " 轮（日志：" +
                      _log.FilePath + "）====");
            if (options.DeleteOriginals) { AppendLog("注意：已开启「解压成功后删除原包」（只处置解压成功且校验通过的原包）。"); }

            // J4：清空上一批的尾部（日志文件是新的一份）。
            _logDirty = true;
            _itemsDirty = true;

            _btnStart.Enabled = false;
            _btnCancel.Enabled = true;
            _chkDelete.Enabled = false;
            _numDepth.Enabled = false;
            _txtPassword.Enabled = false;
            _btnLoadDict.Enabled = false;
            SetItemActionsEnabled(false);

            // 新一批：逐项列表清空，**增量追加的游标也必须归零**（见 RefreshItems）——
            // 不归零的话，新批的前几条会被当成「已经过账」而漏掉，或者与上一批的行混在一起。
            // 逐项列表仍然只显示**这一轮**（口径写在列表上方的计数行里：本轮 / 本次会话两个口径都在），
            // 每一轮的完整记录由「打开日志文件」的会话清单逐轮可达。
            _lstItems.Items.Clear();
            _rows = new List<ArchiveResult>();
            _itemsBuilt = 0;
            _unprocessedCount = 0;

            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = 0;
            RefreshCounterLabels();
            _uiTimer.Start();

            // 修复轮 Finding 2：**每次**开始都写出预检摘要（删除关着也要写）。
            ShowPreflightSummary(paths.Count, options);

            SetKeepAwake(true);

            Thread worker = new Thread(delegate() { RunWorker(paths, options); });
            worker.IsBackground = true;
            worker.Name = "rerar-gui-run";
            worker.Start();
        }

        private string BuildLogPath(Func<string, string> uniquify)
        {
            string root;
            try
            {
                // 生产默认根：%LOCALAPPDATA%\Rerar\logs。进程级覆盖 RERAR_LOG_ROOT 与
                // RERAR_JOURNAL_ROOT / RERAR_SETTINGS_PATH 同一机制：**测试与人工验证绝不能**
                // 往用户真实的应用数据目录里写日志（本轮修复把这个洞补上了）。
                string overridden = Environment.GetEnvironmentVariable(LogRootVariable);
                if (!string.IsNullOrEmpty(overridden))
                {
                    root = overridden;
                }
                else
                {
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        Path.Combine("Rerar", "logs"));
                }
            }
            catch (Exception)
            {
                root = Path.GetTempPath();
            }

            // 目录建不出来（权限/漫游配置）就退回临时目录：日志是辅助，绝不能因此跑不起来。
            try
            {
                if (!Directory.Exists(root)) { Directory.CreateDirectory(root); }
            }
            catch (Exception)
            {
                root = Path.GetTempPath();
            }

            string candidate = Path.Combine(root, BuildLogFileName(DateTime.Now));
            if (uniquify != null) { candidate = uniquify(candidate); }

            try
            {
                string dir = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
                return candidate;
            }
            catch (Exception)
            {
                return Path.Combine(Path.GetTempPath(), Path.GetFileName(candidate));
            }
        }

        // 日志文件名的**唯一性**（修复轮 Minor 8）：以前只用「秒」分辨率，于是同一秒内开始的第二轮
        // 会算出与第一轮**同名**的文件，而 LogSink 用 `new StreamWriter(path, false)` 打开 ——
        // 那一轮会把上一轮的整份日志**截断**（Findings 3 的日志丢失就是这么发生的）。
        // 基准名保持「秒」不变（人读方便），后缀只在**同一秒内被用过**时才追加。
        internal static string BuildLogFileName(DateTime now)
        {
            return "run-" + now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".log";
        }

        // 纯函数版唯一化：base 是不冲突时的首选名；"run-<戳>-1.log"、"…-2.log" 依次备选。
        internal static string UniqueLogPath(string wanted, int ordinal)
        {
            if (ordinal <= 0 || string.IsNullOrEmpty(wanted)) { return wanted; }

            string dir = Path.GetDirectoryName(wanted);
            string name = Path.GetFileNameWithoutExtension(wanted);
            string extension = Path.GetExtension(wanted);
            string unique = name + "-" + ordinal.ToString(CultureInfo.InvariantCulture) + extension;
            return string.IsNullOrEmpty(dir) ? unique : Path.Combine(dir, unique);
        }

        // 生产用唯一化：候选名被**本会话**用过或盘上已经存在（上一轮同名被截断的那一份）时换名。
        private string BuildUniqueLogPath(string wanted)
        {
            string candidate = wanted;
            int ordinal = 1;
            while (_sessionLogPaths.Contains(candidate) || File.Exists(candidate))
            {
                candidate = UniqueLogPath(wanted, ordinal);
                ordinal++;
            }
            return candidate;
        }

        // ==================================================================
        // 计数行的三个口径（修复轮 Finding 3）
        //   * 「本轮」 = 当前这一轮的结局；
        //   * 「本次会话」 = 已收尾的各轮 + 当前轮，按路径取**最后一条**（重试过的包不数两遍）。
        // 两个标签**并排常显**，谁也不冒充谁。
        // ==================================================================

        private void RefreshCounterLabels()
        {
            _lblCounters.Text = FormatScopedCountersLine(ScopeRoundLabel, _results, _unprocessedCount);
            _lblSessionCounters.Text = FormatSessionCountersLine(_sessionResults, _results,
                _sessionUnprocessed, _unprocessedCount);
        }

        // 会话口径的「已解出 N 个文件」：带口径标签，于是它不可能被读成「本轮」的数字
        //（修复轮 Finding 3 要求的正是这件事）。计数行里另有完整的两个口径。
        internal static string FormatScopedExtractedLine(string scope, List<ArchiveResult> results)
        {
            return "【" + scope + "】" + FormatExtractedLine(results);
        }

        // 运行日志提示（修复轮 Finding 3）：会话**每一轮**的日志都在清单里，绝不只留最后一条 ——
        // 以前这里只保留一个路径，重试轮一开始，上一轮的日志就从界面上消失了（那就等于没有日志）。
        private void SetLogHint()
        {
            // 逐轮列出（最多最近 LogHintMaxPaths 条，更早的用一行汇总）：**每一轮的日志都必须可达**，
            // 而且用户得能看见它在哪儿（修复轮 Finding 3）。列表长度有上界，免得几十轮之后把
            // 详情面板挤满。
            const int LogHintMaxPaths = 6;
            int first = _sessionLogPaths.Count - LogHintMaxPaths;
            if (first < 0) { first = 0; }

            string paths = "";
            if (first > 0)
            {
                paths = "更早的 " + first.ToString(CultureInfo.InvariantCulture) +
                    " 份日志仍在「打开日志文件」的清单里（下面只列最近 " + LogHintMaxPaths + " 份路径）：";
            }
            for (int i = first; i < _sessionLogPaths.Count; i++)
            {
                if (paths.Length > 0) { paths += "\r\n"; }
                paths += "第 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 轮：" + _sessionLogPaths[i];
            }

            string heading = _sessionLogPaths.Count == 0
                ? "运行日志（界面只显示最后 " + LogTailLines + " 行；还没有日志文件）"
                : "运行日志（界面只显示最后 " + LogTailLines + " 行；本次会话共 " +
                  _sessionLogPaths.Count.ToString(CultureInfo.InvariantCulture) +
                  " 份日志，下面列出最近几轮的文件路径）";
            _lblLogHint.Text = paths.Length == 0 ? heading : heading + "\r\n" + paths;

            _btnOpenLog.Enabled = _sessionLogPaths.Count > 0;
            _btnOpenLog.AccessibleName = "打开完整日志文件（本次会话共 " + _sessionLogPaths.Count + " 份）";
        }

        // 打开**某一轮**的日志文件。会话清单里就是逐轮的路径（最早 → 最新）；不给序号时开最近一轮。
        // 这是「本次会话的每一份日志都还在界面上可达」这条要求的落地动作（修复轮 Finding 3）。
        private void OpenSessionLog(int index)
        {
            if (_sessionLogPaths.Count == 0) { Warn("本次会话还没有日志文件。"); return; }
            if (index < 0 || index >= _sessionLogPaths.Count) { index = _sessionLogPaths.Count - 1; }

            string path = _sessionLogPaths[index];
            try
            {
                if (File.Exists(path)) { Process.Start(path); }
                else { Warn("日志文件已经不存在了（可能被清理或移走）：" + path); }
            }
            catch (Exception ex)
            {
                Warn("无法打开日志文件（" + ex.GetType().Name + "：" + ex.Message + "）：" + path);
            }
        }

        // 会话日志清单（最早 → 最新）。只读快照：调用方绝不拿到可变的内部列表。
        internal string[] SessionLogPaths()
        {
            return _sessionLogPaths.ToArray();
        }

        // ==================================================================
        // 预检摘要的显示/收起（修复轮 Finding 2）。**非模态**：不抢焦点、不阻塞、不挡按钮。
        // ==================================================================

        private void ShowPreflightSummary(int count, RunOptions options)
        {
            string text = BuildSummaryText(count, options != null && options.DeleteOriginals, _engineResolvedText);
            _lblSummary.Text = text;
            _lblSummary.Visible = true;
            _summaryWatch = Stopwatch.StartNew();

            // 同一份文案另有一份进日志文件：摘要是「本次运行是怎么被配置的」这件事的唯一记录，
            // 复盘（三小时后）时界面上的摘要早就不在了。
            AppendLog(text.Replace("\r\n", " "));
        }

        private void HidePreflightSummary()
        {
            _lblSummary.Visible = false;
            _summaryWatch = null;
        }

        // 由 UI 心跳调用：摘要显示满 SummaryVisibleSeconds 秒后自动收起（它已经完成了「按开始之前
        // 让你看清这一步」的职责；常驻只会让界面在长跑之后越来越挤）。
        private void TickPreflightSummary()
        {
            if (_summaryWatch == null || !_lblSummary.Visible) { return; }
            if (_summaryWatch.Elapsed.TotalSeconds < SummaryVisibleSeconds) { return; }
            HidePreflightSummary();
            _lblSummary.Text = "";
        }

        // 工作线程：定位引擎 → 跑 Extractor → 补未处理剩余项 → 把摘要编组回 UI。
        // 这里**不做任何判定**：成功/失败/跳过全部由 Core 给出。
        private void RunWorker(List<string> paths, RunOptions options)
        {
            RunSummary summary = null;
            string fatal = null;

            try
            {
                EngineInfo engine = EngineLocator.Resolve();
                string engineText = "解压引擎：7-Zip " + EngineLocator.FormatVersion(engine.Version) +
                    (engine.IsEmbedded ? "（内置便携版，已释放到本机）" : "（本机安装）") + "：" + engine.Path;
                options.SevenZipPath = engine.Path;
                // 定位结果也写进字段：预检摘要里那一行从此有**真值**可用（没定位到就是空串，
                // 摘要如实说「将在开始时定位」—— 修复轮 Finding 2）。
                _engineResolvedText = engineText;
                UiPost(delegate { _lblEngineInfo.Text = engineText; });
                AppendLog(engineText);

                Extractor extractor = new Extractor(options, new DriveSpaceProvider(), new Sink(this));
                summary = extractor.Run(paths);
            }
            catch (Exception ex)
            {
                // Extractor 自己会把单个归档的异常收成 FAIL；能跑到这里的是运行级的意外。
                fatal = ex.GetType().Name + "：" + ex.Message;
            }

            if (summary == null)
            {
                summary = new RunSummary();
                summary.FatalReason = "无法开始解压：" + (fatal == null ? "原因未知" : fatal);
            }
            else
            {
                // 未处理剩余项的合成与 CLI **共用同一份实现**（含「不重复计数」这条规则）。
                Program.AddUnprocessedRemainder(summary);
            }

            RunSummary final = summary;
            UiPost(delegate { FinishRun(final); });
        }

        // ------------------------------------------------------------------
        // 进度回调（**工作线程**上被调用：只改字段 + 编组，绝不碰控件、绝不抛）
        // ------------------------------------------------------------------

        private sealed class Sink : IProgressSink
        {
            private readonly MainForm _form;

            public Sink(MainForm form) { _form = form; }

            public void ArchiveStarted(string archivePath, int depth)
            {
                // 这个方法在**工作线程**上跑：只写字段（Interlocked/简单赋值）与线程安全的 LogSink，
                // 控件一律由 UiPost 碰。
                try
                {
                    Interlocked.Increment(ref _form._counters.Started);
                    _form._currentMember = archivePath == null ? "" : Path.GetFileName(archivePath);
                    _form._lastSignalTicks = DateTime.Now.Ticks;
                    // 逐归档写日志：这是「完整日志」的主要内容（Core 只发开始/结束/进度三种回调，
                    // 它自己的 IProgressSink.Message 目前没有调用点 —— 界面自己把逐项事件记下来，
                    // 用户三小时后复盘时才有东西可看）。
                    _form.AppendLog("[开始] " + (archivePath == null ? "" : archivePath) + "（第 " + depth + " 层）");
                }
                catch (Exception) { }
            }

            public void ArchiveFinished(ArchiveResult result)
            {
                try
                {
                    if (result == null) { return; }
                    lock (_form._pendingGate) { _form._pendingResults.Add(result); }
                    Interlocked.Increment(ref _form._counters.Finished);
                    _form._lastSignalTicks = DateTime.Now.Ticks;
                    _form._itemsDirty = true;

                    // 结局文案取自 Reporter（与报告/导出**同一张表**），绝不在这里另写一份。
                    string line = "[结束] " + result.Path + " → " + Reporter.StatusText(result.Status) +
                        "（文件 " + result.Files + "，失败 " + result.Failed + "）";
                    if (result.Layers > 0) { line += "，层数 " + result.Layers; }
                    if (!string.IsNullOrEmpty(result.OutputDir)) { line += "，产物 " + result.OutputDir; }
                    if (!string.IsNullOrEmpty(result.Message)) { line += "；" + result.Message; }
                    _form.AppendLog(line);
                }
                catch (Exception) { }
            }

            public void Progress(string archivePath, int percent, string member)
            {
                try
                {
                    // percent 只用于「有心跳」这件事本身：实测（研究 V27）`7z x` 的这个回调在真实
                    // 解压上一次都不会触发，所以界面**不**把包内百分比当进度来源（那是谎话），
                    // 只把它当作「子进程还活着」的信号，用来区分「大文件处理中」和「卡死」。
                    _form._currentMember = member == null ? "" : member;
                    _form._lastSignalTicks = DateTime.Now.Ticks;
                }
                catch (Exception) { }
            }

            public void Message(string text)
            {
                // 一行日志：Sink 自己写（LogSink 内部有锁，线程安全，且**永不抛**）。
                try { _form.AppendLog(text); }
                catch (Exception) { }
            }
        }

        // 进度字段用 int 字段 + Interlocked（Interlocked 需要 ref 到字段，故直接放在 MainForm 上，
        // Sink 通过 _form 访问）。

        // ------------------------------------------------------------------
        // UI 线程编组（唯一入口）
        // ------------------------------------------------------------------

        // 把一段动作编组到 UI 线程。**绝不抛异常**：窗体一旦被销毁（用户关掉了窗口），
        // 工作线程的后续回调必须被静默丢弃 —— 让一个已销毁的窗体把异常抛回 Extractor 的回调里，
        // 会顺着调用栈掀翻整批解压（Task 3 的裁定：回调异常在 Run 调用点浮出来）。
        private void UiPost(MethodInvoker action)
        {
            if (action == null) { return; }

            try
            {
                if (IsDisposed || Disposing) { return; }

                // 判据是**线程身份**，不是 Control.InvokeRequired：没有句柄时 InvokeRequired 返回
                // false，用它做判据就会把界面动作直接跑在**工作线程**上（那正是「控件必须编组」
                // 要防的事）。线程身份判据在有没有句柄两种情形下都对。
                if (Thread.CurrentThread.ManagedThreadId == _uiThreadId)
                {
                    action();
                    return;
                }

                // 还没有句柄 ⇒ 还没有消息循环 ⇒ 无处投递。丢弃这一次显示是安全的：最终结果由
                // FinishRun 从 RunSummary 整体重建（不是靠逐条回调累出来的），不会丢任何事实。
                if (!IsHandleCreated) { return; }
                BeginInvoke(action);
            }
            catch (Exception)
            {
                // 窗体在竞态中被销毁（InvalidOperationException / ObjectDisposedException）：
                // 丢弃这一次显示。日志文件仍在写，解压本身照常。
            }
        }

        private void AppendLog(string line)
        {
            if (line == null) { return; }
            LogSink sink = _log;
            if (sink == null)
            {
                // 还没有日志文件（例如启动阶段的提权提示）：只记进界面尾部（内存里有界缓冲）。
                _startupLog.Add(line);
                _logDirty = true;
                return;
            }
            sink.Write(line);
            _logDirty = true;
        }

        private readonly List<string> _startupLog = new List<string>();

        // ==================================================================
        // UI 心跳：**唯一定期刷新点**（J4 的合并窗口）
        //   * 无论 Core 回调多密集，界面每秒最多被碰 1000/UiCoalesceMs 次（默认 5 次）；
        //   * 进度、日志尾部、逐项列表都在这里刷新，别处一律只置脏标记。
        // ==================================================================

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (_running && _watch != null) { _lastElapsed = _watch.Elapsed; }
                DrainPendingResults();

                if (_logDirty)
                {
                    _logDirty = false;
                    LogSink sink = _log;
                    if (sink != null) { _logView.SetLines(sink.Snapshot()); sink.Flush(); }
                    else { _logView.SetLines(_startupLog.ToArray()); }
                }

                if (_itemsDirty)
                {
                    _itemsDirty = false;
                    RefreshItems();
                }

                if (_running) { UpdateRunningLabels(); }
                TickPreflightSummary();
            }
            catch (Exception ex)
            {
                // 心跳里的意外绝不能让窗口崩掉（也不该中断运行）：记一条就够。
                try { AppendLog("界面刷新异常（" + ex.GetType().Name + "：" + ex.Message + "）"); }
                catch (Exception) { }
            }
        }

        private void DrainPendingResults()
        {
            List<ArchiveResult> drained = null;
            lock (_pendingGate)
            {
                if (_pendingResults.Count == 0) { return; }
                drained = new List<ArchiveResult>(_pendingResults);
                _pendingResults.Clear();
            }

            foreach (ArchiveResult result in drained)
            {
                _results.Add(result);
                // 需要密码：弹出**停靠式、非模态**面板（J6），并把「还有 N 个待处理」说清楚。
                if (result.Status == ArchiveStatus.SkippedNeedsPassword)
                {
                    ShowPasswordAsk(result.Path);
                }
            }
        }

        private void UpdateRunningLabels()
        {
            int started = _counters.Started;
            // 分母是**已发现的工作量**：嵌套解出来的新包会让它增长，所以显示「已发现 M」而不是百分比。
            int discovered = _discoveredAtStart;
            if (started > discovered) { discovered = started; }

            string text = FormatProgressText(started, discovered, _lastElapsed);
            string eta = FormatEta(_lastElapsed, _counters.Finished, discovered - started);
            if (eta.Length > 0) { text += " · " + eta; }
            if (_cancelRequestedAt != DateTime.MinValue)
            {
                TimeSpan waited = DateTime.Now - _cancelRequestedAt;
                text += " · 正在取消，最长等待 10 秒（已等 " + (int)waited.TotalSeconds + " 秒）";
            }
            _lblProgress.Text = text;

            // 进度条按「已发现的工作量」推进（**不是**百分比——它是同一件事的另一种说法，
            // 但值是真实的已完成/已发现之比，且分母会跟着发现增长）。
            if (discovered > 0)
            {
                int value = (int)((long)started * 100L / discovered);
                if (value < 0) { value = 0; }
                if (value > 100) { value = 100; }
                _progressBar.Value = value;
            }

            // J3：30 秒没有任何信号就明说「正在处理大文件，已用 …」，免得与假死无法区分。
            long silentTicks = DateTime.Now.Ticks - _lastSignalTicks;
            if (silentTicks > TimeSpan.TicksPerSecond * HeartbeatSeconds)
            {
                _lblStatus.Text = "状态：正在处理大文件，已用 " + FormatElapsed(_lastElapsed) +
                    "（" + (int)TimeSpan.FromTicks(silentTicks).TotalSeconds + " 秒没有新进度，仍在运行）";
            }
            else if (_currentMember.Length > 0)
            {
                _lblStatus.Text = "状态：" + FormatExtractedLine(_results) + " · ⏳ 正在解压 " + _currentMember;
            }

            RefreshCounterLabels();
        }

        // ==================================================================
        // 结束（UI 线程）
        // ==================================================================

        private void FinishRun(RunSummary summary)
        {
            _running = false;
            _uiTimer.Stop();
            if (_watch != null) { _lastElapsed = _watch.Elapsed; }
            _watch = null;
            DrainPendingResults();

            _results = summary.Results == null ? new List<ArchiveResult>() : summary.Results;
            _unprocessedCount = summary.NotAttempted == null ? 0 : summary.NotAttempted.Count;

            SetKeepAwake(false);
            HidePreflightSummary();
            AppendLog("==== 本次运行结束，已用 " + FormatElapsed(_lastElapsed) + " ====");
            if (!string.IsNullOrEmpty(summary.FatalReason)) { AppendLog("致命错误（整批中止）：" + summary.FatalReason); }
            if (summary.Cancelled) { AppendLog("本次运行已被取消：未完成的项已如实列出，原包一律保留。"); }
            if (summary.JournalWriteFailures > 0)
            {
                AppendLog("警告：崩溃恢复日志有 " + summary.JournalWriteFailures + " 次写入失败：" +
                          (summary.JournalProblem == null ? "" : summary.JournalProblem));
            }

            LogSink sink = _log;
            if (sink != null) { sink.Flush(); _logView.SetLines(sink.Snapshot()); }

            RefreshItems();
            _itemsDirty = false;
            _logDirty = false;

            // 运行中拖进来的项标着「下一批」；这一批已经结束了，它们就从**现在**起算这一批
            //（不清掉这个标记的话，RefreshInputSummary 会把「开始解压」一直禁用着 —— 用户会
            // 拖入新文件却发现按不动按钮）。
            _nextBatchOnly = false;
            RefreshInputSummary();
            _btnCancel.Enabled = false;
            _chkDelete.Enabled = true;
            _numDepth.Enabled = true;
            _txtPassword.Enabled = true;
            _btnLoadDict.Enabled = true;
            _progressBar.Style = ProgressBarStyle.Continuous;
            // 两个口径**都**写出来（修复轮 Finding 3）：只写「本轮」的话，一轮重试跑完就会显示
            //「❌ 0 个失败」，而整场可能已经攒了几十个失败 —— 那正是被复核者点名的那个谎。
            RefreshCounterLabels();
            // 状态行只报**本轮**（口径标签在计数行里，两个口径并排常显，谁也不冒充谁）。
            _lblStatus.Text = FormatExtractedLine(_results);
            if (!string.IsNullOrEmpty(summary.FatalReason))
            {
                _lblProgress.Text = "整批因致命错误中止（已用 " + FormatElapsed(_lastElapsed) + "）：" + summary.FatalReason;
            }
            else if (summary.Cancelled)
            {
                _lblProgress.Text = "已取消（已用 " + FormatElapsed(_lastElapsed) + "）：未处理的项如实列为「未处理（已取消）」，原包保留。";
            }
            else
            {
                _lblProgress.Text = "本次运行已结束 · 已用 " + FormatElapsed(_lastElapsed);
            }

            // J14：完成通知 —— 托盘气泡 + 任务栏闪烁 + **非模态**结果区（不抢焦点，绝不 modal）。
            if (HasActionResult()) { SetDetailsVisible(true); }
            NotifyCompletion(summary);

            if (_retryQueue.Count > 0 && !_exitWhenDone)
            {
                List<string> retry = new List<string>(_retryQueue);
                _retryQueue.Clear();
                AppendLog("按逐项动作重试 " + retry.Count + " 项。");
                StartRetry(retry);
                return;
            }

            if (_exitWhenDone)
            {
                _allowClose = true;
                Close();
            }
        }

        private bool HasActionResult()
        {
            foreach (ArchiveResult r in _results)
            {
                if (r == null) { continue; }
                if (r.Status != ArchiveStatus.Completed) { return true; }
            }
            return false;
        }

        private void NotifyCompletion(RunSummary summary)
        {
            string text = FormatExtractedLine(_results) + "；" +
                FormatCountersLine(_results, _unprocessedCount);

            try
            {
                if (!Visible || WindowState == FormWindowState.Minimized || !ContainsFocus)
                {
                    EnsureTray();
                    _tray.BalloonTipTitle = "Rerar 解压完成";
                    _tray.BalloonTipText = text;
                    _tray.Visible = true;
                    _tray.ShowBalloonTip(6000);
                }
            }
            catch (Exception) { }

            try
            {
                if (!ContainsFocus && IsHandleCreated)
                {
                    FLASHWINFO info = new FLASHWINFO();
                    info.cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO));
                    info.hwnd = Handle;
                    info.dwFlags = FlashwAll | FlashwTimerNoForeground;
                    info.uCount = 4;
                    info.dwTimeout = 0;
                    FlashWindowEx(ref info);
                }
            }
            catch (Exception) { }
        }

        // 逐项重试：为新的一批建一个新的 Extractor（Extractor 一个实例只跑一次 Run）。
        private void StartRetry(List<string> paths)
        {
            RunOptions options = new RunOptions();
            options.DeleteOriginals = _chkDelete.Checked;
            options.MaxDepth = (int)_numDepth.Value;
            options.Password = _runWidePassword.Length > 0 ? _runWidePassword : null;
            foreach (string forced in _forcedPaths) { options.ForceTreatAsArchive.Add(forced); }
            if (!string.IsNullOrEmpty(_dictPath))
            {
                try { options.DictLines = new List<string>(File.ReadAllLines(_dictPath)); }
                catch (Exception) { }
            }

            // 逐个「仅此压缩包」记住的密码：按密码分组，每组一次 Run（避免为一项就起一轮）。
            Dictionary<string, List<string>> groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                string password;
                string key = _perItemPassword.TryGetValue(path, out password) ? password : "";
                List<string> bucket;
                if (!groups.TryGetValue(key, out bucket)) { bucket = new List<string>(); groups[key] = bucket; }
                bucket.Add(path);
            }

            string first = null;
            foreach (KeyValuePair<string, List<string>> group in groups)
            {
                if (first == null) { first = group.Key; }
            }

            // 简化且诚实：一次 Run 只能带一个「全局密码」。所以把**第一个**分组之外的分组留在队列里，
            // 本轮的下一轮再跑（消息里会说清）。绝大多数情形只有一个分组。
            List<string> untried = new List<string>();
            string chosenKey = first == null ? "" : first;
            List<string> firstPaths = new List<string>();
            foreach (KeyValuePair<string, List<string>> group in groups)
            {
                if (string.Equals(group.Key, chosenKey, StringComparison.Ordinal))
                {
                    firstPaths.AddRange(group.Value);
                }
                else
                {
                    untried.AddRange(group.Value);
                }
            }

            options.Password = chosenKey.Length > 0 ? chosenKey : options.Password;
            if (untried.Count > 0)
            {
                _retryQueue.AddRange(untried);
                AppendLog("另有 " + untried.Count + " 项带不同的「仅此压缩包」密码，会在下一轮重试。");
            }

            StartRun(firstPaths, options, firstPaths.Count);
        }

        // ==================================================================
        // 逐项列表与逐项动作（§6.1）
        // ==================================================================

        // 逐项列表刷新。**增量追加**，绝不每次整体重建：
        // 一批几百上千个归档时，每 200ms 重建整张表（O(n) 的项构造）会让界面明显发卡 ——
        // 这正是 J4 要防的同一类问题（只是对象从日志换成了列表）。
        // 结果在尾部增长（同一批内顺序稳定；未处理剩余项也是追加），所以只需补上新行；
        // 只有列表比结果**长**（新一批开始 / _results 被整体替换）时才整体重建。
        private void RefreshItems()
        {
            int selected = _lstItems.SelectedIndices.Count > 0 ? _lstItems.SelectedIndices[0] : -1;

            if (_itemsBuilt > _results.Count)
            {
                _lstItems.BeginUpdate();
                try
                {
                    _lstItems.Items.Clear();
                    _rows.Clear();
                }
                finally
                {
                    _lstItems.EndUpdate();
                }
                _itemsBuilt = 0;
            }

            if (_itemsBuilt < _results.Count)
            {
                _lstItems.BeginUpdate();
                try
                {
                    for (int i = _itemsBuilt; i < _results.Count; i++)
                    {
                        _itemsBuilt++;                       // 游标按 _results 的下标走，null 也要往前走
                        ArchiveResult r = _results[i];
                        if (r == null) { continue; }         // 与 Reporter 同约定：null 元素不凭空多出一行

                        _rows.Add(r);                        // _rows 与 ListView 的行**一一对应**（下标 = 选中项）
                        ListViewItem item = new ListViewItem(Shorten(r.Path, 80));
                        item.SubItems.Add(Reporter.StatusText(r.Status));   // 与报告/导出**同一套**中文结局文案
                        item.SubItems.Add(r.Files + " / " + r.Failed);
                        item.SubItems.Add(Shorten(r.Message, 200));
                        // 修复轮 Minor 12：不再往 item.Tag 里塞结果对象 —— 选中项经 _rows[下标] 取，
                        // 那个 Tag 从来没有人读过（多一条无人读的路径就等于多一处会说谎的状态）。
                        _lstItems.Items.Add(item);
                    }
                }
                finally
                {
                    _lstItems.EndUpdate();
                }
            }

            if (selected >= 0 && selected < _lstItems.Items.Count) { _lstItems.Items[selected].Selected = true; }
            UpdateItemActions();
        }

        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) { return ""; }
            string single = text.Replace("\r", " ").Replace("\n", " ");
            if (single.Length <= max) { return single; }
            return single.Substring(0, max) + "…";
        }

        private ArchiveResult SelectedResult()
        {
            if (_lstItems.SelectedIndices.Count == 0) { return null; }
            int index = _lstItems.SelectedIndices[0];
            if (index < 0 || index >= _rows.Count) { return null; }
            return _rows[index];
        }

        private void LstItems_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateItemActions();
        }

        // §6.1 的逐项动作按**结局**启用：只有「还没成功」的项才有可做的事，且成功项没有可强制的余地。
        // 判定本身在 SetItemActionsEnabled 里（按钮与右键菜单共用的那一份），这里只是「按当前选中行
        // 重算一次」的语义化入口。
        private void UpdateItemActions()
        {
            SetItemActionsEnabled(true);
        }

        // 逐项动作的**唯一**启用/禁用收口。按钮与右键菜单都必须走这里 ——
        // 两个界面入口由同一处判定，于是它们不可能互相矛盾（修复轮 Finding 1 的第二半）。
        // 索引与 UpdateItemActions 里的按钮逐一同序，取判定结果时按下标取。
        private void SetItemActionsEnabled(bool enabled)
        {
            bool[] states = new bool[4];
            if (enabled)
            {
                ArchiveResult r = SelectedResult();
                bool idle = !_running;
                bool actionable = r != null && r.Status != ArchiveStatus.Completed;
                bool hasOutput = r != null && !string.IsNullOrEmpty(r.OutputDir) && Directory.Exists(r.OutputDir);

                states[0] = idle && actionable;      // 强制按压缩包尝试
                states[1] = idle && actionable;      // 输入密码
                states[2] = idle && actionable;      // 重试
                states[3] = hasOutput || (r != null && !string.IsNullOrEmpty(r.OutputDir));   // 打开输出目录
            }

            _btnForceArchive.Enabled = states[0];
            _btnEnterPassword.Enabled = states[1];
            _btnRetry.Enabled = states[2];
            _btnOpenOutput.Enabled = states[3];

            for (int i = 0; i < _itemMenuItems.Length; i++)
            {
                if (_itemMenuItems[i] != null) { _itemMenuItems[i].Enabled = states[i]; }
            }
        }

        // §6.1 的「强制按压缩包尝试」：映射到 RunOptions.ForceTreatAsArchive（**只**放开格式门控，
        // Core 的 I1/I2/I3/I4 与预检上限照旧全部适用 —— 这一点由 Core 的用例钉住）。
        private void BtnForceArchive_Click(object sender, EventArgs e)
        {
            ArchiveResult r = SelectedResult();
            if (r == null || string.IsNullOrEmpty(r.Path)) { return; }

            _forcedPaths.Add(r.Path);
            AppendLog("逐项动作：对「" + r.Path + "」强制按压缩包尝试（只跳过格式识别门控，其余安全判定照旧）");
            EnqueueRetry(r.Path, "强制按压缩包尝试");
        }

        private void BtnEnterPassword_Click(object sender, EventArgs e)
        {
            ArchiveResult r = SelectedResult();
            if (r == null || string.IsNullOrEmpty(r.Path)) { return; }
            ShowPasswordAsk(r.Path);
        }

        private void BtnRetry_Click(object sender, EventArgs e)
        {
            ArchiveResult r = SelectedResult();
            if (r == null || string.IsNullOrEmpty(r.Path)) { return; }
            EnqueueRetry(r.Path, "重试");
        }

        private void BtnOpenOutput_Click(object sender, EventArgs e)
        {
            ArchiveResult r = SelectedResult();
            if (r == null || string.IsNullOrEmpty(r.OutputDir)) { return; }

            try
            {
                if (Directory.Exists(r.OutputDir)) { Process.Start(r.OutputDir); }
                else { Warn("输出目录已经不存在了：" + r.OutputDir); }
            }
            catch (Exception ex)
            {
                Warn("无法打开输出目录（" + ex.GetType().Name + "：" + ex.Message + "）：" + r.OutputDir);
            }
        }

        // 排进重试队列，并**立刻**在没有批次运行时把它跑起来（TryStartIdleRetry 自己会在运行中
        // 无操作）。修复轮 Finding 1：以前这里只入队，而唯一的出队点只有密码面板的两个按钮，
        // 于是「空闲时点重试」正好落在「按钮可用但永远跑不起来」的空档里 —— 状态行还会谎称
        //「当前批次结束后重试」，可当前根本没有批次。
        private void EnqueueRetry(string path, string what)
        {
            if (!_retryQueue.Contains(path)) { _retryQueue.Add(path); }

            // 状态文案由**事实**决定：正在跑才说「本批结束后」，空闲就如实说立刻开始。
            _lblStatus.Text = _running
                ? "状态：已把「" + Path.GetFileName(path) + "」加入重试队列（" + what +
                  "）；当前批次结束后重试，删除开关沿用当前设置。"
                : "状态：空闲，正在就「" + Path.GetFileName(path) + "」开始重试（" + what + "）。";

            TryStartIdleRetry();
        }

        // ==================================================================
        // 密码：停靠式非模态面板（J6）
        // ==================================================================

        private void ShowPasswordAsk(string path)
        {
            if (string.IsNullOrEmpty(path)) { return; }

            _pendingPasswordPath = path;
            int remaining = _inputs.Count - _counters.Finished;
            if (remaining < 0) { remaining = 0; }
            _lblPasswordAsk.Text = "🔒 当前压缩包需要密码 — 还有 " + remaining + " 个待处理。\r\n" +
                "压缩包：" + path;
            _panelPasswordAsk.Visible = true;
            _txtPasswordAsk.Text = "";
            // 刻意**不**抢焦点（不 Select/Focus）：用户在别处打字时被抢走焦点同样是 bug；
            // 面板只是停靠在那里，用户想用时点它即可。
        }

        private void BtnPwThisOnly_Click(object sender, EventArgs e)
        {
            string path = _pendingPasswordPath;
            string password = _txtPasswordAsk.Text;
            if (string.IsNullOrEmpty(path) || password.Length == 0) { return; }

            _perItemPassword[path] = password;
            if (_log != null) { _log.RegisterSecret(password); }
            _panelPasswordAsk.Visible = false;
            _pendingPasswordPath = null;
            AppendLog("用户为「" + path + "」提供了密码（仅此压缩包；密码值不记录）。");
            EnqueueRetry(path, "输入密码（仅此压缩包）");
        }

        private void BtnPwRememberAll_Click(object sender, EventArgs e)
        {
            string password = _txtPasswordAsk.Text;
            if (password.Length == 0) { return; }

            _runWidePassword = password;
            if (_log != null) { _log.RegisterSecret(password); }

            // 「本次运行全部记住」：写进**正在用的** RunOptions.Password。Core 的密码阶梯第 1 层是
            // 每个归档现读的（Extractor.FindPassword），所以后续归档会立刻用上它 —— 这正是这个动作
            // 该有的语义，而且它走的仍是 Core 原本那条路（没有旁路）。
            if (_options != null) { _options.Password = password; }
            _txtPassword.Text = password;
            _chkRememberRun.Checked = true;

            string path = _pendingPasswordPath;
            _panelPasswordAsk.Visible = false;
            _pendingPasswordPath = null;
            AppendLog("用户选择「本次运行全部记住」（密码值不记录）：后续归档会把它作为首选候选。");
            if (!string.IsNullOrEmpty(path)) { EnqueueRetry(path, "输入密码（本次运行全部记住）"); }
        }

        private void BtnPwSkip_Click(object sender, EventArgs e)
        {
            string path = _pendingPasswordPath;
            _panelPasswordAsk.Visible = false;
            _pendingPasswordPath = null;
            // 跳过的包**如实保留**为「跳过（需要密码）」—— 绝不改成失败，也绝不重试。
            AppendLog("用户跳过「" + path + "」：它的结局仍是「跳过（需要密码）」，不是失败。");
        }

        private void TryStartIdleRetry()
        {
            if (_running || _retryQueue.Count == 0) { return; }
            List<string> retry = new List<string>(_retryQueue);
            _retryQueue.Clear();
            StartRetry(retry);
        }

        // ==================================================================
        // 取消（两段式；J/§6.10）
        // ==================================================================

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            if (!_running) { return; }
            RequestCancel();
        }

        // 两段式：先**请求**取消（Core 的 CancellationToken 会让 SevenZipRunner 通过 Job Object
        // 打断正在跑的子进程，退出码 1223 被 Core 判成「已取消」而不是失败）；界面如实显示倒计时。
        // 绝不 Process.Kill() 了事 —— 那是 Core 的职责，而且它走的是 Job Object 那条正路。
        private void RequestCancel()
        {
            if (!_running) { return; }

            try
            {
                _cancelRequestedAt = DateTime.Now;
                _btnCancel.Enabled = false;
                _lblStatus.Text = "状态：正在取消，最长等待 10 秒（原包一律保留）…";
                AppendLog("用户请求取消：已发出取消信号，正在等待正在进行的 7-Zip 调用结束（原包一律保留）。");
                if (_runCts != null) { _runCts.Cancel(); }
            }
            catch (Exception ex)
            {
                AppendLog("取消信号发出失败（" + ex.GetType().Name + "：" + ex.Message + "）");
            }
        }

        // ==================================================================
        // 关闭窗口三选一（J7）
        // ==================================================================

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_allowClose) { SaveSettings(); return; }

            if (_running)
            {
                // 长任务的默认是「继续在后台运行」（J7）——把默认按钮放在这里，而不是「取消并退出」。
                DialogResult choice = MessageBox.Show(this,
                    "解压仍在进行中（已用 " + FormatElapsed(_lastElapsed) + "）。\r\n\r\n" +
                    "是：继续在后台运行（窗口最小化到通知区域，任务不会中断）\r\n" +
                    "否：取消任务并退出（正在进行的解压会被取消，原包一律保留）\r\n" +
                    "取消：返回，什么都不做",
                    "Rerar", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1);

                if (choice == DialogResult.Cancel) { e.Cancel = true; return; }

                if (choice == DialogResult.Yes)
                {
                    e.Cancel = true;
                    MinimizeToTray();
                    return;
                }

                // 取消任务并退出：如实告知「取消不是立刻的」，等运行真的收尾之后再关。
                e.Cancel = true;
                _exitWhenDone = true;
                RequestCancel();
                return;
            }

            if (_scanRemaining > 0)
            {
                DialogResult choice = MessageBox.Show(this,
                    "还在统计文件夹内容（已发现 " + _scanFound + " 个文件）。要放弃统计并退出吗？",
                    "Rerar", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (choice == DialogResult.No) { e.Cancel = true; return; }
                try { if (_scanCts != null) { _scanCts.Cancel(); } }
                catch (Exception) { }
            }

            SaveSettings();
        }

        private void SaveSettings()
        {
            try
            {
                if (WindowState == FormWindowState.Normal)
                {
                    _settings.HasGeometry = true;
                    _settings.WindowX = Bounds.X;
                    _settings.WindowY = Bounds.Y;
                    _settings.WindowWidth = Bounds.Width;
                    _settings.WindowHeight = Bounds.Height;
                }
                _settings.Maximized = WindowState == FormWindowState.Maximized;
                _settings.Save();
            }
            catch (Exception)
            {
                // 存设置失败绝不能让关闭窗口变成报错。
            }
        }

        private void MinimizeToTray()
        {
            EnsureTray();
            try
            {
                _tray.Visible = true;
                _tray.BalloonTipTitle = "Rerar 仍在后台运行";
                _tray.BalloonTipText = "解压继续在后台进行。双击托盘图标可以重新打开窗口。";
                _tray.ShowBalloonTip(4000);
            }
            catch (Exception) { }

            Hide();
            ShowInTaskbar = false;
        }

        private void EnsureTray()
        {
            if (_tray != null) { return; }

            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Text = "Rerar 递归解压";
            _tray.Visible = false;
            _tray.DoubleClick += delegate(object sender, EventArgs e)
            {
                try
                {
                    ShowInTaskbar = true;
                    Show();
                    WindowState = FormWindowState.Normal;
                    Activate();
                }
                catch (Exception) { }
            };
        }

        // ==================================================================
        // 详情 / 日志 / 字典 / 高级项
        // ==================================================================

        private void BtnDetails_Click(object sender, EventArgs e)
        {
            SetDetailsVisible(!_panelDetails.Visible);
        }

        // 折叠时把详情那一行的高度压到 0：只把 Visible 设成 false 的话，Percent 行仍然占着空间，
        // 窗口下半部会留下一大片空白（布局用 TableLayoutPanel，就得这样收）。
        private void SetDetailsVisible(bool visible)
        {
            _panelDetails.Visible = visible;
            _root.RowStyles[7] = new RowStyle(visible ? SizeType.Percent : SizeType.Absolute, visible ? 100F : 0F);
            _btnDetails.Text = visible ? "收起详情 ▾" : "查看详情 ▸";
            if (visible) { _logView.Invalidate(); }
        }

        private void BtnAdvanced_Click(object sender, EventArgs e)
        {
            _panelAdvanced.Visible = !_panelAdvanced.Visible;
            _btnAdvanced.Text = _panelAdvanced.Visible ? "▾ 密码设置（可折叠）" : "▸ 密码设置（可折叠）";
        }

        private void ChkAutoScroll_CheckedChanged(object sender, EventArgs e)
        {
            // J4：暂停自动滚动 —— 用户往上翻的时候日志一直往下跳本身就是个 bug。
            _logView.AutoScroll = _chkAutoScroll.Checked;
        }

        // 「打开日志文件」：只有一轮时直接开那一份；多轮时先让用户挑是哪一轮 ——
        // 会话里每一轮的日志路径都留在清单里，所以「开错了轮」这件事是**可修的**，而不是
        //「上一轮的日志已经从界面上消失」（修复轮 Finding 3）。
        private void BtnOpenLog_Click(object sender, EventArgs e)
        {
            if (_sessionLogPaths.Count == 0) { Warn("本次会话还没有日志文件。"); return; }

            int index = _sessionLogPaths.Count - 1;
            if (_sessionLogPaths.Count > 1)
            {
                ContextMenuStrip picker = new ContextMenuStrip();
                try
                {
                    for (int i = 0; i < _sessionLogPaths.Count; i++)
                    {
                        int captured = i;
                        string label = "第 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 轮（" +
                            Path.GetFileName(_sessionLogPaths[i]) + "）";
                        ToolStripMenuItem entry = new ToolStripMenuItem(label, null,
                            delegate(object s, EventArgs a) { OpenSessionLog(captured); });
                        picker.Items.Add(entry);
                    }
                    picker.Show(_btnOpenLog, new Point(0, _btnOpenLog.Height));
                }
                catch (Exception ex)
                {
                    Warn("无法列出本次会话的日志（" + ex.GetType().Name + "：" + ex.Message + "）。");
                }
                // 菜单由 WinForms 在关闭时自行回收（不进 Dispose 链），这里不额外持有它。
                return;
            }

            OpenSessionLog(index);
        }

        // ==================================================================
        // 「关于/开源许可」（规格 §8：许可义务必须在程序内可 discharge）
        // ==================================================================

        // 展示 exe 旁边（Application.StartupPath）随包分发的 THIRD-PARTY-NOTICES.txt。
        // 窗口是**非模态**的：与密码面板/提醒横幅同一立场 —— 绝不阻塞界面线程，用户可以边解压
        // 边读许可；同一时刻最多一份，再点一次把已有的带到前台。
        private void BtnLicense_Click(object sender, EventArgs e)
        {
            ShowLicenseNotice(BuildLicenseNotice(Application.StartupPath));
        }

        // 许可声明的**展示文本**：文件在 ⇒ 文件内容（UTF-8，BOM 由 ReadAllText 自动剥离）；
        // 缺席/读不动 ⇒ 指名期望路径的中文说明。internal static + 目录作参数是有意的接缝：
        // 真实点击路径传的是 Application.StartupPath（随包分发 ⇒ 文件总在），「缺席」「读不动」
        // 两个方向由用例在可控目录上经反射打这同一个入口（Gui.LicenseMissingFileShowsPathInsteadOfThrowing）。
        // 任何异常都在这里转成说明文本 —— 「关于/开源许可」这扇门绝不允许抛异常，更不允许崩溃。
        internal static string BuildLicenseNotice(string startupDir)
        {
            string expected;
            try
            {
                expected = string.IsNullOrEmpty(startupDir)
                    ? LicenseFileName
                    : Path.Combine(startupDir, LicenseFileName);
            }
            catch (Exception)
            {
                expected = LicenseFileName;
            }

            try
            {
                if (!File.Exists(expected))
                {
                    return "未找到随包分发的开源许可声明文件 THIRD-PARTY-NOTICES.txt。\n\n" +
                           "期望位置：\n" + expected + "\n\n" +
                           "本程序内嵌了 7-Zip；其 LGPL / BSD / unRAR 限制等许可信息应当随程序一同分发" +
                           "（即上述文件）。文件缺失时请重新获取完整的程序分发包。";
                }

                string text = File.ReadAllText(expected, Encoding.UTF8);
                if (string.IsNullOrEmpty(text))
                {
                    return "开源许可声明文件存在但内容为空：\n" + expected +
                           "\n\n请重新获取完整的程序分发包。";
                }
                return text;
            }
            catch (Exception ex)
            {
                return "开源许可声明文件无法读取（" + ex.GetType().Name + "）：\n" + expected +
                       "\n\n请检查文件的读取权限，或重新获取完整的程序分发包。";
            }
        }

        private void ShowLicenseNotice(string notice)
        {
            if (_licenseDialog != null && !_licenseDialog.IsDisposed)
            {
                if (!_licenseDialog.Visible) { _licenseDialog.Show(this); }
                _licenseDialog.Activate();
                return;
            }

            Form dialog = new Form();
            dialog.Text = "关于/开源许可 — Rerar";
            dialog.Font = Font;
            dialog.ShowInTaskbar = false;
            dialog.MinimizeBox = false;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(780, 560);
            dialog.MinimumSize = new Size(420, 300);

            TextBox body = new TextBox();
            body.Name = "licenseText";
            body.Multiline = true;
            body.ReadOnly = true;
            body.ScrollBars = ScrollBars.Both;
            body.WordWrap = false;
            body.Dock = DockStyle.Fill;
            body.Text = notice;

            FlowLayoutPanel bottom = new FlowLayoutPanel();
            bottom.Dock = DockStyle.Bottom;
            bottom.AutoSize = true;
            bottom.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bottom.FlowDirection = FlowDirection.RightToLeft;
            bottom.Padding = new Padding(8);

            Button close = new Button();
            close.Name = "btnLicenseClose";
            close.Text = "关闭";
            close.AutoSize = true;
            close.Click += delegate { dialog.Close(); };
            bottom.Controls.Add(close);

            dialog.Controls.Add(body);
            dialog.Controls.Add(bottom);
            dialog.FormClosed += delegate { _licenseDialog = null; };

            _licenseDialog = dialog;
            dialog.Show(this);
        }

        private void BtnLoadDict_Click(object sender, EventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Title = "选择密码字典（每行一个）";
            dialog.Filter = "文本文件|*.txt;*.dic;*.lst|所有文件|*.*";
            try
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
                _dictPath = dialog.FileName;
                _lblDict.Text = "已导入：" + _dictPath;
            }
            catch (Exception ex)
            {
                Warn("打开字典选择框失败（" + ex.GetType().Name + "：" + ex.Message + "）");
            }
        }

        private void Warn(string message)
        {
            // 界面上的警告一律**非模态**：一个批量工具绝不能靠弹框来报错（§7：200 个失败不能弹 200 次）。
            AppendLog("警告：" + message);
            _lblStatus.Text = "状态：" + message;
        }

        // ==================================================================
        // 进程/系统交互
        // ==================================================================

        private const uint EsContinuous = 0x80000000;
        private const uint EsSystemRequired = 0x00000001;
        private const uint FlashwAll = 0x00000003;
        private const uint FlashwTimerNoForeground = 0x0000000C;

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        // G5：长任务期间阻止系统睡眠（外接盘掉电 / I/O 报错是真实的故障模式）。结束/取消时清除。
        private static void SetKeepAwake(bool keepAwake)
        {
            try
            {
                SetThreadExecutionState(keepAwake ? (EsContinuous | EsSystemRequired) : EsContinuous);
            }
            catch (Exception)
            {
                // 拿不到执行状态不算错误（某些受策略限制的会话里会失败），绝不能因此中断解压。
            }
        }

        // 析构：把日志、托盘图标、计时器、共享 ToolTip、右键菜单与扫描取消源都收干净 ——
        // 尤其是日志文件句柄（否则会把文件锁着）。修复轮 Minor 7：以前 _tooltip / _itemMenu /
        // _scanCts 三个组件从来没有被释放过（ToolTip 与 ContextMenuStrip 都持有系统资源）。
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (_uiTimer != null) { _uiTimer.Stop(); _uiTimer.Dispose(); } }
                catch (Exception) { }
                try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } }
                catch (Exception) { }
                try { if (_tooltip != null) { _tooltip.Dispose(); } }
                catch (Exception) { }
                try { if (_itemMenu != null) { if (_lstItems != null) { _lstItems.ContextMenuStrip = null; } _itemMenu.Dispose(); } }
                catch (Exception) { }
                try { if (_log != null) { _log.Dispose(); } }
                catch (Exception) { }
                try { if (_scanCts != null) { _scanCts.Cancel(); _scanCts.Dispose(); } }
                catch (Exception) { }
                // _runCts 刻意**不**在这里 Dispose：还可能有一个工作线程正持着它的 Token 在跑，
                // 释放它换不来任何东西，却可能把一个 ObjectDisposedException 抛进正在进行的解压
                //（「进度回调绝不抛」是硬约束）。运行结束时的 CancellationTokenSource 由 GC 收。
                try { SetKeepAwake(false); }
                catch (Exception) { }
            }
            base.Dispose(disposing);
        }
    }

    // ======================================================================
    // 拖放区：画一圈虚线边框（J11 的「大号虚线拖放区」）。就这一件事，没有别的行为。
    // ======================================================================
    internal sealed class DropZonePanel : Panel
    {
        public DropZonePanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(250, 250, 252);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(Color.FromArgb(150, 150, 160), 1F))
            {
                pen.DashStyle = DashStyle.Dash;
                Rectangle bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
                e.Graphics.DrawRectangle(pen, bounds);
            }
        }
    }

    // ======================================================================
    // 删除二次确认（A5）。**不默认聚焦确定**：AcceptButton 为空，回车不会确认；Esc 只能返回。
    // 之所以自己画一个而不是用 MessageBox：MessageBox 的默认按钮只能四选一，做不到「回车不确认」，
    // 而「300 个包一路回车就全删了」正是要防的那件事。
    // ======================================================================
    internal sealed class ConfirmDeleteForm : Form
    {
        // 默认焦点要落到「返回」上（而不是确定）。必须留一个字段：构造函数里控件还没有可见性，
        // 那时设 ActiveControl 会抛「无法激活不可见或已禁用的控件」（实测），所以放到 OnShown。
        private readonly Button _back;

        public ConfirmDeleteForm(int count)
        {
            SuspendLayout();

            Text = "确认：解压成功后删除原包";
            Font = new Font(MainForm.PickFontFamily(), 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 240);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.Padding = new Padding(14);
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label text = new Label();
            text.Name = "lblConfirmText";
            text.AutoSize = true;
            text.MaximumSize = new Size(510, 0);
            text.ForeColor = Color.FromArgb(178, 34, 34);
            text.Text = MainForm.DeleteConfirmText(count);

            Label warning = new Label();
            warning.AutoSize = true;
            warning.MaximumSize = new Size(510, 0);
            warning.Text = "删除走回收站，但回收站「不是保证」：跨卷/可移动介质/超出配额时可能被永久删除，" +
                "程序会在结果里如实报告实际处置方式。";

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Dock = DockStyle.Fill;

            Button back = new Button();
            back.Name = "btnBack";
            back.Text = "返回（不删除）";
            back.AutoSize = true;
            back.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            back.Padding = new Padding(10, 4, 10, 4);
            back.TabIndex = 0;                       // 键盘默认落在这里：**不是**确定
            back.DialogResult = DialogResult.Cancel;
            back.AccessibleName = "返回，不开始（默认）";
            _back = back;

            Button confirm = new Button();
            confirm.Name = "btnConfirm";
            confirm.Text = "我明白，开始解压并删除";
            confirm.AutoSize = true;
            confirm.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            confirm.Padding = new Padding(10, 4, 10, 4);
            confirm.TabIndex = 1;
            confirm.DialogResult = DialogResult.OK;
            confirm.AccessibleName = "确认开始（需要显式点击）";

            buttons.Controls.Add(back);
            buttons.Controls.Add(confirm);

            // AcceptButton **刻意留空**：回车绝不能确认删除（用户一路回车不该把原包删掉）。
            // CancelButton 设为「返回」：Esc 的语义只能是放弃。
            CancelButton = back;
            AcceptButton = null;

            root.Controls.Add(text, 0, 0);
            root.Controls.Add(warning, 0, 1);
            root.Controls.Add(buttons, 0, 2);
            Controls.Add(root);

            ResumeLayout(true);
        }

        // 焦点默认落在「返回」上。放在 OnShown 而不是构造函数里：控件在构造期还没有可见性，
        // 那时设置 ActiveControl 会当场抛异常（实测「无法激活不可见或已禁用的控件」）。
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                if (_back != null) { ActiveControl = _back; }
            }
            catch (Exception)
            {
                // 拿不到焦点也不影响正确性：AcceptButton 为空，回车本来就不能确认。
            }
        }
    }

    // ======================================================================
    // 界面日志尾部：**自绘虚拟化**环形缓冲视图（J4 + 横向滚动修复）。
    //   * 只绘制可见的那几十行（O(可见行)，与总行数无关）；
    //   * 总容量是 MainForm.LogTailLines（2000），更老的行被丢掉（完整日志在文件里）；
    //   * AutoScroll 关掉之后，用户往上翻时行位置保持不动（这就是「暂停自动滚动」）；
    //   * 右手边一根竖向滚动条、下边一根横向滚动条：本项目的日志行经常是完整的绝对中文路径，
    //     没有横向滚动条时超出右边缘的部分**永远读不到**（这就是用户报的那个缺陷）。
    //
    // 【两杆的分工】横向范围只由**当前可见行里最宽的那一行**决定（用户能读到的就是这几行），
    // 宽度一律用 TextRenderer.MeasureText 配**真实 Font** 量出来，绝不假设字宽。
    // 于是纵向滚一下、横向范围就按新的一屏重算 —— 与虚拟化绘制同一个口径。
    // ======================================================================
    internal sealed class LogTailView : Control
    {
        // 文本区左内边距（与绘制时的 4 像素对齐）；横向视口就是从它到竖滚动条之间。
        private const int TextPad = 4;

        private readonly VScrollBar _bar;
        private readonly HScrollBar _hbar;
        private string[] _lines = new string[0];
        private int _first;
        private int _lineHeight = 16;
        private bool _syncingBar;
        private bool _autoScroll = true;

        // 横向偏移与「可见行最宽宽度」的缓存。缓存只按（起始行 + 可见行数）失效：
        // 纵向没动时重绘不必再量一遍文字（每屏几十次 MeasureText 不便宜）。
        private int _hOffset;
        private int _hMaxCached;
        private int _hCacheFirst = -1;
        private int _hCacheVisible = -1;
        private bool _hCacheValid;

        public LogTailView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            BackColor = Color.White;
            ForeColor = Color.FromArgb(30, 30, 30);
            TabStop = true;

            // 【停靠顺序 = 角上不打架】Controls 里 index 0 是 z 序最前、停靠时**最后**处理。
            // 所以先加横条、后加竖条 ⇒ 竖条先占满右边缘的整列高度，横条再在**剩下的**宽度里
            // 铺底 —— 横条自然就短了竖条那一截，角上不会被两根同时认领（反过来加的话，竖条会
            // 一直伸到控件最底部，把横条的右端压掉一截，滑块的右端就点不到了）。
            _hbar = new HScrollBar();
            _hbar.Dock = DockStyle.Bottom;
            _hbar.Visible = true;
            _hbar.ValueChanged += HBar_ValueChanged;
            Controls.Add(_hbar);

            _bar = new VScrollBar();
            _bar.Dock = DockStyle.Right;
            _bar.Visible = true;
            _bar.ValueChanged += Bar_ValueChanged;
            Controls.Add(_bar);
        }

        public bool AutoScroll
        {
            get { return _autoScroll; }
            set
            {
                _autoScroll = value;
                if (value) { ScrollTo(int.MaxValue); }
            }
        }

        public int LineCount { get { return _lines.Length; } }
        public int FirstVisibleLine { get { return _first; } }
        public int VisibleLineCount { get { return VisibleLines; } }

        // ---- 横向滚动的只读量（自动化用例直接读它们，见 src\Tests\MainFormTests.cs） ----
        // 用**产品自己的**算术，而不是测试侧重算一遍公式：重算只能证明测试算得对。
        public int MaxHOffset { get { return Math.Max(0, MaxLineWidth() - HViewportWidth); } }

        // 横向偏移：**读写**都在这儿，越界一律夹回 [0, MaxHOffset]（这是「偏移绝不超过范围」
        // 这条契约的唯一执行点；写进来的值不会让滚动条停在一个非法位置上）。
        public int HOffset
        {
            get { return _hOffset; }
            set { SetHOffset(value); }
        }

        public int HViewportWidth { get { return TextAreaWidth; } }

        // 当前可见行里最宽的那一行的实测宽度（TextRenderer + 真实 Font 量的）。
        // 暴露它是为了让「范围算得对」这条判据可断言：MaxHOffset == WidestVisibleLineWidth - HViewportWidth
        // 应当恒成立（用例 Gui.LogTailHorizontalRangeFollowsWidestVisibleLine 钉住它）。
        public int WidestVisibleLineWidth { get { return MaxLineWidth(); } }
        public int HScrollBarMinimum { get { return _hbar.Minimum; } }

        // 【注意这两个名字的语义】WinForms 的 ScrollBar.Maximum 不是「最大可滚动量」，而是
        // 「文档长度」：滑块最右时 Value = Maximum - LargeChange + 1。本类的口径统一为**可滚动量**
        //（= Maximum - LargeChange + 1，也就是 HOffset 的上界），所以这里做那次换算 ——
        // 用例断言的就是用户真正能滚多远，而不是 WinForms 那个容易读错的裸值。
        public int HScrollBarMaximum { get { return Math.Max(0, _hbar.Maximum - _hbar.LargeChange + 1); } }
        public int HScrollBarLargeChange { get { return _hbar.LargeChange; } }
        public int HScrollBarValue { get { return _hbar.Value; } }

        // ---- 竖向滚动条的只读量（把「范围语义没被改坏」变成可断言的事实） ----
        public int VScrollBarMinimum { get { return _bar.Minimum; } }
        public int VScrollBarMaximum { get { return _bar.Maximum; } }
        public int VScrollBarValue { get { return _bar.Value; } }

        private int VisibleLines
        {
            get { return Math.Max(1, (ClientSize.Height - 2) / _lineHeight); }
        }

        // 文本区（不含竖滚动条与 4 像素内边距）的宽度。
        private int TextAreaWidth
        {
            get { return Math.Max(10, ClientSize.Width - (_bar.Visible ? _bar.Width : 0) - TextPad); }
        }

        // 只在 **UI 线程**调用（MainForm 的心跳里）。
        //
        // 【关键】自动滚动关掉时**绝不能**再跳到尾部：用户往上翻着看历史，而每 200ms 的刷新把
        // 视口拽回底部 ——「日志一直在往下跳」本身就是个 bug（审计 J4 专门点出这件事）。
        // 关掉时只把位置夹回合法范围（内容变长不该改变用户正在看的那一行）。
        public void SetLines(string[] lines)
        {
            _lines = lines == null ? new string[0] : lines;
            _lineHeight = Math.Max(12, Font.Height);
            _hCacheValid = false;          // 行内容变了 ⇒ 最宽行必须重量

            if (_autoScroll)
            {
                ScrollTo(int.MaxValue);
                return;
            }

            int max = Math.Max(0, _lines.Length - VisibleLines);
            if (_first > max) { _first = max; }
            if (_first < 0) { _first = 0; }
            SyncBar();
            Invalidate();
        }

        private void ScrollTo(int firstLine)
        {
            int max = Math.Max(0, _lines.Length - VisibleLines);
            int wanted = firstLine == int.MaxValue ? max : firstLine;
            if (wanted < 0) { wanted = 0; }
            if (wanted > max) { wanted = max; }
            _first = wanted;
            SyncBar();
            Invalidate();
        }

        private void SyncBar()
        {
            int max = Math.Max(0, _lines.Length - VisibleLines);
            _bar.Minimum = 0;
            _bar.Maximum = max <= 0 ? 0 : max + VisibleLines - 1;
            _bar.LargeChange = Math.Max(1, VisibleLines);
            _bar.SmallChange = 1;

            // 【这里原来是一个 `catch (Exception) { }` —— 已删掉，理由如下】
            // 它把 ArgumentOutOfRangeException（例如 Value 越界）整个吞掉，症状是「滚动条静默停在
            // 上一次的范围上」——这一整类 bug 就是这样活下来的：任何测试都看不见它。
            // 现在改成**可证明不会越界**的写法而不是再包一层 catch：
            //   Minimum ≤ Maximum 恒成立（Maximum ≥ 0 = Minimum）；
            //   Value = Min(_first, Maximum - LargeChange + 1)，而 _first ≥ 0 且
            //   Maximum - LargeChange + 1 = max ≥ 0 ⇒ Value 同时也 ≤ Maximum（ScrollBar 的另一条约束）。
            // 万一将来有人改坏了这段算术，下面这条断言在 Debug 里当场炸出来，而不是悄悄留下陈旧范围。
            int value = Math.Min(_first, Math.Max(_bar.Minimum, _bar.Maximum - _bar.LargeChange + 1));

            _syncingBar = true;
            try
            {
                _bar.Value = value;
            }
            finally
            {
                _syncingBar = false;
            }
            _bar.Enabled = max > 0;

            System.Diagnostics.Debug.Assert(_bar.Value >= _bar.Minimum && _bar.Value <= _bar.Maximum,
                "竖向滚动条的值越出了自己的范围：算术被改坏了");

            SyncH();
        }

        // 横向范围/偏移：只按**当前可见行里最宽的那一行**算，并把它夹进 [0, max - viewport]。
        // 范围一旦不再需要（内容变窄、视口变宽）偏移必须**回 0** —— 留着陈旧偏移会让用户下次
        // 打开日志时凭空看不到左边一半。
        private void SyncH()
        {
            int maxOffset = MaxHOffset;
            if (_hOffset > maxOffset) { _hOffset = maxOffset; }
            if (_hOffset < 0) { _hOffset = 0; }

            // 【坐标换算 —— 这里有一个不写清楚就必然踩中的坑】WinForms 的 ScrollBar 里
            // 「滑块能到达的最右位置」是 Maximum - LargeChange + 1，不是 Maximum。所以想让
            // 滑块正好能在 [0, maxOffset] 里滑动，必须设 Maximum = maxOffset + LargeChange - 1；
            // 直接把 Maximum 设成 maxOffset、又把 LargeChange 设成视口宽度（视口比 maxOffset 还宽时），
            // 可滚动量就成了 maxOffset - 视口 + 1 ≤ 0 —— **滑块动不了**，横向滚动条形同虚设。
            int largeChange = Math.Max(1, HViewportWidth);
            _hbar.Minimum = 0;
            _hbar.LargeChange = largeChange;
            _hbar.Maximum = maxOffset <= 0 ? 0 : maxOffset + largeChange - 1;
            _hbar.SmallChange = 16;

            _syncingHBar = true;
            try
            {
                // 换算之后 Value ∈ [0, maxOffset] 恒在 [Minimum, Maximum - LargeChange + 1] 之内，
                // 不会抛 ArgumentOutOfRangeException（这正是以前那个 `catch (Exception) { }` 吞掉的东西）。
                _hbar.Value = _hOffset;
            }
            finally
            {
                _syncingHBar = false;
            }
            _hbar.Enabled = maxOffset > 0;

            System.Diagnostics.Debug.Assert(_hbar.Value <= _hbar.Maximum - _hbar.LargeChange + 1,
                "横向滑块能到达的最右位置比重叠范围还小：算术被改坏了");
        }

        private bool _syncingHBar;

        // 可见行里最宽的那一行有多宽（像素），用真实 Font 量。
        // 缓存按（起始行 + 可见行数）失效：纵向没动时重复重绘不再量文字。
        private int MaxLineWidth()
        {
            int visible = VisibleLines;
            if (_hCacheValid && _hCacheFirst == _first && _hCacheVisible == visible)
            {
                return _hMaxCached;
            }

            int widest = 0;
            bool first = true;
            for (int i = 0; i < visible; i++)
            {
                int index = _first + i;
                if (index >= _lines.Length) { break; }

                string line = _lines[index];
                if (string.IsNullOrEmpty(line)) { continue; }

                if (first)
                {
                    // 【预热】换过字体的那一刻（OnFontChanged 里紧接着就会调到这里）GDI 的字体映射还
                    // 没建立，第一次 MeasureText 可能返回 0 宽 —— 那会把「横向范围 = 0」缓存下来，
                    // 于是滚动条静默消失（正是本缺陷的形态）。先用空串量一次把映射建立起来，再量真的。
                    TextRenderer.MeasureText(" ", Font, new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                    first = false;
                }

                Size measured = TextRenderer.MeasureText(line, Font, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                if (measured.Width > widest) { widest = measured.Width; }
            }

            _hMaxCached = widest;
            _hCacheFirst = _first;
            _hCacheVisible = visible;
            _hCacheValid = true;
            return widest;
        }

        // 把 _hOffset 夹回合法范围并同步滚动条。用例直接调它来钉「两端都夹得住」。
        public void EnsureHOffsetInRange()
        {
            SyncH();
        }

        private void SetHOffset(int value)
        {
            if (value < 0) { value = 0; }
            _hOffset = value;
            SyncH();
        }

        private void Bar_ValueChanged(object sender, EventArgs e)
        {
            if (_syncingBar) { return; }
            _autoScroll = false;
            ScrollTo(_bar.Value);
        }

        private void HBar_ValueChanged(object sender, EventArgs e)
        {
            if (_syncingHBar) { return; }
            // 横向滚动**不动纵向视口**、也不关掉「跟随尾部」：用户要读的是最新那几行的右半截。
            _hOffset = _hbar.Value;
            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            _lineHeight = Math.Max(12, Font.Height);
            _hCacheValid = false;   // 行宽是按字体量的 ⇒ 换字体必须重量
            // 而且要**当场**把范围与偏移重算一遍：字号变小时原来的横向偏移会越界、范围会变小，
            // 不重算就会把陈旧的范围留给滚动条（换字体不是 Resize，走不到 OnResize 那条路）。
            SyncBar();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SyncBar();       // 内部会 SyncH：视口变宽 ⇒ 范围变小 ⇒ 偏移夹回（必要时归 0）
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);

            // Shift + 滚轮 = 横向滚动（长行读尾巴的快捷键，与 Windows 上多数文本框一致）。
            if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift)
            {
                EnsureHOffsetInRange();
                int steps = e.Delta / 120;
                SetHOffset(_hOffset - steps * 3 * _hbar.SmallChange);
                return;
            }

            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines <= 0) { lines = 3; }
            int delta = (e.Delta / 120) * lines;
            _autoScroll = false;
            ScrollTo(_first - delta);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Home:
                case Keys.End:
                // 横向：左右箭头与 Home/End（Shift+左右由它们自己那条分支处理，键码相同）。
                case Keys.Left:
                case Keys.Right:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int page = Math.Max(1, VisibleLines - 1);
            switch (e.KeyCode)
            {
                case Keys.Up: _autoScroll = false; ScrollTo(_first - 1); e.Handled = true; break;
                case Keys.Down: ScrollTo(_first + 1); e.Handled = true; break;
                case Keys.PageUp: _autoScroll = false; ScrollTo(_first - page); e.Handled = true; break;
                case Keys.PageDown: ScrollTo(_first + page); e.Handled = true; break;
                // Home/End 带 Shift 时是**横向**的「回到最左 / 跳到最右」（纵向那两条不带 Shift）。
                case Keys.Home:
                    if ((e.Modifiers & Keys.Shift) == Keys.Shift) { SetHOffset(0); }
                    else { _autoScroll = false; ScrollTo(0); }
                    e.Handled = true;
                    break;
                case Keys.End:
                    if ((e.Modifiers & Keys.Shift) == Keys.Shift) { EnsureHOffsetInRange(); SetHOffset(int.MaxValue); }
                    else { _autoScroll = true; ScrollTo(int.MaxValue); }
                    e.Handled = true;
                    break;
                case Keys.Left:
                    SetHOffset(_hOffset - _hbar.SmallChange);
                    e.Handled = true;
                    break;
                case Keys.Right:
                    SetHOffset(_hOffset + _hbar.SmallChange);
                    e.Handled = true;
                    break;
            }
        }

        // **只画可见行**：无论尾部里有多少行，一次重绘的代价都由窗口高度决定。
        // 横向偏移只作用在这几十行上（画在「平移后的坐标」里，超出文本区的部分被裁掉），
        // 于是水平滚动的代价同样是 O(可见行)，与总行数无关。
        protected override void OnPaint(PaintEventArgs e)
        {
            int textWidth = TextAreaWidth;
            int textBottom = Math.Max(0, ClientSize.Height - (_hbar.Visible ? _hbar.Height : 0));

            using (SolidBrush back = new SolidBrush(BackColor))
            {
                e.Graphics.FillRectangle(back, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
            }

            if (_lines.Length == 0)
            {
                using (SolidBrush empty = new SolidBrush(Color.FromArgb(120, 120, 120)))
                {
                    e.Graphics.DrawString("（还没有日志）", Font, empty, new PointF(TextPad, 2));
                }
                return;
            }

            // 只画可见行 + 只画文本区：竖滚动条那一列、横滚动条那一条都不许被文字盖住。
            Region oldClip = e.Graphics.Clip;
            e.Graphics.SetClip(new Rectangle(0, 0, textWidth, textBottom));

            using (SolidBrush fore = new SolidBrush(ForeColor))
            {
                int visible = VisibleLines;
                for (int i = 0; i < visible; i++)
                {
                    int index = _first + i;
                    if (index >= _lines.Length) { break; }

                    string line = _lines[index];
                    if (line == null) { continue; }

                    // 整行从「左内边距 - 偏移」处开始画，右端不设裁剪框（负坐标在 GDI+ 里合法），
                    // 于是偏移 > 0 时左边那截自然落在裁剪区之外。
                    e.Graphics.DrawString(line, Font, fore,
                        new PointF(TextPad - _hOffset, i * _lineHeight));
                }
            }

            e.Graphics.Clip = oldClip;
        }
    }

    // ======================================================================
    // 日志尾部环形缓冲（纯数据，**不碰任何控件**）：容量固定，满了就丢最老的。
    // 单测直接构造它钉住「有界」这件事（界面侧由 LogSink 加锁使用）。
    // ======================================================================
    internal sealed class LogTailBuffer
    {
        private readonly string[] _lines;
        private int _next;
        private int _count;
        private long _total;

        public LogTailBuffer(int capacity)
        {
            if (capacity < 1) { capacity = 1; }
            _lines = new string[capacity];
        }

        public int Capacity { get { return _lines.Length; } }
        public int Count { get { return _count; } }
        public long TotalWritten { get { return _total; } }

        public void Add(string line)
        {
            _lines[_next] = line;
            _next = (_next + 1) % _lines.Length;
            if (_count < _lines.Length) { _count++; }
            _total++;
        }

        // 从最老到最新。
        public string[] Snapshot()
        {
            string[] result = new string[_count];
            for (int i = 0; i < _count; i++)
            {
                int index = (_next - _count + i + _lines.Length * 2) % _lines.Length;
                result[i] = _lines[index];
            }
            return result;
        }
    }

    // ======================================================================
    // 日志三份存储里的两份（J4）：完整日志**流式**写文件 + 界面尾部环形缓冲。
    //
    // 【密码卫生】写进来的每一行都要过 Redact：任何一个登记过的密码都被换成 ***。
    // Core 本身从不把密码写进判词（Task 10 只记「候选序号 + 来源」），这里是**界面层的兜底**：
    // 界面上任何一条自己拼的文本一旦把密码拼进去，也不会落到日志文件里。
    // 【进程永不因日志崩掉】任何 I/O 失败都只记一条问题，绝不抛。
    // ======================================================================
    internal sealed class LogSink : IDisposable
    {
        private readonly object _gate = new object();
        private readonly LogTailBuffer _tail;
        private readonly List<string> _secrets = new List<string>();
        private StreamWriter _file;
        private bool _problemReported;

        // carryOverTail（可选）：上一轮界面上已有的尾部行。只进**界面尾部**，绝不写进本轮的日志文件 ——
        // 每轮的日志文件仍然只含自己那一轮的内容（逐轮可查、不互相污染），但界面上滚动的日志是
        // 连续的：重试轮开始时不会把之前看过的东西「清屏」（修复轮 Finding 3 的第二半）。
        public LogSink(string filePath) : this(filePath, null) { }

        public LogSink(string filePath, string[] carryOverTail)
        {
            FilePath = filePath;
            FileProblem = "";
            _tail = new LogTailBuffer(MainForm.LogTailLines);
            if (carryOverTail != null)
            {
                for (int i = 0; i < carryOverTail.Length; i++) { _tail.Add(carryOverTail[i]); }
            }

            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) { Directory.CreateDirectory(dir); }

                // UTF-8 **带 BOM**：与报告同一理由（记事本/Excel 双击打开中文不乱码）。
                _file = new StreamWriter(filePath, false, new UTF8Encoding(true));
                _file.AutoFlush = false;      // 由界面心跳每 200ms Flush 一次（见 MainForm.UiTimer_Tick）
            }
            catch (Exception ex)
            {
                _file = null;
                FileProblem = "无法写入日志文件（" + ex.GetType().Name + "：" + ex.Message +
                              "）：本次运行的完整日志只有界面里的尾部";
            }
        }

        public string FilePath { get; private set; }
        public string FileProblem { get; private set; }
        public bool FileOk { get { return _file != null; } }

        // 登记一个绝不能出现在日志里的密码值。
        public void RegisterSecret(string secret)
        {
            if (string.IsNullOrEmpty(secret)) { return; }
            lock (_gate)
            {
                if (!_secrets.Contains(secret)) { _secrets.Add(secret); }
            }
        }

        private string Redact(string line)
        {
            string result = line;
            for (int i = 0; i < _secrets.Count; i++)
            {
                string secret = _secrets[i];
                if (secret.Length == 0) { continue; }
                // 单字符密码会把日志里所有同字符都变成 ***（很吵），这是**刻意**选的那一侧：
                // 宁可日志难看，也不让一个密码出现在盘上。
                result = result.Replace(secret, "***");
            }
            return result;
        }

        public void Write(string line)
        {
            if (line == null) { return; }

            lock (_gate)
            {
                string safe;
                try { safe = Redact(line); }
                catch (Exception) { safe = "（这一行日志无法脱敏，已丢弃）"; }

                _tail.Add(safe);

                if (_file == null) { return; }

                try
                {
                    _file.Write(safe);
                    _file.Write("\r\n");
                }
                catch (Exception ex)
                {
                    // 写失败只报一次，然后停止写文件；界面尾部仍然可用（日志缺席绝不影响解压）。
                    try { _file.Dispose(); }
                    catch (Exception) { }
                    _file = null;
                    FileProblem = "日志文件写入失败（" + ex.GetType().Name + "：" + ex.Message +
                                  "）：已停止写文件，界面里的尾部仍然可用";
                    ReportProblem();
                }
            }
        }

        // 落盘失败要**看得见**（Task 11 对崩溃恢复日志的裁定在此同样适用：静默缺席等于让用户
        // 以为自己有完整日志）。写进尾部一行，界面里就会出现。
        private void ReportProblem()
        {
            if (_problemReported || string.IsNullOrEmpty(FileProblem)) { return; }
            _problemReported = true;
            _tail.Add("⚠ " + FileProblem);
        }

        public string[] Snapshot()
        {
            lock (_gate) { return _tail.Snapshot(); }
        }

        // 界面心跳每 200ms 调一次：崩溃时最多丢 200ms 的日志（而不是整份）。
        public void Flush()
        {
            lock (_gate)
            {
                if (_file == null) { return; }
                try { _file.Flush(); }
                catch (Exception ex)
                {
                    try { _file.Dispose(); }
                    catch (Exception) { }
                    _file = null;
                    FileProblem = "日志落盘失败（" + ex.GetType().Name + "：" + ex.Message + "）：已停止写文件";
                    ReportProblem();
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_file == null) { return; }
                try { _file.Flush(); _file.Dispose(); }
                catch (Exception) { }
                _file = null;
            }
        }
    }

    // ======================================================================
    // 界面设置：窗口几何 + 上次用过的目录（%APPDATA%）。**原子写**（临时文件 + 替换），
    // 加载时校验目录仍然存在（存下来的路径可能早被删了/在拔掉的移动盘上）。
    // 任何 I/O 失败都不抛 —— 设置存不上绝不能让窗口起不来或关不掉。
    // ======================================================================
    internal sealed class AppSettings
    {
        private const string Header = "# Rerar 界面设置（窗口几何 + 上次用过的目录）。程序自动写，可安全删除。";

        private readonly string _path;

        public bool HasGeometry;
        public int WindowX;
        public int WindowY;
        public int WindowWidth;
        public int WindowHeight;
        public bool Maximized;
        public string LastInputFolder = "";

        public AppSettings(string path)
        {
            _path = path;
        }

        public string FilePath { get { return _path; } }

        // 生产默认落点：%APPDATA%\Rerar\settings.ini。
        // 进程级覆盖 RERAR_SETTINGS_PATH 供测试把落点挪到临时目录（与 RERAR_JOURNAL_ROOT 同一机制）。
        public static string DefaultFilePath
        {
            get
            {
                string overridden = Environment.GetEnvironmentVariable("RERAR_SETTINGS_PATH");
                if (!string.IsNullOrEmpty(overridden)) { return overridden; }

                string appData;
                try { appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); }
                catch (Exception) { appData = ""; }
                if (string.IsNullOrEmpty(appData)) { appData = Path.GetTempPath(); }

                return Path.Combine(Path.Combine(appData, "Rerar"), "settings.ini");
            }
        }

        public bool Load()
        {
            try
            {
                if (string.IsNullOrEmpty(_path) || !File.Exists(_path)) { return false; }

                Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(_path, Encoding.UTF8))
                {
                    if (raw == null) { continue; }
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') { continue; }

                    int eq = line.IndexOf('=');
                    if (eq <= 0) { continue; }
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }

                HasGeometry = ReadBool(map, "geometry");
                WindowX = ReadInt(map, "x");
                WindowY = ReadInt(map, "y");
                WindowWidth = ReadInt(map, "width");
                WindowHeight = ReadInt(map, "height");
                Maximized = ReadBool(map, "maximized");

                // 存下来的目录可能已经不在了（删了、在拔掉的移动盘上、权限变了）：
                // 丢弃它，绝不把一个不存在的初始目录塞给用户。
                string folder = ReadString(map, "lastInputFolder");
                LastInputFolder = folder.Length > 0 && Directory.Exists(folder) ? folder : "";

                return true;
            }
            catch (Exception)
            {
                // 坏文件按「没有设置」处理（而不是抛出去把窗口构造打断）。
                return false;
            }
        }

        public void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(_path)) { return; }

                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) { Directory.CreateDirectory(dir); }

                StringBuilder sb = new StringBuilder();
                sb.Append(Header).Append("\r\n");
                sb.Append("geometry=").Append(HasGeometry ? "1" : "0").Append("\r\n");
                sb.Append("x=").Append(Int(WindowX)).Append("\r\n");
                sb.Append("y=").Append(Int(WindowY)).Append("\r\n");
                sb.Append("width=").Append(Int(WindowWidth)).Append("\r\n");
                sb.Append("height=").Append(Int(WindowHeight)).Append("\r\n");
                sb.Append("maximized=").Append(Maximized ? "1" : "0").Append("\r\n");
                sb.Append("lastInputFolder=").Append(LastInputFolder == null ? "" : LastInputFolder).Append("\r\n");

                // **原子替换**：先写同目录的临时文件，再 File.Replace / Move 改名。断电或被杀时，
                // 用户拿到的要么是旧的完整设置，要么是新的完整设置 —— 绝不是一个半截文件。
                string temporary = _path + ".tmp";
                File.WriteAllText(temporary, sb.ToString(), new UTF8Encoding(true));
                if (File.Exists(_path)) { File.Replace(temporary, _path, null); }
                else { File.Move(temporary, _path); }
            }
            catch (Exception)
            {
                // 设置写不进去（权限/盘满）绝不影响解压与关闭。
            }
        }

        private static string Int(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string ReadString(Dictionary<string, string> map, string key)
        {
            string value;
            return map.TryGetValue(key, out value) && value != null ? value : "";
        }

        private static int ReadInt(Dictionary<string, string> map, string key)
        {
            int parsed;
            string raw = ReadString(map, key);
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) { return parsed; }
            return 0;
        }

        private static bool ReadBool(Dictionary<string, string> map, string key)
        {
            string raw = ReadString(map, key);
            return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
