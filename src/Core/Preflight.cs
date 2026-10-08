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
using System.Globalization;
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
        // 安全上限：压缩炸弹 / 海量条目（规格 §9.1 用例 10、§9.2；审计 F2）
        // ------------------------------------------------------------------

        // 单包最大解压比：解压后总字节 ÷ 包自身物理字节。取 100 倍。
        // 依据：真实归档里只有高度重复的内容（长文本、日志、CSV、源码、零填充的测试数据）会接近
        // 这个量级，而 §6.9 点名的炸弹形状是 10^3～10^6 倍。取 100 而不是 10：太紧会把正常的高重复
        // 内容也拒掉；取 100 而不是 1000：1000 倍足够让一个 50 KB 的包变成 50 GB，而那时磁盘空间预检
        // 在大容量磁盘上基本不设防（4 TB 盘上「空间很够」）。两侧代价完全不对称 —— 误拒只是少解
        // 一个包（判词写明是安全上限、原包保留），误放则会写满盘或耗尽文件系统。
        public const double MaxExpansionRatio = 100.0;

        // 解压比分母的下限：64 KiB。
        // 为什么需要它：分母取「包自身字节」，小包的分母太小时比值由噪声主导 —— 一个 200 字节的包
        // 解出一份 40 KB 的说明文件就是 200 倍，那是无害的。这条下限之上的伤害本来就被下面两条
        // 绝对上限兜住，所以比值只需要在真正有意义的量级上生效。
        public const long ExpansionRatioBasisFloorBytes = 64L * 1024;

        // 单包解压后总字节上限：128 GiB。
        // 依据：本工具最主要的输入是媒体包，而媒体本身已经压缩过（解压比 ≈ 1），所以这条**不是**
        // 用来拦媒体的，而是拦「比值的漏网之鱼」—— 解压比没超上限、绝对体量却不合理的包
        //（例如 2 GB 的包解出 90 GB）。128 GiB 高于最大的单部 4K 原盘（约 80 GB）、远低于
        // 「几十 TB」这种明显不属于单个包的体量；更强的保护其实是空间预检（要求可用空间 ≥
        // 解压后总字节 + 低水位），这一条只是不让一个包把机器拖进长时间的写盘。
        public const long MaxTotalUncompressedBytes = 128L * 1024 * 1024 * 1024;

        // 单包条目数上限：10 万条（含目录条目）。
        // 依据：审计 F2 点名的攻击形状是「2 MB → 300 万个条目」——它的伤害不在字节（300 万个 1 字节
        // 条目总共才 3 MB，空间预检完全拦不住），而在文件系统：每个条目占一条 MFT 记录，加上杀软
        // 逐文件扫描，会先耗尽 MFT/配额、再把机器拖到事实性卡死。10 万已远超本工具用途下的任何
        // 真实单包（媒体包几十到几百条；整树打包的源码/素材包通常几千到几万条），同时把 300 万
        // 条目的形状挡在 30 倍以外。
        public const int MaxEntriesPerArchive = 100000;

        // 三条上限 + 「算不出上限」时的保守处置。要在**任何写入之前**调用（Extractor 把它排在
        // 空间预检**之前**：空间不足是**致命**档、会中止整批，而炸弹是**单个归档**的问题，
        // 正确处置是跳过它、继续处理下一个）。
        //
        // 【头部加密的 caveat —— 上限算不出来时怎么办，明确写下来】
        // 被加密的头部会让清单读不出来，那一刻上限定不出来。本工具的处置是：
        //   1. Extractor 在「清单因加密而失败」时先走密码阶梯，用 `l -p<候选>` 验证候选密码；
        //      拿到密码后**重读一次索引**，上限就从那份真索引算 —— 加密包不会绕开上限；
        //   2. 拿不到密码 ⇒ SkippedNeedsPassword，根本走不到解压，也就不需要上限；
        //   3. 因此走到本函数的索引只可能是「成功但没有可度量条目」（单流格式 / 空包 / 只有目录）
        //      或「清单读取失败」这两种。两者一律**拒绝**（保守默认 = 不放行），v1.0 不提供开关。
        // 绝不允许降级成「算不出上限就照解」—— 那是这条上限会形同虚设的唯一路径。
        public static PreflightReport CheckExpansion(ArchiveIndex index, long archiveBytes)
        {
            if (index == null || index.ListingFailed || index.Entries == null)
            {
                return Refuse("预检拒绝（安全上限，不是文件损坏）：归档清单不可用（读取失败或没有条目表），"
                    + "解压比与体积上限都算不出来 —— 拿不到上限就不放行；未写盘一个字节；原包保留");
            }
            if (index.FileCount == 0)
            {
                return Refuse("预检拒绝（安全上限，不是文件损坏）：清单里没有可度量的文件条目"
                    + "（单流格式 / 空包 / 只有目录），解压比与体积上限都算不出来 —— 拿不到上限就不放行；"
                    + "未写盘一个字节；原包保留");
            }
            if (index.TotalBytes < 0)
            {
                return Refuse("预检拒绝（安全上限，不是文件损坏）：清单里的总字节数是畸形值（溢出），"
                    + "解压比与体积上限都算不出来 —— 拿不到上限就不放行；未写盘一个字节；原包保留");
            }

            // 1) 条目数（含目录条目：每个条目都要占一条 MFT 记录，目录同样要占）。
            int entries = index.Entries.Count;
            if (entries > MaxEntriesPerArchive)
            {
                return Refuse("预检拒绝（安全上限，不是文件损坏）：索引里有 " + entries + " 个条目，超过单包上限 "
                    + MaxEntriesPerArchive + " 个 —— 海量条目会耗尽文件系统（MFT / 配额），"
                    + "杀软的逐文件扫描还会把机器拖成事实性卡死；未写盘一个字节；原包保留");
            }

            // 2) 解压后总字节。
            if (index.TotalBytes > MaxTotalUncompressedBytes)
            {
                return Refuse("预检拒绝（安全上限，不是文件损坏）：索引声明解压后总字节 " + index.TotalBytes
                    + "，超过单包上限 " + MaxTotalUncompressedBytes + " 字节；未写盘一个字节；原包保留");
            }

            // 3) 解压比（用 double 比较，避免 archiveBytes * 比例 在靠近 long.MaxValue 时回绕）。
            long basis = archiveBytes > ExpansionRatioBasisFloorBytes ? archiveBytes : ExpansionRatioBasisFloorBytes;
            double ratio = (double)index.TotalBytes / (double)basis;
            if (ratio > MaxExpansionRatio)
            {
                return Refuse("预检拒绝（安全上限，不是文件损坏）：解压比约 "
                    + ratio.ToString("0.#", CultureInfo.InvariantCulture) + " 倍（解压后 " + index.TotalBytes
                    + " 字节 ÷ 包 " + basis + " 字节），超过上限 "
                    + MaxExpansionRatio.ToString("0.#", CultureInfo.InvariantCulture)
                    + " 倍，符合压缩炸弹特征；未写盘一个字节；原包保留");
            }

            return Ok();
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

        // 拒绝：Reason 一定非空且是给人看的中文判词（本类里所有「拒绝」都走这里，绝不静默）。
        private static PreflightReport Refuse(string reason)
        {
            PreflightReport report = new PreflightReport();
            report.Ok = false;
            report.Reason = reason;
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
