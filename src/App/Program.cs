// Rerar 程序入口：GUI / CLI 双模式分发（Task 12 把 CLI 从骨架补成完整契约）。
//
// 【为什么 CLI 是「无头驱动面」】本程序是 /target:winexe 的进程，没有控制台、界面在无人值守的机器上
// 也跑不了；而 Task 15 的验收脚本与全部自动化都必须能驱动**同一条** Extractor 流水线。
// CLI 就是那条不含窗口的入口 —— 它不做任何自己的解压逻辑，只是参数 → RunOptions → Extractor。
//
// 【契约（Task 15 逐字依赖，不得改名）】
//     Rerar.exe --cli --target <path> [--target <path>...] [--delete] [--password <pw>]
//               [--dict <file>] [--depth <n>] [--json-out <file>]
//     退出码：0 全部成功；1 有失败或跳过（含用户取消）；2 致命错误（什么都没跑成 / 报告写不出来）。
//     映射只有一份实现：Rerar.Core.RunExitCodes（修复轮 Finding 2 的裁定见那里）。
//     --json-out：机器可读的结果数组，对象的键逐字是
//                 path,status,layers,files,failed,outputDir,message（= ArchiveResult 的字段名小写）。
//                 数组**每个归档恰好一个对象**：致命中止后没轮到的候选也在里面（修复轮 Finding 1）。
//                 【本轮新增（加法）】每个对象末尾多一个 originalDisposition（"kept"/"deleted"/
//                 "quarantined"）：原包的去向。既有 7 个键的名字与顺序一字未动 —— 顶层仍然是
//                 那个数组（不改成对象），所以按数组读的脚本、验收脚本与 CliTests 全都不受影响。
//
// 【本文件必须自己负责的三件事】
//   1) `--cli --selftest` 打印 version=<n> 并退出 0 —— tests\smoke.ps1 依赖的既有契约，绝不能改坏；
//   2) 「未处理数量」只取自 RunSummary.NotAttempted（Task 10 裁定的**权威清单**），绝不与 Results
//      相加：深度触顶那一项同时出现在两个集合里，相加就是同一件事数两遍（见 PrintSummary）；
//   3) 报告的父目录由本文件创建 —— Task 8 的 Reporter 刻意不建（它只负责写），而一个不存在的父目录
//      会让一次长时间的解压在最后一刻以 DirectoryNotFoundException 收场。
//
// 【进程级日志根覆盖】环境变量 RERAR_JOURNAL_ROOT 非空时，本次运行的崩溃恢复日志根改用它
//（修复轮 Finding 3 的控制方裁定）：CLI 是真进程，测试够不着进程内的 Journal.Root 接缝，有了它
// 就不必再去真实的 %LOCALAPPDATA%\Rerar 里删自己写下的文件。它同时也是可移植 / 调试运行的正规用法。
//
// 【Task 14：本文件多了三件事】
//   1) `[STAThread]` —— WinForms 要求主线程 STA；无头 CLI 走同一入口，STA 对它无害；
//   2) 无参数即起界面（Application.Run(new MainForm())）—— 界面本身全在 MainForm.cs，这里只管入口；
//   3) 带 `--cli` 时先尝试接管父控制台（AttachConsole）：winexe 从终端手敲时没有父控制台，
//      Console.WriteLine 会凭空消失（重定向不受影响，所以验收脚本一直是对的）。
//      判定只看标准输出**句柄的类型**：管道/文件（脚本）一个字节都不动，字符设备/无句柄才接管；
//      没有父控制台时行为与今天完全一致（输出丢弃、不抛异常、退出码不受影响，--json-out 照旧落盘）。
//
// 【凭据卫生】密码只经 RunOptions 交给 Extractor，绝不写进 stdout / stderr / JSON / 日志
//（Task 10 只记「候选序号 + 来源类别」）。需要如实说明的一点：`--password` 在命令行上对同机其它
// 进程是**可见**的（它们能读到自己进程的命令行），这是「无头驱动面」这个形状本身的代价，
// 不是本实现的选择 —— 详见 task-12-report.md。
//
// 【解压引擎由 EngineLocator 定位（Task 13）】RunOptions.SevenZipPath 是必填项，而「哪一份 7z.exe」
// 是规格 §6.2 的问题：本机安装的 7-Zip（≥ 25.00）优先，找不到或低于下限就用内嵌便携版兜底 ——
// 于是「无 7-Zip 的干净机器上双击即用」这条承诺成立。这里只做两件事：把结果如实印给用户
//（实际使用的版本与来源路径），以及把它交给流水线。定位失败时以退出码 2 收场并给出中文原因。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Rerar.Core;

