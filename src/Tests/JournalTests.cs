// Task 11 单元测试：Journal（崩溃恢复日志）。
//
// 前三条用例的名字与形状逐字来自 task-11-brief.md Step 1；其余用例覆盖本轮任务书点名、
// 而 brief 示例没写到的性质：
//   * 测试接缝：生产默认根是 %LOCALAPPDATA%\Rerar\journal（**绝不在目标卷**），
//     测试一律经 Journal.Root 重定向到 TestEnv.Tmp —— 每个用例前 H.Run 都会清空临时根；
//   * 落盘：Note 必须在**返回之前**就可被另一个句柄读到（FileStream + Flush(true)，且不留长开句柄）；
//   * 追加式 + 并发不损坏：同一实例多线程并发 Note，文件必须仍是一行一条、字段完整；
//   * 同一 runId 的第二次运行绝不能让第一段的「未完成」被第一段的 done 掩盖（分段标记 run-start），
//     也绝不丢弃第一段已经写下的记录；
//   * 恢复查询只靠日志文件本身就能回答（FindIncompleteDestinations / RunsWithoutCleanShutdown /
//     FindCommittedWithFailures）；
//   * Extractor 接线：解压前落「即将写入哪里」、提交成功后落「这个目的地处理完了」、
//     删除原包前落「即将删除」；失败/未提交的目的地必须被报成未完成；
//   * 密码绝不进日志（Task 10 的凭据卫生在日志这一侧同样成立）。
//
// 修复轮（本轮三条 Finding）新增的用例：
//   * Finding 1：日志 I/O 失败必须被数下来并**如实汇报**（运行摘要 + 至少一条结果判词），而解压
//     行为一字不改 —— 两个真实触发条件（日志整个打不开 / 打开成功但写入随后失败）各一条用例；
//   * Finding 2：FindIncompleteDestinations 只报**从未提交**的目的地；CompletedWithFailures
//     **有**完成记录，改由 FindCommittedWithFailures 带失败成员数单独报出；
//   * Finding 3：RunsWithoutCleanShutdown 的含义是「进程刻意结束，而不是死在途中」（被取消的运行
//     有 done、不在清单里）—— 只改文档；用例把这个被文档钉住的区别固定下来。
//
// 最终修复轮（整支复审 Finding 1）新增：运行级计数（_journalAttempts / _journalFailures /
// _journalFailureReason）必须在**每次 Run** 重置 —— 同一个实例连跑两次时，第一次的日志失败
// 绝不能算到第二次头上（那会造出一条「崩溃恢复记录不完整」的假警告）。
//
// 全部用例都是「真读写真文件」的端到端形状，没有任何 mock。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Rerar.Core;

