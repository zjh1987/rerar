// Task 3 单元测试：SevenZipRunner（进程、退出码、Job Object、进度解析）。
//
// 开头 6 条用例的名字与期望值逐字来自 task-3-brief.md Step 1（只按 Task 1 的 harness 约定
// 加了 TestEnv. 前缀与排版）。其余用例覆盖 brief 的 Produces/Step 3 里 Step 1 没有触碰的部分：
//   -p 强制（I5）、stdout/stderr 异步累积、超过管道缓冲的输出不死锁、真实 7z 进度行、
//   stdin 关闭后密码提示不会挂起、取消与 TerminateAll 真的杀得掉子进程。
//
// 关于线程：凡是「本来可能永久挂起」的用例都放在后台线程上有界等待（RunBounded / Join），
// 这样 I5 或 Job Object 一旦回归，得到的是一个 FAIL，而不是一套永远跑不完的测试。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Rerar.Core;

internal sealed class SevenZipRunnerTests : TestBase
{
    public static void Run()
    {
        // ---- brief Step 1 的 6 条（用例名与期望值逐字照抄）----
        H.Run("Runner.ParseProgress.ExtractsPercentAndMember", delegate {
            int p; string m;
            SevenZipRunner.ParseProgressLine(" 58% 2       - sub\\big2.bin", out p, out m);
            AssertEq(p, 58); AssertEq(m, "sub\\big2.bin"); });
        H.Run("Runner.ParseProgress.MalformedLineIsIgnored", delegate {
            int p; string m;
            SevenZipRunner.ParseProgressLine("   \r  %  - ", out p, out m);
            AssertEq(p, -1); });
        H.Run("Runner.ParseProgress.TruncatedMultibyteIsIgnored", delegate {
            int p; string m;
            SevenZipRunner.ParseProgressLine(" 12% 1 - \u4e2d\u6587", out p, out m);
            AssertEq(p, 12); });
        H.Run("Runner.IsSuccess.ZeroAndOneAreSuccess", delegate {
            AssertTrue(SevenZipRunner.IsSuccess(0)); AssertTrue(SevenZipRunner.IsSuccess(1));
            AssertFalse(SevenZipRunner.IsSuccess(2)); AssertFalse(SevenZipRunner.IsSuccess(255)); });
        H.Run("Runner.CorruptArchiveExitsTwo", delegate {
            RunResult r = SevenZipRunner.Run(TestEnv.SevenZip, new string[] { "x", TestEnv.CorruptZip, "-o" + TestEnv.Tmp, "-y", "-p" }, null, CancellationToken.None);
            AssertEq(r.ExitCode, 2); });
        H.Run("Runner.EncryptedWithoutPasswordDoesNotHang", delegate {
            Stopwatch sw = Stopwatch.StartNew();
            RunResult r = SevenZipRunner.Run(TestEnv.SevenZip, new string[] { "t", TestEnv.AesZip, "-p", "-y" }, null, CancellationToken.None);
            AssertTrue(sw.Elapsed.TotalSeconds < 20); AssertTrue(r.ExitCode != 0); });

        // ---- 退出码 1 =「完成但有警告」仍算成功（brief 的 IsSuccess 语义，端到端验证）----
        // 独占锁住输入文件，7z 读不到它就只会警告（实测退出码 1，不是 2）。
        H.Run("Runner.WarningExitCodeOneCountsAsSuccess", delegate {
            string seed = TestEnv.MakeFile("locked.txt", "locked seed\r\n");
            RunResult r;
            FileStream hold = File.Open(seed, FileMode.Open, FileAccess.Read, FileShare.None);
            try {
                r = SevenZipRunner.Run(TestEnv.SevenZip,
                    new string[] { "a", "-tzip", TestEnv.TmpFile("warning.zip"), seed, "-pSECRET", "-y" }, null, CancellationToken.None);
            }
            finally { hold.Dispose(); }
            AssertEq(r.ExitCode, 1);
            AssertTrue(SevenZipRunner.IsSuccess(r.ExitCode)); });

        // ---- 进度行的真实形状（实测 7-Zip 26.01：解压 " 34% - name"，压缩 " 11% + name"）----
        H.Run("Runner.ParseProgress.AcceptsRealSevenZipForms", delegate {
            int p; string m;
            SevenZipRunner.ParseProgressLine(" 34% - huge.bin", out p, out m);
            AssertEq(p, 34); AssertEq(m, "huge.bin");
            SevenZipRunner.ParseProgressLine(" 11% + seed.bin", out p, out m);
            AssertEq(p, 11); AssertEq(m, "seed.bin"); });

        // ---- 畸形行一律 -1 且不抛（brief Step 3 / Review Focus #2）----
        H.Run("Runner.ParseProgress.IgnoresEmptyAndPercentOnlyLines", delegate {
            int p;
            string problem = "";
            if (Check(null, out p) != -1) { problem = "null 行返回 " + p; }
            if (problem.Length == 0 && Check("", out p) != -1) { problem = "空行返回 " + p; }
            if (problem.Length == 0 && Check("    ", out p) != -1) { problem = "纯空白行返回 " + p; }
            if (problem.Length == 0 && Check("  0%", out p) != -1) { problem = "只有百分比（无成员名）返回 " + p; }
            if (problem.Length == 0 && Check("100% 1 - ", out p) != -1) { problem = "成员名为空返回 " + p; }
            if (problem.Length == 0 && Check("abc% 1 - x.bin", out p) != -1) { problem = "% 前不是数字返回 " + p; }
            if (problem.Length == 0 && Check("58 1 - x.bin", out p) != -1) { problem = "没有 % 返回 " + p; }
            AssertEq(problem, ""); });

        // ---- I5：没有 -p 的参数表必须在起进程之前就被拒（防止无人值守挂起）----
        H.Run("Runner.RejectsArgsWithoutPasswordSwitch", delegate {
            bool threw = false;
            try { SevenZipRunner.Run(TestEnv.SevenZip, new string[] { "t", TestEnv.AesZip, "-y" }, null, CancellationToken.None); }
            catch (ArgumentException) { threw = true; }
            AssertTrue(threw);

            bool threwEmpty = false;
            try { SevenZipRunner.Run(TestEnv.SevenZip, new string[0], null, CancellationToken.None); }
            catch (ArgumentException) { threwEmpty = true; }
            AssertTrue(threwEmpty); });

        // ---- 参数里的空格必须被正确转义（.NET Framework 只有字符串 Arguments）----
        H.Run("Runner.ExtractsToPathWithSpacesAndCapturesStdOut", delegate {
            string outDir = Path.Combine(TestEnv.Tmp, "out dir with spaces");
            Directory.CreateDirectory(outDir);
            RunResult r = SevenZipRunner.Run(TestEnv.SevenZip,
                new string[] { "x", TestEnv.AesZip, "-o" + outDir, "-pSECRET", "-y" }, null, CancellationToken.None);
            AssertTrue(SevenZipRunner.IsSuccess(r.ExitCode));
            AssertTrue(r.StdOut != null && r.StdOut.Length > 0);
            string[] files = Directory.GetFiles(outDir);
            AssertEq(files.Length, 1);
            AssertTrue(new FileInfo(files[0]).Length > 0); });

        // ---- stderr 单独成流累积 ----
        H.Run("Runner.WrongPasswordReportsToStdErrAndFails", delegate {
            RunResult r = SevenZipRunner.Run(TestEnv.SevenZip,
                new string[] { "t", TestEnv.AesZip, "-pWRONG", "-y" }, null, CancellationToken.None);
            AssertFalse(SevenZipRunner.IsSuccess(r.ExitCode));
            AssertTrue(r.StdErr != null && r.StdErr.Length > 0); });

        // ---- 无管道死锁：7z i 的输出约 11KB，远超 4KB 管道缓冲 ----
        // 老写法（WaitForExit() 之后再 ReadToEnd()）在这里会死锁；异步事件读必须能读完。
        H.Run("Runner.LargeStdOutDoesNotDeadlock", delegate {
            RunResult r = SevenZipRunner.Run(TestEnv.SevenZip, new string[] { "i", "-p" }, null, CancellationToken.None);
            AssertEq(r.ExitCode, 0);
            AssertTrue(r.StdOut != null && r.StdOut.Length > 4000); });

        // ---- I5 的机械保证：stdin 被重定向并立刻关闭 ----
        // 这条命令实测会让 7z 打印 "Enter password (will not be echoed):"（a 命令 + 空 -p）。
        // stdin 关掉后提示读到 EOF，7z 立刻以非 0 退出（26.01 实测 255），绝不挂起等待输入。
        H.Run("Runner.PasswordPromptWithClosedStdinDoesNotHang", delegate {
            long elapsedMs;
            string[] args = new string[] {
                "a", "-tzip", TestEnv.TmpFile("prompt-never.zip"), TestEnv.AesZip, "-p", "-y" };
            RunResult r = RunBounded(20000, args, out elapsedMs);
            AssertTrue(elapsedMs < 20000);
            AssertTrue(r != null && r.ExitCode != 0); });

        // ---- 进度回调：真实 7z 输出 -> 按 \r/\n 切分 -> ParseProgressLine -> 回调 ----
        H.Run("Runner.ProgressCallbackReceivesPercentAndMember", delegate {
            string seed = MakeSeed("progress-seed.bin", 4);          // 不可压缩数据，-mx9 需要约 2 秒
            string archive = TestEnv.TmpFile("progress.zip");
            object gate = new object();
            List<int> percents = new List<int>();
            List<string> members = new List<string>();
            Action<int, string> onProgress = delegate(int percent, string member) {
                lock (gate) { percents.Add(percent); members.Add(member); }
            };

            RunResult r = SevenZipRunner.Run(TestEnv.SevenZip, SlowAddArgs(seed, archive), onProgress, CancellationToken.None);

            string problem = "";
            lock (gate) {
                if (percents.Count == 0) { problem = "一次都没有收到命名进度行"; }
                else {
                    for (int i = 0; i < percents.Count; i++) {
                        if (members[i] != "progress-seed.bin") { problem = "成员名 [" + members[i] + "]"; break; }
                        if (percents[i] < 0 || percents[i] > 100) { problem = "百分比越界 [" + percents[i] + "]"; break; }
                    }
                    if (problem.Length == 0) {
                        int max = 0; foreach (int p in percents) { if (p > max) { max = p; } }
                        if (max == 0) { problem = "只收到 0%（没有任何真实进度）"; }
                    }
                }
            }
            AssertTrue(SevenZipRunner.IsSuccess(r.ExitCode));
            AssertEq(problem, ""); });

        // ---- 取消：ct 触发 TerminateJobObject，7z 必须当场死掉 ----
        H.Run("Runner.CancellationTokenTerminatesRunningChild", delegate {
            string seed = MakeSeed("cancel-seed.bin", 16);           // 完整压缩约 7 秒
            string archive = TestEnv.TmpFile("cancel.zip");
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.CancelAfter(400);

            Stopwatch sw = Stopwatch.StartNew();
            RunResult r = SevenZipRunner.Run(TestEnv.SevenZip, SlowAddArgs(seed, archive), null, cts.Token);
            sw.Stop();

            AssertTrue(sw.Elapsed.TotalSeconds < 2.5);               // 远小于完整压缩所需时间
            AssertTrue(r.ExitCode != 0); });                         // 取消的退出码绝不算成功

        // ---- TerminateAll：Job Object 真的杀掉子进程，系统里不留孤儿 ----
        H.Run("Runner.TerminateAllKillsRunningChildWithoutOrphan", delegate {
            string seed = MakeSeed("kill-seed.bin", 16);
            string archive = TestEnv.TmpFile("kill.zip");
            RunResult r = null;
            Thread worker = new Thread(new ThreadStart(delegate {
                r = SevenZipRunner.Run(TestEnv.SevenZip, SlowAddArgs(seed, archive), null, CancellationToken.None);
            }));
            worker.IsBackground = true;                              // 万一失败也不拖住测试进程退出
            worker.Start();

            int pid = WaitForSevenZipProcess(6000);                  // 此刻系统上只有这个子进程
            AssertTrue(pid != 0);

            Stopwatch sw = Stopwatch.StartNew();
            SevenZipRunner.TerminateAll();
            AssertTrue(worker.Join(5000));
            sw.Stop();

            AssertTrue(sw.Elapsed.TotalSeconds < 2.5);
            AssertTrue(r != null && r.ExitCode != 0);
            AssertFalse(IsSevenZipProcessAlive(pid, 2000));           // 子进程必须真的从系统里消失
        });
    }

