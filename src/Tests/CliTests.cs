// Task 12：CLI 契约的系统测试（无头驱动面）。
//
// 【为什么这一组用例一律起真进程】tests.exe 只编译 src\Core\*.cs + src\Tests\*.cs ——
// src\App\Program.cs **不在**这个目标里。CLI 的唯一可观察面就是 dist\Rerar.exe 本身：
// 参数解析、退出码、stdout/stderr、以及 --json-out 写出的那个文件。进程内断言在结构上够不着它。
//
// 【契约（Task 15 的验收脚本逐字依赖，不得改名）】
//     Rerar.exe --cli --target <path> [--target <path>...] [--delete] [--password <pw>]
//               [--dict <file>] [--depth <n>] [--json-out <file>]
//     退出码：0 全部成功；1 有失败/跳过；2 致命错误。
//     --json-out：机器可读数组，键恰好是 path,status,layers,files,failed,outputDir,message。
//
// 【本组用例钉住的性质】
//   * 不可读目标逐项报 SkippedUnreadable（具体中文原因），**绝不崩溃、绝不静默丢弃**、
//     也绝不把整批拖成致命中止（规格 §6.3 的「永不静默丢弃」、Review Focus #5）；
//   * 加密包绝不挂起（回归 ②：原文 .bat 在加密包上永久挂起）；
//   * I3 经 CLI 的钉子：没给 --delete 就绝不删原包；
//   * 控制方裁定 1：未处理数量只取自 RunSummary.NotAttempted，绝不与 Results 相加
//     （深度触顶那一项同时出现在两个集合里，相加会印刷成 2）；
//   * 控制方裁定 2：报告父目录由 CLI 创建；建不出来必须在**任何解压之前**以退出码 2 收场；
//   * 密码绝不进 stdout/stderr、也绝不进 JSON（Task 10 只记「候选序号 + 来源类别」）。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Diagnostics;
using System.IO;
using System.Text;

