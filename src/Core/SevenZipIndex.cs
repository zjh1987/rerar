// Rerar 的归档清单解析（规格 §4.1「索引」、不变式 I1；审计 G1）。
//
// I1 规定「成功」只能由「磁盘实际结果 vs 本索引」的比对决定，绝不能用 7-Zip 的退出码代替
//（"Everything is Ok" 不代表每条都写出了）。于是本类算出的两个数字 —— FileCount / TotalBytes ——
// 就是整个工具的完整性基线：少算一个条目、少算一个字节，Verifier 就可能把只解了一半的归档判成
// 成功，而「提交并复核通过后删除原包」正站在这个结论上。所以解析对 7z 真实输出的畸形必须诚实。
//
// 本机实测（7-Zip 26.01，中文 Windows；Runner 已集中注入 -sccUTF-8）的 `l -slt` 形状：
//
//   <空行>
//   7-Zip 26.01 (x64) : Copyright (c) 1999-2026 Igor Pavlov : 2026-04-27
//   <空行>
//   Scanning the drive for archives:
//   1 file, 206 bytes (1 KiB)
//   <空行>
//   Listing archive: two.zip
//   <空行>
//   --                                  ← 归档属性段的开始标记（两个连字符）
//   Path = two.zip                      ← 这是「包自己」，不是条目
//   Type = zip
//   Physical Size = 310
//   <空行>
//   ----------                          ← 条目段的开始标记（十个连字符）
//   Path = a.txt
//   Folder = -
//   Size = 10
//   <空行>
//   Path = b.txt
//   ...
//
// 每条解析规则都对应一种实测到的畸形输出：
//   * 只有条目段标记之后的块才算条目。第一个标记后的「归档属性段」里也有 `Path = `（包自己），
//     当成条目会让 FileCount 永远多 1；错密码 / 损坏 / 非归档时 7z 根本不打印条目段标记。
//   * 归档属性段里可能混进 `ERRORS:` / `Unexpected end of archive`（实测：截断的 zip 会把
//     错误块写进 **stdout** 的属性段中间），所以判断依据只能是「标记位置」而不是「行像不像键值」。
//   * 键值只按**第一个** '=' 切分：成员名里带 " = " 是合法的（实测 `Path = eq = q.txt`），
//     用最后一个 '=' 或要求整行只有一个 '=' 都会把名字截断。
//   * 缺 `Size =`（目录、部分元数据缺失）按 0 处理，绝不因此丢掉条目；负数等畸形值同样按 0。
//   * 输入末尾没有空行（stdout 被截断、最后一个键写到一半）时，最后一块照样算数。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.Threading;

namespace Rerar.Core
{
    // 清单里的一个条目（brief 的 Produces 形状：public 字段，不是属性）。
    public sealed class IndexEntry
    {
        public string Path;
        public long Size;
        public bool IsDirectory;
        public bool IsReparsePoint;
    }

    // 一个归档的清单 + 完整性基线。
    //
    // FileCount / TotalBytes 只统计**文件**（非目录）条目，Entries 则包含目录在内的全部条目：
    // 7-Zip 自己的汇总也是分开报「Folders: N / Files: M / Size: S」（实测 `7z t` 尾部），
    // Verifier 拿这两个数字跟磁盘上的文件数、字节数比对；把目录算进 FileCount 会让
    // 「磁盘文件数」永远对不上（Every 目录都被算成一个文件）。
    public sealed class ArchiveIndex
    {
        public List<IndexEntry> Entries = new List<IndexEntry>();
        public long TotalBytes;
        public int FileCount;
        public bool HasEncryptedHeaders;
    }

    public static class SevenZipIndex
    {
        // 「失败原因是头部加密」的实测特征串（7-Zip 26.01，中文 Windows 下这些错误消息仍是英文）：
        //   ERROR: <包> : Cannot open encrypted archive. Wrong password?
        //   ERRORS: / Headers Error
        // 只认这两个片段：损坏包（`Open ERROR: Cannot open the file as [7z] archive` /
        // `Unexpected end of archive`）与非归档（`Is not archive`）都不含它们（实测），
        // 所以「缺密码 / 包坏了 / 根本不是包」这三件在 `l` 输出上无法区分的事不会被混为一谈。
        private static readonly string[] EncryptionMarkers = new string[] { "encrypted archive", "wrong password" };

        // 条目段标记的长度（实测 7-Zip 用十个连字符把归档属性段与条目段分开）。
        private const int EntriesSeparatorLength = 10;

