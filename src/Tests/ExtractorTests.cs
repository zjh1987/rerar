// Task 10 单元测试：Preflight + Extractor 核心编排（I1、I2，以及 I3/I4/I5 的端到端回归）。
//
// 前 10 条用例的名字逐字来自 task-10-brief.md Step 1。其余用例覆盖本轮任务书里点名的安全要求：
//   * I1：ListingFailed == true ⇒ **无论条目表是否非空**都没有可用基线（截断的包会给出
//         「失败 + 非空半截清单」，拿那个偏低的基线去比对会让同样截断的解压结果看起来「完整」）；
//         FileCount == 0 ⇒ 退回 x 尾部汇总（单流格式只有 Size: 必然出现）；绝不只看退出码。
//   * I2：写入必须先落同卷空暂存目录再改名提交；被同名**文件**占用时中止该归档（不回落父目录）。
//   * I3：删除默认关；只有「完成且校验通过」才可删；CompletedWithFailures/跳过/失败一律不删；
//         回收站删完必须核实，核实不到就如实报「已永久删除」。
//   * I4：门控拒绝（容器文档）的归档绝不递归、绝不删除原包。
//   * I5：候选密码一律用 `t`（或头部加密时的 `l`）验证，绝不用 `x`；只在验证成功后缓存。
//   * Review Focus #1：预检 + 运行中轮询可用空间，低水位时干净中止（原包保留、绝不提交半成品）。
//
// 全部用例都是「端到端跑一遍 Extractor」或「直接喂纯函数」两种形状之一，没有 mock 掉被测逻辑。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Rerar.Core;

