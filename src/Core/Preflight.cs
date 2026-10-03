// Rerar 预检（Task 10；规格 §4.2 / §6.7 / §6.11、不变式 I1、Review Focus #1）。
//
// 本类只做**判定**：不写盘、不改名、不删任何东西（唯一的对外调用是注入进来的 IDiskSpaceProvider）。
// 每条判定都返回「是否放行 + 中文判词」，由 Extractor 决定怎么处置。这样切的原因有两个：
//   1. 判定是纯逻辑，可以直接喂畸形输入（长路径、重名条目、截断清单）钉住行为 ——
//      而这些形状靠真归档造不出来；
//   2. 安全方向必须一眼可审：本类里没有任何一条路径会「因为判不出来就放行」。
//
// 四类预检 + 一条基线规则：
//   * 体积/空间  —— 可用空间必须 ≥ 归档总字节 + 低水位（低水位同时是运行中轮询的门槛）；
//   * 长路径     —— 规格 §10.2 把 `\\?\` 全链路推到 v1.1，v1.0 用「预检拒绝 + 明确提示」兜住；
//   * 条目重名   —— Readme.txt 与 README.TXT 在 Windows 上是同一个路径，必须提前拒绝（绝不静默覆盖）；
//   * 密码需求   —— 只凭「清单因加密而读不出来」判定；损坏包的清单也是失败，但那不是「需要密码」；
//   * 完整基线   —— I1 的核心规则（见 TryGetBaseline），Extractor 与用例共用同一份实现。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.IO;

namespace Rerar.Core
{
    // 预检结论。Ok = false 时 Reason 一定非空且是给人看的中文判词。
    public sealed class PreflightReport
    {
        public bool Ok;
        public string Reason;
    }

    public static class Preflight
    {
        // MAX_PATH(260) - 1：留给结尾的 NUL。规格 §10.2 明确把 `\\?\` 全链路推到 v1.1，
        // v1.0 的做法是「预检拒绝 + 明确提示」，所以这里是**拒绝**的阈值而不是「尝试一下」。
        public const int MaxPathLength = 259;

        // ------------------------------------------------------------------
        // 体积 / 空间
        // ------------------------------------------------------------------

        // 可用空间是否够本次解压。neededBytes = 索引里的总字节；minFreeBytes = 低水位。
        // 任何输入异常（disk 为 null、需要的字节为负）都按「不够」处理：预检绝不在数字可疑时放行。
        public static PreflightReport CheckFreeSpace(IDiskSpaceProvider disk, string destinationRoot, long neededBytes, long minFreeBytes)
        {
            if (disk == null) { throw new ArgumentNullException("disk"); }

            long need = neededBytes > 0 ? neededBytes : 0;
            long reserve = minFreeBytes > 0 ? minFreeBytes : 0;

            // 溢出安全：畸形索引（TotalBytes 极大）不得让要求值回绕成一个很小的数。
            long required;
            if (need > long.MaxValue - reserve) { required = long.MaxValue; }
            else { required = need + reserve; }

            long free = disk.FreeBytes(destinationRoot);
            if (free >= required) { return Ok(); }

            PreflightReport report = new PreflightReport();
            report.Ok = false;
            report.Reason = "磁盘空间不足：目标 " + Shorten(destinationRoot) + " 所在卷可用 " + free +
                " 字节，需要约 " + required + " 字节（归档 " + need + " 字节 + 低水位 " + reserve + " 字节）";
            return report;
        }

        // ------------------------------------------------------------------
        // 长路径
        // ------------------------------------------------------------------

        // 返回第一个会让写入路径超过 MaxPathLength 的条目路径；都放得下时返回 null。
        // writeRoot 应当是**实际写入的那个目录名**：调用方要取「暂存目录」与「最终目标」中较长者
        //（暂存目录名是随机的 .rerar-stage-xxx，最终目标名可能很长 —— 两者都可能成为最长的那一个）。
        public static string FindOverlongEntry(string writeRoot, ArchiveIndex index)
        {
            if (string.IsNullOrEmpty(writeRoot) || index == null || index.Entries == null) { return null; }

            for (int i = 0; i < index.Entries.Count; i++)
            {
                IndexEntry entry = index.Entries[i];
                if (entry == null || string.IsNullOrEmpty(entry.Path)) { continue; }

                string predicted;
                try { predicted = Path.Combine(writeRoot, entry.Path); }
                catch (Exception) { return entry.Path; }     // 拼不出来（非法字符）⇒ 按放不下处理

                if (predicted.Length > MaxPathLength) { return entry.Path; }
            }
            return null;
        }

        // ------------------------------------------------------------------
        // 条目重名（大小写不敏感）
        // ------------------------------------------------------------------