        // 读一次清单。任何失败都**不抛异常**：返回一个（可能只有 HasEncryptedHeaders 为真的）索引，
        // 由调用方按 I1 的语义决定跳过还是重试（Task 10 的 SkippedNeedsPassword 就靠这个标志）。
        public static ArchiveIndex Read(string sevenZipPath, string archivePath, string password)
        {
            if (sevenZipPath == null) { throw new ArgumentNullException("sevenZipPath"); }
            if (archivePath == null) { throw new ArgumentNullException("archivePath"); }

            // I5：参数表里必须始终有 -p。拿不到密码时给一个**空的** -p：7z 于是立刻失败，
            // 而不是等着 stdin 上有人敲密码（-y 也压不住那个提示）。-y 让 7z 不问任何问题。
            // -sccUTF-8 由 SevenZipRunner 自己注入，这里不需要（也不该）重复传。
            string[] args = new string[] { "l", "-slt", archivePath, PasswordSwitch(password), "-y" };
            RunResult result = SevenZipRunner.Run(sevenZipPath, args, null, CancellationToken.None);

            ArchiveIndex index = ParseListing(result.StdOut);

            // 消解「头部加密」的歧义：`l` 失败 **且** 输出里有加密特征 → 缺密码，而不是包坏了。
            // stdout 与 stderr 都要看：实测加密错误在 stderr，而损坏包的 ERRORS: 块会混进 stdout。
            // 成功的 `l`（哪怕包里确实有加密的头部，例如用对了密码）绝不置这个标志：
            // 那时没有任何歧义要消解，也没有理由让调用方把它当成「需要密码」。
            if (!SevenZipRunner.IsSuccess(result.ExitCode) &&
                (MentionsEncryption(result.StdErr) || MentionsEncryption(result.StdOut)))
            {
                index.HasEncryptedHeaders = true;
            }

            return index;
        }