internal sealed class ExtractorTests : TestBase
{
    public static void Run()
    {
        // ==================================================================
        // brief Step 1 的 10 条（名字逐字）
        // ==================================================================

        H.Run("Extract.NestedZipFullyExtracted", delegate {
            RunSummary s = TestEnv.RunExtract(TestEnv.NestedZip);
            AssertEq(s.Results[0].Status, ArchiveStatus.Completed);
            AssertTrue(File.Exists(TestEnv.OutOf(TestEnv.NestedZip, "inner", "hello.txt")));
            AssertTrue(File.Exists(TestEnv.NestedZip));            // I3：默认不删
        });

        H.Run("Extract.CorruptKeepsOriginal", delegate {            // 回归 ①（失败仍删包）
            RunSummary s = TestEnv.RunExtract(TestEnv.CorruptZip);
            AssertEq(s.Results[0].Status, ArchiveStatus.Failed);
            AssertTrue(File.Exists(TestEnv.CorruptZip));
        });

        H.Run("Extract.AesKeepsOriginalAndReportsNeedsPassword", delegate {   // 回归 ①②
            RunSummary s = TestEnv.RunExtract(TestEnv.AesZip);
            AssertEq(s.Results[0].Status, ArchiveStatus.SkippedNeedsPassword);
            AssertTrue(File.Exists(TestEnv.AesZip));
            // 候选全部经 `t` 验证、没有一个成功 ⇒ 根本没有进过暂存目录（错的 x 会写出大量垃圾）。
            AssertFalse(Directory.Exists(TestEnv.OutOf(TestEnv.AesZip)));
        });

        H.Run("Extract.DocxIsNotRecursed", delegate {               // I4
            RunSummary s = TestEnv.RunExtract(TestEnv.Docx);
            AssertEq(s.Results[0].Status, ArchiveStatus.SkippedContainer);
            AssertTrue(File.Exists(TestEnv.Docx));
        });

        H.Run("Extract.NonEmptyTargetGetsNumberedName", delegate {  // I2
            RunSummary s = TestEnv.RunExtractWithExistingTarget(TestEnv.PlainZip);
            AssertTrue(s.Results[0].OutputDir.EndsWith("(2)"));
            AssertTrue(File.Exists(TestEnv.OutOf(TestEnv.PlainZip, "a.txt")) == false);
            AssertTrue(File.Exists(Path.Combine(s.Results[0].OutputDir, "a.txt")));   // 落在 (2) 里
        });

        H.Run("Extract.LeavesNoReparsePoint", delegate {            // 实测 7z 会按 tar 条目建软链
            TestEnv.RunExtract(TestEnv.SymlinkTar);
            AssertFalse(TestEnv.AnyReparsePointUnder(TestEnv.OutRoot));
            AssertTrue(File.Exists(TestEnv.SymlinkTar));            // 安全断言失败也绝不删原包
        });

        H.Run("Extract.CorrectPasswordSucceeds", delegate {         // §6.5 候选阶梯第 1 层
            RunSummary s = TestEnv.RunExtractWithPassword(TestEnv.AesZip, "SECRET");
            AssertEq(s.Results[0].Status, ArchiveStatus.Completed);
            AssertTrue(File.Exists(TestEnv.AesZip));
            AssertTrue(s.Results[0].Files > 0);
        });

        H.Run("Extract.WrongPasswordLeavesNoGarbage", delegate {    // 候选须经 t 验证，不得凭「有文件出现」缓存
            RunSummary s = TestEnv.RunExtractWithPassword(TestEnv.AesZip, "WRONG");
            AssertEq(s.Results[0].Status, ArchiveStatus.SkippedNeedsPassword);
            AssertFalse(Directory.Exists(Path.Combine(TestEnv.OutRoot, "aes")));
        });

        H.Run("Extract.AbortsWhenFreeSpaceBelowThreshold", delegate {   // Review Focus #1（预检）
            RunSummary s = TestEnv.RunExtractWithFakeDisk(1024, TestEnv.BigSevenZip);
            AssertEq(s.Results[0].Status, ArchiveStatus.Failed);
            AssertTrue(s.FatalReason != null && s.FatalReason.Contains("空间"));
            AssertTrue(File.Exists(TestEnv.BigSevenZip));           // 致命中止也绝不删原包
        });

        H.Run("Extract.DepthLimitListsUnprocessed", delegate {
            RunSummary s = TestEnv.RunExtractWithDepth(1, TestEnv.DeepNestedZip);
            AssertTrue(s.Results.Exists(delegate(ArchiveResult r) { return r.Status == ArchiveStatus.NotAttemptedDepthLimit; }));
            AssertTrue(File.Exists(TestEnv.DeepNestedZip));
        });

        // ==================================================================
        // 本轮任务书点名的其余安全要求
        // ==================================================================

        // Review Focus #1 的「运行中」那一半：预检时空间充足，之后掉到低水位以下 ⇒
        // 干净中止（不提交半成品、不删原包、整批以致命原因收尾）。
        //
        // 本用例走的是**确定性**的那条入口：低水位轮询在「预检通过 → 真正开始解压」之间同步复检
        // 一次（DiskPollSeconds=0 时后台轮询也在跑同一段代码），因此序列 {充足, 不足} 必然在这里被
        // 抓住，不依赖解压耗时与定时器精度。
        // 「解压进行中被打断（退出码 1223 → 走同一条不提交路径）」由本轮的一次性探针实测确认
        // （见 task-10-report.md 的探针节：16 MB 归档上定时器在解压途中触发，判词为「已在解压中途中止」）。
        H.Run("Extract.AbortsWhenDiskDropsBeforeExtraction", delegate {
            RunSummary s = TestEnv.RunExtractWithDroppingDisk(TestEnv.BigSevenZip, "SECRET", 10L * 1024 * 1024 * 1024, 1024, 0);
            AssertEq(s.Results[0].Status, ArchiveStatus.Failed);
            AssertTrue(s.FatalReason != null && s.FatalReason.Contains("空间"));
            AssertFalse(Directory.Exists(TestEnv.OutOf(TestEnv.BigSevenZip)));     // 半成品绝不当成提交成功
            AssertTrue(File.Exists(TestEnv.BigSevenZip));
        });

        // I1 的危险方向：截断的包。实测 `l` 对截断 zip 会失败（退出码 2）却给出**非空**的半截条目表；
        // 若拿那份偏低的基线去比对，同样截断的解压结果就会「看起来完整」→ 提交 → 开删除即丢原包。
        // 这里必须落到 Failed（7z 自己的退出码 >= 2 同样不允许判成功）。
        H.Run("Extract.TruncatedZipIsNotReportedComplete", delegate {
            string truncated = TruncatedPlainZip();
            RunSummary s = TestEnv.RunExtract(truncated);
            AssertEq(s.Results[0].Status, ArchiveStatus.Failed);
            AssertTrue(File.Exists(truncated));
            AssertTrue(s.Results[0].OutputDir.Length == 0 || !Directory.Exists(s.Results[0].OutputDir));
        });

        // 分卷集：交给 7-Zip 的必须是权威成员（.001），且解压成功。密码 SECRET（夹具是加密 7z 分卷）。
        H.Run("Extract.SplitVolumeSetExtracted", delegate {
            RunSummary s = TestEnv.RunExtractWithPassword(TestEnv.SplitVolume7z, "SECRET");
            AssertEq(s.Results[0].Status, ArchiveStatus.Completed);
            AssertTrue(File.Exists(TestEnv.OutOf(TestEnv.SplitVolume7z, "big.bin")));
            AssertTrue(File.Exists(TestEnv.SplitVolume7z));
        });

        // 缺卷：报**具体缺哪一个**（规格 §6.6），不误报「损坏」，且什么都不删。
        H.Run("Extract.MissingVolumeReportedByName", delegate {
            string first = CopySplitVolumeMinusOne();
            RunSummary s = TestEnv.RunExtract(Path.Combine(Path.GetDirectoryName(first), "vol.7z.001"));
            AssertEq(s.Results[0].Status, ArchiveStatus.SkippedUnreadable);
            AssertTrue(s.Results[0].Message.Contains("vol.7z.002"));
            AssertTrue(File.Exists(Path.Combine(Path.GetDirectoryName(first), "vol.7z.001")));
        });

        // I3：删除开着也绝不删「失败」的原包。
        H.Run("Extract.FailedArchiveNeverDeleted", delegate {
            RunSummary s = TestEnv.RunExtractWithDelete(TestEnv.CorruptZip);
            AssertEq(s.Results[0].Status, ArchiveStatus.Failed);
            AssertTrue(File.Exists(TestEnv.CorruptZip));
        });

        // I3/I4：门控拒绝（容器文档）的归档，删除开着也绝不删。
        H.Run("Extract.ContainerDocumentNeverDeleted", delegate {
            RunSummary s = TestEnv.RunExtractWithDelete(TestEnv.Docx);
            AssertEq(s.Results[0].Status, ArchiveStatus.SkippedContainer);
            AssertTrue(File.Exists(TestEnv.Docx));
        });

        // I3：需密码的归档，删除开着也绝不删（原脚本的 ①② defect 就是在这里丢文件的）。
        H.Run("Extract.NeedsPasswordNeverDeleted", delegate {
            RunSummary s = TestEnv.RunExtractWithDelete(TestEnv.AesZip);
            AssertEq(s.Results[0].Status, ArchiveStatus.SkippedNeedsPassword);
            AssertTrue(File.Exists(TestEnv.AesZip));
        });

        // I3：完成 + 校验通过 ⇒ 才允许删除；删除后必须核实（回收站或隔离文件夹，二者都算处置）。
        // 用 Tmp 里的**副本**，绝不动任何 fixture（fixture 被删会让后续用例失去输入）。
        // 判词的具体期望随本机 Plan 走：卷能回收时必须说「回收站」，只能隔离时必须说「隔离」——
        // 这样这条用例在任何机器上都钉住「处置方式与 Plan 一致」，而不是只钉「文件没了」。
        H.Run("Extract.DeletesOnlyCompletedOriginal", delegate {
            string target = CopyToTmp(TestEnv.PlainZip, "delme.zip");

            string why;
            DeletePlan plan = RecycleBinGuard.Plan(target, out why);

            RunSummary s = TestEnv.RunExtractWithDelete(target);
            AssertEq(s.Results[0].Status, ArchiveStatus.Completed);
            AssertFalse(File.Exists(target));                       // 已处置（回收站或隔离）
            if (plan == DeletePlan.Recycle)
            {
                AssertTrue(s.Results[0].Message.Contains("回收站"));
            }
            else if (plan == DeletePlan.Quarantine)
            {
                AssertTrue(s.Results[0].Message.Contains("隔离"));
            }
            else
            {
                // Plan 说连删除都不安全时，文件必须还在原地（Refuse 是非破坏性的）。
                AssertTrue(File.Exists(target));
            }
        });

        // I3 的隔离分支：本机 subst 出来的「没有 $Recycle.Bin 的固定卷」上，Plan 必然给 Quarantine
        // ⇒ 原包必须被移动到同卷 `_originals_<时间戳>\`，不许永久删除、也不许留在原地。
        H.Run("Extract.QuarantinesWhenVolumeNotRecyclable", delegate {
            string root;
            try { root = TestEnv.NoRecycleBinVolumeRoot; }
            catch (InvalidOperationException ex)
            {
                // 造不出 subst 卷（没有空闲盘符 / subst 不可用）时如实记一条 SKIPPED，绝不假 PASS。
                H.Skip("Extract.QuarantinesWhenVolumeNotRecyclable", ex.Message);
                return;
            }

            try
            {
                string target = CopyToDir(TestEnv.PlainZip, root, "quarantine_me.zip");
                RunSummary s = TestEnv.RunExtractWithDelete(target);
                AssertEq(s.Results[0].Status, ArchiveStatus.Completed);
                AssertFalse(File.Exists(target));

                string[] originals = Directory.GetDirectories(root, "_originals_*");
                AssertTrue(originals.Length > 0);
                AssertTrue(File.Exists(Path.Combine(originals[0], "quarantine_me.zip")));
            }
            finally
            {
                TestEnv.ReleaseNoRecycleBinVolume();
            }
        });

        // Review Focus #5：不可读目标逐项记为 SkippedUnreadable（带原因），不得崩溃、不得静默跳过。
        H.Run("Extract.UnreadableTargetReportedPerItem", delegate {
            string missing = TestEnv.TmpFile("no-such-archive.zip");
            RunSummary s = TestEnv.RunExtract(missing);
            AssertEq(s.Results.Count, 1);
            AssertEq(s.Results[0].Status, ArchiveStatus.SkippedUnreadable);
            AssertTrue(s.Results[0].Message.Length > 0);
            AssertTrue(s.FatalReason == null);
        });

        // 规格 §6.11 的**默认**布局：不指定输出根时产物落在归档所在目录（原地）。
        // 其余用例都显式指定 OutRoot，所以这条是「原地」这个默认行为唯一的钉子。
        H.Run("Extract.InPlaceOutputWhenNoRoot", delegate {
            string target = CopyToTmp(TestEnv.PlainZip, "inplace.zip");
            RunSummary s = TestEnv.RunExtractInPlace(target);
            AssertEq(s.Results[0].Status, ArchiveStatus.Completed);
            AssertEq(s.Results[0].OutputDir, TestEnv.TmpFile("inplace"));
            AssertTrue(File.Exists(Path.Combine(TestEnv.TmpFile("inplace"), "a.txt")));
            AssertTrue(File.Exists(target));                      // 原包保留
        });

        // 取消：预置一个已取消的令牌 ⇒ 连试都不试（如实列进 NotAttempted），运行级标成 Cancelled，
        // **绝不当成成功、绝不留下输出、绝不删原包**。
        // （ArchiveStatus 里没有 Cancelled 成员 —— 那是 Task 8 定下的契约 —— 所以「取消」由
        //   RunSummary.Cancelled 承载；不变式 I3 的「取消的运行绝不删原包」由此结构性保证：
        //   取消路径只写结果，从不进入删除环节。）
        H.Run("Extract.CancelledRunKeepsOriginal", delegate {
            RunSummary s = TestEnv.RunExtractCancelled(TestEnv.NestedZip);
            AssertTrue(s.Cancelled);
            AssertEq(s.Results.Count, 0);
            AssertEq(s.NotAttempted.Count, 1);
            AssertFalse(Directory.Exists(TestEnv.OutOf(TestEnv.NestedZip)));
            AssertTrue(File.Exists(TestEnv.NestedZip));
        });

        // ==================================================================
        // 纯函数：I1 的基线规则（这些形状真归档造不出来，只能直接喂索引）
        // ==================================================================

        // ListingFailed == true ⇒ 没有基线，**与 FileCount 无关**（哪怕条目表非空、哪怕 x 有汇总）。
        H.Run("Extract.BaselineRejectsFailedListingWithEntries", delegate {
            ArchiveIndex index = Index(5, 100, true, false);
            int files; long bytes; bool counts;
            AssertFalse(Preflight.TryGetBaseline(index, "Files: 5\r\nSize: 100\r\n", out files, out bytes, out counts));
        });

        // 成功但 0 条目（单流格式）⇒ 退回 x 尾部汇总，且只有字节可用（files == 0 是「没被计数」）。
        H.Run("Extract.BaselineFallsBackToByteSummary", delegate {
            ArchiveIndex index = Index(0, 0, false, false);
            int files; long bytes; bool counts;
            AssertTrue(Preflight.TryGetBaseline(index, "\r\nSize:       3\r\nCompressed: 38\r\n", out files, out bytes, out counts));
            AssertEq(bytes, 3L);
            AssertFalse(counts);
        });

        // 可用索引 ⇒ 条目数与总字节都要比。
        H.Run("Extract.BaselineUsesIndexCountAndBytes", delegate {
            ArchiveIndex index = Index(4, 30, false, false);
            int files; long bytes; bool counts;
            AssertTrue(Preflight.TryGetBaseline(index, null, out files, out bytes, out counts));
            AssertEq(files, 4);
            AssertEq(bytes, 30L);
            AssertTrue(counts);
        });

        // 长路径预检（规格 §10.2：v1.0 用「预检拒绝 + 明确提示」兜住）。
        H.Run("Extract.PreflightRejectsOverlongEntryPath", delegate {
            ArchiveIndex index = Index(1, 1, false, false);
            index.Entries[0].Path = "deep\\entry.txt";

            string shortRoot = Path.Combine(TestEnv.OutRoot, "out");
            AssertTrue(Preflight.FindOverlongEntry(shortRoot, index) == null);

            string longRoot = @"C:\" + new string('x', 300);
            AssertEq(Preflight.FindOverlongEntry(longRoot, index), "deep\\entry.txt");
        });

        // 条目重名（大小写不敏感）预检：Readme.txt 与 README.TXT 在 Windows 上会互相覆盖，
        // 必须提前拒绝（规格 §9.1 用例 16「不静默覆盖」）。
        H.Run("Extract.PreflightRejectsCaseInsensitiveDuplicateEntries", delegate {
            ArchiveIndex ok = Index(0, 0, false, false);
            ok.Entries.Add(Entry("a.txt", 1));
            ok.Entries.Add(Entry("b.txt", 1));
            AssertTrue(Preflight.FindConflictingEntries(ok) == null);

            ArchiveIndex clash = Index(0, 0, false, false);
            clash.Entries.Add(Entry("Readme.txt", 1));
            clash.Entries.Add(Entry("README.TXT", 1));
            AssertEq(Preflight.FindConflictingEntries(clash), "README.TXT");
        });

        // 加密探针：只认 `Encrypted = +` 这条**键值行**。实测坑：截断 zip 的清单里有
        // `Characteristics = Local : Encrypt`，按子串 "Encrypt" 判会把损坏包误判成加密包。
        H.Run("Extract.PreflightDetectsEncryptionMarkerOnlyAsKeyValue", delegate {
            AssertTrue(Preflight.MentionsEncryption("Path = a.txt\r\nEncrypted = +\r\n"));
            AssertFalse(Preflight.MentionsEncryption("Path = a.txt\r\nEncrypted = -\r\n"));
            AssertFalse(Preflight.MentionsEncryption("Characteristics = Local : Encrypt\r\n"));
            AssertFalse(Preflight.MentionsEncryption(null));
        });

        // 「需密码」只在「清单因加密而读不出来」时为真：损坏包（无加密特征）不算需密码。
        H.Run("Extract.PreflightNeedsPasswordOnlyForEncryptedListing", delegate {
            AssertTrue(Preflight.NeedsPassword(Index(0, 0, true, true)));
            AssertFalse(Preflight.NeedsPassword(Index(3, 9, true, false)));   // 损坏：失败但不是加密
            AssertFalse(Preflight.NeedsPassword(Index(3, 9, false, false)));  // 清单读得出来
        });
    }