    // ---------------- 辅助 ----------------

    private static int Check(string line, out int percent)
    {
        string member;
        SevenZipRunner.ParseProgressLine(line, out percent, out member);
        return percent;
    }

    // 在后台线程上有界地跑一次 Run：返回 null 表示超时（用例应据此 FAIL）。
    private static RunResult RunBounded(int timeoutMs, string[] args, out long elapsedMs)
    {
        RunResult result = null;
        Thread worker = new Thread(new ThreadStart(delegate {
            result = SevenZipRunner.Run(TestEnv.SevenZip, args, null, CancellationToken.None);
        }));
        worker.IsBackground = true;

        Stopwatch sw = Stopwatch.StartNew();
        worker.Start();
        bool finished = worker.Join(timeoutMs);
        sw.Stop();
        elapsedMs = sw.ElapsedMilliseconds;

        return finished ? result : null;
    }

    // 一条真正耗时的 7z 命令：-mx9 压缩不可压缩数据（实测 4MB ≈ 2 秒、16MB ≈ 7 秒）。
    // 密码必须是真值：Runner 的 -p 强制只保证「有 -p」，而 7z 的 a 命令在 -p 为空值时会弹提示。
    private static string[] SlowAddArgs(string seedPath, string archivePath)
    {
        return new string[] {
            "a", "-tzip", "-mx9", "-mmt=1", "-bsp1", archivePath, seedPath, "-pSECRET", "-mem=AES256", "-y"
        };
    }