internal sealed class CliTests : TestBase
{
    public static void Run()
    {
        // ------------------------------------------------------------------
        // brief Step 1 的五条（名字逐字来自 task-12-brief.md）
        // ------------------------------------------------------------------

        H.Run("Cli.SelfTestExitsZero", delegate {
            CliResult r = TestEnv.RunCli("--selftest");

            // tests\smoke.ps1 依赖的既有契约：`--cli --selftest` 打印 version=<n> 并退出 0。
            AssertEq(r.ExitCode, 0);
            AssertTrue(r.StdOut.Trim().StartsWith("version=", StringComparison.Ordinal));
            AssertTrue(r.StdOut.Trim().Length > "version=".Length);
            AssertTrue(r.Arguments.IndexOf("--cli", StringComparison.Ordinal) >= 0);   // 无头模式要显式带 --cli
        });

        H.Run("Cli.ExtractsAndWritesJson", delegate {
            CliResult r = TestEnv.RunCli("--target", TestEnv.NestedZip, "--json-out", TestEnv.JsonOut);
            AssertEq(r.ExitCode, 0);

            string json = ReadJson(TestEnv.JsonOut);
            AssertHasStatus(json, "Completed");
            AssertJsonIsUtf8WithBomAndCompact(TestEnv.JsonOut, json);

            // 无头模式跑的是**完整**流程：产物真的落在盘上（默认原地，规格 §6.11）。
            AssertTrue(File.Exists(TestEnv.InPlaceOutOf(TestEnv.NestedZip, "inner", "hello.txt")));
            AssertTrue(File.Exists(TestEnv.NestedZip));            // I3：没给 --delete，原包必须还在
        });

        H.Run("Cli.NonExistentTargetReportedPerItem", delegate {   // Review Focus #5
            string missing = @"C:\nope\missing.zip";
            CliResult r = TestEnv.RunCli("--target", missing, "--json-out", TestEnv.JsonOut);

            AssertEq(r.ExitCode, 1);                               // 有跳过 ⇒ 1（既不是 0 也不是致命 2）
            string json = ReadJson(TestEnv.JsonOut);
            AssertHasStatus(json, "SkippedUnreadable");
            AssertTrue(json.IndexOf(JsonEscaped(missing), StringComparison.Ordinal) >= 0);   // 逐项如实报告
            AssertTrue(json.IndexOf("不存在", StringComparison.Ordinal) >= 0);                // 具体的中文原因
            AssertJsonIsUtf8WithBomAndCompact(TestEnv.JsonOut, json);
        });

        H.Run("Cli.PathWithTrailingSpaceHandled", delegate {                       // Review Focus #5
            CliResult r = TestEnv.RunCli("--target", TestEnv.PathWithTrailingSpace, "--json-out", TestEnv.JsonOut);
            AssertTrue(r.ExitCode == 0 || r.ExitCode == 1);

            // 两种结局都合法（Win32 的正常路径形式会把尾空格规整掉，所以它可能被正常解出、也可能被判
            // 不可读）；唯一不允许的是崩溃，或**静默丢弃** —— 所以这里要求 JSON 里确实有这一项。
            string json = ReadJson(TestEnv.JsonOut);
            AssertTrue(json.IndexOf("nested", StringComparison.Ordinal) >= 0);
            AssertTrue(json.IndexOf("\"status\":\"", StringComparison.Ordinal) >= 0);
        });

        H.Run("Cli.NeverHangsOnEncrypted", delegate {                              // 回归 ②
            Stopwatch sw = Stopwatch.StartNew();
            // 自带 60 秒上限：真的挂住时用例在 60 秒内 FAIL（被杀掉），而不是把整套测试挂住。
            CliResult r = TestEnv.RunCliWithTimeout(60000, "--target", TestEnv.AesZip, "--json-out", TestEnv.JsonOut);
            sw.Stop();

            AssertTrue(sw.Elapsed.TotalSeconds < 60);
            AssertEq(r.ExitCode, 1);                                              // 需要密码 ⇒ 跳过 ⇒ 1
            AssertHasStatus(ReadJson(TestEnv.JsonOut), "SkippedNeedsPassword");
            AssertTrue(File.Exists(TestEnv.AesZip));                              // 绝不删原包
        });

        // ------------------------------------------------------------------
        // 参数契约与两条控制方裁定
        // ------------------------------------------------------------------

        H.Run("Cli.MissingTargetExitsTwoWithChineseMessage", delegate {
            CliResult r = TestEnv.RunCli("--json-out", TestEnv.JsonOut);

            AssertEq(r.ExitCode, 2);                                              // 致命：什么都没跑
            AssertTrue(r.Output.IndexOf("--target", StringComparison.Ordinal) >= 0);
            AssertTrue(r.Output.IndexOf("未指定", StringComparison.Ordinal) >= 0);  // 清楚地说明缺了什么
            AssertFalse(File.Exists(TestEnv.JsonOut));                            // 没跑就不产出报告
        });

        H.Run("Cli.JsonOutParentDirectoryIsCreated", delegate {                   // 控制方裁定 2
            string report = Path.Combine(TestEnv.Tmp, "尚不存在", "子目录", "报告.json");
            CliResult r = TestEnv.RunCli("--target", TestEnv.NestedZip, "--json-out", report);

            // Task 8 的 Reporter 刻意不创建父目录；创建它是 CLI 的责任。
            AssertEq(r.ExitCode, 0);
            AssertTrue(File.Exists(report));
            AssertHasStatus(ReadJson(report), "Completed");
        });

        H.Run("Cli.FatalWhenReportDirectoryCannotBeCreated", delegate {            // 控制方裁定 2 的另一半
            string blocker = TestEnv.MakeFile("blocker.txt", "用文件挡住报告的父目录");
            string report = Path.Combine(blocker, "sub", "report.json");

            CliResult r = TestEnv.RunCli("--target", TestEnv.NestedZip, "--json-out", report);

            AssertEq(r.ExitCode, 2);                                              // 致命：报告建不出来就不该开跑
            AssertTrue(r.Output.IndexOf("目录", StringComparison.Ordinal) >= 0);
            // 「先失败再解压」：一个字节都不该写出去（否则用户会拿到一份没有报告的结果目录）。
            AssertFalse(Directory.Exists(TestEnv.InPlaceOutOf(TestEnv.NestedZip)));
            AssertTrue(File.Exists(TestEnv.NestedZip));
        });

        H.Run("Cli.DepthFlagListsUnprocessedWithoutDoubleCounting", delegate {      // 控制方裁定 1
            CliResult r = TestEnv.RunCli("--depth", "1", "--target", TestEnv.DeepNestedZip,
                "--json-out", TestEnv.JsonOut);
            AssertEq(r.ExitCode, 1);

            string json = ReadJson(TestEnv.JsonOut);
            AssertHasStatus(json, "NotAttemptedDepthLimit");

            // 深度触顶那一项**同时**在 Results 与 NotAttempted 里：只数 NotAttempted 才是 1，
            // 两个集合相加会印成 2。计数来源也写在输出里，读者不必猜。
            AssertTrue(r.StdOut.IndexOf("未处理数量：1", StringComparison.Ordinal) >= 0);
            AssertTrue(r.StdOut.IndexOf("NotAttempted", StringComparison.Ordinal) >= 0);
            AssertTrue(r.StdOut.IndexOf("mid.zip", StringComparison.Ordinal) >= 0);   // 未处理项逐个列出
            AssertTrue(File.Exists(TestEnv.DeepNestedZip));                           // 绝不删原包
        });

        H.Run("Cli.JsonKeysMatchTheCrossTaskContract", delegate {
            CliResult r = TestEnv.RunCli("--target", TestEnv.NestedZip, "--json-out", TestEnv.JsonOut);
            AssertEq(r.ExitCode, 0);
            AssertJsonKeyOrder(ReadJson(TestEnv.JsonOut));
        });

        H.Run("Cli.JsonEscapesQuotesBackslashesAndChinese", delegate {
            string chinese = TestEnv.ChineseNamedZip;

            // 引号在 Windows 文件名里不可能存在 —— 所以只能拿一个**不存在的**目标来测转义
            // （路径原样进结果与判词：`C:\nope\quote"name.zip`）。
            string quoted = "C:\\nope\\quote\"name.zip";
            // 控制字符同理：换行只可能出现在参数里（判词会把路径原样带上），不可能出现在文件名里。
            string multiline = "C:\\nope\\line\nbreak.zip";

            CliResult r = TestEnv.RunCli("--target", chinese, "--target", quoted, "--target", multiline,
                "--json-out", TestEnv.JsonOut);
            AssertEq(r.ExitCode, 1);

            string json = ReadJson(TestEnv.JsonOut);
            AssertHasStatus(json, "Completed");                    // 中文名那一项真的解出来了
            AssertHasStatus(json, "SkippedUnreadable");            // 另外两项逐项报不可读
            AssertTrue(json.IndexOf(JsonEscaped(chinese), StringComparison.Ordinal) >= 0);
            AssertTrue(json.IndexOf(JsonEscaped(quoted), StringComparison.Ordinal) >= 0);
            AssertTrue(json.IndexOf(JsonEscaped(multiline), StringComparison.Ordinal) >= 0);

            // 反证：未被转义的裸形式绝不能出现（裸控制字符会让整份 JSON 非法）。
            AssertTrue(json.IndexOf("quote\"name.zip", StringComparison.Ordinal) < 0);
            AssertJsonIsUtf8WithBomAndCompact(TestEnv.JsonOut, json);
        });

        // ------------------------------------------------------------------
        // I3 与密码卫生经 CLI 的钉子
        // ------------------------------------------------------------------

        H.Run("Cli.DeleteStaysOffUnlessFlagPassed", delegate {
            CliResult r = TestEnv.RunCli("--target", TestEnv.NestedZip, "--json-out", TestEnv.JsonOut);
            AssertEq(r.ExitCode, 0);
            AssertHasStatus(ReadJson(TestEnv.JsonOut), "Completed");   // 唯一允许被删除的结局
            AssertTrue(File.Exists(TestEnv.NestedZip));                // 但没给 --delete ⇒ 原包必须还在
        });

        H.Run("Cli.PasswordFlagNeverAppearsInOutput", delegate {
            CliResult r = TestEnv.RunCli("--target", TestEnv.ImportedDictZip, "--password", "ImportedSecret",
                "--json-out", TestEnv.JsonOut);
            AssertEq(r.ExitCode, 0);

            string json = ReadJson(TestEnv.JsonOut);
            AssertHasStatus(json, "Completed");                        // 密码真的被用上了
            AssertTrue(json.IndexOf("手动输入", StringComparison.Ordinal) >= 0);   // 只记来源类别，不记值
            AssertFalse(r.Output.IndexOf("ImportedSecret", StringComparison.Ordinal) >= 0);
            AssertFalse(json.IndexOf("ImportedSecret", StringComparison.Ordinal) >= 0);
        });

        H.Run("Cli.DictFlagFeedsThePasswordLadder", delegate {
            // ImportedDictZip 的口令（ImportedSecret）刻意**不在**内置字典里（Extract.ImportedDictionaryHitIsLabelled
            // 同理），所以命中只可能来自导入字典这一层 —— 这就是 --dict 真的被读进去的证据。
            string dict = TestEnv.MakeFile("dict.txt", "wrong1\r\nImportedSecret\r\n");
            CliResult r = TestEnv.RunCli("--target", TestEnv.ImportedDictZip, "--dict", dict,
                "--json-out", TestEnv.JsonOut);
            AssertEq(r.ExitCode, 0);

            string json = ReadJson(TestEnv.JsonOut);
            AssertHasStatus(json, "Completed");
            AssertTrue(json.IndexOf("导入字典", StringComparison.Ordinal) >= 0);
        });

        H.Run("Cli.BatchContinuesAfterUnreadableTarget", delegate {
            string missing = TestEnv.TmpFile("no-such-archive.zip");
            CliResult r = TestEnv.RunCli("--target", missing, "--target", TestEnv.NestedZip,
                "--json-out", TestEnv.JsonOut);

            AssertEq(r.ExitCode, 1);                                   // 有一项跳过 ⇒ 1（绝不是致命中止 2）
            string json = ReadJson(TestEnv.JsonOut);
            AssertHasStatus(json, "SkippedUnreadable");
            AssertHasStatus(json, "Completed");                        // 坏的那一项绝不拖垮整批
            AssertTrue(File.Exists(TestEnv.InPlaceOutOf(TestEnv.NestedZip, "inner", "hello.txt")));
        });

        // `--target ""`：Extractor.FreezeTargets 对这种值会**静默跳过**（空串没有路径可言），
        // 所以 CLI 自己把它补成一条 SkippedUnreadable —— 逐项可见、且不影响同一批里的其它目标。
        H.Run("Cli.EmptyTargetIsReportedNotDropped", delegate {
            CliResult r = TestEnv.RunCli("--target", "", "--target", TestEnv.NestedZip,
                "--json-out", TestEnv.JsonOut);

            AssertEq(r.ExitCode, 1);
            string json = ReadJson(TestEnv.JsonOut);
            AssertHasStatus(json, "SkippedUnreadable");
            AssertTrue(json.IndexOf("目标路径为空", StringComparison.Ordinal) >= 0);
            AssertHasStatus(json, "Completed");                        // 空参数绝不拖垮整批
            AssertTrue(File.Exists(TestEnv.InPlaceOutOf(TestEnv.NestedZip, "inner", "hello.txt")));
        });

        // 畸形调用一律**在解压之前**以 2 收场（「致命错误 = 什么都没跑成」），
        // 且绝不能静默忽略一个拼错的开关。
        H.Run("Cli.MalformedInvocationExitsTwoWithoutExtracting", delegate {
            string nested = TestEnv.NestedZip;

            AssertEq(TestEnv.RunCli("--depth", "0", "--target", nested).ExitCode, 2);
            AssertEq(TestEnv.RunCli("--depth", "不是数字", "--target", nested).ExitCode, 2);
            AssertEq(TestEnv.RunCli("--deleet", "--target", nested).ExitCode, 2);
            AssertEq(TestEnv.RunCli("--target").ExitCode, 2);           // 开关后面没有值

            CliResult dict = TestEnv.RunCli("--target", nested, "--dict", TestEnv.TmpFile("no-such-dict.txt"));
            AssertEq(dict.ExitCode, 2);
            AssertTrue(dict.Output.IndexOf("字典", StringComparison.Ordinal) >= 0);

            // 「什么都没跑成」必须是字面意义上的：一个字节都没解出来。
            AssertFalse(Directory.Exists(TestEnv.InPlaceOutOf(nested)));
            AssertTrue(File.Exists(nested));
        });
    }