namespace Rerar
{
    internal static class Program
    {
        // 退出码是契约（Task 15 的验收脚本按它判定），逐字固定，且**只有一份实现**：
        // Rerar.Core.RunExitCodes（修复轮 Finding 2 之后 CLI 与用例共用同一段代码）。
        private const int ExitSuccess = RunExitCodes.Success;            // 全部成功
        private const int ExitFailedOrSkipped = RunExitCodes.FailedOrSkipped;  // 有失败或跳过（含取消、含未处理项）
        private const int ExitFatal = RunExitCodes.Fatal;                // 致命错误：什么都没跑成 / 报告写不出来

        // 崩溃恢复日志根的**进程级覆盖**（修复轮 Finding 3 的控制方裁定）。名字与 TestEnv 里的一致。
        private const string JournalRootVariable = "RERAR_JOURNAL_ROOT";

        private const string Usage =
            "用法：Rerar.exe --cli --target <路径> [--target <路径>...] [--delete] [--password <密码>] " +
            "[--dict <字典文件>] [--depth <层数>] [--json-out <报告文件>]";

        // 【STAThread 是必须的（Task 14 的控制方要求 1）】WinForms 要求主线程是 STA：不是的话
        // OLE 拖放 / 通用文件对话框（COM）会以各种奇怪的方式不工作（拖放注册失败、对话框不响应）。
        // 无头 CLI 走同一个入口，STA 对它完全无害（它不碰 COM）。
        [STAThread]
        private static int Main(string[] args)
        {
            if (args == null) { args = new string[0]; }

            // 【控制方要求 2：交互式 CLI 的输出必须看得见】/target:winexe 的进程 PE 子系统是 GUI，
            // 从终端手敲 `Rerar.exe --cli …` 时它没有连着父控制台，Console.WriteLine 的输出就没地方去
            //（重定向时不受影响 —— 那正是验收脚本一直能跑通的原因）。所以带 --cli 时先尝试接管父控制台。
            //
            // 关键约束：**绝不能碰重定向**。判定用的是「标准输出句柄的类型」而不是猜：
            //   * 管道 / 文件（所有脚本、所有验收测试）⇒ 已经是有效句柄，**一个字节都不改**；
            //   * 字符设备 / 没有句柄（终端手敲的典型形状）⇒ 尝试 AttachConsole；
            //   * 本来就连着控制台 ⇒ 什么都不做。
            // 接管成功之后**不**再把标准流接管成 UTF-8（见 ConfigureStdio）：写 UTF-8 字节到一个
            // CP936 的控制台上，用户看到的中文就是乱码 —— 那种情形要用控制台自己的代码页。
            bool consoleAttached = false;
            bool wantCli = Array.IndexOf(args, "--cli") >= 0;
            if (wantCli) { consoleAttached = TryAttachParentConsole(); }

            // 接管标准输出（重定向 / 无头模式的输出必须是确定性 UTF-8，理由见 UseUtf8Stdio）。
            ConfigureStdio(consoleAttached);

            // 构建骨架的契约（tests\smoke.ps1）：打印 version=<n> 并以 0 退出。
            // 它是「这个 exe 能不能跑」的自检，带不带 --cli 都成立，也不该被别的开关影响。
            if (Array.IndexOf(args, "--selftest") >= 0)
            {
                Console.WriteLine("version=" + Assembly.GetExecutingAssembly().GetName().Version);
                return ExitSuccess;
            }

            if (!wantCli)
            {
                // 界面模式：Task 14 的 MainForm 就从这里起（无参数双击即是它）。
                return RunGui();
            }

            return RunCli(args);
        }

        // ------------------------------------------------------------------
        // 界面入口（Task 14）
        // ------------------------------------------------------------------

