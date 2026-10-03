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

            // 【Task 12 修复轮 #2 Finding 1】第 8 个成员：致命中止 / 取消后没轮到处理的项。
            // 它必须有自己的一档计数 —— 否则归档总数把它算进来，而各类之和小于总数，
            // 读者会以为有归档从报告里凭空消失。
            ArchiveResult aborted = TestEnv.SampleResult("中止未处理.zip");
            aborted.Status = ArchiveStatus.NotAttemptedFatal; aborted.Layers = 0; aborted.Files = 0; aborted.Failed = 0;

            // 【Task 14】第 9 个成员：**取消**之后没轮到处理的项。它与「整批中止」必须分开计数与
            // 分开措辞 —— 取消在退出码裁定里是非致命的（取消 ⇒ 1），把非致命的事说成「整批中止」
            // 就是同一件事两种说法。这一档是 Task 12 留给 Task 14 的账，在这里当场钉住。
            ArchiveResult cancelled = TestEnv.SampleResult("取消未处理.zip");
            cancelled.Status = ArchiveStatus.NotAttemptedCancelled; cancelled.Layers = 0; cancelled.Files = 0; cancelled.Failed = 0;

            Reporter.WriteSummary(new ArchiveResult[] { ok, bad, locked, deep, aborted, cancelled }, TestEnv.TmpFile("s.txt"));
            string s = File.ReadAllText(TestEnv.TmpFile("s.txt"), Encoding.UTF8);

            AssertTrue(s.Contains("成功包.zip")); AssertTrue(s.Contains("失败包.rar"));
            AssertTrue(s.Contains("加密包.7z")); AssertTrue(s.Contains("太深.zip"));
            AssertTrue(s.Contains("中止未处理.zip")); AssertTrue(s.Contains("取消未处理.zip"));
            // 各类之和 == 归档总数（6 = 1+0+1+1+1+1+1），且分类与 CLI stdout 的汇总行逐字同形。
            AssertTrue(s.Contains("归档总数：6（完成 1，部分失败 0，跳过 1，失败 1，未处理（深度上限）1，未处理（整批中止）1，未处理（已取消）1）"));
            AssertTrue(s.Contains("文件总数：7，失败文件数：3"));
            AssertTrue(s.Contains("跳过（需要密码）"));        // 中文结局文案（汇总表的「结果」列）
            AssertTrue(s.Contains("未处理（深度上限）"));
            AssertTrue(s.Contains("未处理（整批中止）"));
            AssertTrue(s.Contains("未处理（已取消）"));        // 取消**绝不**与「整批中止」共用一行文案
            AssertFalse(s.Contains("\uFFFD")); });

        // ---- 枚举里**每一个**结局都要有自己的中文文案（漏一个就会在表里露出英文枚举名）----
        //
        // 【Task 12 修复轮 #2 Finding 2】覆盖**必须**由 Enum.GetValues 迭代来保证。上一版把 7 个成员
        // 手写成一个数组，于是第 8 个成员 NotAttemptedFatal 静默漏过：用例仍然全绿，而英文枚举名
        // 一路走到了用户看到的表格里（CLI 的 Program.PrintSummary 打印的就是 RenderTable）。
        // 手写清单的失效模式是**沉默** —— 它不认识的东西它根本不检查，所以「枚举里多了一个成员」
        // 这件事本身不会让它变红；迭代枚举则让新成员**自动**进入检查范围。逐条断言：
        //   1) 「结果」列那一格非空（有文案）；
        //   2) 那一格**不等于**枚举名本身（即真的被翻译过，而不是 StatusText 的兜底回显）；
        //   3) 那一格逐字等于 ExpectedLabel 钉住的中文（新成员忘了在那里补一行 ⇒ 也是失败，
        //      于是「忘记翻译」不可能再以任何形式通过）；
        //   4) 各成员文案两两不同（两个结局在表里长得一样，读者就分不出它们的区别）。
        H.Run("Reporter.TableLabelsEveryArchiveStatus", delegate {
            ArchiveStatus[] statuses = (ArchiveStatus[])Enum.GetValues(typeof(ArchiveStatus));
            AssertTrue(statuses.Length >= 9);   // 7 个原有成员 + 致命中止 + Task 14 的取消未处理；少一个说明枚举被改小了

            ArchiveResult[] list = new ArchiveResult[statuses.Length];
            for (int i = 0; i < statuses.Length; i++)
            {
                list[i] = TestEnv.SampleResult("status-" + i + ".zip");
                list[i].Status = statuses[i];
            }

            string table = Reporter.RenderTable(list);
            AssertEq(table.Split('\n').Length, statuses.Length + 1);   // 表头 + 每个结局恰好一行

            string problem = "";
            string[] labels = new string[statuses.Length];
            for (int i = 0; i < statuses.Length && problem.Length == 0; i++)
            {
                ArchiveStatus status = statuses[i];
                labels[i] = StatusCell(list[i]);
                string expected = ExpectedLabel(status);

                if (labels[i].Length == 0) { problem = status + " 在「结果」列里没有文案。表=[" + table + "]"; }
                else if (string.Equals(labels[i], status.ToString(), StringComparison.Ordinal)) { problem = status + " 露出的是英文枚举名（StatusText 漏了它的中文文案）"; }
                else if (expected == null) { problem = status + " 是本用例尚未钉住文案的成员：请在 ExpectedLabel 里补一行"; }
                else if (!string.Equals(labels[i], expected, StringComparison.Ordinal)) { problem = status + " 的文案是 [" + labels[i] + "]，期望 [" + expected + "]"; }
                // 「 | 文案 | 」把这一格夹住：既钉住它出现在「结果」列，也不会被别的格里的同名字串误命中。
                else if (table.IndexOf(" | " + labels[i] + " | ", StringComparison.Ordinal) < 0) { problem = status + " 的文案 [" + labels[i] + "] 不在「结果」列里：表=[" + table + "]"; }
            }
            AssertEq(problem, "");

            for (int i = 0; i < labels.Length && problem.Length == 0; i++)
            {
                for (int j = i + 1; j < labels.Length; j++)
                {
                    if (string.Equals(labels[i], labels[j], StringComparison.Ordinal))
                    {
                        problem = statuses[i] + " 与 " + statuses[j] + " 的文案相同（[" + labels[i] + "]）：读者分不出这两个结局";
                    }
                }
            }
            AssertEq(problem, ""); });
    }

    // 汇总表「结果」列（第 2 格）的文案：表格是纯文本，按分隔符 " | " 分格读**这一格**。
    // 刻意不在整张表里搜字符串：那样「这一格就是英文枚举名」会被别的格里的同名字串（例如判词里
    // 正好写了这个枚举名）掩盖，正是这条用例要防的退化。
    private static string StatusCell(ArchiveResult result)
    {
        string[] rows = Reporter.RenderTable(new ArchiveResult[] { result }).Split('\n');
        if (rows.Length < 2) { return ""; }
        string[] cells = rows[1].Split(new string[] { " | " }, StringSplitOptions.None);
        return cells.Length > 1 ? cells[1] : "";
    }

    // 每个成员**逐字**钉住的中文文案（规格 §6.9「结果」列）。
    // 【关键】这张表不是覆盖的来源，只是期望值：成员的**集合**由 Enum.GetValues 发现，
    // 新增成员在这里没有一行也会让用例失败（ExpectedLabel 返回 null ⇒ 上面判为问题）。
    private static string ExpectedLabel(ArchiveStatus status)
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
            case ArchiveStatus.NotAttemptedFatal: return "未处理（整批中止）";
            // Task 14：取消后的剩余项 —— 与「整批中止」文案必须不同（用例还会断言两两不同）。
            case ArchiveStatus.NotAttemptedCancelled: return "未处理（已取消）";
        }
        return null;
    }
}