    // ------------------------------------------------------------------
    // 断言助手
    // ------------------------------------------------------------------

    // 报告必须真的写出来了（否则后面读它会抛 FileNotFoundException，用例的失败原因会跑偏）。
    private static string ReadJson(string path)
    {
        AssertTrue(File.Exists(path));
        return File.ReadAllText(path);
    }

    private static void AssertHasStatus(string json, string status)
    {
        AssertTrue(json.IndexOf("\"status\":\"" + status + "\"", StringComparison.Ordinal) >= 0);
    }

    // 报告的编码与形状：UTF-8 **带 BOM**（规格 §2 第 6 条 / §3：报告一律 BOM，否则 PowerShell 5.1
    // 会按系统代码页读中文），紧凑**单行**（字符串里的任何控制字符都必须已转义 —— JSON 不允许裸控制字符）。
    private static void AssertJsonIsUtf8WithBomAndCompact(string path, string json)
    {
        byte[] head = File.ReadAllBytes(path);
        AssertTrue(head.Length >= 3);
        AssertEq(head[0], (byte)0xEF);
        AssertEq(head[1], (byte)0xBB);
        AssertEq(head[2], (byte)0xBF);

        AssertTrue(json.Length > 1);
        AssertEq(json[0], '[');
        AssertEq(json[json.Length - 1], ']');
        for (int i = 0; i < json.Length; i++)
        {
            AssertTrue(json[i] >= ' ');
        }
    }

    // 键名与顺序是跨任务契约（Task 8 的 CSV 列名逐字相同）：path,status,layers,files,failed,outputDir,message。
    private static void AssertJsonKeyOrder(string json)
    {
        string[] keys = new string[] { "path", "status", "layers", "files", "failed", "outputDir", "message" };

        int at = json.IndexOf('{');
        AssertTrue(at >= 0);
        for (int i = 0; i < keys.Length; i++)
        {
            int found = json.IndexOf("\"" + keys[i] + "\":", at, StringComparison.Ordinal);
            AssertTrue(found >= 0);            // 键必须存在
            at = found + 1;                    // 且必须按契约顺序出现
        }
    }

    // 与 CLI 的 JSON 转义规则一致（JSON 标准：引号、反斜杠、控制字符）。
    // 用例用它拼出「路径在报告里应当长什么样」，而不是假设它原样出现。
    private static string JsonEscaped(string value)
    {
        StringBuilder sb = new StringBuilder();
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }
}