    // ---------------- 测试内的小工具 ----------------

    // 截断的 zip：把 PlainZip 的字节砍掉尾部（中央目录没了）。这是 I1 最危险的那种形状 ——
    // 7-Zip 可能给出「失败 + 非空半截清单」。写进 Tmp，绝不动 fixture 自己。
    private static string TruncatedPlainZip()
    {
        byte[] bytes = File.ReadAllBytes(TestEnv.PlainZip);
        int keep = bytes.Length / 2;
        byte[] head = new byte[keep];
        Array.Copy(bytes, head, keep);

        string path = TestEnv.TmpFile("truncated.zip");
        File.WriteAllBytes(path, head);
        return path;
    }

    // 把 fixture 复制到 Tmp 下一个名字（删除类用例只动副本）。
    private static string CopyToTmp(string source, string name)
    {
        string path = TestEnv.TmpFile(name);
        File.Copy(source, path, true);
        return path;
    }

    private static string CopyToDir(string source, string dir, string name)
    {
        string path = Path.Combine(dir, name);
        File.Copy(source, path, true);
        return path;
    }

    // 把分卷夹具复制到 Tmp 下的一份副本里并删掉 .002：缺卷必须被点名报出，且什么都不删。
    private static string CopySplitVolumeMinusOne()
    {
        string sourceDir = Path.GetDirectoryName(TestEnv.SplitVolume7z);
        string targetDir = Path.Combine(TestEnv.Tmp, "missingvol");
        if (Directory.Exists(targetDir)) { Directory.Delete(targetDir, true); }
        Directory.CreateDirectory(targetDir);

        string first = null;
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string copy = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, copy, true);
            if (first == null) { first = copy; }
        }
        File.Delete(Path.Combine(targetDir, "vol.7z.002"));
        return first;
    }

    private static ArchiveIndex Index(int fileCount, long totalBytes, bool listingFailed, bool encryptedHeaders)
    {
        ArchiveIndex index = new ArchiveIndex();
        index.FileCount = fileCount;
        index.TotalBytes = totalBytes;
        index.ListingFailed = listingFailed;
        index.HasEncryptedHeaders = encryptedHeaders;
        index.ExitCode = listingFailed ? 2 : 0;
        for (int i = 0; i < fileCount; i++)
        {
            IndexEntry entry = new IndexEntry();
            entry.Path = "f" + i + ".bin";
            entry.Size = 1;
            index.Entries.Add(entry);
        }
        return index;
    }

    private static IndexEntry Entry(string path, long size)
    {
        IndexEntry entry = new IndexEntry();
        entry.Path = path;
        entry.Size = size;
        return entry;
    }
}