        // 解析 `x` / `t`（以及部分格式的 `l`）尾部的汇总块（brief 的 Produces 形状）：
        //   Folders: 2
        //   Files: 1
        //   Size:       7
        //   Compressed: 429
        // 逐行扫描、行首匹配（7z 把这些行顶格打印）；同名行出现多次时以**最后一次**为准
        //（尾部的汇总才是整轮的结果）。任何一项缺失或解析不出数字都返回 false，
        // 此时三个 out 一律为 0 —— 调用方据此判定「没有可用的汇总」，而不是拿半截数字去比对。
        public static bool TryParseSummary(string stdOut, out int folders, out int files, out long size)
        {
            folders = 0;
            files = 0;
            size = 0;
            if (string.IsNullOrEmpty(stdOut)) { return false; }

            int parsedFolders = 0;
            int parsedFiles = 0;
            long parsedSize = 0;
            bool gotFolders = false;
            bool gotFiles = false;
            bool gotSize = false;

            int lineStart = 0;
            for (int i = 0; i <= stdOut.Length; i++)
            {
                if (i < stdOut.Length && stdOut[i] != '\n') { continue; }

                string line = stdOut.Substring(lineStart, i - lineStart).Trim();
                lineStart = i + 1;

                int intValue;
                long longValue;
                if (line.StartsWith("Folders:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line.Substring("Folders:".Length).Trim(), out intValue))
                {
                    parsedFolders = intValue;
                    gotFolders = true;
                }
                else if (line.StartsWith("Files:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line.Substring("Files:".Length).Trim(), out intValue))
                {
                    parsedFiles = intValue;
                    gotFiles = true;
                }
                else if (line.StartsWith("Size:", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(line.Substring("Size:".Length).Trim(), out longValue))
                {
                    parsedSize = longValue;
                    gotSize = true;
                }
            }

            if (!gotFolders || !gotFiles || !gotSize) { return false; }

            folders = parsedFolders;
            files = parsedFiles;
            size = parsedSize;
            return true;
        }

        // 把 `l -slt` 的 stdout 解析成索引。internal 而不是 private：同一程序集里的测试要直接喂
        // 畸形文本（空清单、被截断的最后一块、缺 Size 的目录条目、成员名里带 " = "）——
        // 这些形状光靠真归档造不出来，而它们正是 I1 基线最容易被算错的地方。
        // Read 走的就是这个函数，不存在「测试用的解析器」和「产品用的解析器」两份实现。
        internal static ArchiveIndex ParseListing(string stdOut)
        {
            ArchiveIndex index = new ArchiveIndex();
            if (string.IsNullOrEmpty(stdOut)) { return index; }

            bool inEntries = false;
            int separators = 0;
            IndexEntry pending = null;

            int lineStart = 0;
            for (int i = 0; i <= stdOut.Length; i++)
            {
                if (i < stdOut.Length && stdOut[i] != '\n') { continue; }

                string line = stdOut.Substring(lineStart, i - lineStart);
                lineStart = i + 1;
                if (line.Length > 0 && line[line.Length - 1] == '\r') { line = line.Substring(0, line.Length - 1); }

                if (IsSeparator(line))
                {
                    Flush(index, ref pending);
                    separators++;

                    // 正常形状：第一个标记（`--`）结束前言，第二个标记（`----------`）才开始条目段。
                    // 只打印一个长标记的格式（属性段缺失）同样要能开始收集，所以长度也算一个条件。
                    if (separators >= 2 || line.Trim().Length >= EntriesSeparatorLength) { inEntries = true; }
                    continue;
                }

                if (!inEntries) { continue; }                                  // 前言 / 归档属性段

                if (line.Trim().Length == 0) { Flush(index, ref pending); continue; }   // 条目块结束

                // 只按**第一个** '=' 切分：成员名里带 " = " 是合法的（实测 `Path = eq = q.txt`）。
                int equals = line.IndexOf('=');
                if (equals < 0) { continue; }                                  // 非键值行（错误信息等）

                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1);
                if (value.Length > 0 && value[0] == ' ') { value = value.Substring(1); }

                if (pending == null) { pending = new IndexEntry(); }

                if (string.Equals(key, "Path", StringComparison.Ordinal)) { pending.Path = value; }
                else if (string.Equals(key, "Size", StringComparison.Ordinal)) { pending.Size = ParseSize(value); }
                else if (string.Equals(key, "Folder", StringComparison.Ordinal)) { if (value.Trim() == "+") { pending.IsDirectory = true; } }
                else if (string.Equals(key, "Attributes", StringComparison.Ordinal))
                {
                    // 7-Zip 把 FILE_ATTRIBUTE_* 映射成字母：D = 目录、L = reparse point。
                    // 实测：`7z a -snl` 存的软链在 `l -slt` 里是 `Attributes = AL`（没有 Symbolic Link 行）。
                    string attributes = value.Trim();
                    if (attributes.IndexOf('D') >= 0) { pending.IsDirectory = true; }
                    if (attributes.IndexOf('L') >= 0 || attributes.IndexOf("reparse", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        pending.IsReparsePoint = true;
                    }
                }
                else if (string.Equals(key, "Symbolic Link", StringComparison.Ordinal))
                {
                    // 实测 tar 的软链：`Symbolic Link = /tmp/target.txt`；普通文件则是同一行给空值，
                    // 所以非空才算 reparse point。
                    if (value.Trim().Length > 0) { pending.IsReparsePoint = true; }
                }
            }

            Flush(index, ref pending);      // 输入末尾没有空行（stdout 被截断）时，最后一块照样算数
            return index;
        }

        // ---------------- 内部 ----------------

        // 一个条目块结束。没有 Path 的块一律丢弃：归档属性段（Path = 包自己）、尾部的
        // Folders:/Files:/Size: 汇总、以及 7z 插在条目之间的错误行，都不该变成清单条目。
        //
        // 注意（实测）：单流格式的 bz2 / xz 的条目块**没有 Path 行** —— 7z 只打印
        // `Size` / `Packed Size`（bz2 连 Size 都是空的），因为这两种格式根本不存成员名。
        // 于是按 brief 的「条目 = 有 Path 的块」定义，它们的索引是 0 条目 / 0 字节。
        // 这是如实反映 `l` 的输出，但它们的完整性判定因此**不能**只看索引，
        // 必须走 `x`/`t` 的汇总（TryParseSummary）—— 已列入 task-4-report 的遗留问题。
        private static void Flush(ArchiveIndex index, ref IndexEntry pending)
        {
            IndexEntry entry = pending;
            pending = null;
            if (entry == null || entry.Path == null) { return; }

            index.Entries.Add(entry);
            if (entry.IsDirectory) { return; }          // 目录进 Entries，但不进 FileCount / TotalBytes

            index.FileCount++;
            index.TotalBytes += entry.Size;
        }

        // 「整行都是连字符」的行：归档属性段的 `--` 与条目段的 `----------`。
        // 它只会出现在块边界上（-slt 的内容行永远是 "键 = 值"），所以不会把成员名误判成标记。
        private static bool IsSeparator(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length < 2) { return false; }

            for (int i = 0; i < trimmed.Length; i++)
            {
                if (trimmed[i] != '-') { return false; }
            }
            return true;
        }

        // Size 可能整行缺失（目录）或为空（部分元数据的软链、被截断的输出）。一律按 0，
        // 绝不因为一个数字读不出来就丢掉整条目（丢条目 = 少算 = 可能放过半成品）。
        private static long ParseSize(string value)
        {
            long size;
            if (long.TryParse(value.Trim(), out size) && size > 0) { return size; }
            return 0;
        }

        private static string PasswordSwitch(string password)
        {
            if (string.IsNullOrEmpty(password)) { return "-p"; }
            return "-p" + password;
        }

        private static bool MentionsEncryption(string text)
        {
            if (string.IsNullOrEmpty(text)) { return false; }

            for (int i = 0; i < EncryptionMarkers.Length; i++)
            {
                if (text.IndexOf(EncryptionMarkers[i], StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
            }
            return false;
        }
    }
}
