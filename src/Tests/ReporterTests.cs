// Task 8 单元测试：Reporter（UTF-8 with BOM 的 CSV / 汇总报告 / 汇总表）。
//
// 前 3 条用例的名字与期望值逐字来自 task-8-brief.md Step 1，只用一处必要改写：
//   brief 的 CsvContainsChineseUncorrupted 直接读上一条用例写出的 r.csv —— 但 H.Run 在**每个**用例
//   开始前都调用 TestEnv.Cleanup() 清空 Tmp，上一条用例的文件到这里已经不存在（会 FileNotFoundException）。
//   故该用例自己先 WriteCsv 一次，断言的内容与 brief 完全一致。
// 其余用例覆盖 brief 的 Produces 里 Step 1 没触碰的部分：CSV 列名与 Task 12 的 JSON 键逐字一致、
// 含逗号/引号/换行/首尾空格的字段能原样往返（否则导出的报告不是数据）、BOM 恰好写一次、
// 空结果集下的 CSV 与汇总报告。
//
// 断言约定：AssertEq 对 byte[] 是按**引用**比较（harness 约定 4），所以字节一律逐位比较。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.IO;
using System.Text;
using Rerar.Core;

internal sealed class ReporterTests : TestBase
{
    // 导出文件的列名契约：与 Task 12 的 CLI JSON 键（path,status,layers,files,failed,outputDir,message）逐字一致。
    private const string Header = "path,status,layers,files,failed,outputDir,message";

