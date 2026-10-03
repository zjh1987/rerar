// Rerar 程序入口：GUI / CLI 双模式分发（Task 12 把 CLI 从骨架补成完整契约）。
//
// 【为什么 CLI 是「无头驱动面」】本程序是 /target:winexe 的进程，没有控制台、界面在无人值守的机器上
// 也跑不了；而 Task 15 的验收脚本与全部自动化都必须能驱动**同一条** Extractor 流水线。
// CLI 就是那条不含窗口的入口 —— 它不做任何自己的解压逻辑，只是参数 → RunOptions → Extractor。
//
// 【契约（Task 15 逐字依赖，不得改名）】
//     Rerar.exe --cli --target <path> [--target <path>...] [--delete] [--password <pw>]
//               [--dict <file>] [--depth <n>] [--json-out <file>]
//     退出码：0 全部成功；1 有失败或跳过；2 致命错误（什么都没跑成 / 报告写不出来）。
//     --json-out：机器可读的结果数组，对象的键逐字是
//                 path,status,layers,files,failed,outputDir,message（= ArchiveResult 的字段名小写）。
//
// 【本文件必须自己负责的三件事】
//   1) `--cli --selftest` 打印 version=<n> 并退出 0 —— tests\smoke.ps1 依赖的既有契约，绝不能改坏；
//   2) 「未处理数量」只取自 RunSummary.NotAttempted（Task 10 裁定的**权威清单**），绝不与 Results
//      相加：深度触顶那一项同时出现在两个集合里，相加就是同一件事数两遍（见 PrintSummary）；
//   3) 报告的父目录由本文件创建 —— Task 8 的 Reporter 刻意不建（它只负责写），而一个不存在的父目录
//      会让一次长时间的解压在最后一刻以 DirectoryNotFoundException 收场。
//
// 【凭据卫生】密码只经 RunOptions 交给 Extractor，绝不写进 stdout / stderr / JSON / 日志
//（Task 10 只记「候选序号 + 来源类别」）。需要如实说明的一点：`--password` 在命令行上对同机其它
// 进程是**可见**的（它们能读到自己进程的命令行），这是「无头驱动面」这个形状本身的代价，
// 不是本实现的选择 —— 详见 task-12-report.md。
//
// 【为什么这里有一个极简的 7-Zip 定位】RunOptions.SevenZipPath 是必填项，而 Task 13 的
// EngineLocator（注册表 + 版本下限 + 内嵌兜底）尚未落地，Task 12 又要先跑通完整流水线。
// 所以这里只做「常见安装位置 + PATH」的最小查找，并明确报错；Task 13 落地后应由 EngineLocator 取代。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Rerar.Core;

namespace Rerar
{
    internal static class Program
    {
        // 退出码是契约（Task 15 的验收脚本按它判定），逐字固定。
        private const int ExitSuccess = 0;            // 全部成功
        private const int ExitFailedOrSkipped = 1;    // 有失败或跳过（含未处理项）
        private const int ExitFatal = 2;              // 致命错误：什么都没跑成 / 报告写不出来

        private const string Usage =
            "用法：Rerar.exe --cli --target <路径> [--target <路径>...] [--delete] [--password <密码>] " +
            "[--dict <字典文件>] [--depth <层数>] [--json-out <报告文件>]";

        private static int Main(string[] args)
        {
            // 先接管标准输出：无头模式的输出必须是确定性 UTF-8（理由见 UseUtf8Stdio）。
            UseUtf8Stdio();

            if (args == null) { args = new string[0]; }

            // 构建骨架的契约（tests\smoke.ps1）：打印 version=<n> 并以 0 退出。
            // 它是「这个 exe 能不能跑」的自检，带不带 --cli 都成立，也不该被别的开关影响。
            if (Array.IndexOf(args, "--selftest") >= 0)
            {
                Console.WriteLine("version=" + Assembly.GetExecutingAssembly().GetName().Version);
                return ExitSuccess;
            }

            if (Array.IndexOf(args, "--cli") < 0)
            {
                // 界面模式：Task 14 的 MainForm 落地后这里接 Application.Run(new MainForm())。
                // 本任务不含界面代码，故保持「无参数即静默成功」的既有行为，也不引入 WinForms 依赖。
                return ExitSuccess;
            }

            return RunCli(args);
        }