        // 起界面。构造函数里的任何意外都在这里收口：**绝不让一个异常静默吞掉整个进程**
        //（双击没反应是最难排查的一类故障），而是弹一条中文对话框并以退出码 2 结束。
        private static int RunGui()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                return ExitSuccess;
            }
            catch (Exception ex)
            {
                string detail = "界面无法启动（" + ex.GetType().Name + "：" + ex.Message + "）：" +
                    "可以改用命令行模式 Rerar.exe --cli --target <路径>";
                try { MessageBox.Show(detail, "Rerar", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                catch (Exception) { }
                Console.Error.WriteLine("错误：" + detail);
                return ExitFatal;
            }
        }

        // ------------------------------------------------------------------
        // 父控制台接管（控制方要求 2）
        // ------------------------------------------------------------------

        private const uint AttachParentProcess = 0xFFFFFFFF;
        private const int StdOutputHandle = -11;
        private const uint FileTypeUnknown = 0x0000;
        private const uint FileTypeChar = 0x0002;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetFileType(IntPtr hFile);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        // 尝试接管**父**进程的控制台。返回 true = 现在真的连着一个控制台。
        //
        // 【没有任何父控制台时怎么办（控制方要求回答的问题）】AttachConsole 返回 false
        //（实测错误码 ERROR_INVALID_HANDLE 6 / ERROR_ACCESS_DENIED 5），于是：
        //   * 进程行为与今天**完全一致**：Console 拿不到有效句柄，写入被静默丢弃（Stream.Null），
        //     既不抛异常也不写乱码；
        //   * 退出码不受影响 —— 「输出看不见」绝不能被读成「这次运行失败了」；
        //   * 机器读的那条路仍然完整：--json-out 照旧落盘（它写文件，与控制台无关），
        //     退出码照旧是 0/1/2。这也是 Task 12 把「无头驱动面」设计成文件 + 退出码的原因。
        private static bool TryAttachParentConsole()
        {
            try
            {
                // 已经连着控制台（例如从某些启动器起）：不做任何事，免得把标准句柄搞乱。
                if (GetConsoleWindow() != IntPtr.Zero) { return true; }

                IntPtr handle = GetStdHandle(StdOutputHandle);
                uint type = FileTypeUnknown;
                if (handle != IntPtr.Zero && handle != new IntPtr(-1)) { type = GetFileType(handle); }

                // 管道(3) / 磁盘文件(1)：重定向。**绝不接管** —— 那会把脚本的管道输出变成控制台输出，
                // 于是 TestEnv.RunCli / tests\smoke.ps1 / tests\acceptance.ps1 全都会读不到东西。
                if (type != FileTypeUnknown && type != FileTypeChar) { return false; }

                if (!AttachConsole(AttachParentProcess)) { return false; }

                // 接管成功后先空一行：控制台里光标通常停在 shell 的提示符后面，直接输出会和提示符粘在
                // 一起（cmd 不等 GUI 进程，提示符已经打出来了）。脚本路径不会走到这里。
                Console.WriteLine();
                return true;
            }
            catch (Exception)
            {
                // 接管失败绝不是致命错误：输出看不见 ≠ 这次运行没跑成。
                return false;
            }
        }

        // ------------------------------------------------------------------
        // CLI
        // ------------------------------------------------------------------

        private static int RunCli(string[] args)
        {
            // 先把日志根定下来并**如实报出**（环境变量覆盖 + 默认根）。放在解析参数之前：连
            // 用法级致命错误（什么都没跑）也应当说明「这次运行根本不写恢复记录到哪里」。
            ApplyJournalRoot();

            List<string> targets = new List<string>();
            // 显式给了 `--target ""` 的项：Extractor.FreezeTargets 会把空串**静默跳过**（它没有路径
            // 可言）—— 那正是规格 §6.3 禁止的「静默丢弃」。所以 CLI 在入口就把它变成一条
            // SkippedUnreadable 结果（见 EmptyTargetResult），其余目标照常处理。
            List<ArchiveResult> emptyTargets = new List<ArchiveResult>();
            bool sawTargetFlag = false;
            bool deleteOriginals = false;
            string password = null;
            string dictPath = null;
            string jsonOut = null;
            int depth = new RunOptions().MaxDepth;      // 规格 §10.1 的默认层数（10）

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string value;
                string problem;

                switch (arg)
                {
                    case "--cli":
                        break;

                    case "--delete":
                        deleteOriginals = true;
                        break;

                    case "--target":
                        sawTargetFlag = true;
                        if (!TryTakeValue(args, ref i, arg, out value, out problem)) { return Fatal(problem); }
                        if (value.Length == 0) { emptyTargets.Add(EmptyTargetResult()); }
                        else { targets.Add(value); }
                        break;

                    case "--password":
                        if (!TryTakeValue(args, ref i, arg, out value, out problem)) { return Fatal(problem); }
                        password = value;
                        break;

                    case "--dict":
                        if (!TryTakeValue(args, ref i, arg, out value, out problem)) { return Fatal(problem); }
                        dictPath = value;
                        break;

                    case "--json-out":
                        if (!TryTakeValue(args, ref i, arg, out value, out problem)) { return Fatal(problem); }
                        jsonOut = value;
                        break;

                    case "--depth":
                        if (!TryTakeValue(args, ref i, arg, out value, out problem)) { return Fatal(problem); }
                        int parsed;
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ||
                            parsed < 1)
                        {
                            return Fatal("--depth 需要一个不小于 1 的整数，实际收到「" + value + "」。" + Usage);
                        }
                        depth = parsed;
                        break;

                    default:
                        // 拼错的开关绝不能静默忽略：`--deleet` 被无视就等于「删除开关没生效」却报成功。
                        return Fatal("无法识别的参数「" + arg + "」。" + Usage);
                }
            }

