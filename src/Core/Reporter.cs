// Rerar 报告与汇总（Task 8）。
//
// 编码（规格 §2 第 6 条 / §3 第 138 行）：本类写出的每个文件都是 **UTF-8 with BOM**。这是对原 .bat
// 脚本四个缺陷之一（报告里的中文归档名在记事本/Excel 里是乱码）的直接回归防线 ——
// 不带 BOM 的 UTF-8 会被记事本按系统代码页解读，Excel 双击打开同样认代码页。
//
// 两份产出的分工：
//   * WriteCsv     —— 给机器/Excel 的数据文件：列名与 Task 12 的 CLI JSON 键逐字一致，
//                     status 用枚举名（英文、稳定、可被脚本匹配），字段按 RFC 4180 转义；
//   * WriteSummary —— 给人读的汇总报告：中文结局文案 + 汇总表 + 计数行（规格 §6.9）。
// 两者都以「每归档一行」为不变式：行数永远等于结果数（RenderTable 的表头另算一行）。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Rerar.Core
{
    public static class Reporter
    {
        // 导出数据的列名 = Task 12 的 JSON 键，顺序也一致（契约，改动即破坏报告/CLI 一致性）。
        private const string CsvHeader = "path,status,layers,files,failed,outputDir,message";

        // 汇总表列名（规格 §6.9：压缩包 / 结果 / 层数 / 文件数 / 失败数 / 输出去向 + 说明）。
        // 规格的列表里还有「耗时」：ArchiveResult 没有该字段（那是 Task 10 的 ArchiveTask/RunOptions
        // 才可能有的东西），所以本任务不凭空造一列，宁可少一列也不写假数据。
        private const string TableHeader = "压缩包 | 结果 | 层数 | 文件数 | 失败数 | 输出去向 | 说明";

        // 导出 CSV。path 由调用方决定（GUI 的另存为 / CLI 的 --report），本类不创建父目录。
        public static void WriteCsv(IEnumerable<ArchiveResult> results, string path)
        {
            if (path == null) { throw new ArgumentNullException("path"); }

            StringBuilder sb = new StringBuilder();
            sb.Append(CsvHeader).Append("\r\n");
            foreach (ArchiveResult r in Enumerate(results))
            {
                sb.Append(CsvField(r.Path)).Append(',')
                  .Append(CsvField(r.Status.ToString())).Append(',')   // 枚举名，未知值退化为数字形式，不抛
                  .Append(Int(r.Layers)).Append(',')
                  .Append(Int(r.Files)).Append(',')
                  .Append(Int(r.Failed)).Append(',')
                  .Append(CsvField(r.OutputDir)).Append(',')
                  .Append(CsvField(r.Message)).Append("\r\n");
            }

            WriteAllText(path, sb.ToString());
        }

        // 汇总表（纯文本，'\n' 分行，不含结尾换行）：表头一行 + 每个归档一行。
        // 空/null 结果集只返回表头 —— 调用方（GUI 详情区 / CLI 摘要）不必先判空。
        public static string RenderTable(IEnumerable<ArchiveResult> results)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(TableHeader);
            foreach (ArchiveResult r in Enumerate(results))
            {
                sb.Append('\n')
                  .Append(Cell(r.Path)).Append(" | ")
                  .Append(StatusText(r.Status)).Append(" | ")
                  .Append(Int(r.Layers)).Append(" | ")
                  .Append(Int(r.Files)).Append(" | ")
                  .Append(Int(r.Failed)).Append(" | ")
                  .Append(Cell(r.OutputDir)).Append(" | ")
                  .Append(Cell(r.Message));
            }
            return sb.ToString();
        }

        // 汇总报告（TXT 导出，UTF-8 with BOM，CRLF 分行）：标题 + 计数行 + 汇总表。
        // 空结果集同样是一份有意义（且非空）的文件：「归档总数：0」+ 表头。
        public static void WriteSummary(IEnumerable<ArchiveResult> results, string path)
        {
            if (path == null) { throw new ArgumentNullException("path"); }

            // 先把结果物化一次：计数与表格必须是**同一批**归档（惰性序列枚举两遍可能得到两种结果，
            // 例如调用方直接传了一个一次性迭代器）。
            List<ArchiveResult> list = new List<ArchiveResult>(Enumerate(results));

            int completed = 0, partial = 0, skipped = 0, failed = 0, notAttemptedDepthLimit = 0, notAttemptedFatal = 0;
            int files = 0, fileFailures = 0;
            // 刻意不写 default：枚举里不存在的值（后续任务可能新增成员）不进任何一类 ——
            // 它照样会出现在下面的表格里，计数不会骗人，只是各类之和可能小于归档总数。
            //
            // 【Task 12 修复轮 #2 Finding 1】正因如此，每新增一个成员都必须**同时**在这里挂一档、
            // 并在 StatusText 里给中文文案：NotAttemptedFatal 当初就是漏了这两处，于是表格里出现
            // 一行英文、而归档总数把它算进来、各类之和却小于总数 —— 读者会以为有归档凭空消失。
            // 现在「有没有中文文案」由 ReporterTests.TableLabelsEveryArchiveStatus 迭代
            // Enum.GetValues 逐个成员钉住（新增成员不补文案就当场变红）；计数档本身由
            // Reporter.SummaryCountsStatusesAndListsArchives 逐字钉住（含这个新档）。
            foreach (ArchiveResult r in list)
            {
                switch (r.Status)
                {
                    case ArchiveStatus.Completed: completed++; break;
                    case ArchiveStatus.CompletedWithFailures: partial++; break;
                    case ArchiveStatus.SkippedNeedsPassword:
                    case ArchiveStatus.SkippedContainer:
                    case ArchiveStatus.SkippedUnreadable: skipped++; break;
                    case ArchiveStatus.Failed: failed++; break;
                    case ArchiveStatus.NotAttemptedDepthLimit: notAttemptedDepthLimit++; break;
                    case ArchiveStatus.NotAttemptedFatal: notAttemptedFatal++; break;
                }
                files += r.Files;
                fileFailures += r.Failed;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("Rerar 解压汇总\r\n");
            // 计数行的形状与 CLI stdout 的汇总行**逐字一致**（Program.PrintSummary）：同一份报告
            // 不管从 TXT 导出还是从无头 stdout 看，读者读到的分类与数字都是同一套。
            // 「未处理」拆成两档（深度上限 / 整批中止），各类之和因此**恒等于**归档总数。
            sb.Append("归档总数：").Append(Int(list.Count))
              .Append("（完成 ").Append(Int(completed))
              .Append("，部分失败 ").Append(Int(partial))
              .Append("，跳过 ").Append(Int(skipped))
              .Append("，失败 ").Append(Int(failed))
              .Append("，未处理（深度上限）").Append(Int(notAttemptedDepthLimit))
              .Append("，未处理（整批中止）").Append(Int(notAttemptedFatal))
              .Append("）\r\n");
            sb.Append("文件总数：").Append(Int(files))
              .Append("，失败文件数：").Append(Int(fileFailures))
              .Append("\r\n\r\n");
            sb.Append(RenderTable(list).Replace("\n", "\r\n"));

            WriteAllText(path, sb.ToString());
        }

        // ---------------- 内部 ----------------

        // null 结果集按空处理、序列里的 null 元素跳过：调用方（GUI/CLI）在「什么都没解」或某个
        // 结果尚未填好时不该拿到 NullReferenceException；被跳过的 null 也不会凭空多出一行。
        private static IEnumerable<ArchiveResult> Enumerate(IEnumerable<ArchiveResult> results)
        {
            if (results == null) { yield break; }
            foreach (ArchiveResult r in results)
            {
                if (r != null) { yield return r; }
            }
        }

        // RFC 4180 转义：字段含逗号、双引号、CR、LF 或首尾空格时整体套引号，内部双引号翻倍。
        // 首尾空格也要套引号，是因为很多 CSV 读方（含 Excel 的某些导入路径）会 trim 未加引号的字段，
        // 而目标路径含首尾空格正是本工具要如实报告的情形之一。
        private static string CsvField(string value)
        {
            if (string.IsNullOrEmpty(value)) { return ""; }

            bool needsQuote =
                value.IndexOf(',') >= 0 ||
                value.IndexOf('"') >= 0 ||
                value.IndexOf('\r') >= 0 ||
                value.IndexOf('\n') >= 0 ||
                value != value.Trim();

            if (!needsQuote) { return value; }
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        // 表格单元格不承载换行：一个换行会把「每归档一行」拆成两行，行数与人工阅读都会失真。
        private static string Cell(string value)
        {
            if (string.IsNullOrEmpty(value)) { return ""; }
            return value.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        // 中文结局文案（规格 §6.9 的「结果」列）。**每一个** ArchiveStatus 成员都必须在这里有一行 ——
        // 「所有面向用户的文本为中文」这条全局约束就落在这张表上（漏一行，用户就会在表里看到英文
        // 枚举名；Task 12 修复轮 #2 的 NotAttemptedFatal 就是这么漏的）。
        //
        // 下面的兜底是**最后手段，不是「设计上可以露出英文」**：它只防「枚举外的非法数值」把整份报告
        // 搞崩；任何**具名成员**走到那一行都是缺陷。这条不再靠人眼把关：
        // ReporterTests.TableLabelsEveryArchiveStatus 迭代 Enum.GetValues，逐个成员断言文案非空且
        // **不等于**枚举名 —— 新增成员不补文案就当场变红。
        private static string StatusText(ArchiveStatus status)
        {
            switch (status)
            {
                case ArchiveStatus.Completed: return "完成";
                case ArchiveStatus.CompletedWithFailures: return "完成（部分失败）";
                case ArchiveStatus.SkippedNeedsPassword: return "跳过（需要密码）";
                case ArchiveStatus.SkippedContainer: return "跳过（容器文档）";
                case ArchiveStatus.SkippedUnreadable: return "跳过（无法读取）";
                case ArchiveStatus.Failed: return "失败";
                case ArchiveStatus.NotAttemptedDepthLimit: return "未处理（深度上限）";

                // 【Task 12 修复轮 #2 Finding 1】新增成员必须有中文文案：它由 CLI 在呈现层合成
                //（整批因致命错误中止 / 取消之后没轮到处理的项），落在这张表里就是给人看的。
                // 漏了它这一格会露出英文枚举名 —— 那正是「所有面向用户的文本为中文」被破坏的样子。
                // 这条不再靠人眼：ReporterTests.TableLabelsEveryArchiveStatus 迭代 Enum.GetValues
                // 逐个成员断言「有文案、且不等于枚举名」，将来新增成员会当场变红。
                case ArchiveStatus.NotAttemptedFatal: return "未处理（整批中止）";
            }

            // 兜底：只该被「枚举外的非法数值」命中；任何具名成员走到这里都是缺陷（见方法头注释）。
            return status.ToString();
        }

        // 报告是数据文件：数字一律不变文化格式化，不随系统区域设置变成本地数字。
        private static string Int(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        // 本类唯一的写入点：UTF-8 **带 BOM**。BOM 由 File.WriteAllText 写在文件最开头，只写一次；
        // 正文里不含 U+FEFF，所以整份文件恰好只有一个 BOM（Reporter.CsvWritesBomOnce 钉住这一点）。
        private static void WriteAllText(string path, string text)
        {
            File.WriteAllText(path, text, new UTF8Encoding(true));
        }
    }
}