        // 返回第二个出现的冲突条目路径（用户一眼能看出是哪两个撞了）；没有冲突返回 null。
        // 判定按 Windows 的文件名语义（序数忽略大小写），并去掉尾部路径分隔符 —— 于是
        // 「文档/」这个目录条目与「文档」这个文件条目也算冲突（它们在磁盘上是同一个名字）。
        public static string FindConflictingEntries(ArchiveIndex index)
        {
            if (index == null || index.Entries == null) { return null; }

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < index.Entries.Count; i++)
            {
                IndexEntry entry = index.Entries[i];
                if (entry == null || string.IsNullOrEmpty(entry.Path)) { continue; }

                string key = Normalize(entry.Path);
                if (key.Length == 0) { continue; }
                if (!seen.Add(key)) { return entry.Path; }
            }
            return null;
        }

        // ------------------------------------------------------------------
        // 密码需求
        // ------------------------------------------------------------------

        // `7z l -slt` 的清单里有没有**加密成员**。只认 `Encrypted = +` 这条键值行。
        //
        // 为什么不能按子串 "Encrypt" 找：实测截断的 zip 清单里带着局部头的
        // `Characteristics = Local : Encrypt`，按子串判会把**损坏包**误判成加密包 ——
        // 于是「需要密码」的提示会把「下载坏了」这条真正的原因盖掉，用户会一直去输密码。
        // `Encrypted = -`（明文成员）与空值都不算。
        public static bool MentionsEncryption(string listingText)
        {
            if (string.IsNullOrEmpty(listingText)) { return false; }

            int start = 0;
            for (int i = 0; i <= listingText.Length; i++)
            {
                if (i < listingText.Length && listingText[i] != '\n') { continue; }

                string line = listingText.Substring(start, i - start).Trim();
                start = i + 1;

                if (line.StartsWith("Encrypted", StringComparison.OrdinalIgnoreCase))
                {
                    int equals = line.IndexOf('=');
                    if (equals >= 0 && line.Substring(equals + 1).Trim() == "+") { return true; }
                }
            }
            return false;
        }

        // 「该归档需要密码」的判据只有一条：**清单读取失败且失败原因是加密**。
        // 损坏包（ListingFailed 但无加密特征）不算 —— 它需要的是「损坏」提示，不是密码提示。
        public static bool NeedsPassword(ArchiveIndex index)
        {
            if (index == null) { return false; }
            return index.ListingFailed && index.HasEncryptedHeaders;
        }

        // ------------------------------------------------------------------
        // I1 的完整基线规则
        // ------------------------------------------------------------------

        // 判定「这次解压到底该拿什么当完整性基线」。返回值 = 有没有**任何**可用基线。
        //
        // 规则（顺序即语义，不可重排）：
        //   1. ListingFailed == true ⇒ **没有基线，与 FileCount 无关**。截断的包会给出
        //      「失败 + 非空半截清单」，拿那份偏低的基线去比对，同样截断的解压结果就会
        //      「看起来完整」→ 提交 → 开删除即丢原包。这是本工具最怕的方向，所以这一档
        //      连 x 尾部的汇总都不认（那份汇总是**同一次运行**的产物，不是独立证据）。
        //   2. 清单成功但 FileCount == 0 ⇒ 没有索引基线：单流格式（bz2/xz/gz）本来就不存成员名。
        //      退回解压尾部汇总，且**只有字节可用**（汇总里 files == 0 是「没被计数」，
        //      不是「包里没有文件」，所以 countsFiles = false，调用方不得比对文件数）。
        //   3. 清单成功且非空 ⇒ 条目数与总字节都要比。
        // 三档都不成立时返回 false：调用方必须判「无法校验」，绝不能用退出码顶上来当成功证据。
        public static bool TryGetBaseline(ArchiveIndex index, string extractionStdOut, out int files, out long bytes, out bool countsFiles)
        {
            files = 0;
            bytes = 0;
            countsFiles = false;
            if (index == null) { return false; }

            if (index.ListingFailed) { return false; }

            if (index.FileCount > 0)
            {
                files = index.FileCount;
                bytes = index.TotalBytes;
                countsFiles = true;
                return true;
            }

            int folders;
            int summaryFiles;
            long summarySize;
            if (SevenZipIndex.TryParseSummary(extractionStdOut, out folders, out summaryFiles, out summarySize))
            {
                files = summaryFiles;
                bytes = summarySize;
                countsFiles = summaryFiles > 0;
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // 内部
        // ------------------------------------------------------------------

        private static PreflightReport Ok()
        {
            PreflightReport report = new PreflightReport();
            report.Ok = true;
            report.Reason = "";
            return report;
        }

        // 重名比较用的归一化键：两种分隔符统一成 '\'，去掉尾部与重复分隔符。
        private static string Normalize(string path)
        {
            string normalized = path.Replace('/', '\\');
            while (normalized.Length > 0 && normalized[normalized.Length - 1] == '\\')
            {
                normalized = normalized.Substring(0, normalized.Length - 1);
            }
            return normalized;
        }

        private static string Shorten(string path)
        {
            if (path == null) { return ""; }
            string flat = path.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return flat.Length <= 120 ? flat : flat.Substring(0, 120) + "…";
        }
    }
}
