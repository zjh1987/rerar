// Task 4 单元测试：SevenZipIndex（清单解析与完整性基线）。
//
// 开头 4 条用例的名字与期望值逐字来自 task-4-brief.md Step 1，只按 harness 约定加了
// TestEnv. 前缀与排版。两处按控制方裁定 / brief 自身矛盾做的调整，均在下面就地注明：
//   1) HeaderEncryptedRar → HeaderEncrypted7z：7-Zip 造不出 rar（只有 WinRAR 能），
//      用「头部加密的 7z」复现同一个歧义；
//   2) TryParseSummary 在 brief 的接口里是 3 个 out（folders/files/size），而 Step 1 的
//      示例只传 2 个 out（f/s）—— 签名以 Produces 为准，示例的 f 落在 files 上，期望值不变。
//
// 其余用例覆盖 brief Step 1 没有触碰、但 I1 基线最容易被算错的形状（本机 7-Zip 26.01 实测）：
// 空清单、被截断的最后一块、缺 Size 的目录条目、成员名里带 " = "，以及
// 「损坏包 ≠ 头部加密」这条歧义消解的负例。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Threading;
using Rerar.Core;

internal sealed class SevenZipIndexTests : TestBase
{
    public static void Run()
    {
        // ---- brief Step 1 的 4 条（用例名与期望值逐字照抄）----
        H.Run("Index.ReadsEntryCountAndBytes", delegate {
            ArchiveIndex ix = SevenZipIndex.Read(TestEnv.SevenZip, TestEnv.TwoFileZip, null);
            AssertEq(ix.FileCount, 2); AssertTrue(ix.TotalBytes > 0);
            // 两个种子分别是 3 字节与 5 字节：总字节必须精确等于 8，而不是仅仅「> 0」。
            // （I1 的比对靠这两个数字，差一个字节就会把半成品判成完整。）
            AssertEq(ix.TotalBytes, 8L);
            AssertEq(ix.Entries.Count, 2);                       // 归档属性段（Path = 包自己）不算条目
            AssertFalse(ix.HasEncryptedHeaders); });

        H.Run("Index.DetectsReparsePointEntry", delegate {
            ArchiveIndex ix = SevenZipIndex.Read(TestEnv.SevenZip, TestEnv.SymlinkTar, null);
            AssertTrue(ix.Entries.Exists(delegate(IndexEntry e) { return e.IsReparsePoint; })); });

        H.Run("Index.EncryptedHeadersReported", delegate {
            ArchiveIndex ix = SevenZipIndex.Read(TestEnv.SevenZip, TestEnv.HeaderEncrypted7z, "wrong");
            AssertTrue(ix.HasEncryptedHeaders); });

        H.Run("Index.ParsesExtractSummary", delegate {
            int d; int f; long s;
            string o = "Folders: 1\nFiles: 3\nSize:       12582922\nCompressed: 12583932";
            AssertTrue(SevenZipIndex.TryParseSummary(o, out d, out f, out s));
            AssertEq(d, 1); AssertEq(f, 3); AssertEq(s, 12582922L); });

        // ---- 头部加密 + 正确密码：条目正常列出，且归档属性段（Path = 包自己）不被当成条目 ----
        // 同时钉住「只有 l 失败时才谈 HasEncryptedHeaders」这条歧义消解的分界。
        H.Run("Index.HeaderEncryptedWithCorrectPasswordListsEntries", delegate {
            ArchiveIndex ix = SevenZipIndex.Read(TestEnv.SevenZip, TestEnv.HeaderEncrypted7z, "SECRET");
            AssertEq(ix.FileCount, 1);
            AssertEq(ix.Entries.Count, 1);
            AssertTrue(ix.TotalBytes > 0); });

        // ---- 负例：损坏包（截断的 zip）绝不能被当成「头部加密」----
        // 这正是 HasEncryptedHeaders 存在的意义：只有加密特征串才让它为真。
        H.Run("Index.CorruptArchiveIsNotMistakenForEncryptedHeaders", delegate {
            ArchiveIndex ix = SevenZipIndex.Read(TestEnv.SevenZip, TestEnv.CorruptZip, null);
            AssertFalse(ix.HasEncryptedHeaders);
            // 【Finding 3】失败必须能被看见：损坏包同样是 0 条目 / 0 字节，但它是「没有基线」。
            AssertTrue(ix.ListingFailed);
            AssertEq(ix.ExitCode, 2); });

        // ---- 空清单：`l` 成功但没有任何带 Path 的条目，必须得到「0 条目、0 字节」----
        // 【Finding 3】必须走 Read，不能直接喂 ParseListing：HasEncryptedHeaders 只有 Read 才会计算，
        // 直接喂解析器时它恒为 false —— 原来的写法里那条断言永远不可能失败（等于没断言）。
        H.Run("Index.EmptyListingYieldsZeroCounts", delegate {
            // 单流格式（bz2）：`l` 成功（退出码 0），但条目块没有 Path 行 → 0 条目 / 0 字节。
            ArchiveIndex noEntries = SevenZipIndex.Read(TestEnv.SevenZip, TestEnv.SingleStreamBz2, null);
            AssertEq(noEntries.FileCount, 0);
            AssertEq(noEntries.TotalBytes, 0L);
            AssertEq(noEntries.Entries.Count, 0);
            AssertFalse(noEntries.HasEncryptedHeaders);

            // null 输入（调用方根本没拿到 stdout）也必须得到一个空索引，而不是崩溃。
            ArchiveIndex nothing = SevenZipIndex.ParseListing(null);
            string problem = "";
            if (nothing == null || nothing.Entries == null) { problem = "ParseListing(null) 返回 null 或 Entries 为 null"; }
            else if (nothing.FileCount != 0 || nothing.TotalBytes != 0 || nothing.Entries.Count != 0) { problem = "null 清单不为空"; }
            AssertEq(problem, "");

            // 合成的「有属性段、但条目段是空的」清单同样必须为空（这一段只覆盖解析器本身）。
            ArchiveIndex emptySection = SevenZipIndex.ParseListing(Listing(""));
            AssertEq(emptySection.FileCount, 0);
            AssertEq(emptySection.TotalBytes, 0L);
            AssertEq(emptySection.Entries.Count, 0); });

        // ---- 被截断的最后一块：stdout 末尾没有空行、甚至最后一个键写了一半 ----
        // 最后一个条目必须照样算进基线（少算 = Verifier 可能放过半成品）。
        H.Run("Index.TruncatedFinalBlockIsStillCounted", delegate {
            ArchiveIndex noTrailingNewline = SevenZipIndex.ParseListing(Listing(
                "Path = a.txt\nFolder = -\nSize = 10\n\nPath = b.txt\nFolder = -\nSize = 20"));   // 末尾无换行
            AssertEq(noTrailingNewline.FileCount, 2);
            AssertEq(noTrailingNewline.TotalBytes, 30L);

            ArchiveIndex cutMidKey = SevenZipIndex.ParseListing(Listing(
                "Path = c.txt\nFolder = -\nSize = 5\nSymbolic Lin"));                              // 键写到一半就断了
            AssertEq(cutMidKey.FileCount, 1);
            AssertEq(cutMidKey.TotalBytes, 5L); });

        // ---- 目录条目：算进 Entries，但绝不算进 FileCount / TotalBytes ----
        // 7z 对目录给的是 `Folder = +`（zip 另带 `Attributes = D`，目录的 Size 通常是 0）。
        // 第二个条目抄自 tar 的实测形状：只有 `Folder = +` 与 `Mode = d...`，**整行没有 Size、
        // 也没有 Attributes** —— 所以目录判定不能只靠 Attributes。
        H.Run("Index.DirectoryEntriesAreNotCountedAsFiles", delegate {
            ArchiveIndex ix = SevenZipIndex.ParseListing(Listing(
                "Path = d1\nFolder = +\nSize = 0\nAttributes = D\n\n" +
                "Path = d2\nFolder = +\nMode = drwxr-xr-x\n\n" +                // 没有 Size，也没有 Attributes
                "Path = d1\\inner.txt\nFolder = -\nSize = 7\nAttributes = A\n"));

            AssertEq(ix.Entries.Count, 3);
            AssertEq(ix.FileCount, 1);
            AssertEq(ix.TotalBytes, 7L);
            AssertTrue(ix.Entries[0].IsDirectory);
            AssertTrue(ix.Entries[1].IsDirectory);
            AssertEq(ix.Entries[1].Size, 0L);                                     // 缺 Size → 0
            AssertFalse(ix.Entries[2].IsDirectory); });

        // ---- 成员名里带 " = "：只按第一个 '=' 切分才能原样保留（实测 Path = eq = q.txt）----
        H.Run("Index.PathContainingEqualsSignIsPreserved", delegate {
            ArchiveIndex ix = SevenZipIndex.ParseListing(Listing("Path = eq = q.txt\nFolder = -\nSize = 4\n"));
            AssertEq(ix.FileCount, 1);
            AssertEq(ix.Entries[0].Path, "eq = q.txt");
            AssertEq(ix.Entries[0].Size, 4L); });

        // ---- 软链条目的两种真实写法：tar 的 Symbolic Link=… 与 7z 的 Attributes=AL ----
        // （本机实测：GNU tar 存软链 → Symbolic Link 有值、无 Attributes；7z -snl → Attributes=AL）
        H.Run("Index.ReparsePointDetectedFromEitherMarker", delegate {
            ArchiveIndex tarForm = SevenZipIndex.ParseListing(Listing(
                "Path = link.txt\nFolder = -\nSize = 28\nMode = lrwxrwxrwx\nSymbolic Link = /tmp/target.txt\n"));
            AssertTrue(tarForm.Entries[0].IsReparsePoint);

            ArchiveIndex sevenZipForm = SevenZipIndex.ParseListing(Listing(
                "Path = link.txt\nSize = 264\nAttributes = AL\n"));
            AssertTrue(sevenZipForm.Entries[0].IsReparsePoint);

            ArchiveIndex plainFile = SevenZipIndex.ParseListing(Listing(
                "Path = plain.txt\nFolder = -\nSize = 4\nSymbolic Link = \nAttributes = A\n"));
            AssertFalse(plainFile.Entries[0].IsReparsePoint); });

        // ---- 【Finding 1】分卷清单：7-Zip 对 `x.7z.001` 会打印**四个**段标记 ----
        // 实测形状：`--` / `----` / `--` / `----------`。中间那两个属性块里也有 `Path = `，
        // 一旦被当成条目，基线就会凭空多 2 个文件、多算一整个包的字节数 —— 于是所有分卷归档
        //（规格 §9 用例 6/7）都永远对不上磁盘，而 `.7z.001` 正是指挥部交给 7-Zip 的权威成员
        //（规格 §6.6 / 计划 Task 6）。这里走真 Read + 真分卷夹具（夹具自检保证它真的切开了），
        // 不用合成的 Listing() —— 那个辅助固定写两个标记，正是这条 bug 溜过去的原因。
        H.Run("Index.SplitVolumeListingHasNoPhantomEntries", delegate {
            ArchiveIndex ix = SevenZipIndex.Read(TestEnv.SevenZip, TestEnv.SplitVolume7z, null);

            AssertEq(ix.FileCount, 1);                   // 真相：包里只有 big.bin 一个文件
            AssertEq(ix.TotalBytes, 5120L);              // 且正好 5120 字节
            AssertEq(ix.Entries.Count, 1);               // 卷列表与包属性块都不是条目
            AssertEq(ix.Entries[0].Path, "big.bin");
            AssertFalse(ix.Entries[0].IsDirectory);
            AssertFalse(ix.HasEncryptedHeaders); });

        // ---- 【Finding 2】真实的 `t` 汇总：0 个目录时 7-Zip **不打印 `Folders:` 行** ----
        // 原实现要求 Folders:/Files:/Size: 三行齐全，于是在「本该靠汇总兜底」的归档上全部返回 false。
        H.Run("Index.ParsesSummaryWithoutFoldersLine", delegate {
            RunResult tested = RunSevenZip("t", TestEnv.TwoFileZip, "-pSECRET", "-y");
            AssertTrue(SevenZipRunner.IsSuccess(tested.ExitCode));

            // 形状自检：本用例存在的理由就是「真实输出里没有 Folders: 行」。若将来 7-Zip 开始打印它，
            // 这里当场说明「本用例不再是它要覆盖的形状」，而不是默默失去意义。
            if (tested.StdOut != null && tested.StdOut.IndexOf("Folders:", StringComparison.Ordinal) >= 0)
            {
                AssertEq("7-Zip 现在会打印 Folders: 行 —— 本用例已不再是「缺 Folders:」的形状", "");
            }

            int d; int f; long s;
            AssertTrue(SevenZipIndex.TryParseSummary(tested.StdOut, out d, out f, out s));
            AssertEq(d, 0);                              // 那一行不存在 → 0，而不是「整份汇总不可用」
            AssertEq(f, 2);                              // 真夹具：two-file-a.txt + two-file-b.txt
            AssertEq(s, 8L); });                         // 真夹具：3 + 5 字节

        // ---- 【Finding 2】单流格式：连 `Files:` 也不打印，只有 `Size:` 是必然出现的 ----
        // 这是 bz2/xz 唯一可用的基线（它们的 `-slt` 索引是 0 条目），所以它必须可用；
        // 同时钉住语义：7-Zip 不把这条无名流算成一个文件，所以 files 是 0 而不是 1。
        H.Run("Index.SingleStreamSummaryCarriesOnlyByteTotal", delegate {
            RunResult tested = RunSevenZip("t", TestEnv.SingleStreamBz2, "-p", "-y");
            AssertTrue(SevenZipRunner.IsSuccess(tested.ExitCode));

            int d; int f; long s;
            AssertTrue(SevenZipIndex.TryParseSummary(tested.StdOut, out d, out f, out s));
            AssertEq(f, 0);                              // 7-Zip 不把无名流算成文件
            AssertEq(d, 0);
            AssertEq(s, 3L); });                         // 解压后的真实字节数：单流格式唯一有意义的数

        // ---- 【Finding 3】「读不到清单」必须能被看见，否则 0/0 与真基线不可分 ----
        // 实测：bz2 / xz（`l` 成功但格式没有成员名）、非归档文本文件、被截断的 7z —— 这四种
        // 在读 `l` 的结果前完全同形（0 条目 / 0 字节）。前者的「0」是格式使然，后三者的「0」是
        // 压根没读到清单。计划 line 634 的判定是「条目数/字节比对」，若两者不可分，失败归档的
        // 「磁盘 0 个文件 == 索引 0 个文件」就会被读成「完整」→ 删除原包（本项目最怕的方向）。
        H.Run("Index.ListingFailureIsDistinguishableFromNamelessSingleStream", delegate {
            // 单流格式：`l` 成功、0 条目 —— 这不是失败，只是这个格式不存成员名。
            ArchiveIndex nameless = SevenZipIndex.Read(TestEnv.SevenZip, TestEnv.SingleStreamBz2, null);
            AssertEq(nameless.FileCount, 0);
            AssertEq(nameless.ExitCode, 0);
            AssertFalse(nameless.ListingFailed);
            AssertFalse(nameless.HasEncryptedHeaders);

            // 根本不是压缩包：`l` 失败（实测退出码 2），索引同样是 0 条目 —— 但这次是「没有基线」。
            string notArchive = TestEnv.MakeFile("not-an-archive.txt", "这不是压缩包，只是纯文本。\r\n");
            ArchiveIndex failed = SevenZipIndex.Read(TestEnv.SevenZip, notArchive, null);
            AssertEq(failed.FileCount, 0);
            AssertEq(failed.TotalBytes, 0L);
            AssertEq(failed.ExitCode, 2);
            AssertTrue(failed.ListingFailed);            // ← 没有这个字段，它与上面的 nameless 完全同形
            AssertFalse(failed.HasEncryptedHeaders);     // 它是失败了，但不是「缺密码」

            // 两张索引的条目数与字节数一模一样，唯一的区别就是 ListingFailed / ExitCode。
            AssertEq(failed.FileCount, nameless.FileCount);
            AssertEq(failed.TotalBytes, nameless.TotalBytes); });
    }

    // ---------------- 辅助 ----------------

    // 真跑一次 7-Zip 并把完整结果交给调用方：汇总是「x/t 尾部那一块」，它的形状必须是**真实**输出，
    // 合成文本不算证据（brief 的 Index.ParsesExtractSummary 用合成文本，这里补上真实输出这一层）。
    private static RunResult RunSevenZip(params string[] args)
    {
        return SevenZipRunner.Run(TestEnv.SevenZip, args, null, CancellationToken.None);
    }

    // 合成一份 `l -slt` 的 stdout：前言 + 归档属性段（Path = 包自己）+ 条目段标记，形状逐字抄自
    // 本机 7-Zip 26.01 的实测输出（只保留解析器关心的部分）。entriesBody 是条目段正文。
    private static string Listing(string entriesBody)
    {
        return "\n7-Zip 26.01 (x64) : Copyright (c) 1999-2026 Igor Pavlov : 2026-04-27\n" +
               "\nScanning the drive for archives:\n1 file, 310 bytes (1 KiB)\n" +
               "\nListing archive: two.zip\n\n" +
               "--\nPath = two.zip\nType = zip\nPhysical Size = 310\n\n" +
               "----------\n" + entriesBody;
    }
}