    // 在 Tmp 下造一个 megabytes 大小的伪随机（不可压缩）文件。H.Run 每用例清空 Tmp，故每次重建。
    private static string MakeSeed(string name, int megabytes)
    {
        string path = TestEnv.TmpFile(name);
        byte[] chunk = new byte[1024 * 1024];
        Random rng = new Random(20261002);                            // 固定种子：内容可复现
        FileStream fs = File.Create(path);
        try
        {
            for (int i = 0; i < megabytes; i++)
            {
                rng.NextBytes(chunk);
                fs.Write(chunk, 0, chunk.Length);
            }
        }
        finally
        {
            fs.Dispose();
        }
        return path;
    }

    // 抓当前正在跑的 7z 子进程 PID（只用于验证 Job Object 真的杀掉了它）。
    private static int WaitForSevenZipProcess(int timeoutMs)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Process[] found = Process.GetProcessesByName("7z");
            try
            {
                if (found.Length > 0) { return found[0].Id; }
            }
            finally
            {
                foreach (Process p in found) { p.Dispose(); }
            }
            Thread.Sleep(50);
        }
        return 0;
    }

    private static bool IsSevenZipProcessAlive(int pid, int settleMs)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (true)
        {
            Process[] found = Process.GetProcessesByName("7z");
            bool alive = false;
            try
            {
                foreach (Process p in found) { if (p.Id == pid) { alive = true; } }
            }
            finally
            {
                foreach (Process p in found) { p.Dispose(); }
            }

            if (!alive) { return false; }
            if (sw.ElapsedMilliseconds >= settleMs) { return true; }
            Thread.Sleep(50);
        }
    }
}
