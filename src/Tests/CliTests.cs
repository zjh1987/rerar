// Task 12：CLI 契约的系统测试（无头驱动面）。
//
// 【为什么这一组用例一律起真进程】tests.exe 只编译 src\Core\*.cs + src\Tests\*.cs ——
// src\App\Program.cs **不在**这个目标里。CLI 的唯一可观察面就是 dist\Rerar.exe 本身：
// 参数解析、退出码、stdout/stderr、以及 --json-out 写出的那个文件。进程内断言在结构上够不着它。
//
// 【契约（Task 15 的验收脚本逐字依赖，不得改名）】
//     Rerar.exe --cli --target <path> [--target <path>...] [--delete] [--password <pw>]
//               [--dict <file>] [--depth <n>] [--json-out <file>]
//     退出码：0 全部成功；1 有失败/跳过；2 致命错误。
//     --json-out：机器可读数组，键恰好是 path,status,layers,files,failed,outputDir,message。
//
// 【本组用例钉住的性质】
//   * 不可读目标逐项报 SkippedUnreadable（具体中文原因），**绝不崩溃、绝不静默丢弃**、
//     也绝不把整批拖成致命中止（规格 §6.3 的「永不静默丢弃」、Review Focus #5）；
//   * 加密包绝不挂起（回归 ②：原文 .bat 在加密包上永久挂起）；
//   * I3 经 CLI 的钉子：没给 --delete 就绝不删原包；
//   * 控制方裁定 1：未处理数量只取自 RunSummary.NotAttempted，绝不与 Results 相加
//     （深度触顶那一项同时出现在两个集合里，相加会印刷成 2）；
//   * 控制方裁定 2：报告父目录由 CLI 创建；建不出来必须在**任何解压之前**以退出码 2 收场；
//   * 密码绝不进 stdout/stderr、也绝不进 JSON（Task 10 只记「候选序号 + 来源类别」）。
//
// 【修复轮（Task 12 第一轮 fix）新增/强化的三条】
//   * Finding 1：致命中止后的未处理剩余项也必须出现在 --json-out 里（状态 NotAttemptedFatal），
//     且**每个归档恰好一个对象**（深度触顶项绝不被再合成一条）；
//   * Finding 2：取消 ⇒ 退出码 1（不是 2）。CLI 没有取消入口（RunOptions.Cancellation 从不设置），
//     进程级构造不出取消的运行，所以这条断言打在 CLI **与用例共用**的映射实现上（RunExitCodes）；
//   * Finding 3：CLI 认环境变量 RERAR_JOURNAL_ROOT —— 全组用例的恢复日志都落在 TestEnv.Tmp 之下，
//     测试**再也不碰**真实的 %LOCALAPPDATA%\Rerar。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Rerar.Core;

