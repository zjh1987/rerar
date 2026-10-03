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
//   * 恢复查询只靠日志文件本身就能回答（FindIncompleteDestinations / RunsWithoutCleanShutdown）；
//   * Extractor 接线：解压前落「即将写入哪里」、提交成功后落「这个目的地处理完了」、
//     删除原包前落「即将删除」；失败/未提交的目的地必须被报成未完成；
//   * 密码绝不进日志（Task 10 的凭据卫生在日志这一侧同样成立）。
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

        // 「已提交但内容不全」（CompletedWithFailures）刻意**不**算做完：恢复查询要回答的是
        // 「哪些目的地不是完整结果」，不是「这个进程有没有继续碰它」。宁可多报，绝不少报。
        H.Run("Journal.PartialCommitStaysIncomplete", delegate {
            Journal j = Journal.Open("t5b");
            j.Note("about-to-extract", @"C:\out\partial" + "\tsource=" + @"C:\in\c.zip");
            j.Note("extract-done", @"C:\out\partial" + "\tstatus=CompletedWithFailures\toriginal=kept");

            List<string> incomplete = new List<string>(Journal.FindIncompleteDestinations());
            AssertEq(incomplete.Count, 1);
            AssertEq(incomplete[0], @"C:\out\partial");

            // 同一个目的地的记录改口说 status=Completed 时，才算「做完」。
            j.Note("extract-done", @"C:\out\partial" + "\tstatus=Completed\toriginal=kept");
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
}