        // ------------------------------------------------------------------
        // CLI
        // ------------------------------------------------------------------

        private static int RunCli(string[] args)
        {
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

            string sevenZip = LocateSevenZip();
            if (sevenZip == null)
            {
                return Fatal("未找到 7-Zip（7z.exe）：请安装 7-Zip 或把 7z.exe 所在目录加入 PATH。已查找：" +
                    string.Join("；", SevenZipCandidates().ToArray()));
            }

            RunOptions options = new RunOptions();
            options.SevenZipPath = sevenZip;
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

            PrintSummary(summary);

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

            return ExitCodeFor(summary);
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
        // 7-Zip 定位（Task 13 的 EngineLocator 落地前的**临时**最小实现，见文件头）
        // ------------------------------------------------------------------

        private static string LocateSevenZip()
        {
            foreach (string candidate in SevenZipCandidates())
            {
                try
                {
                    if (File.Exists(candidate)) { return candidate; }
                }
                catch (Exception)
                {
                    // 畸形候选路径（超长/非法字符）跳过就好，绝不因为一个候选而放弃整轮查找。
                }
            }
            return null;
        }

        private static List<string> SevenZipCandidates()
        {
            List<string> candidates = new List<string>();

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (programFiles.Length > 0) { candidates.Add(Path.Combine(programFiles, @"7-Zip\7z.exe")); }

            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (programFilesX86.Length > 0) { candidates.Add(Path.Combine(programFilesX86, @"7-Zip\7z.exe")); }

            // 32 位进程看不到 64 位的 Program Files，补一个环境变量候补。
            string programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
            if (!string.IsNullOrEmpty(programW6432)) { candidates.Add(Path.Combine(programW6432, @"7-Zip\7z.exe")); }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (localAppData.Length > 0) { candidates.Add(Path.Combine(localAppData, @"Programs\7-Zip\7z.exe")); }

            string path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (string rawDir in path.Split(';'))
                {
                    string dir = rawDir.Trim();
                    if (dir.Length == 0) { continue; }
                    candidates.Add(Path.Combine(dir, "7z.exe"));
                }
            }

            return candidates;
        }

        // ------------------------------------------------------------------
        // 退出码（契约：0 / 1 / 2）
        // ------------------------------------------------------------------

        private static int ExitCodeFor(RunSummary summary)
        {
            // 致命档：整批因盘满 / 取消而中止 ⇒ 什么都没跑完。
            if (!string.IsNullOrEmpty(summary.FatalReason)) { return ExitFatal; }
            if (summary.Cancelled) { return ExitFatal; }

            if (summary.Results != null)
            {
                foreach (ArchiveResult result in summary.Results)
                {
                    // 只有一个状态算「这项成功了」：Completed。部分失败、跳过、失败、深度触顶都算 1。
                    if (result == null || result.Status != ArchiveStatus.Completed) { return ExitFailedOrSkipped; }
                }
            }

            // 未处理项（权威清单）：没试过的项也绝不是「全部成功」——
            // 致命中止之后还没轮到处理的候选就是这种形状（它们**没有** Results 条目）。
            if (summary.NotAttempted != null && summary.NotAttempted.Count > 0) { return ExitFailedOrSkipped; }

            return ExitSuccess;
        }

        // ------------------------------------------------------------------
        // 汇总（人读）+ JSON 报告（机器读）
        // ------------------------------------------------------------------

        private static void PrintSummary(RunSummary summary)
        {
            Console.WriteLine(Reporter.RenderTable(summary.Results));
            Console.WriteLine("----");

            int total = 0, completed = 0, partial = 0, skipped = 0, failed = 0, depthLimit = 0;
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
                    }
                }
            }

            Console.WriteLine("归档总数：" + Int(total) +
                "（完成 " + Int(completed) +
                "，部分失败 " + Int(partial) +
                "，跳过 " + Int(skipped) +
                "，失败 " + Int(failed) +
                "，未处理（深度上限）" + Int(depthLimit) + "）");

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
        private static void UseUtf8Stdio()
        {
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