internal sealed class CliTests : TestBase
{
    public static void Run()
    {
        // ------------------------------------------------------------------
        // brief Step 1 的五条（名字逐字来自 task-12-brief.md）
        // ------------------------------------------------------------------

        H.Run("Cli.SelfTestExitsZero", delegate {
            CliResult r = TestEnv.RunCli("--selftest");

            // tests\smoke.ps1 依赖的既有契约：`--cli --selftest` 打印 version=<n> 并退出 0。
            AssertEq(r.ExitCode, 0);
            AssertTrue(r.StdOut.Trim().StartsWith("version=", StringComparison.Ordinal));
            AssertTrue(r.StdOut.Trim().Length > "version=".Length);
            AssertTrue(r.Arguments.IndexOf("--cli", StringComparison.Ordinal) >= 0);   // 无头模式要显式带 --cli
        });

        H.Run("Cli.ExtractsAndWritesJson", delegate {
            CliResult r = TestEnv.RunCli("--target", TestEnv.NestedZip, "--json-out", TestEnv.JsonOut);
            AssertEq(r.ExitCode, 0);

            string json = ReadJson(TestEnv.JsonOut);
            AssertHasStatus(json, "Completed");
            AssertJsonIsUtf8WithBomAndCompact(TestEnv.JsonOut, json);

            // 无头模式跑的是**完整**流程：产物真的落在盘上（默认原地，规格 §6.11）。
            AssertTrue(File.Exists(TestEnv.InPlaceOutOf(TestEnv.NestedZip, "inner", "hello.txt")));
            AssertTrue(File.Exists(TestEnv.NestedZip));            // I3：没给 --delete，原包必须还在
        });

        H.Run("Cli.NonExistentTargetReportedPerItem", delegate {   // Review Focus #5
            string missing = @"C:\nope\missing.zip";
            CliResult r = TestEnv.RunCli("--target", missing, "--json-out", TestEnv.JsonOut);

            AssertEq(r.ExitCode, 1);                               // 有跳过 ⇒ 1（既不是 0 也不是致命 2）
            string json = ReadJson(TestEnv.JsonOut);
            AssertHasStatus(json, "SkippedUnreadable");
            AssertTrue(json.IndexOf(JsonEscaped(missing), StringComparison.Ordinal) >= 0);   // 逐项如实报告
            AssertTrue(json.IndexOf("不存在", StringComparison.Ordinal) >= 0);                // 具体的中文原因
            AssertJsonIsUtf8WithBomAndCompact(TestEnv.JsonOut, json);
        });

        H.Run("Cli.PathWithTrailingSpaceHandled", delegate {                       // Review Focus #5
            CliResult r = TestEnv.RunCli("--target", TestEnv.PathWithTrailingSpace, "--json-out", TestEnv.JsonOut);
            AssertTrue(r.ExitCode == 0 || r.ExitCode == 1);

            // 两种结局都合法（Win32 的正常路径形式会把尾空格规整掉，所以它可能被正常解出、也可能被判
            // 不可读）；唯一不允许的是崩溃，或**静默丢弃** —— 所以这里要求 JSON 里确实有这一项。
            string json = ReadJson(TestEnv.JsonOut);
            AssertTrue(json.IndexOf("nested", StringComparison.Ordinal) >= 0);
            AssertTrue(json.IndexOf("\"status\":\"", StringComparison.Ordinal) >= 0);
        });

        H.Run("Cli.NeverHangsOnEncrypted", delegate {                              // 回归 ②
            Stopwatch sw = Stopwatch.StartNew();
            // 自带 60 秒上限：真的挂住时用例在 60 秒内 FAIL（被杀掉），而不是把整套测试挂住。
            CliResult r = TestEnv.RunCliWithTimeout(60000, "--target", TestEnv.AesZip, "--json-out", TestEnv.JsonOut);
            sw.Stop();

            AssertTrue(sw.Elapsed.TotalSeconds < 60);
            AssertEq(r.ExitCode, 1);                                              // 需要密码 ⇒ 跳过 ⇒ 1
            AssertHasStatus(ReadJson(TestEnv.JsonOut), "SkippedNeedsPassword");
            AssertTrue(File.Exists(TestEnv.AesZip));                              // 绝不删原包
        });

        // ------------------------------------------------------------------
        // 参数契约与两条控制方裁定
        // ------------------------------------------------------------------

        H.Run("Cli.MissingTargetExitsTwoWithChineseMessage", delegate {
            CliResult r = TestEnv.RunCli("--json-out", TestEnv.JsonOut);

            AssertEq(r.ExitCode, 2);                                              // 致命：什么都没跑
            AssertTrue(r.Output.IndexOf("--target", StringComparison.Ordinal) >= 0);
            AssertTrue(r.Output.IndexOf("未指定", StringComparison.Ordinal) >= 0);  // 清楚地说明缺了什么
            AssertFalse(File.Exists(TestEnv.JsonOut));                            // 没跑就不产出报告
        });

        H.Run("Cli.JsonOutParentDirectoryIsCreated", delegate {                   // 控制方裁定 2
            string report = Path.Combine(TestEnv.Tmp, "尚不存在", "子目录", "报告.json");
            CliResult r = TestEnv.RunCli("--target", TestEnv.NestedZip, "--json-out", report);

            // Task 8 的 Reporter 刻意不创建父目录；创建它是 CLI 的责任。
            AssertEq(r.ExitCode, 0);
            AssertTrue(File.Exists(report));
            AssertHasStatus(ReadJson(report), "Completed");
        });

        H.Run("Cli.FatalWhenReportDirectoryCannotBeCreated", delegate {            // 控制方裁定 2 的另一半
            string blocker = TestEnv.MakeFile("blocker.txt", "用文件挡住报告的父目录");
            string report = Path.Combine(blocker, "sub", "report.json");

            CliResult r = TestEnv.RunCli("--target", TestEnv.NestedZip, "--json-out", report);

            AssertEq(r.ExitCode, 2);                                              // 致命：报告建不出来就不该开跑
            AssertTrue(r.Output.IndexOf("目录", StringComparison.Ordinal) >= 0);
            // 「先失败再解压」：一个字节都不该写出去（否则用户会拿到一份没有报告的结果目录）。
            AssertFalse(Directory.Exists(TestEnv.InPlaceOutOf(TestEnv.NestedZip)));
            AssertTrue(File.Exists(TestEnv.NestedZip));
        });

        H.Run("Cli.DepthFlagListsUnprocessedWithoutDoubleCounting", delegate {      // 控制方裁定 1
            CliResult r = TestEnv.RunCli("--depth", "1", "--target", TestEnv.DeepNestedZip,
                "--json-out", TestEnv.JsonOut);
            AssertEq(r.ExitCode, 1);

            string json = ReadJson(TestEnv.JsonOut);
            AssertHasStatus(json, "NotAttemptedDepthLimit");

            // 深度触顶那一项**同时**在 Results 与 NotAttempted 里：只数 NotAttempted 才是 1，
            // 两个集合相加会印成 2。计数来源也写在输出里，读者不必猜。
            AssertTrue(r.StdOut.IndexOf("未处理数量：1", StringComparison.Ordinal) >= 0);
            AssertTrue(r.StdOut.IndexOf("NotAttempted", StringComparison.Ordinal) >= 0);
            AssertTrue(r.StdOut.IndexOf("mid.zip", StringComparison.Ordinal) >= 0);   // 未处理项逐个列出
            AssertTrue(File.Exists(TestEnv.DeepNestedZip));                           // 绝不删原包

            // 【修复轮 Finding 1 的另一半】**每个归档恰好一个对象**：这一轮有两个归档
            //（outer 完成 + mid 深度触顶），所以 JSON 里就是两条；深度触顶项**绝不**被再合成一条
            // NotAttemptedFatal（它已经在 Results 里有自己的结局了）。
            AssertEq(ObjectCount(json), 2);
            AssertEq(StatusCount(json, "NotAttemptedDepthLimit"), 1);
            AssertEq(StatusCount(json, "NotAttemptedFatal"), 0);
        });

        // 【修复轮 Finding 1 的控制方裁定】致命中止之后**没轮到处理**的候选必须在 --json-out 里
        // 逐项可见：只读 JSON 的机器读方（Task 15 的验收脚本 / Task 14 的界面）否则完全看不到
        // 这部分 —— 而「未处理数量」正是 Task 10 裁定要读 RunSummary.NotAttempted 的那个数字。
        H.Run("Cli.FatalAbortReportsUnprocessedRemainderInJson", delegate {
            // 前置条件：一个**声明**出比本机可用空间还大总量的稀疏 tar（唯一能从外部真实构造出
            // 致命中止的东西 —— 空间预检失败）。造不出来（例如机器可用空间大到声明量会超过单包
            // 上限）就如实跳过，绝不假通过。
            string huge;
            try { huge = TestEnv.SparseHugeTar; }
            catch (Exception ex)
            {
                H.Skip("Cli.FatalAbortReportsUnprocessedRemainderInJson",
                    "无法构造致命中止的前置条件（声明量超过本机可用空间的稀疏 tar）：" + ex.Message);
                return;
            }

            CliResult r = TestEnv.RunCli("--target", huge, "--target", TestEnv.NestedZip,
                "--json-out", TestEnv.JsonOut);

            AssertEq(r.ExitCode, 2);                                   // 致命中止：什么都没跑成

            string json = ReadJson(TestEnv.JsonOut);
            AssertHasStatus(json, "Failed");                           // 触发致命中止的那一项有真实结局
            AssertHasStatus(json, "NotAttemptedFatal");                // 没轮到的那一项**逐项可见**
            AssertTrue(json.IndexOf(JsonEscaped(TestEnv.NestedZip), StringComparison.Ordinal) >= 0);
            AssertEq(StatusCount(json, "NotAttemptedFatal"), 1);
            AssertEq(ObjectCount(json), 2);                            // 每个归档恰好一个对象

            // 那一项确实**没有被处理**：原包原样、产物目录不存在。
            AssertTrue(File.Exists(TestEnv.NestedZip));
            AssertFalse(Directory.Exists(TestEnv.InPlaceOutOf(TestEnv.NestedZip)));

            // 人读面照旧（stdout 的清单没有被 JSON 取代）：权威计数 + 逐行列出未处理路径。
            AssertTrue(r.StdOut.IndexOf("未处理数量：1", StringComparison.Ordinal) >= 0);
            AssertTrue(r.StdOut.IndexOf("未处理：" + TestEnv.NestedZip, StringComparison.Ordinal) >= 0);

            // 【修复轮 #2 Finding 1】表格「结果」列这一格必须是**中文**：之前 Reporter.StatusText
            // 没有这个成员的 case，于是用户看到的 stdout 里是英文枚举名 NotAttemptedFatal ——
            // 违反「所有面向用户的文本为中文」。用带分隔符的整格断言（判词里也含「未处理（」，
            // 只搜子串会假通过），并反向断言英文枚举名没有以整格形式出现。
            AssertTrue(r.StdOut.IndexOf("| 未处理（整批中止） |", StringComparison.Ordinal) >= 0);
            AssertFalse(r.StdOut.IndexOf("| NotAttemptedFatal |", StringComparison.Ordinal) >= 0);
        });

        // 【修复轮 Finding 2 的控制方裁定】取消 ⇒ 退出码 1，不是 2：2 的定义是「什么都没跑成」，
        // 而一次被取消的运行**跑过**（可能已经解出一批 Completed）。
        //
        // 【为什么这条断言不经过 CLI 子进程】CLI 没有取消入口（RunOptions.Cancellation 从不设置，
        // 取消只可能来自界面），进程级根本构造不出 Cancelled 的运行。所以映射被放进
        // Rerar.Core.RunExitCodes —— CLI 与用例**共用同一份实现**，这里断言的就是 CLI 真正执行的那段代码。
        H.Run("Cli.CancelledExitCodeIsOneNotTwo", delegate {
            RunSummary cancelled = new RunSummary();
            cancelled.Cancelled = true;
            cancelled.Results.Add(CompletedResult(TestEnv.NestedZip));   // 取消之前已经解出来的那一批
            cancelled.NotAttempted.Add(@"C:\nope\remaining.zip");       // 没轮到的那一项
            AssertEq(RunExitCodes.For(cancelled), 1);

            // 取消绝不是成功：哪怕结果集为空也不能退化成 0。
            RunSummary cancelledNothing = new RunSummary();
            cancelledNothing.Cancelled = true;
            AssertEq(RunExitCodes.For(cancelledNothing), 1);

            // 反向：2 只留给「什么都没跑成」（致命原因），取消不再与它同档。
            RunSummary fatal = new RunSummary();
            fatal.FatalReason = "磁盘空间不足：目标卷可用空间不够（用例构造）";
            AssertEq(RunExitCodes.For(fatal), 2);

            // 顺带钉住这一档的其它两条：全完成 ⇒ 0；有未处理项 ⇒ 1。
            RunSummary clean = new RunSummary();
            clean.Results.Add(CompletedResult(TestEnv.NestedZip));
            AssertEq(RunExitCodes.For(clean), 0);

            RunSummary unprocessed = new RunSummary();
            unprocessed.Results.Add(CompletedResult(TestEnv.NestedZip));
            unprocessed.NotAttempted.Add(@"C:\nope\remaining.zip");
            AssertEq(RunExitCodes.For(unprocessed), 1);
        });

        // 【修复轮 Finding 3 的控制方裁定】CLI 认环境变量 RERAR_JOURNAL_ROOT：全组用例的恢复日志
        // 都落在 TestEnv.Tmp 之下，测试**再也不碰**真实的 %LOCALAPPDATA%\Rerar（那条「删掉自己
        // 写进用户应用数据目录的文件」的逻辑已从 TestEnv.RunCli 里删除）。
        H.Run("Cli.JournalRootOverrideKeepsRealAppDataUntouched", delegate {
            string realRoot;
            try { realRoot = Journal.DefaultRoot; }
            catch (Exception ex)
            {
                H.Skip("Cli.JournalRootOverrideKeepsRealAppDataUntouched",
                    "无法定位 %LOCALAPPDATA%（" + ex.GetType().Name + "）：无法证明真实日志根未被触碰");
                return;
            }

            bool existedBefore = Directory.Exists(realRoot);
            string[] before = LogNames(realRoot);

            // (1) 设了变量 ⇒ 日志真的落在覆盖根里（于是「运行头报出来的根 = 真正用的根」是可证的）。
            string overrideRoot = TestEnv.CliJournalRoot;
            CliResult r = TestEnv.RunCli("--target", TestEnv.NestedZip, "--json-out", TestEnv.JsonOut);
            AssertEq(r.ExitCode, 0);
            AssertTrue(r.StdOut.IndexOf(overrideRoot, StringComparison.OrdinalIgnoreCase) >= 0);
            AssertTrue(Directory.Exists(overrideRoot));
            AssertTrue(Directory.GetFiles(overrideRoot, "*.log").Length > 0);

            // (2) 真实的应用数据根一字未动（不新增文件、目录存在性也不变）。
            AssertEq(Directory.Exists(realRoot), existedBefore);
            AssertNoNewLogs(realRoot, before);

            // (3) 没设变量 ⇒ 仍然用**生产默认根**。这一条只能读运行头报出来的根：那个分支不许
            //     真的去写真实根，所以调用的必须是一次**用法级致命**（--depth 0 ⇒ 连 Extractor 都
            //     不构造，因而一个日志文件都不会产生）；TestEnv 用期望退出码把这个前提钉住。
            CliResult unset = TestEnv.RunCliWithoutJournalRootOverride(2, "--depth", "0",
                "--target", TestEnv.NestedZip);
            AssertEq(unset.ExitCode, 2);
            AssertTrue(unset.StdOut.IndexOf(TestEnv.JournalRootVariable, StringComparison.Ordinal) >= 0);
            AssertTrue(unset.StdOut.IndexOf(realRoot, StringComparison.OrdinalIgnoreCase) >= 0);

            // 用法级致命绝不开日志 ⇒ 真实根仍然一字未动。
            AssertEq(Directory.Exists(realRoot), existedBefore);
            AssertNoNewLogs(realRoot, before);
        });

        H.Run("Cli.JsonKeysMatchTheCrossTaskContract", delegate {
            CliResult r = TestEnv.RunCli("--target", TestEnv.NestedZip, "--json-out", TestEnv.JsonOut);
            AssertEq(r.ExitCode, 0);
            AssertJsonKeyOrder(ReadJson(TestEnv.JsonOut));
        });

        H.Run("Cli.JsonEscapesQuotesBackslashesAndChinese", delegate {
            string chinese = TestEnv.ChineseNamedZip;

            // 引号在 Windows 文件名里不可能存在 —— 所以只能拿一个**不存在的**目标来测转义
            // （路径原样进结果与判词：`C:\nope\quote"name.zip`）。
            string quoted = "C:\\nope\\quote\"name.zip";
            // 控制字符同理：换行只可能出现在参数里（判词会把路径原样带上），不可能出现在文件名里。
            string multiline = "C:\\nope\\line\nbreak.zip";

            CliResult r = TestEnv.RunCli("--target", chinese, "--target", quoted, "--target", multiline,
                "--json-out", TestEnv.JsonOut);
            AssertEq(r.ExitCode, 1);

            string json = ReadJson(TestEnv.JsonOut);
            AssertHasStatus(json, "Completed");                    // 中文名那一项真的解出来了
            AssertHasStatus(json, "SkippedUnreadable");            // 另外两项逐项报不可读
            AssertTrue(json.IndexOf(JsonEscaped(chinese), StringComparison.Ordinal) >= 0);
            AssertTrue(json.IndexOf(JsonEscaped(quoted), StringComparison.Ordinal) >= 0);
            AssertTrue(json.IndexOf(JsonEscaped(multiline), StringComparison.Ordinal) >= 0);

            // 反证：未被转义的裸形式绝不能出现（裸控制字符会让整份 JSON 非法）。
            AssertTrue(json.IndexOf("quote\"name.zip", StringComparison.Ordinal) < 0);
            AssertJsonIsUtf8WithBomAndCompact(TestEnv.JsonOut, json);
        });

        // ------------------------------------------------------------------
        // I3 与密码卫生经 CLI 的钉子
        // ------------------------------------------------------------------

        H.Run("Cli.DeleteStaysOffUnlessFlagPassed", delegate {
            CliResult r = TestEnv.RunCli("--target", TestEnv.NestedZip, "--json-out", TestEnv.JsonOut);
            AssertEq(r.ExitCode, 0);
            AssertHasStatus(ReadJson(TestEnv.JsonOut), "Completed");   // 唯一允许被删除的结局
            AssertTrue(File.Exists(TestEnv.NestedZip));                // 但没给 --delete ⇒ 原包必须还在
        });

        // 【本轮】「顶层包已完成并被回收」与「退出码非 0」可以同时成立 —— 这时
        //「退出码 != 0 ⇒ 什么都没被销毁」是**假的**。三件事必须同时可见：
        //   ① 人读的一行：处置了哪个原包、怎么处置的；
        //   ② --json-out 里每条结果的 originalDisposition（脚本可读，且顶层仍然是那个数组）；
        //   ③ 退出码非 0 **且**处置过原包 ⇒ 一条显式警告。
        //
        // 场景就是复核复现出来的形状：顶层包 Completed（I3 据此把它的原包处置掉），而嵌套成员
        // 没有完成（这里用 --depth 1 让它成为 NotAttemptedDepthLimit —— 与
        // Cli.DepthFlagListsUnprocessedWithoutDoubleCounting 走同一条路径）⇒ 退出码 1。
        H.Run("Cli.ReportsDisposedOriginalsAndWarnsWhenExitCodeIsNotZero", delegate {
            // 处置的是 **Tmp 里的副本**：fixture 被删会让同一轮里后续的步骤失去输入（与
            // Journal.ExtractorRecordsAboutToDeleteBeforeDisposal、Extract.DeletesOnlyCompletedOriginal 同一约定）。
            string target = Path.Combine(TestEnv.Tmp, "del-outer.zip");
            File.Copy(TestEnv.NestedZip, target, true);

            // 本机这一卷到底走哪条处置路径（回收站 / 隔离 / 拒绝）由 RecycleBinGuard 决定；
            // 下面按它分支断言 —— 没有处置发生的那一支也照样在断言，不是跳过。
            string why;
            DeletePlan plan = RecycleBinGuard.Plan(target, out why);

            CliResult r = TestEnv.RunCli("--depth", "1", "--delete", "--target", target,
                "--json-out", TestEnv.JsonOut);
            AssertEq(r.ExitCode, 1);                  // 嵌套项没完成 ⇒ 1（既不是 0，也不是致命的 2）

            string json = ReadJson(TestEnv.JsonOut);
            AssertJsonIsUtf8WithBomAndCompact(TestEnv.JsonOut, json);   // 顶层仍然是那个数组（契约未变）

            // ① 人读的一行：无论本机走哪条路，这一行都必须存在（否则就是又把它静默了）。
            AssertTrue(r.StdOut.IndexOf("原包处置：", StringComparison.Ordinal) >= 0);

            if (plan == DeletePlan.Refuse)
            {
                // 本机策略连删除都不安全（非破坏性）：一个原包都没被处置 ⇒ ① 明说「没有处置任何原包」，
                // ③ 绝不出现。JSON 里那两条都必须是 kept。
                AssertTrue(r.StdOut.IndexOf("没有处置任何原包", StringComparison.Ordinal) >= 0);
                AssertFalse(r.StdOut.IndexOf("⚠", StringComparison.Ordinal) >= 0);
                AssertTrue(File.Exists(target));
                AssertTrue(json.IndexOf("\"originalDisposition\":\"kept\"", StringComparison.Ordinal) >= 0);
            }
            else
            {
                string expected = plan == DeletePlan.Quarantine ? "quarantined" : "deleted";
                string expectedText = plan == DeletePlan.Quarantine ? "已移入隔离文件夹" : "已移入回收站";

                // 原包真的没了（回收站 / 隔离），而退出码是 1 —— 这就是那个假推理的现场。
                AssertFalse(File.Exists(target));

                // ① 清单点名了**这个**原包，并给出中文处置方式。
                AssertTrue(r.StdOut.IndexOf(target, StringComparison.Ordinal) >= 0);
                AssertTrue(r.StdOut.IndexOf(expectedText, StringComparison.Ordinal) >= 0);

                // ② JSON：每条结果都带自己的原包去向 —— 被处置的那条是 deleted/quarantined，
                //    没被处置的那条（嵌套项）必须是 kept。
                AssertTrue(json.IndexOf("\"originalDisposition\":\"" + expected + "\"", StringComparison.Ordinal) >= 0);
                AssertTrue(json.IndexOf("\"originalDisposition\":\"kept\"", StringComparison.Ordinal) >= 0);

                // ③ 显式警告：退出码非 0 但已处置原包。
                AssertTrue(r.StdOut.IndexOf("⚠", StringComparison.Ordinal) >= 0);
                AssertTrue(r.StdOut.IndexOf("已处置 1 个原包", StringComparison.Ordinal) >= 0);
            }

            // 反向（与退出码**无关**的那一半）：同一批参数但**不开删除** ⇒ 一个原包都没处置。
            // 这时无论退出码是不是 1：① 明说「没有处置任何原包」，③ 警告**绝不**出现。
            // 这一支在本机一定会走到（不依赖本卷的处置策略），所以「没处置就不警告」这条判据
            // 不靠上面那个 Refuse 分支碰运气。
            string keep = Path.Combine(TestEnv.Tmp, "keep-outer.zip");
            File.Copy(TestEnv.NestedZip, keep, true);
            CliResult noDelete = TestEnv.RunCli("--depth", "1", "--target", keep,
                "--json-out", TestEnv.JsonOut);
            AssertEq(noDelete.ExitCode, 1);                            // 嵌套项没完成 ⇒ 同样是 1
            AssertTrue(noDelete.StdOut.IndexOf("没有处置任何原包", StringComparison.Ordinal) >= 0);
            AssertFalse(noDelete.StdOut.IndexOf("⚠", StringComparison.Ordinal) >= 0);
            AssertTrue(File.Exists(keep));                             // 原包一字未动
            AssertTrue(ReadJson(TestEnv.JsonOut).IndexOf("\"originalDisposition\":\"kept\"",
                StringComparison.Ordinal) >= 0);                       // JSON 里如实写 kept
        });

        H.Run("Cli.PasswordFlagNeverAppearsInOutput", delegate {
            CliResult r = TestEnv.RunCli("--target", TestEnv.ImportedDictZip, "--password", "ImportedSecret",
                "--json-out", TestEnv.JsonOut);
            AssertEq(r.ExitCode, 0);

            string json = ReadJson(TestEnv.JsonOut);
            AssertHasStatus(json, "Completed");                        // 密码真的被用上了
            AssertTrue(json.IndexOf("手动输入", StringComparison.Ordinal) >= 0);   // 只记来源类别，不记值
            AssertFalse(r.Output.IndexOf("ImportedSecret", StringComparison.Ordinal) >= 0);
            AssertFalse(json.IndexOf("ImportedSecret", StringComparison.Ordinal) >= 0);
        });

        H.Run("Cli.DictFlagFeedsThePasswordLadder", delegate {
            // ImportedDictZip 的口令（ImportedSecret）刻意**不在**内置字典里（Extract.ImportedDictionaryHitIsLabelled
            // 同理），所以命中只可能来自导入字典这一层 —— 这就是 --dict 真的被读进去的证据。
            string dict = TestEnv.MakeFile("dict.txt", "wrong1\r\nImportedSecret\r\n");
            CliResult r = TestEnv.RunCli("--target", TestEnv.ImportedDictZip, "--dict", dict,
                "--json-out", TestEnv.JsonOut);
            AssertEq(r.ExitCode, 0);

            string json = ReadJson(TestEnv.JsonOut);
            AssertHasStatus(json, "Completed");
            AssertTrue(json.IndexOf("导入字典", StringComparison.Ordinal) >= 0);
        });

        H.Run("Cli.BatchContinuesAfterUnreadableTarget", delegate {
            string missing = TestEnv.TmpFile("no-such-archive.zip");
            CliResult r = TestEnv.RunCli("--target", missing, "--target", TestEnv.NestedZip,
                "--json-out", TestEnv.JsonOut);

            AssertEq(r.ExitCode, 1);                                   // 有一项跳过 ⇒ 1（绝不是致命中止 2）
            string json = ReadJson(TestEnv.JsonOut);
            AssertHasStatus(json, "SkippedUnreadable");
            AssertHasStatus(json, "Completed");                        // 坏的那一项绝不拖垮整批
            AssertTrue(File.Exists(TestEnv.InPlaceOutOf(TestEnv.NestedZip, "inner", "hello.txt")));
        });

        // `--target ""`：Extractor.FreezeTargets 对这种值会**静默跳过**（空串没有路径可言），
        // 所以 CLI 自己把它补成一条 SkippedUnreadable —— 逐项可见、且不影响同一批里的其它目标。
        H.Run("Cli.EmptyTargetIsReportedNotDropped", delegate {
            CliResult r = TestEnv.RunCli("--target", "", "--target", TestEnv.NestedZip,
                "--json-out", TestEnv.JsonOut);

            AssertEq(r.ExitCode, 1);
            string json = ReadJson(TestEnv.JsonOut);
            AssertHasStatus(json, "SkippedUnreadable");
            AssertTrue(json.IndexOf("目标路径为空", StringComparison.Ordinal) >= 0);
            AssertHasStatus(json, "Completed");                        // 空参数绝不拖垮整批
            AssertTrue(File.Exists(TestEnv.InPlaceOutOf(TestEnv.NestedZip, "inner", "hello.txt")));
        });

        // 畸形调用一律**在解压之前**以 2 收场（「致命错误 = 什么都没跑成」），
        // 且绝不能静默忽略一个拼错的开关。
        H.Run("Cli.MalformedInvocationExitsTwoWithoutExtracting", delegate {
            string nested = TestEnv.NestedZip;

            AssertEq(TestEnv.RunCli("--depth", "0", "--target", nested).ExitCode, 2);
            AssertEq(TestEnv.RunCli("--depth", "不是数字", "--target", nested).ExitCode, 2);
            AssertEq(TestEnv.RunCli("--deleet", "--target", nested).ExitCode, 2);
            AssertEq(TestEnv.RunCli("--target").ExitCode, 2);           // 开关后面没有值

            CliResult dict = TestEnv.RunCli("--target", nested, "--dict", TestEnv.TmpFile("no-such-dict.txt"));
            AssertEq(dict.ExitCode, 2);
            AssertTrue(dict.Output.IndexOf("字典", StringComparison.Ordinal) >= 0);

            // 「什么都没跑成」必须是字面意义上的：一个字节都没解出来。
            AssertFalse(Directory.Exists(TestEnv.InPlaceOutOf(nested)));
            AssertTrue(File.Exists(nested));
        });
    }

