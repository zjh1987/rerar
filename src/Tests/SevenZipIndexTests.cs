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
            AssertFalse(ix.HasEncryptedHeaders); });

        // ---- 空清单：没有任何输出 / 有归档但没有条目，都必须得到「0 条目、0 字节」----
        H.Run("Index.EmptyListingYieldsZeroCounts", delegate {
            ArchiveIndex nothing = SevenZipIndex.ParseListing(null);
            ArchiveIndex noEntries = SevenZipIndex.ParseListing(Listing(""));

            string problem = "";
            if (nothing == null || nothing.Entries == null) { problem = "ParseListing(null) 返回 null 或 Entries 为 null"; }
            else if (nothing.FileCount != 0 || nothing.TotalBytes != 0 || nothing.Entries.Count != 0) { problem = "null 清单不为空"; }
            else if (noEntries.FileCount != 0 || noEntries.TotalBytes != 0 || noEntries.Entries.Count != 0) { problem = "空条目段不为空"; }
            else if (nothing.HasEncryptedHeaders || noEntries.HasEncryptedHeaders) { problem = "无输出的清单被当成头部加密"; }
            AssertEq(problem, ""); });

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
    }

    // ---------------- 辅助 ----------------

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