            if (!sawTargetFlag)
            {
                return Fatal("未指定任何 --target：无头模式至少需要一个目标路径。" + Usage);
            }

            // 字典是全局输入：读不出来就什么都不跑（而不是拿一份「缺了几行」的阶梯去猜密码）。
            List<string> dictLines = null;
            if (dictPath != null)
            {
                try
                {
                    dictLines = new List<string>(File.ReadAllLines(dictPath));
                }
                catch (Exception ex)
                {
                    return Fatal("无法读取密码字典文件「" + dictPath + "」（" + ex.GetType().Name + "：" +
                        ex.Message + "）：本次不执行任何解压");
                }
            }

            // 控制方裁定 2：报告的父目录由 CLI 创建，且必须在**任何解压之前**就确定能写 ——
            // 否则一次长时间的解压会在最后一刻倒在「目录不存在」上（Task 8 的 Reporter 只管写）。
            string reportPath = null;
            if (jsonOut != null)
            {
                string problem;
                if (!TryPrepareReportPath(jsonOut, out reportPath, out problem)) { return Fatal(problem); }
            }

            // 引擎定位（规格 §6.2）：本机 7-Zip ≥ 25.00 优先，否则释放内嵌便携版兜底。
            // 失败原因里带着「找过哪些位置、各自为什么不行」，原样交给用户（可操作）。
            EngineInfo engine;
            try
            {
                engine = EngineLocator.Resolve();
            }
            catch (Exception ex)
            {
                // EngineLocator 的判词本身就是可操作的中文；异常类型照旧带上，便于区分
                //「引擎确实不可用」与「定位过程出了意外」。
                return Fatal("解压引擎不可用（" + ex.GetType().Name + "）：" + ex.Message);
            }

            // 如实报出**实际使用**的版本与来源路径（规格 §6.2：要能显示给用户）。
            // 内嵌副本的落点也一并说明：用户/IT 需要知道这颗 exe 往 %LOCALAPPDATA% 下写了什么。
            Console.WriteLine("解压引擎：7-Zip " + EngineLocator.FormatVersion(engine.Version) +
                (engine.IsEmbedded ? "（内置便携版，已释放到本机）" : "（本机安装）") +
                "：" + engine.Path);

            RunOptions options = new RunOptions();
            options.SevenZipPath = engine.Path;
            options.Password = password;
            options.DictLines = dictLines;
            options.MaxDepth = depth;
            // 不变式 I3：删除默认**关**，只有显式传了 --delete 才是 true（OutputRoot 留空 = 原地输出，
            // 规格 §6.11 的默认布局）。
            options.DeleteOriginals = deleteOriginals;

            RunSummary summary;
            try
            {
                summary = new Extractor(options, new DriveSpaceProvider(), null).Run(targets);
            }
            catch (Exception ex)
            {
                // Extractor 自己会把单个归档的异常收成 FAIL；能跑到这里的是运行级的意外。
                return Fatal("运行解压流水线时发生致命错误（" + ex.GetType().Name + "：" + ex.Message + "）");
            }

            // 空目标的结局补进结果集：它们不是「试过但失败」，而是逐项如实报告「无法读取」——
            // 于是 --json-out 与汇总里都能看到它们（规格 §6.3 的「永不静默丢弃」）。
            if (emptyTargets.Count > 0) { summary.Results.InsertRange(0, emptyTargets); }

            // 致命中止之后**没轮到处理**的候选也补进结果集（修复轮 Finding 1 的控制方裁定）：
            // 机器读方只读 --json-out 时，否则会完全看不到这部分。
            AddUnprocessedRemainder(summary);

            // 【本轮】退出码在打印汇总**之前**算出来：汇总里那条「退出码非 0 但已处置原包」的警告
            // 用的就是它。映射仍然只有一份实现（Rerar.Core.RunExitCodes），下面的返回值也用它 ——
            // 打印出去的那条警告与进程真正返回的退出码因此不可能不一致。
            int exitCode = RunExitCodes.For(summary);

            PrintSummary(summary, exitCode);