    // ------------------------------------------------------------------
    // 断言助手
    // ------------------------------------------------------------------

    // 报告必须真的写出来了（否则后面读它会抛 FileNotFoundException，用例的失败原因会跑偏）。
    private static string ReadJson(string path)
    {
        AssertTrue(File.Exists(path));
        return File.ReadAllText(path);
    }

    private static void AssertHasStatus(string json, string status)
    {
        AssertTrue(json.IndexOf("\"status\":\"" + status + "\"", StringComparison.Ordinal) >= 0);
    }

    // JSON 数组里有几个对象（= 几个归档）。「每个归档恰好一个对象」这类断言用它。
    private static int ObjectCount(string json)
    {
        return CountOccurrences(json, "{\"path\":");
    }

    // 某个结局出现了几次。深度触顶项绝不被再合成一条（= 恰好 1），合成项也绝不重复发。
    private static int StatusCount(string json, string status)
    {
        return CountOccurrences(json, "\"status\":\"" + status + "\"");
    }

    private static int CountOccurrences(string text, string needle)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) { return 0; }

        int count = 0;
        int at = text.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }

    // 一个日志根下的日志文件名（目录不存在就是空集）。用于证明真实的应用数据根没被动过。
    private static string[] LogNames(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { return new string[0]; }

        string[] files;
        try { files = Directory.GetFiles(root, "*" + Journal.FileExtension); }
        catch (Exception) { return new string[0]; }

        string[] names = new string[files.Length];
        for (int i = 0; i < files.Length; i++) { names[i] = Path.GetFileName(files[i]); }
        return names;
    }

    // 快照之后这个日志根里**不许**多出任何文件（也不许少 —— 测试绝不删别人的记录）。
    private static void AssertNoNewLogs(string root, string[] before)
    {
        string[] after = LogNames(root);
        AssertEq(after.Length, before.Length);

        List<string> known = new List<string>(before);
        for (int i = 0; i < after.Length; i++)
        {
            AssertTrue(known.Contains(after[i]));
        }
    }

    // 一条「已完成」的结果（RunExitCodes 的用例只需要形状，不需要真跑一次解压）。
    private static ArchiveResult CompletedResult(string archivePath)
    {
        ArchiveResult result = new ArchiveResult();
        result.Path = archivePath;
        result.Status = ArchiveStatus.Completed;
        result.Layers = 1;
        result.Files = 1;
        result.Failed = 0;
        result.OutputDir = "";
        result.Message = "";
        return result;
    }

    // 报告的编码与形状：UTF-8 **带 BOM**（规格 §2 第 6 条 / §3：报告一律 BOM，否则 PowerShell 5.1
    // 会按系统代码页读中文），紧凑**单行**（字符串里的任何控制字符都必须已转义 —— JSON 不允许裸控制字符）。
    private static void AssertJsonIsUtf8WithBomAndCompact(string path, string json)
    {
        byte[] head = File.ReadAllBytes(path);
        AssertTrue(head.Length >= 3);
        AssertEq(head[0], (byte)0xEF);
        AssertEq(head[1], (byte)0xBB);
        AssertEq(head[2], (byte)0xBF);

        AssertTrue(json.Length > 1);
        AssertEq(json[0], '[');
        AssertEq(json[json.Length - 1], ']');
        for (int i = 0; i < json.Length; i++)
        {
            AssertTrue(json[i] >= ' ');
        }
    }

    // 键名与顺序是跨任务契约（Task 8 的 CSV 列名逐字相同）：path,status,layers,files,failed,outputDir,message。
    private static void AssertJsonKeyOrder(string json)
    {
        string[] keys = new string[] { "path", "status", "layers", "files", "failed", "outputDir", "message" };

        int at = json.IndexOf('{');
        AssertTrue(at >= 0);
        for (int i = 0; i < keys.Length; i++)
        {
            int found = json.IndexOf("\"" + keys[i] + "\":", at, StringComparison.Ordinal);
            AssertTrue(found >= 0);            // 键必须存在
            at = found + 1;                    // 且必须按契约顺序出现
        }
    }

    // 与 CLI 的 JSON 转义规则一致（JSON 标准：引号、反斜杠、控制字符）。
    // 用例用它拼出「路径在报告里应当长什么样」，而不是假设它原样出现。
    private static string JsonEscaped(string value)
    {
        StringBuilder sb = new StringBuilder();
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
                    if (c < ' ')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }
}