internal sealed class JournalTests : TestBase
{
    public static void Run()
    {
        // ==================================================================
        // brief Step 1 的三条（名字逐字）
        // ==================================================================

        H.Run("Journal.FlushesBeforeIrreversibleStep", delegate {
            Journal j = Journal.Open("t1");
            j.Note("about-to-extract", @"C:\x\a.zip");

            // 另开句柄立刻读得到 ⇒ 记录已离开进程缓冲（Note 内部 FileStream + Flush(true)，
            // 并在返回前关闭句柄）。缓冲住、要等句柄关掉才可见的记录，遇崩溃等于没写。
            string text = File.ReadAllText(j.Path);
            AssertTrue(text.Contains("about-to-extract"));
            AssertTrue(text.Contains(@"C:\x\a.zip"));

            // 句柄没有遗留：以 FileShare.None 独占打开必须成功。
            // （长开句柄会让 TestEnv.Cleanup() 删不掉临时根，用例之间就不再自洽。）
            using (FileStream exclusive = new FileStream(j.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
            }
        });

        H.Run("Journal.DetectsUncleanShutdown", delegate {
            Journal.Open("t2").Note("about-to-extract", @"C:\x\a.zip");
            AssertTrue(new List<string>(Journal.RunsWithoutCleanShutdown()).Contains("t2"));
        });

        H.Run("Journal.CleanRunNotReported", delegate {
            Journal j = Journal.Open("t3");
            j.Note("about-to-extract", "x");
            j.Note("done", "x");
            AssertFalse(new List<string>(Journal.RunsWithoutCleanShutdown()).Contains("t3"));
        });

        // ==================================================================
        // 测试接缝与「绝不在目标卷」的默认根
        // ==================================================================

        H.Run("Journal.DefaultRootIsUnderLocalAppData", delegate {
            string expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rerar", "journal");
            AssertEq(Journal.DefaultRoot, expected);

            // 恢复记录绝不能落在目标卷（输出根所在的位置）：目标卷正是「会被写满、会掉线」的那一个。
            AssertFalse(Journal.DefaultRoot.StartsWith(TestEnv.OutRoot, StringComparison.OrdinalIgnoreCase));
        });

        H.Run("Journal.PathIsUnderInjectedRoot", delegate {
            Journal j = Journal.Open("t4");
            AssertEq(j.Path, Path.Combine(TestEnv.JournalRoot, "t4.log"));
            AssertTrue(File.Exists(j.Path));

            // 接缝生效：测试期间日志根被重定向到临时目录，绝不写真实 %LOCALAPPDATA%。
            AssertFalse(string.Equals(Journal.Root, Journal.DefaultRoot, StringComparison.OrdinalIgnoreCase));
            AssertTrue(Journal.Root.StartsWith(TestEnv.Tmp, StringComparison.OrdinalIgnoreCase));
        });

        // ==================================================================
        // 恢复查询：只看日志文件本身
        // ==================================================================

        // about-to-extract 记下目的地，之后同目的地的 extract-done 把它消掉；剩下的就是未完成。
        // 明细里首字段就是目的地绝对路径 —— 恢复查询不需要任何进程内状态。
        H.Run("Journal.FindsIncompleteDestinationsFromRecordsAlone", delegate {
            Journal j = Journal.Open("t5");
            j.Note("about-to-extract", @"C:\out\committed" + "\tsource=" + @"C:\in\a.zip");
            j.Note("extract-done", @"C:\out\committed" + "\tstatus=Completed\toriginal=kept");
            j.Note("about-to-extract", @"C:\out\half" + "\tsource=" + @"C:\in\b.zip");

            List<string> incomplete = new List<string>(Journal.FindIncompleteDestinations());
            AssertEq(incomplete.Count, 1);
            AssertEq(incomplete[0], @"C:\out\half");
        });

        // 修复轮 Finding 2（控制方裁定）：FindIncompleteDestinations 报的是**没有完成记录**的目的地。
        // 只有 about-to-extract、没有 extract-done ⇒ 从未提交 ⇒ 必须报出来。
        H.Run("Journal.NeverCommittedDestinationIsReported", delegate {
            Journal j = Journal.Open("t5b");
            j.Note("about-to-extract", @"C:\out\committed" + "\tsource=" + @"C:\in\c.zip");
            j.Note("extract-done", @"C:\out\committed" + "\tstatus=Completed\tfailed=0\toriginal=kept");
            j.Note("about-to-extract", @"C:\out\never-committed" + "\tsource=" + @"C:\in\d.zip");

            List<string> incomplete = new List<string>(Journal.FindIncompleteDestinations());
            AssertEq(incomplete.Count, 1);
            AssertEq(incomplete[0], @"C:\out\never-committed");
        });

        // 修复轮 Finding 2：CompletedWithFailures **有**完成记录（提交成功、校验跑过），所以它**不是**
        // 「没有完成记录」的目的地 —— 报成未完成会让用户去重新解压一个其实已经在盘上的目录。
        // 它的不完整性由 FindCommittedWithFailures() 带失败成员数单独报出（见下一条用例）。
        H.Run("Journal.CommittedWithFailuresIsNotIncomplete", delegate {
            Journal j = Journal.Open("t5c");
            j.Note("about-to-extract", @"C:\out\partial" + "\tsource=" + @"C:\in\e.zip");
            j.Note("extract-done", @"C:\out\partial" + "\tstatus=CompletedWithFailures\tfailed=3\toriginal=kept");

            AssertEq(new List<string>(Journal.FindIncompleteDestinations()).Count, 0);

            // 「已提交但内容不全」要单独报出来，而且**带上失败成员数**：消费者据此提示
            //「这个目录已经在盘上，但内容不全」，而不是让用户去重新解压。
            List<Journal.CommittedWithFailures> warned =
                new List<Journal.CommittedWithFailures>(Journal.FindCommittedWithFailures());
            AssertEq(warned.Count, 1);
            AssertEq(warned[0].Destination, @"C:\out\partial");
            AssertEq(warned[0].Failed, 3);

            // 干净的 Completed 记录不是「有失败成员」：警告清单里绝不能凭空多出目的地。
            j.Note("about-to-extract", @"C:\out\clean" + "\tsource=" + @"C:\in\f.zip");
            j.Note("extract-done", @"C:\out\clean" + "\tstatus=Completed\tfailed=0\toriginal=kept");
            AssertEq(new List<Journal.CommittedWithFailures>(Journal.FindCommittedWithFailures()).Count, 1);

            // 两条目的地都已提交 ⇒ 「没有完成记录」的清单是空的。
            AssertEq(new List<string>(Journal.FindIncompleteDestinations()).Count, 0);
        });

        // 同一个 runId 的第二次运行：新一段以 run-start 开始，于是
        //   ① 第一段的 done **绝不**把第二段的未完成掩盖掉；
        //   ② 第一段已经写下的记录一条都不会被改写/丢弃（追加式）。
        H.Run("Journal.SecondRunForSameIdIsNotMaskedByFirstDone", delegate {
            Journal first = Journal.Open("t6");
            first.Note("about-to-extract", @"C:\out\one");
            first.Note("done", "results=1");
            AssertFalse(new List<string>(Journal.RunsWithoutCleanShutdown()).Contains("t6"));

            Journal second = Journal.Open("t6");
            second.Note("about-to-extract", @"C:\out\two");

            AssertTrue(new List<string>(Journal.RunsWithoutCleanShutdown()).Contains("t6"));
            AssertTrue(File.ReadAllText(second.Path).Contains(@"C:\out\one"));

            // 两段的目的地都没有完成记录 ⇒ 两个都报（保守方向：宁可多报，绝不少报）。
            List<string> incomplete = new List<string>(Journal.FindIncompleteDestinations());
            AssertEq(incomplete.Count, 2);
            AssertEq(incomplete[0], @"C:\out\one");
            AssertEq(incomplete[1], @"C:\out\two");

            second.Note("done", "results=1");
            AssertFalse(new List<string>(Journal.RunsWithoutCleanShutdown()).Contains("t6"));
        });

        // 并发追加绝不能交错出「半条记录」：Note 走静态锁，记录只有一行一条这一个形状。
        H.Run("Journal.ConcurrentNotesStayWellFormed", delegate {
            Journal j = Journal.Open("t7");
            int threads = 8;
            int perThread = 40;

            List<Thread> workers = new List<Thread>();
            for (int t = 0; t < threads; t++)
            {
                int worker = t;      // 闭包捕获的是变量本身，必须每轮新建
                Thread thread = new Thread(delegate() {
                    for (int i = 0; i < perThread; i++) { j.Note("worker-" + worker, "n=" + i); }
                });
                workers.Add(thread);
                thread.Start();
            }
            foreach (Thread thread in workers) { thread.Join(); }

            string[] lines = File.ReadAllLines(j.Path);
            AssertEq(lines.Length, 1 + threads * perThread);      // 1 = Open 写的 run-start

            int fromWorker0 = 0;
            foreach (string line in lines)
            {
                string[] fields = line.Split('\t');
                AssertEq(fields.Length, 3);                        // 时间戳 / 步骤 / 明细，一条一行
                if (fields[1] == "worker-0") { fromWorker0++; }
            }
            AssertEq(fromWorker0, perThread);
        });

        // ==================================================================
        // Extractor 接线（端到端）
        // ==================================================================

        // 一次正常跑完的运行：每个目的地都留下「即将解压」与「处理完了」两条记录，最后是 done；
        // 于是恢复查询既报不出未完成的目的地，也报不出没有正常结束的运行。
        H.Run("Journal.ExtractorWritesRecoveryTrailForCleanRun", delegate {
            RunSummary s = TestEnv.RunExtract(TestEnv.NestedZip);
            AssertEq(s.Results[0].Status, ArchiveStatus.Completed);

            string text = AllJournalText();
            AssertTrue(text.Length > 0);                           // 空文本上的 Contains 是假通过

            string destination = TestEnv.OutOf(TestEnv.NestedZip);
            AssertTrue(text.Contains("\t" + Journal.AboutToExtractStep + "\t"));
            AssertTrue(text.Contains("\t" + Journal.ExtractDoneStep + "\t"));
            AssertTrue(text.Contains("\t" + Journal.DoneStep + "\t"));

            // 「即将写入哪里、写什么」两件事都在记录里（目的地 + 源归档）。
            AssertTrue(text.Contains(destination + "\tsource=" + TestEnv.NestedZip));
            AssertTrue(text.Contains(destination + "\tstatus=Completed"));

            AssertEq(new List<string>(Journal.FindIncompleteDestinations()).Count, 0);
            AssertEq(new List<string>(Journal.RunsWithoutCleanShutdown()).Count, 0);
        });

        // 解压真的开始过、但最终没有提交的目的地（这里 7z 退出码 2）：日志里只有
        // about-to-extract 而没有 extract-done ⇒ 恢复查询必须把它报出来，且只有它一个。
        H.Run("Journal.ExtractorReportsFailedDestinationIncomplete", delegate {
            RunSummary s = TestEnv.RunExtract(TestEnv.CorruptPayloadZip);
            AssertEq(s.Results[0].Status, ArchiveStatus.Failed);

            List<string> incomplete = new List<string>(Journal.FindIncompleteDestinations());
            AssertEq(incomplete.Count, 1);
            AssertEq(incomplete[0], TestEnv.OutOf(TestEnv.CorruptPayloadZip));
        });

        // 删除（I3）在日志里的形状：「即将删除」记录写在**真正处置之前**，
        // 处置结局写在随后的 extract-done 明细里（本机 Plan 决定是回收站还是隔离）。
        H.Run("Journal.ExtractorRecordsAboutToDeleteBeforeDisposal", delegate {
            string target = TestEnv.TmpFile("journal-delete-me.zip");
            File.Copy(TestEnv.PlainZip, target, true);

            string why;
            DeletePlan plan = RecycleBinGuard.Plan(target, out why);

            RunSummary s = TestEnv.RunExtractWithDelete(target);
            AssertEq(s.Results[0].Status, ArchiveStatus.Completed);

            string text = AllJournalText();
            AssertTrue(text.Length > 0);
            AssertTrue(text.Contains("\t" + Journal.ExtractDoneStep + "\t"));

            if (plan == DeletePlan.Refuse)
            {
                // 连处置都不安全：绝不尝试，于是根本没有「即将删除」的记录，原包仍在原地。
                AssertFalse(text.Contains("\t" + Journal.AboutToDeleteStep + "\t"));
                AssertTrue(text.Contains("original=kept"));
                AssertTrue(File.Exists(target));
            }
            else
            {
                AssertFalse(File.Exists(target));

                int aboutToDelete = text.IndexOf("\t" + Journal.AboutToDeleteStep + "\t", StringComparison.Ordinal);
                int extractDone = text.IndexOf("\t" + Journal.ExtractDoneStep + "\t", StringComparison.Ordinal);
                AssertTrue(aboutToDelete >= 0);                    // 处置之前确实落了记录
                AssertTrue(extractDone > aboutToDelete);           // 结局记在它之后

                AssertTrue(plan == DeletePlan.Quarantine
                    ? text.Contains("original=quarantined")
                    : text.Contains("original=deleted"));
            }
        });

        // 凭据卫生：日志里绝不能出现密码值（Task 10 只记「候选序号 + 来源类别」，日志照旧）。
        H.Run("Journal.ExtractorNeverWritesPassword", delegate {
            RunSummary s = TestEnv.RunExtractWithPassword(TestEnv.AesZip, "SECRET");
            AssertEq(s.Results[0].Status, ArchiveStatus.Completed);

            string text = AllJournalText();
            AssertTrue(text.Length > 0);
            AssertFalse(text.Contains("SECRET"));
        });

        // ==================================================================
        // 修复轮 Finding 1：日志写失败必须**可见**（不再静默）
        // ==================================================================

        // 日志**整个打不开**：把日志根指到一个「其实是文件」的路径上 —— 权限 / 盘满 / 路径不可用
        // 这一族的最小可复现形状。失败必须被数下来并如实汇报（中文 + 具体原因），而解压行为
        // 必须一字不改（这次解压仍然 Completed）—— 吞掉异常的原意保留，只是不再不吭声。
        H.Run("Journal.ExtractorReportsUnavailableJournal", delegate {
            string blocked = Path.Combine(TestEnv.Tmp, "journal-root-is-a-file");
            File.WriteAllText(blocked, "not a directory");
            // 下一个用例的 TestEnv.Cleanup() 会把 Journal.Root 重定向回临时日志根，无需手工恢复。
            Journal.Root = blocked;

            RunSummary s = TestEnv.RunExtract(TestEnv.NestedZip);

            AssertEq(s.Results[0].Status, ArchiveStatus.Completed);    // 日志失败绝不改变解压行为
            AssertEq(s.JournalWriteFailures, 1);                       // 日志整个没打开 = 1 次失败
            AssertTrue(s.JournalProblem != null && s.JournalProblem.Length > 0);
            AssertTrue(s.JournalProblem.Contains("崩溃恢复记录不完整"));
            AssertTrue(s.JournalProblem.Contains("Exception"));        // 原因具体到异常类型
            AssertTrue(s.JournalProblem.Contains("无法打开崩溃恢复日志"));
            AssertTrue(s.JournalProblem.Contains("没有写入任何恢复记录"));  // 一条都没有，不能含糊

            // 至少一条**结果判词**里也要出现：只放在 RunSummary 上，只渲染判词的消费方仍然看不到。
            AssertTrue(s.Results[s.Results.Count - 1].Message.Contains("崩溃恢复记录不完整"));
        });

        // 日志**打开成功、写入随后失败**：在第 1 个归档开始（run-start 已落盘）时把日志根目录换成
        // 同名文件，于是随后每条 Note 都必然失败。这一档同样必须被数下来并如实汇报。
        H.Run("Journal.ExtractorReportsWriteFailures", delegate {
            BreakingJournalSink sink = new BreakingJournalSink();
            RunSummary s = TestEnv.RunExtractWithSink(TestEnv.NestedZip, sink);

            if (!sink.Broke)
            {
                H.Skip("Journal.ExtractorReportsWriteFailures",
                    "无法构造前置条件：日志根目录没能换成同名文件（" + sink.Error + "）");
                return;
            }

            AssertEq(s.Results[0].Status, ArchiveStatus.Completed);    // 日志失败绝不改变解压行为
            AssertTrue(s.JournalWriteFailures >= 1);
            AssertTrue(s.JournalProblem != null && s.JournalProblem.Contains("无法写入崩溃恢复日志"));
            AssertTrue(s.JournalProblem.Contains("Exception"));
            AssertTrue(s.JournalProblem.Contains("条恢复记录中有"));     // 说清「几条没写成」
            AssertTrue(s.Results[s.Results.Count - 1].Message.Contains("崩溃恢复记录不完整"));
        });

        // 修复轮 Finding 3（只改文档、**不改行为**）：done 在「用户取消」「致命中止」时也会写，
        // 所以 RunsWithoutCleanShutdown 的含义是「进程**刻意**结束，而不是死在途中」，绝不是
        //「这次运行没有未做完的工作」。这条用例把新写在 API 文档与记录格式里的区别钉住：
        // 一次被取消的运行（done 明细 cancelled=true）**不**出现在崩溃清单里。
        H.Run("Journal.CancelledRunIsDeliberateExitNotCrash", delegate {
            RunSummary s = TestEnv.RunExtractCancelled(TestEnv.NestedZip);
            AssertTrue(s.Cancelled);

            string text = AllJournalText();
            AssertTrue(text.Contains("\t" + Journal.DoneStep + "\t"));     // 刻意结束照样写 done
            AssertTrue(text.Contains("cancelled=true"));

            // 刻意结束 ≠ 崩溃：这份清单只回答「进程有没有死在途中」。
            AssertEq(new List<string>(Journal.RunsWithoutCleanShutdown()).Count, 0);
        });

        // ==================================================================
        // 最终修复轮 Finding 1：运行级日志计数必须**每次 Run 重置**
        // ==================================================================

        // 同一个 Extractor 实例连跑两次：第一次日志整个打不开（记 1 次失败），第二次日志恢复可用。
        // 计数若只在**字段声明**上初始化（而不在 Run() 里重置），第二次运行就会把上一次的失败当成
        // 自己的 —— 一条「崩溃恢复记录不完整」的**假警告**，恰好出现在这个以「如实汇报」为全部
        // 目的的单元里（Reporter/CLI/界面都会照它说这一次没有可信的恢复记录）。
        //
        // 注意两次运行用的是**同一个** Extractor：这正是「字段声明处初始化」与「Run() 里重置」的
        // 唯一区别所在，也是本用例存在的全部意义。
        H.Run("Journal.CountersResetBetweenRunsOnSameInstance", delegate {
            string goodRoot = TestEnv.JournalRoot;
            string blockedRoot = Path.Combine(TestEnv.Tmp, "journal-root-blocked-once");
            File.WriteAllText(blockedRoot, "not a directory");

            RunOptions options = new RunOptions();
            options.SevenZipPath = TestEnv.SevenZip;
            options.OutputRoot = TestEnv.OutRoot;
            options.MaxDepth = 10;
            options.DiskPollSeconds = 2;
            Extractor extractor = new Extractor(options, new DriveSpaceProvider(), null);

            // --- 第 1 次：日志整个打不开 ⇒ 必须被数下来并如实汇报（既有行为，这里当前提）---
            Journal.Root = blockedRoot;
            RunSummary first = extractor.Run(new string[] { TestEnv.NestedZip });

            AssertEq(first.Results[0].Status, ArchiveStatus.Completed);   // 日志失败绝不改变解压行为
            AssertEq(first.JournalWriteFailures, 1);                      // 日志整个没打开 = 1 次失败
            AssertTrue(first.JournalProblem != null);

            // --- 第 2 次：日志恢复可用 ⇒ 计数必须从**零**开始 ---
            Journal.Root = goodRoot;
            RunSummary second = extractor.Run(new string[] { TestEnv.NestedZip });

            AssertEq(second.Results[0].Status, ArchiveStatus.Completed);
            AssertEq(second.JournalWriteFailures, 0);                     // 上一次的失败不算到这一次头上
            AssertTrue(second.JournalProblem == null);                    // 也就没有「记录不完整」的说明
            AssertFalse(second.Results[0].Message.Contains("崩溃恢复记录不完整"));

            // 非空性守卫：这一次日志**真的**写下来了。否则「0 次失败」只是因为日志压根没打开过
            // （例如 Journal.Root 没恢复成功），这条断言就成了空话。
            AssertTrue(Directory.GetFiles(goodRoot, "*.log").Length > 0);

            Journal.Root = goodRoot;   // 收尾：本用例动过静态根，恢复成测试根
        });
    }

    // 本次用例产生的全部日志文本（日志根每用例前都被 Cleanup 清空，所以这里只有本用例的记录）。
    private static string AllJournalText()
    {
        string text = "";
        foreach (string file in Directory.GetFiles(TestEnv.JournalRoot, "*.log"))
        {
            text = text + File.ReadAllText(file) + "\n";
        }
        return text;
    }

    // 修复轮 Finding 1 的夹具：在「归档开始」这一刻（此时日志**已经打开**、run-start 也已落盘）把
    // 日志根**目录**换成同名**文件** —— 于是随后每条 Note 都必然失败（父路径不再是目录）。
    // 这是「日志打开成功、写入随后失败」的真实触发条件，不是 mock：写盘照旧真写、真失败。
    // 换名失败时如实记下原因（Broke 仍为 false），让用例走 H.Skip 而不是假 PASS。
    private sealed class BreakingJournalSink : IProgressSink
    {
        public bool Broke;
        public string Error;

        public void ArchiveStarted(string archivePath, int depth) { Break(); }
        public void ArchiveFinished(ArchiveResult result) { }
        public void Progress(string archivePath, int percent, string member) { }
        public void Message(string text) { }

        private void Break()
        {
            if (Broke) { return; }

            string root = Journal.Root;
            try
            {
                if (Directory.Exists(root)) { Directory.Delete(root, true); }
                if (!File.Exists(root)) { File.WriteAllText(root, "not a directory"); }
                Broke = true;
            }
            catch (Exception ex)
            {
                Error = ex.GetType().Name + "：" + ex.Message;
            }
        }
    }
}