            if (reportPath != null)
            {
                try
                {
                    // 报告是 UTF-8 **带 BOM**：规格 §2 第 6 条要求报告一律 BOM，而 Task 15 的
                    // PowerShell 5.1 脚本正是按「有 BOM 才当 UTF-8」来读中文的（不带 BOM 会按系统
                    // 代码页读成乱码）。正文是紧凑**单行**：字符串里的控制字符全部转义，绝不换行。
                    File.WriteAllText(reportPath, RenderJsonReport(summary.Results), new UTF8Encoding(true));
                    Console.WriteLine("JSON 报告已写入：" + reportPath);
                }
                catch (Exception ex)
                {
                    // 报告是这次调用的契约之一：写不出来就不能报成功（退出码 2）。
                    Console.Error.WriteLine("错误：无法写入 JSON 报告「" + reportPath + "」（" +
                        ex.GetType().Name + "：" + ex.Message + "）。解压结果已在上面的汇总里列出，但" +
                        "机器可读报告缺失。");
                    return ExitFatal;
                }
            }

            return exitCode;
        }

        // ------------------------------------------------------------------
        // 崩溃恢复日志根（进程级覆盖；修复轮 Finding 3）
        // ------------------------------------------------------------------

        // 应用环境变量覆盖（**唯一实现**，CLI 与界面共用）。
        //
        // 【为什么界面也要认它（Task 14 补）】这个覆盖的定义是「**进程级**」（Task 12 修复轮的裁定），
        // 而界面模式与 CLI 是同一个进程 —— 界面不认它，这条定义就不成立。补上之后有两件实际好处：
        //   * 可移植 / 调试运行：把恢复日志指到一个指定目录，两种模式行为一致；
        //   * 测试与人工验证不必往真实的 %LOCALAPPDATA%\Rerar\journal 里写东西（界面模式下
        //     Journal 由 Core 的 Extractor 打开，落点是生产默认根）。
        // 返回 true = 确实覆盖了（调用方据此决定要不要把有效根印出来）。
        internal static bool TryApplyJournalRootOverride()
        {
            try
            {
                string overridden = Environment.GetEnvironmentVariable(JournalRootVariable);
                // 空串按「没有设置」处理：绝不让一个空值把日志根变成当前目录。
                if (!string.IsNullOrEmpty(overridden))
                {
                    Journal.Root = overridden;
                    return true;
                }
            }
            catch (Exception)
            {
                // 读环境变量失败（极罕见）：沿用生产默认根，绝不因此中断。
            }
            return false;
        }

        // 如实报出有效根（人读面）。
        //
        // 为什么把有效的根印出来（而不是只写进日志）：用例要证明「没设变量时仍然用生产默认根」，
        // 而**那个分支不能真的去写真实日志根**（真实根在用户的 %LOCALAPPDATA% 下）—— 于是那条
        // 断言只能读运行头报出来的根。它之所以可信，由「设了变量的那一半」证明：日志文件确实
        // 落在报出来的那个目录里（用例 Cli.JournalRootOverrideKeepsRealAppDataUntouched 两侧都钉）。
        private static void ApplyJournalRoot()
        {
            try
            {
                TryApplyJournalRootOverride();

                Console.WriteLine("崩溃恢复日志根：" + Journal.Root +
                    "（可用环境变量 " + JournalRootVariable + " 覆盖）");
            }
            catch (Exception ex)
            {
                // 日志根算不出来（例如 %LOCALAPPDATA% 不可用）：绝不致命 —— Extractor 会把
                //「日志不可用」如实汇报成 JournalWriteFailures，解压本身照常跑。
                Console.WriteLine("崩溃恢复日志根不可用（" + ex.GetType().Name + "：" + ex.Message +
                    "）：本次运行没有恢复记录，解压照常进行");
            }
        }

        // `--target ""` 的逐项结局：空参数无法定位任何文件，所以它和「不存在的路径」同类 ——
        // 报 SkippedUnreadable 并给出具体中文原因，**不是**致命错误（其余目标必须照常处理）。
        private static ArchiveResult EmptyTargetResult()
        {
            ArchiveResult result = new ArchiveResult();
            result.Path = "";
            result.Status = ArchiveStatus.SkippedUnreadable;
            result.Layers = 0;
            result.Files = 0;
            result.Failed = 0;
            result.OutputDir = "";
            result.Message = "目标路径为空：已跳过（空参数无法定位任何文件），其余目标照常处理";
            return result;
        }

        // `--flag value` 取值。缺值一律致命（绝不把下一个开关当成它的值）。
        private static bool TryTakeValue(string[] args, ref int index, string flag, out string value, out string problem)
        {
            value = null;
            problem = null;

            if (index + 1 >= args.Length)
            {
                problem = flag + " 后面缺少值。" + Usage;
                return false;
            }

            index++;
            value = args[index] == null ? "" : args[index];
            return true;
        }

        // 报告路径的预检 + 父目录创建（控制方裁定 2）。失败时给出**具体**的中文原因，
        // 且调用点在任何解压之前 —— 一个字节都不会写出去。
        private static bool TryPrepareReportPath(string raw, out string full, out string problem)
        {
            full = null;
            problem = null;

            if (string.IsNullOrEmpty(raw))
            {
                problem = "--json-out 的值不能为空。" + Usage;
                return false;
            }

            try
            {
                full = Path.GetFullPath(raw);
            }
            catch (Exception ex)
            {
                problem = "报告路径无效（" + ex.GetType().Name + "：" + ex.Message + "）：" + raw;
                return false;
            }

            if (Directory.Exists(full))
            {
                problem = "报告路径是一个目录，无法写入 JSON 报告：" + full;
                return false;
            }

            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch (Exception ex)
                {
                    problem = "无法创建 JSON 报告的父目录「" + dir + "」（" + ex.GetType().Name + "：" +
                        ex.Message + "）：本次不执行任何解压";
                    return false;
                }
            }
            return true;
        }

        // ------------------------------------------------------------------
        // 未处理剩余项 → 结果集（修复轮 Finding 1）
        // ------------------------------------------------------------------

        // 致命中止 / 取消之后**没轮到处理**的候选（RunSummary.NotAttempted 里没有 Results 条目的
        // 那一部分）合成为结果对象，于是 --json-out 里**每个归档恰好一条**，只读 JSON 的机器读方
        // 也看得见「这一批还有哪些归档根本没被处理过」。界面的逐项列表**复用同一份合成**
        //（MainForm 直接调它）—— 两处各写一遍的话，「不重复计数」这条规则就有了两个实现。
        //
        // 为什么不谎报原因：合成的状态按事实分两种（Task 12 的 NotAttemptedFatal，与 Task 14 新增的
        // NotAttemptedCancelled），判词里写明真正的原因。绝不复用 NotAttemptedDepthLimit ——
        // 那会把「盘满/取消」谎报成「深度触顶」；也绝不用「整批中止」去说一次**取消**
        //（取消在退出码裁定里是非致命的，同一件事不能有两种说法）。
        //
        // 绝不重复发：深度触顶的那一项在 Results 里**已经**有 NotAttemptedDepthLimit 条目，
        // 这里按路径匹配跳过它（两个集合各自都含它，但对象只有一个）。
        internal static void AddUnprocessedRemainder(RunSummary summary)
        {
            if (summary == null || summary.NotAttempted == null) { return; }

            // 致命原因非空 ⇒ 事实就是「整批因致命错误中止」；只有**取消**（且没有致命原因）才是
            // 那个非致命的剩余项。两者同时成立时如实报致命 —— 那才是真正的肇因。
            bool cancelled = summary.Cancelled && string.IsNullOrEmpty(summary.FatalReason);
            ArchiveStatus status = cancelled
                ? ArchiveStatus.NotAttemptedCancelled
                : ArchiveStatus.NotAttemptedFatal;

            string reason = !string.IsNullOrEmpty(summary.FatalReason)
                ? "整批因致命错误中止：" + summary.FatalReason
                : (cancelled ? "本次运行已被取消" : "整批已中止（原因未记录）");

            foreach (string path in summary.NotAttempted)
            {
                if (string.IsNullOrEmpty(path)) { continue; }
                if (HasResultFor(summary.Results, path)) { continue; }

                ArchiveResult synthesized = new ArchiveResult();
                synthesized.Path = path;
                synthesized.Status = status;
                synthesized.Layers = 0;          // 没开始过：计数器一律 0，绝不编造层数 / 文件数
                synthesized.Files = 0;
                synthesized.Failed = 0;
                synthesized.OutputDir = "";      // 没有产物目录（一个字节都没写）
                synthesized.Message = "未处理（" + reason + "）：此项尚未开始，原包保留";
                summary.Results.Add(synthesized);
                // 追加进 Results 之后，同一个路径若在 NotAttempted 里再次出现，上面那句匹配就会拦住它 ——
                // 于是每个归档**恰好**一条对象，重复的未处理路径绝不会变成两条。
            }
        }

        // 结果集里有没有就是这个路径的结局。两个集合里的路径都来自**同一个** ArchiveTask.Path，
        // 所以逐字比较就够（大小写不敏感只是对 Windows 路径的稳妥处理）。
        private static bool HasResultFor(List<ArchiveResult> results, string path)
        {
            if (results == null) { return false; }

            foreach (ArchiveResult result in results)
            {
                if (result == null || result.Path == null) { continue; }
                if (string.Equals(result.Path, path, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        // ------------------------------------------------------------------
        // 汇总（人读）+ JSON 报告（机器读）
        // ------------------------------------------------------------------

        private static void PrintSummary(RunSummary summary, int exitCode)
        {
            Console.WriteLine(Reporter.RenderTable(summary.Results));
            Console.WriteLine("----");

            int total = 0, completed = 0, partial = 0, skipped = 0, failed = 0, depthLimit = 0, fatalUnprocessed = 0;
            int cancelledUnprocessed = 0;
            if (summary.Results != null)
            {
                total = summary.Results.Count;
                foreach (ArchiveResult result in summary.Results)
                {
                    if (result == null) { continue; }
                    switch (result.Status)
                    {
                        case ArchiveStatus.Completed: completed++; break;
                        case ArchiveStatus.CompletedWithFailures: partial++; break;
                        case ArchiveStatus.SkippedNeedsPassword:
                        case ArchiveStatus.SkippedContainer:
                        case ArchiveStatus.SkippedUnreadable: skipped++; break;
                        case ArchiveStatus.Failed: failed++; break;
                        case ArchiveStatus.NotAttemptedDepthLimit: depthLimit++; break;
                        case ArchiveStatus.NotAttemptedFatal: fatalUnprocessed++; break;
                        // Task 14：取消后的剩余项单列一档（取消是非致命的，绝不并进「整批中止」）。这一行
                        // 与 Reporter.WriteSummary 的计数行**逐字同形**（ReporterTests 钉住这一点）。
                        case ArchiveStatus.NotAttemptedCancelled: cancelledUnprocessed++; break;
                    }
                }
            }

            Console.WriteLine("归档总数：" + Int(total) +
                "（完成 " + Int(completed) +
                "，部分失败 " + Int(partial) +
                "，跳过 " + Int(skipped) +
                "，失败 " + Int(failed) +
                "，未处理（深度上限）" + Int(depthLimit) +
                "，未处理（整批中止）" + Int(fatalUnprocessed) +
                "，未处理（已取消）" + Int(cancelledUnprocessed) + "）");

            // 【控制方裁定 1】未处理数量 = RunSummary.NotAttempted.Count —— **唯一权威**的「没处理」清单，
            // 与原因无关（深度触顶、致命中止后没轮到的候选都在里面）。绝不与 Results 相加：
            // 深度触顶那一项在 Results 里**同时**有一条 Status = NotAttemptedDepthLimit 的条目，
            // 相加会把同一件事数两遍。来源写在输出里，读者不必猜这个数字是从哪来的。
            int unprocessed = summary.NotAttempted == null ? 0 : summary.NotAttempted.Count;
            Console.WriteLine("未处理数量：" + Int(unprocessed) +
                "（取自 RunSummary.NotAttempted（权威清单，与原因无关）；深度触顶项同时出现在结果表中，不重复计数）");

            if (summary.NotAttempted != null)
            {
                // 逐项列出：规格 §6.3 的「永不静默丢弃」在这里落到人读的输出上。
                foreach (string path in summary.NotAttempted) { Console.WriteLine("  未处理：" + path); }
            }

            if (!string.IsNullOrEmpty(summary.FatalReason))
            {
                Console.WriteLine("致命错误（整批中止）：" + summary.FatalReason);
            }
            if (summary.Cancelled)
            {
                Console.WriteLine("本次运行已被取消：未完成的项已如实列出，原包一律保留");
            }
            if (summary.JournalWriteFailures > 0)
            {
                // Task 11 的裁定：日志写失败**必须可见**（静默缺席等于让用户以为自己有恢复记录）。
                Console.WriteLine("警告：崩溃恢复日志有 " + Int(summary.JournalWriteFailures) +
                    " 次写入失败：" + (summary.JournalProblem == null ? "" : summary.JournalProblem));
            }

            // 【本轮】原包处置的诚实面（两条，顺序固定）。
            //
            // ① 本次运行**到底处置了哪些原包**：逐条点名 + 中文处置方式。没有处置时也明说
            //   「没有处置任何原包」—— 一行都不打印的话，「真没处置」与「这条信息没实现」在用户
            //   眼里长得一样，而这恰恰是本轮要修的那个静默。
            Console.WriteLine(Reporter.DescribeDisposedOriginals(summary.Results));

            // ② 退出码非 0 但处置过原包 ⇒ 明确警告。关掉的正是「退出码 != 0 ⇒ 什么都没被销毁」
            //   这个假推理（顶层包可能已经 Completed 并被回收，而某个嵌套成员没完成）。
            string disposedWarning = Reporter.DisposedOriginalsWarning(summary.Results, exitCode);
            if (disposedWarning.Length > 0) { Console.WriteLine(disposedWarning); }
        }

        // 机器可读的结果数组。键名与顺序是跨任务契约（与 Task 8 的 CSV 列名逐字一致）。
        // 手写而不是序列化：本项目不引第三方库，BCL 也没有 JSON 序列化器。
        private static string RenderJsonReport(List<ArchiveResult> results)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('[');

            bool first = true;
            if (results != null)
            {
                foreach (ArchiveResult result in results)
                {
                    if (result == null) { continue; }     // 与 Reporter 同约定：null 元素不凭空多出一行
                    if (!first) { sb.Append(','); }
                    first = false;

                    sb.Append("{\"path\":").Append(JsonString(result.Path))
                      .Append(",\"status\":").Append(JsonString(result.Status.ToString()))
                      .Append(",\"layers\":").Append(Int(result.Layers))
                      .Append(",\"files\":").Append(Int(result.Files))
                      .Append(",\"failed\":").Append(Int(result.Failed))
                      .Append(",\"outputDir\":").Append(JsonString(result.OutputDir))
                      .Append(",\"message\":").Append(JsonString(result.Message))
                      // 【本轮新增（加法）】原包去向："kept" / "deleted"（已移入回收站）/ "quarantined"
                      // （已移入隔离文件夹）。**加在末尾**：既有 7 个键的名字与顺序一字未动，按键名
                      // 读取的脚本不受影响（Cli.JsonKeysMatchTheCrossTaskContract 钉住那 7 个键）。
                      // 它让只读 JSON 的脚本也能看出「这次运行处置过哪些原包」——退出码非 0 与
                      // 原包已被处置可以同时成立（见 Reporter.DisposedOriginalsWarning）。
                      .Append(",\"originalDisposition\":").Append(JsonString(result.OriginalDisposition))
                      .Append('}');
                }
            }

            sb.Append(']');
            return sb.ToString();
        }

        // JSON 字符串（含引号）。null 一律写成空串：ArchiveResult 的约定就是「没有则 ""」，
        // 机器读方不必为同一个「空」处理两种写法。
        private static string JsonString(string value)
        {
            if (value == null) { return "\"\""; }

            StringBuilder sb = new StringBuilder(value.Length + 8);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // 其余控制字符必须写成 \uXXXX：JSON 字符串里不允许出现裸控制字符
                        //（一个裸换行就会让整份文件非法，读方直接报错）。
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            // 中文等非 ASCII 字符原样输出：文件是 UTF-8，逐字节保留原名。
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // 报告是数据文件：数字一律不变文化格式化，绝不随系统区域设置变成本地数字。
        private static string Int(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------
        // 标准输出接管
        // ------------------------------------------------------------------

        // 无头模式（由脚本驱动）的 stdout/stderr 必须是**确定性 UTF-8**：
        //   * /target:winexe 的进程没有控制台，`Console.OutputEncoding = …` 在重定向时实测抛
        //     IOException（「句柄无效」），而 Console.Out 默认退回系统 ANSI 代码页
        //     —— 实测中文会变成 GBK 字节，按 UTF-8 读回的脚本直接得到乱码；
        //   * 所以这里直接接管两个标准流。句柄无效时 Console.OpenStandard* 返回 Stream.Null，
        //     相应的写入被静默丢弃 —— 不抛异常、也不写乱码（无参数启动的界面模式正是这种情形）。
        //
        // 【Task 14：连上真控制台时**不**接管】接管之后写出去的是 UTF-8 字节，而控制台按它自己的
        // 输出代码页（中文系统默认 936）解释这些字节 —— 用户看到的中文会是乱码。所以只有
        //「重定向 / 没有控制台」这条路径才接管，真控制台交给 Console 自己按控制台代码页去写。
        private static void ConfigureStdio(bool consoleAttached)
        {
            if (consoleAttached) { return; }

            try
            {
                UTF8Encoding utf8 = new UTF8Encoding(false);

                StreamWriter stdout = new StreamWriter(Console.OpenStandardOutput(), utf8);
                stdout.AutoFlush = true;
                Console.SetOut(stdout);

                StreamWriter stderr = new StreamWriter(Console.OpenStandardError(), utf8);
                stderr.AutoFlush = true;
                Console.SetError(stderr);
            }
            catch (Exception)
            {
                // 接管失败就沿用默认流：输出编码问题绝不能让一次解压失败。
            }
        }

        // 致命错误：给出中文原因到 stderr 并以 2 退出（这一档什么都没跑成）。
        private static int Fatal(string message)
        {
            Console.Error.WriteLine("错误：" + message);
            return ExitFatal;
        }
    }
}