    public static void Run()
    {
        // ---- brief Step 1 的 3 条（用例名与期望值逐字照抄）----
        H.Run("Reporter.CsvIsUtf8WithBom", delegate {
            Reporter.WriteCsv(new ArchiveResult[] { TestEnv.SampleResult("中文名.zip") }, TestEnv.TmpFile("r.csv"));
            byte[] b = File.ReadAllBytes(TestEnv.TmpFile("r.csv"));
            AssertEq(b[0], 0xEF); AssertEq(b[1], 0xBB); AssertEq(b[2], 0xBF); });

        H.Run("Reporter.CsvContainsChineseUncorrupted", delegate {
            // 回归：原 .bat 脚本按系统代码页写报告，中文归档名在记事本/Excel 里是乱码（U+FFFD）。
            Reporter.WriteCsv(new ArchiveResult[] { TestEnv.SampleResult("中文名.zip") }, TestEnv.TmpFile("r.csv"));
            string s = File.ReadAllText(TestEnv.TmpFile("r.csv"), Encoding.UTF8);
            AssertTrue(s.Contains("中文名.zip")); AssertFalse(s.Contains("\uFFFD")); });

        H.Run("Reporter.TableHasOneRowPerArchive", delegate {
            string table = Reporter.RenderTable(new ArchiveResult[] { TestEnv.SampleResult("a.zip"), TestEnv.SampleResult("b.zip") });
            AssertEq(table.Split('\n').Length >= 2, true);                        // brief 的弱断言，照旧保留
            AssertEq(table.Split('\n').Length, 3);                                // 另加：表头 + 每个归档恰好一行
            AssertEq(Reporter.RenderTable(new ArchiveResult[] { TestEnv.SampleResult("a.zip") }).Split('\n').Length, 2);
            AssertEq(Reporter.RenderTable(new ArchiveResult[0]).Split('\n').Length, 1); });   // 空结果集只剩表头

        // ---- CSV 字段形状：列名是契约；status 用枚举名（机器可读）；空字段就是空字段 ----
        H.Run("Reporter.CsvHeaderMatchesResultContract", delegate {
            ArchiveResult r = new ArchiveResult();
            r.Path = "a.zip";
            r.Status = ArchiveStatus.SkippedNeedsPassword;
            r.Layers = 0; r.Files = 0; r.Failed = 0;
            r.OutputDir = ""; r.Message = "";
            Reporter.WriteCsv(new ArchiveResult[] { r }, TestEnv.TmpFile("r.csv"));

            string[] lines = File.ReadAllLines(TestEnv.TmpFile("r.csv"), Encoding.UTF8);
            AssertEq(lines.Length, 2);
            AssertEq(lines[0], Header);                                    // 与 Task 12 的 JSON 键同序同名
            AssertEq(lines[1], "a.zip,SkippedNeedsPassword,0,0,0,,");       // 空输出去向/说明 = 两个空字段，不是占位符
        });

        // ---- 空结果集：只写一份表头，同样带 BOM，且不得抛异常 ----
        H.Run("Reporter.CsvWithNoResultsIsHeaderOnly", delegate {
            Reporter.WriteCsv(new ArchiveResult[0], TestEnv.TmpFile("empty.csv"));
            byte[] b = File.ReadAllBytes(TestEnv.TmpFile("empty.csv"));
            AssertEq(b[0], 0xEF); AssertEq(b[1], 0xBB); AssertEq(b[2], 0xBF);

            string[] lines = File.ReadAllLines(TestEnv.TmpFile("empty.csv"), Encoding.UTF8);
            AssertEq(lines.Length, 1);
            AssertEq(lines[0], Header); });

        // ---- 含逗号/引号/换行/首尾空格的字段必须能原样往返（RFC 4180 转义）----
        H.Run("Reporter.CsvQuotesFieldsThatWouldBreakRoundTrip", delegate {
            ArchiveResult r = new ArchiveResult();
            r.Path = "has,comma\"quote.zip";
            r.Status = ArchiveStatus.Failed;
            r.Layers = 3; r.Files = 0; r.Failed = 2;
            r.OutputDir = " out ";                     // 首尾空格：不套引号会被读方 trim 掉
            r.Message = "第一行\n第二行";
            Reporter.WriteCsv(new ArchiveResult[] { r }, TestEnv.TmpFile("r.csv"));

            string s = File.ReadAllText(TestEnv.TmpFile("r.csv"), Encoding.UTF8);
            AssertEq(s.Contains("\"has,comma\"\"quote.zip\""), true);   // 逗号/引号：整体套引号 + 内部引号翻倍
            AssertEq(s.Contains(",\" out \","), true);
            AssertEq(s.Contains(",\"第一行\n第二行\""), true);
            AssertEq(s.Contains(",Failed,3,0,2,"), true);               // 普通字段不得被无谓地套引号
        });

        // ---- BOM 恰好写一次：开头三字节是 BOM，正文里不再出现 U+FEFF，字节长度逐一对得上 ----
        H.Run("Reporter.CsvWritesBomOnce", delegate {
            Reporter.WriteCsv(new ArchiveResult[] { TestEnv.SampleResult("一次.zip") }, TestEnv.TmpFile("r.csv"));
            byte[] b = File.ReadAllBytes(TestEnv.TmpFile("r.csv"));
            AssertEq(b[0], 0xEF); AssertEq(b[1], 0xBB); AssertEq(b[2], 0xBF);

            string text = Encoding.UTF8.GetString(b, 3, b.Length - 3);   // 剥掉前导 BOM 再解码
            AssertFalse(text.Contains("\uFEFF"));                        // 正文里不得再藏一个 BOM 字符
            AssertEq(b.Length, 3 + new UTF8Encoding(false).GetByteCount(text)); });

        // ---- 汇总报告：空结果集不得抛，也不得写出一份空文件 ----
        H.Run("Reporter.SummaryHandlesEmptyResultList", delegate {
            Reporter.WriteSummary(new ArchiveResult[0], TestEnv.TmpFile("empty.txt"));
            byte[] b = File.ReadAllBytes(TestEnv.TmpFile("empty.txt"));
            AssertEq(b[0], 0xEF); AssertEq(b[1], 0xBB); AssertEq(b[2], 0xBF);

            string s = File.ReadAllText(TestEnv.TmpFile("empty.txt"), Encoding.UTF8);
            AssertTrue(s.Contains("归档总数：0"));
            AssertTrue(s.Contains("压缩包"));                  // 表头仍在：用户看到的不是一份空文件
            AssertFalse(s.Contains("\uFFFD")); });

        // ---- 汇总报告：每个归档一行 + 按结局分类的计数（规格 §6.9 的汇总表）----
        H.Run("Reporter.SummaryCountsStatusesAndListsArchives", delegate {
            ArchiveResult ok = TestEnv.SampleResult("成功包.zip");
            ok.Status = ArchiveStatus.Completed; ok.Layers = 1; ok.Files = 5; ok.Failed = 0;

            ArchiveResult bad = TestEnv.SampleResult("失败包.rar");
            bad.Status = ArchiveStatus.Failed; bad.Layers = 1; bad.Files = 2; bad.Failed = 3;
            bad.Message = "7z 退出码 2";

            ArchiveResult locked = TestEnv.SampleResult("加密包.7z");
            locked.Status = ArchiveStatus.SkippedNeedsPassword; locked.Layers = 0; locked.Files = 0; locked.Failed = 0;

            ArchiveResult deep = TestEnv.SampleResult("太深.zip");
            deep.Status = ArchiveStatus.NotAttemptedDepthLimit; deep.Layers = 4; deep.Files = 0; deep.Failed = 0;

            Reporter.WriteSummary(new ArchiveResult[] { ok, bad, locked, deep }, TestEnv.TmpFile("s.txt"));
            string s = File.ReadAllText(TestEnv.TmpFile("s.txt"), Encoding.UTF8);

            AssertTrue(s.Contains("成功包.zip")); AssertTrue(s.Contains("失败包.rar"));
            AssertTrue(s.Contains("加密包.7z")); AssertTrue(s.Contains("太深.zip"));
            AssertTrue(s.Contains("归档总数：4（完成 1，部分失败 0，跳过 1，失败 1，未处理 1）"));
            AssertTrue(s.Contains("文件总数：7，失败文件数：3"));
            AssertTrue(s.Contains("跳过（需要密码）"));        // 中文结局文案（汇总表的「结果」列）
            AssertTrue(s.Contains("未处理（深度上限）"));
            AssertFalse(s.Contains("\uFFFD")); });

        // ---- 枚举里 7 个结局都要有自己的中文文案（漏一个就会在表里露出英文枚举名）----
        // 上一条只钉住了 4 个结局的文案；这条把 ArchiveStatus 的全部成员逐一钉住。
        H.Run("Reporter.TableLabelsEveryArchiveStatus", delegate {
            ArchiveStatus[] statuses = new ArchiveStatus[] {
                ArchiveStatus.Completed, ArchiveStatus.CompletedWithFailures, ArchiveStatus.SkippedNeedsPassword,
                ArchiveStatus.SkippedContainer, ArchiveStatus.SkippedUnreadable, ArchiveStatus.Failed,
                ArchiveStatus.NotAttemptedDepthLimit };
            string[] labels = new string[] {
                "完成", "完成（部分失败）", "跳过（需要密码）", "跳过（容器文档）", "跳过（无法读取）",
                "失败", "未处理（深度上限）" };

            ArchiveResult[] list = new ArchiveResult[statuses.Length];
            for (int i = 0; i < statuses.Length; i++)
            {
                list[i] = TestEnv.SampleResult("status-" + i + ".zip");
                list[i].Status = statuses[i];
            }

            string table = Reporter.RenderTable(list);
            AssertEq(table.Split('\n').Length, statuses.Length + 1);   // 表头 + 每个结局一行

            string problem = "";
            for (int i = 0; i < statuses.Length && problem.Length == 0; i++)
            {
                // 「 | 文案 | 」把这一格夹住：既钉住文案，也不会被别的格里的同名字串误命中。
                if (table.IndexOf(" | " + labels[i] + " | ", StringComparison.Ordinal) < 0)
                {
                    problem = statuses[i].ToString() + " 的文案不是 [" + labels[i] + "]，实际表=[" + table + "]";
                }
            }
            AssertEq(problem, ""); });
    }
}
