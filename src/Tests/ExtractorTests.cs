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
//   * 预检安全上限（§9.1 用例 10「zip 炸弹 → 预检拒绝，不写盘」；§6.9 的真实风险）：解压比 /
//     单包总字节 / 条目数三条上限，在任何写入之前生效；拒绝判词必须写明是安全上限、只跳过该项、
//     整批继续、原包保留；「上限算不出来」时保守拒绝，绝不降级成直接解压。
//   * 「强制按压缩包尝试」（§6.1 的逐项动作 + §6.3「永不静默丢弃」）：RunOptions.ForceTreatAsArchive
//     只覆盖格式门控，I1/I2/I3/I4 与上述安全上限一律照旧。
//
// 全部用例都是「端到端跑一遍 Extractor」或「直接喂纯函数」两种形状之一，没有 mock 掉被测逻辑。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
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
            // 修复轮 #3 的 Finding 1：触顶那一项**同时**是权威未处理清单的一员
            //（只放进 Results 会让打印 NotAttempted 的消费方少报未处理项）。
            AssertEq(s.NotAttempted.Count, 1);
            AssertEq(s.NotAttempted[0], Path.Combine(TestEnv.OutOf(TestEnv.DeepNestedZip), "mid.zip"));
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

        // 最终修复轮 Finding 3：`allMembersHaveFullSignature` 必须真的由产品传进来。
        //
        // 形状：x.zip.001 与 x.zip.002 **各自**都是一份完整的独立 zip（不是 7-Zip 切出来的分卷，
        // 只是名字撞上了分卷命名）。只按名字分组时它们会被并成一个「分卷集」，权威成员恒为 .001，
        // 于是 .002 里的东西**永远解不出来**（用户看到两个正常的包只解出一个）。产品已经逐片按真实
        // 字节嗅探（Sniffer），所以这条判据是可得的：每一片自身都是完整归档 ⇒ 按独立文件处理。
        H.Run("Extract.IndependentArchivesWithVolumeNamesStayIndependent", delegate {
            string dir = Path.Combine(TestEnv.Tmp, "independent");
            Directory.CreateDirectory(dir);

            string first = Path.Combine(dir, "x.zip.001");
            string second = Path.Combine(dir, "x.zip.002");
            File.Copy(TestEnv.PlainZip, first, true);      // 完整 zip：a.txt / b.txt / docs/readme.md
            File.Copy(TestEnv.NestedZip, second, true);    // 完整 zip：inner.zip + readme.txt

            // 前提自检：两片**各自**都得是一份真能列出来的完整归档。任一片不是，本用例什么也证明不了
            // （那种情况下「按分卷集处理」反而是对的）—— 所以这里当场断言，绝不静默弱化。
            ArchiveIndex firstIndex = SevenZipIndex.Read(TestEnv.SevenZip, first, null);
            AssertFalse(firstIndex.ListingFailed);
            AssertTrue(firstIndex.FileCount > 0);

            ArchiveIndex secondIndex = SevenZipIndex.Read(TestEnv.SevenZip, second, null);
            AssertFalse(secondIndex.ListingFailed);
            AssertTrue(secondIndex.FileCount > 0);

            RunSummary s = TestEnv.RunExtractMany(first, second);

            // 结果条数 = 2 个目标 + 从 .002 里解出来的内层 inner.zip。第 3 条本身又是一份证据：
            // 被并成一个「分卷集」时 .002 根本不会被打开，这条递归结果也就不会存在。
            AssertEq(s.Results.Count, 3);
            AssertEq(s.Results[0].Status, ArchiveStatus.Completed);
            AssertEq(s.Results[1].Status, ArchiveStatus.Completed);

            // 每个包解到**自己的**输出目录：.002 的内容绝不是被并进 .001 的「分卷集」而消失。
            AssertTrue(File.Exists(Path.Combine(s.Results[0].OutputDir, "a.txt")));
            AssertTrue(File.Exists(Path.Combine(s.Results[1].OutputDir, "inner", "hello.txt")));
            AssertFalse(string.Equals(s.Results[0].OutputDir, s.Results[1].OutputDir,
                StringComparison.OrdinalIgnoreCase));

            // 判词里不得出现「分卷集」：这两个包从来不是一个卷集（更不是「缺卷」）。
            AssertFalse(s.Results[0].Message.Contains("分卷集"));
            AssertFalse(s.Results[1].Message.Contains("分卷集"));
            AssertTrue(File.Exists(first));                // I3：默认不删
            AssertTrue(File.Exists(second));
        });

        // ------------------------------------------------------------------
        // 复审 Critical 的回归：分卷集的独立性判别必须**按族**且要求**完整**，
        // 绝不能是「每片都有一个归档 magic」。
        //
        // 7-Zip 造不出 rar（tests\fixtures.ps1 的构造事实 1），所以这里的 rar 夹具是**手写的
        // 签名正确字节**：解析器的裁决只看头部签名，签名正确就足以钉住分组决定（真正的 rar
        // 内容与否不影响这条判据）。两片都不是真 rar ⇒ 7-Zip 必然读不出来 ⇒「交给 7-Zip 的是
        // 哪一片」无法从结局里观察，因此前两条用 ResolveByProduct 直接钉**解析器的裁决**本身。
        // ------------------------------------------------------------------

        // 真 RAR 分卷集的形状：**每一卷都以 RAR5 签名开头**（规格 §6.6「RAR 每卷有独立头」）。
        // 旧的判据「每片都有归档签名」在这里恒真，于是真分卷集被切成两个独立文件：part1.rar 被
        // 丢弃、part2.rar 被单独交给 7-Zip —— 一个完好的集合被报成「不是压缩包或已损坏」。
        H.Run("Extract.RarVolumePairWithSignaturesIsStillOneSet", delegate {
            string dir = Path.Combine(TestEnv.Tmp, "rar-set");
            Directory.CreateDirectory(dir);
            string part1 = Path.Combine(dir, "x.part1.rar");
            string part2 = Path.Combine(dir, "x.part2.rar");

            WriteRar5Stub(part1, 512);
            WriteRar5Stub(part2, 512);

            // 前提自检（也是非平凡性证明）：两片都认得出 RAR5 签名 ⇒ 那条错误判据在这份夹具上
            // 必然为真。若这条前提不成立，本用例证明不了任何东西，所以当场断言、绝不静默弱化。
            AssertEq(SniffOf(part1), SniffKind.Rar5);
            AssertEq(SniffOf(part2), SniffKind.Rar5);

            // 产品裁决：仍是一个分卷集，权威成员是 .part1.rar，两片都在集里、不缺卷。
            ArchiveTask task = new ArchiveTask();
            string authoritative = ResolveByProduct(part2, task);

            AssertEq(Path.GetFileName(authoritative), "x.part1.rar");
            AssertTrue(task.VolumeMembers != null);              // null = 被判成了独立文件（就是那个 Critical）
            AssertEq(task.VolumeMembers.Count, 2);
            AssertEq(Path.GetFileName(task.VolumeMembers[0]), "x.part1.rar");
            AssertEq(Path.GetFileName(task.VolumeMembers[1]), "x.part2.rar");
        });

        // 同一族走**完整产品路径**：三片 RAR5 标记、缺中间卷 ⇒ 必须报「缺具体哪一个」，
        // 而不是把候选单独交给 7-Zip 得到「不是压缩包或已损坏」。
        H.Run("Extract.RarVolumeSetReportsMissingVolumeNotCorruption", delegate {
            string dir = Path.Combine(TestEnv.Tmp, "rar-gap");
            Directory.CreateDirectory(dir);
            string part1 = Path.Combine(dir, "y.part1.rar");
            string part2 = Path.Combine(dir, "y.part2.rar");
            WriteRar5Stub(part1, 64);
            WriteRar5Stub(part2, 64);
            WriteRar5Stub(Path.Combine(dir, "y.part4.rar"), 64);      // 故意缺 y.part3.rar

            RunSummary s = TestEnv.RunExtract(part2);

            AssertEq(s.Results[0].Status, ArchiveStatus.SkippedUnreadable);
            AssertTrue(s.Results[0].Message.Contains("缺"));            // 规格 §6.6：报具体缺哪一个
            AssertTrue(s.Results[0].Message.Contains("y.part3.rar"));
            AssertFalse(s.Results[0].Message.Contains("已损坏"));        // 这正是要消弭的误报
            AssertTrue(File.Exists(part1));
            AssertTrue(File.Exists(part2));
        });

        // §9.2④ 全不处置守卫必须对**解析出来的分卷集**重新生效：候选是第二卷、删除开关打开，
        // 权威成员（第一卷）真能读 ⇒ 走得到删除路径 ⇒ 守卫必须把两个成员**都**留下并说明原因。
        // 两片都是完整 zip 是刻意的：要让删除路径真的被走到（家族由**名字**决定，见规格 §6.6）。
        H.Run("Extract.RarVolumeSetDeleteGuardKeepsEveryMember", delegate {
            string dir = Path.Combine(TestEnv.Tmp, "rar-delete-guard");
            Directory.CreateDirectory(dir);
            string part1 = Path.Combine(dir, "z.part1.rar");
            string part2 = Path.Combine(dir, "z.part2.rar");
            File.Copy(TestEnv.PlainZip, part1, true);
            File.Copy(TestEnv.PlainZip, part2, true);

            RunSummary s = TestEnv.RunExtractWithDelete(part2);        // 以第二卷为候选

            // 交给 7-Zip 的是 .part1.rar（可读）—— 若候选自己被当作独立文件交给 7-Zip，
            // 这里也会 Completed（它同样是完整 zip）；所以真正的证据是下面那条守卫判词。
            AssertEq(s.Results[0].Status, ArchiveStatus.Completed);
            AssertTrue(s.Results[0].Message.Contains("分卷集"));          // §9.2④ 守卫真的触发了
            AssertTrue(File.Exists(part1));                            // 「全部不处置」：一个都没少
            AssertTrue(File.Exists(part2));
            AssertTrue(File.Exists(Path.Combine(s.Results[0].OutputDir, "a.txt")));
        });

        // 2 分卷 WinZip 形状：.z01 是一份完整 zip，最后的 .zip **只有尾部 EOCD、头部无签名**
        // （Sniffer 判 DamagedHeader —— 这是合法形状，中央目录就在 .zip 里）。DamagedHeader 绝
        // 不能算「完整归档」，否则 .z01 + .zip 两片都"有签名"，这个真分卷集会被切成两个独立包。
        H.Run("Extract.WinZipSplitPairWithEocdOnlyZipIsStillOneSet", delegate {
            string dir = Path.Combine(TestEnv.Tmp, "winzip-set");
            Directory.CreateDirectory(dir);
            string z01 = Path.Combine(dir, "w.z01");
            string zip = Path.Combine(dir, "w.zip");

            File.Copy(TestEnv.PlainZip, z01, true);                    // 完整 zip：带头签名 + 自己的 EOCD
            WriteEocdOnlyStub(zip, 256);                               // 只有尾部 EOCD、头部无签名

            AssertEq(SniffOf(z01), SniffKind.Zip);
            AssertEq(SniffOf(zip), SniffKind.DamagedHeader);

            ArchiveTask task = new ArchiveTask();
            string authoritative = ResolveByProduct(z01, task);        // 以 .z01 为候选

            AssertEq(Path.GetFileName(authoritative), "w.zip");        // WinZip 方案的权威成员是最后的 .zip
            AssertTrue(task.VolumeMembers != null);                    // null = 被判成独立文件
            AssertEq(task.VolumeMembers.Count, 2);
            AssertEq(Path.GetFileName(task.VolumeMembers[0]), "w.z01");
            AssertEq(Path.GetFileName(task.VolumeMembers[1]), "w.zip");
        });

        // 同一形状走**完整产品路径**，并额外钉住「一片只有头部签名、没有自己的 EOCD」不算完整归档：
        // 中间卷 v.z01 带 zip 头签名但没有 EOCD（分卷的中间段就是这样），最后的 v.zip 才是权威成员
        // 且真能读。旧判据只看"有签名" ⇒ 两片都算完整 ⇒ 切成独立包 ⇒ v.z01 被单独交给 7-Zip 失败。
        H.Run("Extract.WinZipSplitVolumeUsesZipAsAuthoritative", delegate {
            string dir = Path.Combine(TestEnv.Tmp, "winzip-authoritative");
            Directory.CreateDirectory(dir);
            string z01 = Path.Combine(dir, "v.z01");
            string zip = Path.Combine(dir, "v.zip");

            WriteZipHeadStub(z01, 512);
            File.Copy(TestEnv.PlainZip, zip, true);

            AssertEq(SniffOf(z01), SniffKind.Zip);
            AssertEq(SniffOf(zip), SniffKind.Zip);

            RunSummary s = TestEnv.RunExtract(z01);                    // 以中间卷为候选

            AssertEq(s.Results[0].Status, ArchiveStatus.Completed);     // 交给 7-Zip 的是 .zip
            AssertTrue(File.Exists(Path.Combine(s.Results[0].OutputDir, "a.txt")));
            AssertTrue(File.Exists(z01));
            AssertTrue(File.Exists(zip));
        });

        // 复审相邻项：孤零零一个 m.z01（权威成员 m.zip 不在）必须报「缺 m.zip」—— 纯函数
        // Volume.ZipWithoutLeadIsMissingItself 早就这么判了，产品侧的 `Members.Count < 2` 门槛
        // 却把它丢掉、把 m.z01 原样交给 7-Zip，于是又变成「不是压缩包或已损坏」。
        H.Run("Extract.LoneZ01ReportsMissingZipNotCorruption", delegate {
            string dir = Path.Combine(TestEnv.Tmp, "lone-z01");
            Directory.CreateDirectory(dir);
            string z01 = Path.Combine(dir, "m.z01");
            WriteZipHeadStub(z01, 512);

            RunSummary s = TestEnv.RunExtract(z01);

            AssertEq(s.Results[0].Status, ArchiveStatus.SkippedUnreadable);
            AssertTrue(s.Results[0].Message.Contains("m.zip"));
            AssertFalse(s.Results[0].Message.Contains("已损坏"));
            AssertTrue(File.Exists(z01));
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

        // 【修复轮 1 / 发现 1】伪装后缀的包必须**解得开**，不是「中止该项」。
        // 形状：photo.jpg 是一个真 zip。输出名由归档名消毒而来，而 PathSanitizer 只剥「真压缩包」
        // 后缀（伪装后缀按 §6.11 保留原样）⇒ 原地输出时消毒后的目标名**正好等于源归档自己的路径**。
        // 修复前命中「目标被同名文件占用 ⇒ 中止该归档」，于是任何伪装后缀的包都永远解不开 ——
        // 而伪装/改名后的网盘包正是本工具的目标场景（规格 §9.1 行 5：识别并解压，宿主保留）。
        // 正确处置：换一个不冲突的名字（"photo.jpg (2)"），归档与产物因此可区分；宿主逐字节保留。
        H.Run("Extract.CloakedExtensionArchiveExtractsBesideItself", delegate {
            string cloaked = CopyToTmp(TestEnv.PlainZip, "photo.jpg");
            long hostBytes = new FileInfo(cloaked).Length;

            RunSummary s = TestEnv.RunExtractInPlace(cloaked);

            AssertEq(s.Results[0].Status, ArchiveStatus.Completed);
            AssertEq(s.Results[0].OutputDir, TestEnv.TmpFile("photo.jpg (2)"));
            AssertTrue(File.Exists(Path.Combine(TestEnv.TmpFile("photo.jpg (2)"), "a.txt")));
            AssertEq(s.Results[0].OutputDir, Path.Combine(TestEnv.Tmp, "photo.jpg (2)"));   // 绝不等于源归档
            AssertTrue(File.Exists(cloaked));                                            // 宿主保留
            AssertEq(new FileInfo(cloaked).Length, hostBytes);                           // 且逐字节原样
            AssertEq(File.ReadAllText(cloaked, Encoding.UTF8).Length,
                File.ReadAllText(TestEnv.PlainZip, Encoding.UTF8).Length);               // 内容仍是原包
        });

        // 反向守门（证明上面那条修复**没有**放松 I2）：目标被一个**不相干的**同名文件占用时，
        // 照旧「中止该归档、不回落父目录、不覆盖那个文件、不删原包」。
        H.Run("Extract.UnrelatedOccupantStillAbortsInsteadOfRenaming", delegate {
            string archive = CopyToTmp(TestEnv.PlainZip, "occupied.zip");
            string occupant = TestEnv.TmpFile("occupied");      // 消毒后的目标名，被一个**别人**的文件占着
            File.WriteAllText(occupant, "别人放在这儿的文件", new UTF8Encoding(false));

            RunSummary s = TestEnv.RunExtractInPlace(archive);

            AssertEq(s.Results[0].Status, ArchiveStatus.SkippedUnreadable);
            AssertTrue(s.Results[0].Message.IndexOf("被同名文件占用", StringComparison.Ordinal) >= 0);
            // 不回落父目录：Tmp 里既不能多出那个目录，也不能多出任何「(2)」变体。
            AssertFalse(Directory.Exists(occupant));
            AssertFalse(Directory.Exists(TestEnv.TmpFile("occupied (2)")));
            // 占位文件逐字节未变（绝不覆盖、绝不删别人的文件），原包也在。
            AssertEq(File.ReadAllText(occupant, Encoding.UTF8), "别人放在这儿的文件");
            AssertTrue(File.Exists(archive));
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

        // ==================================================================
        // 修复轮 #1：压缩炸弹 / 解压比预检上限（规格 §9.1 用例 10 与 §9.2；审计 F2）
        //
        // 三条要求，缺一条这个上限就等于没实现：
        //   * 靠**索引**算（解压后总字节、条目数），不靠猜测；
        //   * 在**任何写入之前**生效 —— 高压缩比先打满的是磁盘，海量条目先打满的是文件系统
        //     （MFT / 配额 / 杀软逐文件扫描），两者都不能等到写起来才发现；
        //   * 拒绝判词必须让人看出这是**安全上限**而不是「文件坏了」，处置是**跳过这一项、
        //     整批继续**（不是致命档），原包一律保留。
        // ==================================================================

        // 端到端：真·高压缩比包（16 MiB 全零 → 几十 KB）。一个字节都不写盘、原包保留，
        // 并且**同一批的下一个归档照常解出** —— 证明处置是「跳过这一项」而不是「中止整批」。
        H.Run("Extract.BombRejectedByCapsWithNothingWritten", delegate {
            string bomb = TestEnv.BombZip;
            string plain = TestEnv.PlainZip;

            RunSummary s = TestEnv.RunExtractMany(bomb, plain);

            AssertEq(s.Results[0].Status, ArchiveStatus.SkippedUnreadable);
            AssertTrue(s.Results[0].Message.Contains("安全上限"));
            // 判词必须点名是**哪一条**上限、并且让用户能区分「安全上限」与「文件坏了」。
            AssertTrue(s.Results[0].Message.Contains("解压比"));
            AssertTrue(s.Results[0].Message.Contains("不是文件损坏"));
            AssertTrue(s.FatalReason == null);                       // 不是致命档：整批继续
            AssertTrue(File.Exists(bomb));                          // 原包保留
            AssertFalse(Directory.Exists(TestEnv.OutOf(bomb)));      // 没写盘：连输出目录都没有

            AssertEq(s.Results[1].Status, ArchiveStatus.Completed);  // 下一个归档照常处理
            AssertTrue(File.Exists(TestEnv.OutOf(plain, "a.txt")));

            // 输出根下只允许留下 plain 的那一个产物目录（没有暂存残留、没有炸弹的半个目录）。
            string[] produced = Directory.GetDirectories(TestEnv.OutRoot);
            AssertEq(produced.Length, 1);
        });

        // 条目数上限的判定与边界（纯函数；真归档那一条见下一条用例）。
        // 边界一并钉住：**正好**上限必须放行 —— 否则「上限」可以靠「全都拒」通过。
        H.Run("Extract.CapsRejectOverCountIndex", delegate {
            ArchiveIndex over = Index(Preflight.MaxEntriesPerArchive + 1, 1024, false, false);
            PreflightReport report = Preflight.CheckExpansion(over, 1024);
            AssertFalse(report.Ok);
            AssertTrue(report.Reason.Contains("条目"));

            ArchiveIndex atCap = Index(Preflight.MaxEntriesPerArchive, Preflight.MaxEntriesPerArchive, false, false);
            AssertTrue(Preflight.CheckExpansion(atCap, Preflight.MaxEntriesPerArchive).Ok);
        });

        // 条目数上限的**端到端**那一半：真归档（10 万 + 1 条零长度条目）。
        // 它同时是「总字节 == 0 但 FileCount > 0 仍然可度量」的活样本（零字节不触发体积上限），
        // 所以这条只可能被**条目数**上限拦住 —— 判词必须点名「条目」，且一个字节都不写盘。
        H.Run("Extract.EntryFloodArchiveRejectedWithNothingWritten", delegate {
            string flood = TestEnv.EntryFloodZip;
            RunSummary s = TestEnv.RunExtract(flood);

            AssertEq(s.Results[0].Status, ArchiveStatus.SkippedUnreadable);
            AssertTrue(s.Results[0].Message.Contains("安全上限"));
            AssertTrue(s.Results[0].Message.Contains("条目"));
            AssertTrue(s.FatalReason == null);                     // 不是致命档
            AssertFalse(Directory.Exists(TestEnv.OutOf(flood)));    // 不写盘
            AssertTrue(File.Exists(flood));                        // 原包保留
        });

        // 解压比与单包总字节两条上限各自单独触发（不能被对方掩盖）。
        H.Run("Extract.CapsRejectHighRatioAndHugeIndex", delegate {
            ArchiveIndex ratio = Index(1, 1024L * 1024 * 1024, false, false);      // 1 GiB / 1 KiB
            PreflightReport r1 = Preflight.CheckExpansion(ratio, 1024);
            AssertFalse(r1.Ok);
            AssertTrue(r1.Reason.Contains("解压比"));

            // 分母取「包自身大小」，所以这条只可能被总字节上限拦住（比值约 1 倍）。
            ArchiveIndex huge = Index(1, Preflight.MaxTotalUncompressedBytes + 1, false, false);
            PreflightReport r2 = Preflight.CheckExpansion(huge, Preflight.MaxTotalUncompressedBytes);
            AssertFalse(r2.Ok);
            AssertTrue(r2.Reason.Contains("总字节"));
        });

        // **算不出上限就不放行**（这是头部加密那条 caveat 的落地形状，见 Preflight 的注释）：
        // 清单不可用、或索引里没有可度量的条目时，保守默认是**拒绝**，绝不降级成「那就直接解压吧」。
        H.Run("Extract.CapsRejectUnmeasurableIndex", delegate {
            AssertFalse(Preflight.CheckExpansion(null, 1024).Ok);
            AssertFalse(Preflight.CheckExpansion(Index(0, 0, false, false), 1024).Ok);   // 单流/空包：无成员可量
            AssertFalse(Preflight.CheckExpansion(Index(3, 9, true, false), 1024).Ok);    // 清单读取失败

            AssertTrue(Preflight.CheckExpansion(Index(3, 9, false, false), 1024).Ok);    // 普通包照常放行
        });

        // 普通归档绝不能被上限拒：否则「上限」可以靠「把一切都拒掉」假通过。
        H.Run("Extract.NormalArchiveNotRejectedByCaps", delegate {
            RunSummary s = TestEnv.RunExtract(TestEnv.PlainZip);
            AssertEq(s.Results[0].Status, ArchiveStatus.Completed);
            AssertFalse(s.Results[0].Message.Contains("安全上限"));
            AssertTrue(File.Exists(TestEnv.OutOf(TestEnv.PlainZip, "a.txt")));
        });

        // ==================================================================
        // 修复轮 #2：「强制按压缩包尝试」的逐项覆盖（规格 §6.1 的逐项动作 + §6.3 的「永不静默丢弃」）
        //
        // 覆盖范围只有**格式门控**：I1（索引比对）、I2（暂存→校验→提交）、I3（失败绝不删）、
        // I4（容器文档门控）与 #1 的安全上限全部照旧适用。所以：强制一份 Sniffer 不认识但
        // 7-Zip 认识的真归档 ⇒ 正常解出；强制一份非归档 ⇒ 如实失败、绝不报成功、原包保留。
        // ==================================================================

        H.Run("Extract.ForcedUnknownFormatIsAttempted", delegate {
            string wim = TestEnv.UnknownFormatWim;

            // 先证明它**默认会被格式门控跳过**（否则这条用例根本没测到「强制」这件事）。
            RunSummary skipped = TestEnv.RunExtract(wim);
            AssertEq(skipped.Results[0].Status, ArchiveStatus.SkippedUnreadable);
            AssertTrue(skipped.Results[0].Message.Contains("无法识别"));
            AssertFalse(Directory.Exists(TestEnv.OutOf(wim)));

            RunSummary forced = TestEnv.RunExtractForced(wim);
            AssertEq(forced.Results[0].Status, ArchiveStatus.Completed);      // 真的被解出来了
            AssertTrue(forced.Results[0].Message.Contains("强制"));
            AssertTrue(forced.Results[0].Files > 0);
            AssertTrue(File.Exists(TestEnv.OutOf(wim, "hello.txt")));         // 产物在盘上（I1 校验通过）
            AssertTrue(File.Exists(wim));                                    // I3：原包保留
        });

        // 强制**不是**「跳过校验」：非归档被强制后 7-Zip 读不出清单 ⇒ 必须 Failed（I1），
        // 绝不能因为「用户要求了」就判成功；也绝不写盘、绝不删原包。
        H.Run("Extract.ForcedNonArchiveStillFailsVerification", delegate {
            string junk = TestEnv.UnknownGarbage;
            RunSummary forced = TestEnv.RunExtractForced(junk);
            AssertEq(forced.Results[0].Status, ArchiveStatus.Failed);
            AssertTrue(forced.Results[0].Message.Contains("强制"));
            AssertFalse(Directory.Exists(TestEnv.OutOf(junk)));
            AssertTrue(File.Exists(junk));
        });

        // 强制**不**绕过 #1 的安全上限：「下载未完成的 .part」也在同一道格式门控里，
        // 强制它 ⇒ 仍然被安全上限拒绝、一个字节都不写盘、原包保留。
        H.Run("Extract.ForcedAttemptStillAppliesCaps", delegate {
            string part = CopyToTmp(TestEnv.BombZip, "forced-bomb.part");

            RunSummary skipped = TestEnv.RunExtract(part);
            AssertEq(skipped.Results[0].Status, ArchiveStatus.SkippedUnreadable);
            AssertTrue(skipped.Results[0].Message.Contains("下载"));

            RunSummary forced = TestEnv.RunExtractForced(part);
            AssertEq(forced.Results[0].Status, ArchiveStatus.SkippedUnreadable);
            AssertTrue(forced.Results[0].Message.Contains("安全上限"));
            AssertFalse(Directory.Exists(TestEnv.OutOf(part)));
            AssertTrue(File.Exists(part));
        });

        // ==================================================================
        // OLE2/CFB 复合文档（.doc/.xls/.ppt/.msi/.msg/.vsd 一族）：数据丢失事故回归
        //
        // 真事故的完整形状（用户机器上实测）：
        //   1. 这些文件是**传统 OLE2 复合文档**，头部是 D0 CF 11 E0 A1 B1 1A E1 —— 不是 zip；
        //   2. Sniffer 的签名表里当时没有这一族，于是判 Unknown；
        //   3. Unknown 按设计落到 7-Zip 兜底，而 **7-Zip 26.01 会打开 CFB**（`Type = Compound`，
        //      条目名是 Data / 1Table / WordDocument / [5]SummaryInformation…），看起来像一次
        //      干净成功的解压；
        //   4. ArchiveGater 只按**zip 内容身份**判容器文档（[Content_Types].xml / _rels / mimetype…），
        //      CFB 里一个这样的标记都没有 ⇒ 一路 Allow；
        //   5. 输出目录名由归档名派生，和源文件自己撞名 ⇒ 被改名成「<名字>.doc (2)」；
        //   6. 校验「成功」（磁盘结果与索引一致）⇒ status=Completed ⇒ 开了删除就把原文件回收。
        //
        // 下面 4 条用例把这一族**钉死在 Sniffer 的头部签名**这一档上（与 gater 裁定 ContainerDocument
        // 完全同一条拒绝路径）：
        //   * 非强制 / 强制 两条钉「结局 = SkippedContainer、零输出、原件逐字节不变」；
        //   * 「+ 删除开关」两条钉「原件必须还在」（这正是能阻止那起事故的那条断言）。
        // 刻意**两个方向都钉**（非强制与强制）：强制那条才是真正会复现事故的入口 —— 只有它是
        // 「用户明确要求对这份 .doc 试一次」的那条路，也正是它必须在 I4 面前停下。
        // ==================================================================

        // 非强制：OLE2 复合文档必须走与 gater 裁定 ContainerDocument **完全相同**的收场 ——
        // 不递归、不删除、不留输出目录，判词如实说清身份。
        H.Run("Extract.Ole2DocIsRefusedAsContainerDocument", delegate {
            string doc = TestEnv.Ole2Doc;
            string before = Sha256(doc);

            RunSummary s = TestEnv.RunExtract(doc);

            AssertEq(s.Results[0].Status, ArchiveStatus.SkippedContainer);
            AssertTrue(s.Results[0].Message.Contains("OLE2"));           // 判词点名真实原因
            AssertEq(s.Results[0].OutputDir, "");                        // 没有输出去向
            AssertFalse(Directory.Exists(TestEnv.OutOf(doc)));           // 连输出目录都没建
            AssertEq(Directory.GetFileSystemEntries(TestEnv.OutRoot).Length, 0);   // 一个字节都没写盘
            AssertEq(Sha256(doc), before);                               // 原件逐字节不变
        });

        // 强制：**这是真正会复现事故的入口**。修复前它一路走到 Completed 并把文档的内部流
        // 解到「legacy-ole2.doc (2)\」里；修复后必须在 I4 面前停下（与「强制一份 .docx 也照样
        // 被门控拒绝」同一条语义：ForceTreatAsArchive 只放开**格式识别**门控，从不放开 I4）。
        H.Run("Extract.ForcedOle2DocIsStillRefused", delegate {
            string doc = TestEnv.Ole2Doc;
            string before = Sha256(doc);

            RunSummary s = TestEnv.RunExtractForced(doc);

            AssertEq(s.Results[0].Status, ArchiveStatus.SkippedContainer);
            AssertTrue(s.Results[0].Message.Contains("OLE2"));
            AssertTrue(s.Results[0].Message.Contains("强制"));            // 用户点过的动作照样如实留痕
            AssertEq(s.Results[0].OutputDir, "");
            AssertFalse(Directory.Exists(TestEnv.OutOf(doc)));
            AssertEq(Directory.GetFileSystemEntries(TestEnv.OutRoot).Length, 0);
            AssertEq(Sha256(doc), before);
        });

        // 删除开关打开（默认关，这里显式打开）：OLE2 原件必须还在 —— 这一条就是能阻止那起事故的断言。
        H.Run("Extract.Ole2DocSurvivesDeleteEnabled", delegate {
            string doc = CopyToTmp(TestEnv.Ole2Doc, "legacy-ole2-delete.doc");
            string before = Sha256(doc);

            RunSummary s = TestEnv.RunExtractWithDelete(doc);

            AssertEq(s.Results[0].Status, ArchiveStatus.SkippedContainer);
            AssertTrue(File.Exists(doc));                                // **原件必须还在**
            AssertEq(Sha256(doc), before);
            AssertFalse(Directory.Exists(TestEnv.OutOf(doc)));
        });

        // 事故现场的**完整**选项组合：强制 + 删除。修复前这一条会真的把副本回收掉
        //（status=Completed ⇒ I3 允许删除），修复后原件逐字节留在原地。
        H.Run("Extract.ForcedOle2DocSurvivesDeleteEnabled", delegate {
            string doc = CopyToTmp(TestEnv.Ole2Doc, "legacy-ole2-forced-delete.doc");
            string before = Sha256(doc);

            RunSummary s = TestEnv.RunExtractForcedWithDelete(doc);

            AssertEq(s.Results[0].Status, ArchiveStatus.SkippedContainer);
            AssertTrue(File.Exists(doc));                                // **原件必须还在**
            AssertEq(Sha256(doc), before);
            AssertEq(Directory.GetFileSystemEntries(TestEnv.OutRoot).Length, 0);
        });

        // ==================================================================
        // 修复轮 #3（Finding 1–4）
        // ==================================================================

        // Finding 1：NotAttempted 是**权威**的未处理清单，与原因无关。深度触顶那一项既在
        // Results 里（Status = NotAttemptedDepthLimit，供报告按状态分类），也在 NotAttempted 里
        //（供「打印未处理项」的消费方）—— 两个集合**不能相加**，未处理项总数 = NotAttempted.Count。
        H.Run("Extract.NotAttemptedIsCanonicalUnprocessedList", delegate {
            RunSummary s = TestEnv.RunExtractWithDepth(1, TestEnv.DeepNestedZip);

            ArchiveResult limited = null;
            foreach (ArchiveResult r in s.Results)
            {
                if (r.Status == ArchiveStatus.NotAttemptedDepthLimit) { limited = r; }
            }
            AssertTrue(limited != null);
            AssertTrue(s.NotAttempted.Contains(limited.Path));           // 同一个归档同时在两处
            AssertEq(s.NotAttempted.Count, 1);                           // 权威数量（不是两处之和）
            AssertEq(s.Results.Count, 2);                                // outer 完成 + mid 触顶
        });

        // Finding 1 的另一半：**致命中止**（盘满）之后本轮还没轮到的候选也进 NotAttempted，
        // 且**没有** Results 条目（没被处理过，谈不上结局）。
        H.Run("Extract.FatalAbortListsUnprocessedRemainder", delegate {
            RunSummary s = TestEnv.RunExtractManyWithFakeDisk(1024, TestEnv.BigSevenZip, TestEnv.PlainZip);

            AssertTrue(s.FatalReason != null && s.FatalReason.Contains("空间"));
            AssertEq(s.Results.Count, 1);                                // 只有第 1 个目标有结局
            AssertEq(s.Results[0].Status, ArchiveStatus.Failed);
            AssertEq(s.NotAttempted.Count, 1);
            AssertEq(s.NotAttempted[0], TestEnv.PlainZip);               // 第 2 个：未处理
            AssertTrue(File.Exists(TestEnv.PlainZip));                   // 未处理 = 原样没动
            AssertTrue(File.Exists(TestEnv.BigSevenZip));
        });

        // Finding 2：取消必须能打断**正在跑的那一次**密码验证 `t`，而不是等它自己跑完。
        //
        // 判据是**判词落在哪一段**，不是计时：修好前那次 `t` 拿的是 CancellationToken.None，
        // 于是一次完整校验会跑完、手工候选被判定成功 ⇒ 流程走到解压前那一步才发现取消 ⇒
        // 判词是「已取消：尚未开始解压」；修好后 token 打响、子进程被 Job Object 打断 ⇒
        // 候选**没有**验证成功 ⇒ 判词是「已取消：密码尚未验证完成」。
        // 取消的时机由一个看门狗给：等一个已经烧掉 150 ms CPU 的 7z 子进程出现（`l -slt` 只读中央
        // 目录、CPU 时间几乎为 0，只有 `t` 会这样），这样「取消落在这次调用之内」与机器快慢无关。
        H.Run("Extract.CancelInterruptsPasswordVerification", delegate {
            string archive = TestEnv.SlowEncryptedZip;

            // 基线：一次 `t -pSECRET` 要多久。太短就区分不了「打断这一次」与「下一个候选之前才发现」。
            long oneCall = TestEnv.TimeSingleVerification(archive, "SECRET");
            if (oneCall < 200)
            {
                H.Skip("Extract.CancelInterruptsPasswordVerification",
                    "本机一次完整校验只要 " + oneCall + " ms，无法把「打断正在跑的那一次 `t`」与「下一个候选之前才发现取消」区分开");
                return;
            }

            CancellationTokenSource source = new CancellationTokenSource();
            long[] cancelledAt = new long[1];
            Stopwatch watch = Stopwatch.StartNew();
            Thread watchdog = new Thread(delegate() {
                try
                {
                    while (!source.IsCancellationRequested && watch.ElapsedMilliseconds < 20000)
                    {
                        if (TestEnv.AnySevenZipBurningCpu(150)) { break; }
                        Thread.Sleep(5);
                    }
                    cancelledAt[0] = watch.ElapsedMilliseconds;
                    source.Cancel();
                }
                catch (Exception)
                {
                }
            });
            watchdog.IsBackground = true;
            watchdog.Start();

            RunSummary s = TestEnv.RunExtractWithToken(archive, "SECRET", source.Token);
            long afterCancelMs = watch.ElapsedMilliseconds - cancelledAt[0];
            watchdog.Join(2000);

            AssertEq(s.Results[0].Status, ArchiveStatus.Failed);
            AssertTrue(s.Cancelled);                                     // 运行级也如实标注「取消」
            AssertTrue(s.Results[0].Message.Contains("密码尚未验证完成"));
            AssertFalse(s.Results[0].Message.Contains("尚未开始解压"));   // 手工候选从未被验证通过
            AssertTrue(afterCancelMs < oneCall / 2);                     // 是**被打断**，不是等它跑完
            AssertFalse(Directory.Exists(TestEnv.OutOf(archive)));        // 什么都没提交
            AssertTrue(File.Exists(archive));                            // 原包保留
        });

        // Finding 2 的第二半：解压后那次 FreeBytes 也必须被包住。注入的 provider 在第 1 次
        //（预检）之后每次查询都抛 —— 正确处置是「按空间不足处理、不提交、原包保留」，
        // 而不是让异常逃出 Process 把结果降级成一条「内部错误」。
        H.Run("Extract.DiskProbeFailureTakesLowWaterPath", delegate {
            RunSummary s = TestEnv.RunExtractWithThrowingDisk(TestEnv.PlainZip);

            AssertEq(s.Results[0].Status, ArchiveStatus.Failed);
            AssertFalse(s.Results[0].Message.Contains("内部错误"));
            AssertTrue(s.Results[0].Message.Contains("空间"));
            AssertTrue(s.FatalReason != null && s.FatalReason.Contains("空间"));
            AssertFalse(Directory.Exists(TestEnv.OutOf(TestEnv.PlainZip)));      // 半成品绝不提交
            AssertTrue(File.Exists(TestEnv.PlainZip));                           // 原包保留

            // 失败路径仍然留下了「未完成」标记（哨兵先写、再改名 ⇒ 哨兵随目录一起搬过去）。
            string incomplete = TestEnv.OutOf(TestEnv.PlainZip) + " (未完成)";
            AssertTrue(Directory.Exists(incomplete));
            AssertEq(Directory.GetFiles(incomplete, "_RERAR_INCOMPLETE.txt").Length, 1);
        });

        // Finding 3 的第一半：长路径预检必须按**流程实际会用到的最长目录名**度量
        //（目标名 + " (未完成)" + 可能的 " (2)"，比裸目标名长 10 个字符），而不是裸目标名。
        // 夹具的条目名长度让**旧度量**正好等于 MaxPathLength（放行）、新度量 +10 字符（拒绝）。
        H.Run("Extract.PreflightUsesLongestIncompleteName", delegate {
            string archive = TestEnv.LongNamedArchiveCopy();          // 目标目录名 = 120 字符（消毒上限）
            string bareTarget = Path.Combine(TestEnv.OutRoot, PathSanitizer.Sanitize(Path.GetFileName(archive)));

            // 算术前提用纯函数直接钉住：旧度量放行、新度量拒绝。
            ArchiveIndex index = new ArchiveIndex();
            index.FileCount = 1;
            index.TotalBytes = 22;
            index.Entries.Add(Entry(TestEnv.LongEntryName, 22));
            AssertTrue(Preflight.FindOverlongEntry(bareTarget, index) == null);
            AssertTrue(Preflight.FindOverlongEntry(bareTarget + " (未完成) (2)", index) != null);

            RunSummary s = TestEnv.RunExtract(archive);
            AssertEq(s.Results[0].Status, ArchiveStatus.SkippedUnreadable);
            AssertTrue(s.Results[0].Message.Contains("路径过长"));
            AssertFalse(Directory.Exists(bareTarget));                // 一个字节都没写盘
            AssertTrue(File.Exists(archive));                         // 原包保留
        });

        // Finding 3 的第二半：失败路径的哨兵**先写、后改名**，所以「改名成功、往长名字里写哨兵
        // 失败」这条曾经静默丢标记的路不再存在。这里的构造正是那个窗口：目标目录名 235 字符 ⇒
        // 改名后的哨兵路径 263 > MaxPathLength（而**旧**度量仍放行，所以流程真的会走到失败路径）。
        //
        // 【本机如实说明】本机 LongPathsEnabled=1，CLR 也支持超长路径 ⇒ 实测**旧**实现同样能把这个
        // 哨兵写成功，这条用例在本机**区分不了新旧**（它钉的是契约：失败路径的目录必须有哨兵，
        // 且判词不得谎报标记失败）。在 LongPathsEnabled=0（Windows 默认）的机器上，263 字符的写入
        // 会抛 PathTooLongException 并被旧实现吞掉 ⇒ 这条用例在那里会以「没有哨兵」失败。
        // Finding 3 的**预检**那一半（旧度量放行、新度量拒绝）由 Extract.PreflightUsesLongestIncompleteName
        // 钉住，那条在本机也真的会因为修复前的代码而失败（修复轮 #3 的 RED 实测）。
        H.Run("Extract.IncompleteMarkerSurvivesLongTargetName", delegate {
            string outRoot = TestEnv.LongOutputRoot(116);
            int entryLength = TestEnv.CorruptPayloadEntryName.Length;

            // 目标目录名取「新度量刚好放行（T + 10 + 1 + E <= 259）」而「改名后的哨兵路径越界
            //（T + 6 + 1 + 21 > 259）」的长度：E = 7、" (未完成)" 6 字符、哨兵名 21 字符
            // ⇒ 目标目录名 = 235 - 1 - outRoot.Length。
            int targetDirLength = 235 - 1 - outRoot.Length;
            if (targetDirLength < 1)
            {
                H.Skip("Extract.IncompleteMarkerSurvivesLongTargetName",
                    "临时目录已经太深（" + outRoot.Length + " 字符），造不出这条用例要的长度关系");
                return;
            }

            string archiveName = new string('p', targetDirLength) + ".zip";
            string archive = Path.Combine(TestEnv.Tmp, archiveName);
            File.Copy(TestEnv.CorruptPayloadZip, archive, true);

            string target = Path.Combine(outRoot, PathSanitizer.Sanitize(archiveName));
            int newMeasure = target.Length + " (未完成) (2)".Length + 1 + entryLength;
            int markerAfterRename = (target + " (未完成)").Length + 1 + "_RERAR_INCOMPLETE.txt".Length;
            if (newMeasure > Preflight.MaxPathLength || markerAfterRename <= Preflight.MaxPathLength)
            {
                H.Skip("Extract.IncompleteMarkerSurvivesLongTargetName",
                    "本机构造不出这条长度关系：新度量 " + newMeasure + "，改名后哨兵路径 " + markerAfterRename);
                return;
            }
            // 前提：**旧**度量必须放行（否则这个输入在修复前就被预检拦掉，压根走不到失败路径）。
            AssertTrue(target.Length + 1 + entryLength <= Preflight.MaxPathLength);

            RunSummary s = TestEnv.RunExtractWithOutputRoot(archive, outRoot);

            AssertEq(s.Results[0].Status, ArchiveStatus.Failed);
            AssertTrue(s.Results[0].Message.Contains("7-Zip 退出码 2"));
            AssertFalse(s.Results[0].Message.Contains("未完成标记写入失败"));

            string incomplete = target + " (未完成)";
            AssertTrue(Directory.Exists(incomplete));                              // 改名成功
            // 哨兵在不在：**按名字**数（不能给 GetFiles 传 pattern —— 它会拼成
            // "<241 字符目录>\<pattern>" 的 263 字符搜索路径而抛 PathTooLong；不带 pattern 的枚举
            // 只用目录本身（243 字符），返回的是字符串，不需要再对长路径调 API）。
            bool marked = false;
            foreach (string entry in Directory.GetFileSystemEntries(incomplete))
            {
                if (Path.GetFileName(entry) == "_RERAR_INCOMPLETE.txt") { marked = true; }
            }
            AssertTrue(marked);
            AssertFalse(Directory.Exists(target));                                 // 没有提交成正式输出
            AssertTrue(File.Exists(archive));                                      // 原包保留
        });

        // Finding 3 的第三半（**与机器无关**的那一半）：标记**真的**放不下时，判词必须如实说出来。
        // 夹具里有一个与哨兵同名的目录条目 ⇒ 无论先写还是后写，`File.WriteAllText` 都必然失败。
        //   * 修好后：MarkIncomplete 把失败如实返回，判词里出现「未完成标记写入失败」；
        //   * 修好前：异常被吞掉，判词只有「解压失败…；原包保留」—— 用户拿到一个没有标记的目录
        //     却完全不知道（这条断言在修复前的代码上会失败，修复轮 #3 的 RED 实测）。
        H.Run("Extract.MarkerFailureIsReportedNotSilent", delegate {
            RunSummary s = TestEnv.RunExtract(TestEnv.SentinelNameClashZip);

            AssertEq(s.Results[0].Status, ArchiveStatus.Failed);
            AssertTrue(s.Results[0].Message.Contains("7-Zip 退出码 2"));
            AssertTrue(s.Results[0].Message.Contains("未完成标记写入失败"));
            AssertFalse(s.Results[0].Message.Contains("内部错误"));
            AssertTrue(File.Exists(TestEnv.SentinelNameClashZip));                 // 原包保留

            // 标记确实没写进去（判词说的就是这件事，不是谎报）；目录仍带「(未完成)」名字。
            string incomplete = TestEnv.OutOf(TestEnv.SentinelNameClashZip) + " (未完成)";
            AssertTrue(Directory.Exists(incomplete));
            bool marked = false;
            foreach (string entry in Directory.GetFileSystemEntries(incomplete))
            {
                if (Path.GetFileName(entry) == "_RERAR_INCOMPLETE.txt" && !Directory.Exists(entry)) { marked = true; }
            }
            AssertFalse(marked);
        });

        // Finding 4：字典层命中的候选必须被报成**字典**（内置/导入），而不是「本次运行已验证过的
        // 密码」。旧实现先把命中的候选塞进 _verifiedPasswords、再分类，于是字典标签永远不可达。
        // 这里两条都钉住：同一批里的第 2 个归档，候选确实来自「本次已验证」层（阶梯顺序如此），
        // 那时标签就**应该**是那一类 —— 标签说的是「来自阶梯哪一层」，与阶梯层次一致。
        H.Run("Extract.BuiltInDictionaryHitIsLabelled", delegate {
            string one = CopyToTmp(TestEnv.BuiltInDictZip, "dict-one.zip");
            string two = CopyToTmp(TestEnv.BuiltInDictZip, "dict-two.zip");

            RunSummary s = TestEnv.RunExtractMany(one, two);

            AssertEq(s.Results.Count, 2);
            AssertEq(s.Results[0].Status, ArchiveStatus.Completed);
            AssertTrue(s.Results[0].Message.Contains("内置字典"));
            AssertFalse(s.Results[0].Message.Contains("本次运行已验证过的密码"));

            AssertEq(s.Results[1].Status, ArchiveStatus.Completed);
            AssertTrue(s.Results[1].Message.Contains("本次运行已验证过的密码"));
        });

        H.Run("Extract.ImportedDictionaryHitIsLabelled", delegate {
            RunSummary s = TestEnv.RunExtractWithDictionary(TestEnv.ImportedDictZip, new string[] { "ImportedSecret" });

            AssertEq(s.Results[0].Status, ArchiveStatus.Completed);
            AssertTrue(s.Results[0].Message.Contains("导入字典"));
            AssertFalse(s.Results[0].Message.Contains("本次运行已验证过的密码"));
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

    // ------------------------------------------------------------------
    // 复审 Critical 的夹具小工具（见上面那组用例的说明）
    // ------------------------------------------------------------------

    // 手写一份「头部带 RAR5 签名」的字节：7-Zip 造不出 rar，而解析器的裁决只看头部签名，
    // 签名正确就足以钉住分组决定（内容真假不参与这条判据）。
    // 填 'R' 是刻意的：它不会撞出任何别的签名，也不会撞出 zip 的 EOCD 标记。
    private static void WriteRar5Stub(string path, int fillerLength)
    {
        byte[] signature = TestEnv.B("Rar!\x1a\x07\x01\x00");              // Sniffer 的 8 字节 RAR5 签名
        byte[] bytes = new byte[signature.Length + fillerLength];
        Array.Copy(signature, bytes, signature.Length);
        for (int i = signature.Length; i < bytes.Length; i++) { bytes[i] = (byte)'R'; }
        File.WriteAllBytes(path, bytes);
    }

    // 只有尾部 EOCD 标记、头部没有签名的字节（Sniffer → DamagedHeader）：WinZip 2 分卷集里
    // 最后一卷 .zip 的合法形状（中央目录在它里面，头部被清零/改写）。
    private static void WriteEocdOnlyStub(string path, int fillerLength)
    {
        byte[] eocd = TestEnv.B("PK\x05\x06");
        byte[] bytes = new byte[fillerLength + eocd.Length];
        for (int i = 0; i < fillerLength; i++) { bytes[i] = (byte)'Q'; }
        Array.Copy(eocd, 0, bytes, fillerLength, eocd.Length);
        File.WriteAllBytes(path, bytes);
    }

    // 带 zip 头签名、但**没有自己的 EOCD** 的字节：分卷的中间段（.z01/.z02）就是这样。
    // 填 'A' 保证尾部不会撞出 PK\x05\x06 / PK\x01\x02。
    private static void WriteZipHeadStub(string path, int fillerLength)
    {
        byte[] header = TestEnv.B("PK\x03\x04");
        byte[] bytes = new byte[header.Length + fillerLength];
        Array.Copy(header, bytes, header.Length);
        for (int i = header.Length; i < bytes.Length; i++) { bytes[i] = (byte)'A'; }
        File.WriteAllBytes(path, bytes);
    }

    // 用产品自己的 Sniffer 读出结论（与 Extractor.Sniff 同一调用形状：head 前 262 字节、tail 末 64KB；
    // 夹具都很小，整份文件既是 head 也是 tail）。断言夹具形状本身，避免它悄悄变形成什么也证明不了。
    private static SniffKind SniffOf(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return Sniffer.Classify(bytes, bytes.Length, Path.GetFileName(path), bytes);
    }

    // 通过反射调产品自己的分卷解析器（Extractor.ResolveVolumeMember 是私有实现）。
    // 为什么必须白盒：上面那份 rar 夹具不是真 rar，7-Zip 必然读不出来，于是「交给 7-Zip 的是
    // 哪一片」无法从运行结局里观察 —— 这里要钉的正是**解析器的裁决**本身。Task 14 的 GuiProbe
    // 也是同一族的反射手法。方法被改名/挪走时当场 FAIL（method == null），绝不静默跳过。
    private static string ResolveByProduct(string sourcePath, ArchiveTask task)
    {
        MethodInfo method = typeof(Extractor).GetMethod("ResolveVolumeMember",
            BindingFlags.NonPublic | BindingFlags.Static);
        AssertTrue(method != null);

        object[] args = new object[] { sourcePath, task, null };
        return (string)method.Invoke(null, args);      // task.VolumeMembers 由被调方法就地写入
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

    // 原件的 SHA-256（十六进制小写）。用例用「运行前后哈希相等」钉住「原件逐字节未变」——
    // 比 AssertTrue(File.Exists(...)) 强：后者放得过「文件还在但被改写了」。
    private static string Sha256(string path)
    {
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(stream);
            StringBuilder text = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) { text.Append(b.ToString("x2", CultureInfo.InvariantCulture)); }
            return text.ToString();
        }
    }

    private static IndexEntry Entry(string path, long size)
    {
        IndexEntry entry = new IndexEntry();
        entry.Path = path;
        entry.Size = size;
        return entry;
    }
}
