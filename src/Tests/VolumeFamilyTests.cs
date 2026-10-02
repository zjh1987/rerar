// Task 6 单元测试：VolumeFamily 分卷族（权威成员 / 具体缺卷 / 独立性判别）。
//
// 前 5 条用例的名字与期望值逐字来自 task-6-brief.md Step 1；唯一改写是按 carry-forward 约定 8
// 把裸 F(...) 写成 TestEnv.F(...)（C# 5 没有 using static，裸 F(...) 是 CS0103）。
// 其余用例覆盖 brief Step 1 未触碰、但规格 §6.6 与本任务自审清单点名的行为：
//   * 旧式 RAR 的 .rar 是第一个卷，.r00/.r01… 在其后；
//   * ZIP 的 .zip 是**最后一个**卷（故 .zip 缺失时缺的正是它自己）；
//   * 缺卷清单按卷序完整给出（多个空洞也要逐个列出）；
//   * 位宽不一致的命名不并进同一集（.7z.001 与 .7z.01），但数字自然变长的连续命名（part1/part10）仍是一集；
//   * CD1.rar / CD2.rar 这类「数字在基名里」的文件不是同一分卷集；
//   * 扩展名大小写不敏感；全路径原样保留；合成的缺卷名沿用同集成员的写法；
//   * 单独一个完整成员不算分卷集（返回 false）；畸形卷数（part999999）被上界挡住而不是合成海量缺卷名。
//
// 本任务没有文件 fixture：输入全是名字列表，判定是纯字符串运算（不碰文件系统）。
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System.IO;
using Rerar.Core;

internal sealed class VolumeFamilyTests : TestBase
{
    public static void Run()
    {
        // ---- brief Step 1 的 5 条（用例名与期望值逐字照抄）----
        H.Run("Volume.Part1RarIsAuthoritative", delegate {
            VolumeSet s;
            AssertTrue(VolumeFamily.TryResolve(TestEnv.F("m.part1.rar", "m.part2.rar", "m.part3.rar"), "m.part1.rar", out s));
            AssertEq(Path.GetFileName(s.AuthoritativeMember), "m.part1.rar"); });

        H.Run("Volume.ZipIsAuthoritativeNotZ01", delegate {                       // 规格 §6.6
            VolumeSet s;
            AssertTrue(VolumeFamily.TryResolve(TestEnv.F("a.zip", "a.z01", "a.z02"), "a.z01", out s));
            AssertEq(Path.GetFileName(s.AuthoritativeMember), "a.zip"); });

        H.Run("Volume.SevenZip001IsAuthoritative", delegate {
            VolumeSet s;
            AssertTrue(VolumeFamily.TryResolve(TestEnv.F("b.7z.001", "b.7z.002"), "b.7z.002", out s));
            AssertEq(Path.GetFileName(s.AuthoritativeMember), "b.7z.001"); });

        H.Run("Volume.ReportsExactMissingMember", delegate {                      // 规格 §6.6
            VolumeSet s;
            VolumeFamily.TryResolve(TestEnv.F("c.7z.001", "c.7z.003"), "c.7z.001", out s);
            AssertTrue(s.Missing.Contains("c.7z.002")); });

        H.Run("Volume.IndependentArchivesAreNotASet", delegate {                  // Review Focus #4
            VolumeSet s;
            AssertFalse(VolumeFamily.TryResolve(TestEnv.F("x.zip", "x.z01"), "x.zip", out s, allMembersHaveFullSignature: true)); });

        // ---- 旧式 RAR：.rar 是第一个卷（权威成员），续卷是 .r00/.r01… ----
        H.Run("Volume.LegacyRarLeadIsAuthoritative", delegate {
            VolumeSet s;
            AssertTrue(VolumeFamily.TryResolve(TestEnv.F("m.rar", "m.r00", "m.r01"), "m.r01", out s));
            AssertEq(Path.GetFileName(s.AuthoritativeMember), "m.rar");          // 规格 §6.6：.rar 是第一个
            AssertEq(s.Members.Count, 3);
            AssertEq(Path.GetFileName(s.Members[0]), "m.rar");
            AssertEq(s.Missing.Count, 0); });

        H.Run("Volume.LegacyRarReportsExactMissingContinuation", delegate {
            VolumeSet s;
            AssertTrue(VolumeFamily.TryResolve(TestEnv.F("m.rar", "m.r00", "m.r02"), "m.rar", out s));
            AssertEq(Path.GetFileName(s.AuthoritativeMember), "m.rar");
            AssertEq(s.Missing.Count, 1);
            AssertEq(s.Missing[0], "m.r01"); });                                 // 缺哪一卷就报哪一卷

        H.Run("Volume.LegacyRarMissingLeadIsAuthoritative", delegate {
            VolumeSet s;
            AssertTrue(VolumeFamily.TryResolve(TestEnv.F("m.r00", "m.r01"), "m.r00", out s));
            AssertEq(s.AuthoritativeMember, "m.rar");                            // 权威成员缺失也必须报出来
            AssertEq(s.Missing.Count, 1);
            AssertEq(s.Missing[0], "m.rar");
            AssertEq(s.Members.Count, 2); });

        // ---- ZIP 分卷：.zip 是最后一卷，成员顺序按卷序（.z01… 在前，.zip 在后）----
        H.Run("Volume.ZipLeadIsLastVolume", delegate {
            VolumeSet s;
            AssertTrue(VolumeFamily.TryResolve(TestEnv.F("a.zip", "a.z01", "a.z02"), "a.zip", out s));
            AssertEq(s.AuthoritativeMember, "a.zip");
            AssertEq(s.Members.Count, 3);
            AssertEq(s.Members[0], "a.z01");
            AssertEq(s.Members[1], "a.z02");
            AssertEq(s.Members[2], "a.zip");
            AssertEq(s.Missing.Count, 0); });

        H.Run("Volume.ZipReportsEveryGapInVolumeOrder", delegate {
            VolumeSet s;
            AssertTrue(VolumeFamily.TryResolve(TestEnv.F("a.zip", "a.z01", "a.z04"), "a.z01", out s));
            AssertEq(s.AuthoritativeMember, "a.zip");
            AssertEq(s.Missing.Count, 2);
            AssertEq(s.Missing[0], "a.z02");
            AssertEq(s.Missing[1], "a.z03"); });

        H.Run("Volume.ZipWithoutLeadIsMissingItself", delegate {
            VolumeSet s;
            AssertTrue(VolumeFamily.TryResolve(TestEnv.F("a.z01", "a.z02"), "a.z02", out s));
            AssertEq(s.AuthoritativeMember, "a.zip");                            // 缺的正是权威成员 .zip
            AssertEq(s.Missing.Count, 1);
            AssertEq(s.Missing[0], "a.zip");
            AssertEq(s.Members.Count, 2);

            // 只剩一个 .z01 时同样必须报「缺 .zip」：把 .z01 交给 7-Zip 只会得到
            //「无法作为压缩包打开」，那正是本任务要消弭的误报路径。
            VolumeSet lone;
            AssertTrue(VolumeFamily.TryResolve(TestEnv.F("m.z01"), "m.z01", out lone));
            AssertEq(lone.Missing.Count, 1);
            AssertEq(lone.Missing[0], "m.zip"); });

        // ---- 7z 分卷：.001 是第一个卷；空洞逐卷列出 ----
        H.Run("Volume.SevenZipMissingFirstIsAuthoritative", delegate {
            VolumeSet s;
            AssertTrue(VolumeFamily.TryResolve(TestEnv.F("b.7z.002", "b.7z.003"), "b.7z.003", out s));
            AssertEq(s.AuthoritativeMember, "b.7z.001");
            AssertEq(s.Missing.Count, 1);
            AssertEq(s.Missing[0], "b.7z.001"); });

        H.Run("Volume.SevenZipEveryGapInVolumeOrder", delegate {
            VolumeSet s;
            AssertTrue(VolumeFamily.TryResolve(TestEnv.F("c.7z.001", "c.7z.005"), "c.7z.005", out s));
            AssertEq(s.AuthoritativeMember, "c.7z.001");
            AssertEq(s.Missing.Count, 3);
            AssertEq(s.Missing[0], "c.7z.002");
            AssertEq(s.Missing[1], "c.7z.003");
            AssertEq(s.Missing[2], "c.7z.004"); });

        // ---- 位宽一致：.001（宽 3）与 .01（宽 2）不是同一集 ----
        H.Run("Volume.DigitWidthIsNotMixed", delegate {
            VolumeSet s;
            string[] files = TestEnv.F("w.7z.001", "w.7z.002", "w.7z.01");
            AssertTrue(VolumeFamily.TryResolve(files, "w.7z.001", out s));
            AssertEq(s.AuthoritativeMember, "w.7z.001");
            AssertEq(s.Members.Count, 2);                                        // .01 不得并进来
            AssertFalse(s.Members.Contains("w.7z.01"));
            AssertEq(s.Missing.Count, 0);                                        // 也不得凭空报 .002 缺失

            VolumeSet other;
            AssertFalse(VolumeFamily.TryResolve(files, "w.7z.01", out other));    // 与该集位宽冲突 ⇒ 按独立文件处理
            AssertTrue(other == null);

            // 位宽一致不等于「数字位数必须相等」：数字自然变长的连续命名仍是一集。
            VolumeSet tenth;
            AssertTrue(VolumeFamily.TryResolve(TestEnv.F("m.part1.rar", "m.part10.rar"), "m.part10.rar", out tenth));
            AssertEq(tenth.AuthoritativeMember, "m.part1.rar");                   // partN 不补零，"10" 就是 10
            AssertEq(tenth.Members.Count, 2);
            AssertEq(tenth.Missing.Count, 8);                                     // 缺 part2…part9
            AssertEq(tenth.Missing[0], "m.part2.rar");
            AssertEq(tenth.Missing[7], "m.part9.rar");

            // 同一族里只有一套连续位宽时，非主流宽度（.01/.02）照样自成一集。
            VolumeSet twoDigit;
            AssertTrue(VolumeFamily.TryResolve(TestEnv.F("n.7z.01", "n.7z.02"), "n.7z.01", out twoDigit));
            AssertEq(twoDigit.AuthoritativeMember, "n.7z.01");
            AssertEq(twoDigit.Missing.Count, 0); });

        // ---- 卷数上界：畸形数字（part999999）不得让我们合成上万个缺卷名 ----
        H.Run("Volume.AbsurdVolumeCountIsRefused", delegate {
            VolumeSet s;
            AssertFalse(VolumeFamily.TryResolve(TestEnv.F("m.part1.rar", "m.part999999.rar"), "m.part1.rar", out s));
            AssertFalse(VolumeFamily.TryResolve(TestEnv.F("big.7z.001", "big.7z.999999"), "big.7z.001", out s)); });

        // ---- 终止式 token + 基名一致：CD1.rar / CD2.rar 不是同一分卷集 ----
        H.Run("Volume.CdNumberedRarsAreNotOneSet", delegate {                     // 审计 D4
            VolumeSet s;
            string[] files = TestEnv.F("CD1.rar", "CD2.rar", "CD1.r00");
            AssertTrue(VolumeFamily.TryResolve(files, "CD1.rar", out s));
            AssertEq(s.AuthoritativeMember, "CD1.rar");
            AssertEq(s.Members.Count, 2);
            AssertEq(s.Members[0], "CD1.rar");
            AssertEq(s.Members[1], "CD1.r00");
            AssertEq(s.Missing.Count, 0);                                        // CD2.rar 既不是成员也不是缺卷

            VolumeSet other;
            AssertFalse(VolumeFamily.TryResolve(files, "CD2.rar", out other)); });

        H.Run("Volume.BaseNameMustMatch", delegate {
            VolumeSet s;
            AssertFalse(VolumeFamily.TryResolve(TestEnv.F("a.rar", "b.r00"), "a.rar", out s));
            VolumeSet other;
            AssertTrue(VolumeFamily.TryResolve(TestEnv.F("a.rar", "b.r00"), "b.r00", out other));
            AssertEq(other.AuthoritativeMember, "b.rar");                        // b.r00 的集与 a.rar 无关
            AssertEq(other.Missing.Count, 1);
            AssertEq(other.Missing[0], "b.rar"); });

        // ---- 扩展名大小写不敏感（基名按序数忽略大小写比较）----
        H.Run("Volume.ExtensionCaseInsensitive", delegate {
            VolumeSet rar;
            AssertTrue(VolumeFamily.TryResolve(TestEnv.F("M.PART1.RAR", "m.part2.rar"), "m.part2.rar", out rar));
            AssertEq(rar.AuthoritativeMember, "M.PART1.RAR");                    // 存在的成员用调用方给的原字符串
            AssertEq(rar.Members.Count, 2);
            AssertEq(rar.Missing.Count, 0);

            VolumeSet zip;
            AssertTrue(VolumeFamily.TryResolve(TestEnv.F("A.ZIP", "A.Z01"), "a.z01", out zip));
            AssertEq(zip.AuthoritativeMember, "A.ZIP");
            AssertEq(zip.Missing.Count, 0);

            // 合成的缺卷名沿用同集参考成员的写法（大小写风格一致），而不是拍脑袋的小写 canonical
            VolumeSet gap;
            AssertTrue(VolumeFamily.TryResolve(TestEnv.F("M.PART1.RAR", "M.PART3.RAR"), "M.PART1.RAR", out gap));
            AssertEq(gap.Missing.Count, 1);
            AssertEq(gap.Missing[0], "M.PART2.RAR"); });

        // ---- 全路径原样保留：缺卷名与候选同目录 ----
        H.Run("Volume.AbsolutePathsArePreserved", delegate {
            VolumeSet s;
            string[] files = TestEnv.F(@"C:\dl\movie.7z.001", @"C:\dl\movie.7z.003");
            AssertTrue(VolumeFamily.TryResolve(files, @"C:\dl\movie.7z.003", out s));
            AssertEq(s.AuthoritativeMember, @"C:\dl\movie.7z.001");
            AssertEq(s.Missing.Count, 1);
            AssertEq(s.Missing[0], @"C:\dl\movie.7z.002"); });

        // ---- 不构成分卷集的各种输入 ----
        H.Run("Volume.UnrelatedFilesAreNotASet", delegate {
            VolumeSet s;
            string[] files = TestEnv.F("notes.txt", "movie.7z", "plain.zip", "readme.md");
            AssertFalse(VolumeFamily.TryResolve(files, "notes.txt", out s));
            AssertFalse(VolumeFamily.TryResolve(files, "movie.7z", out s));      // 单卷 .7z 不是分卷族命名
            AssertFalse(VolumeFamily.TryResolve(files, "plain.zip", out s));     // 单独一个 .zip 且不缺卷
            AssertFalse(VolumeFamily.TryResolve(TestEnv.F("plain.zip"), "plain.zip", out s));
            AssertFalse(VolumeFamily.TryResolve(TestEnv.F(), "a.7z.001", out s));         // 空列表
            AssertFalse(VolumeFamily.TryResolve(null, "a.7z.001", out s));                // null 列表
            AssertFalse(VolumeFamily.TryResolve(TestEnv.F("a.7z.001"), null, out s));     // null 候选
            AssertFalse(VolumeFamily.TryResolve(TestEnv.F("a.7z.001"), "", out s)); });

        H.Run("Volume.CandidateMustBeInTheDirectory", delegate {
            VolumeSet s;
            AssertFalse(VolumeFamily.TryResolve(TestEnv.F("a.zip", "a.z01"), @"C:\other\b.z01", out s)); });

        H.Run("Volume.SingleCompleteMemberIsNotASet", delegate {
            VolumeSet s;
            AssertFalse(VolumeFamily.TryResolve(TestEnv.F("b.7z.001"), "b.7z.001", out s));
            AssertFalse(VolumeFamily.TryResolve(TestEnv.F("m.part1.rar"), "m.part1.rar", out s));
            AssertFalse(VolumeFamily.TryResolve(TestEnv.F("m.rar"), "m.rar", out s));

            VolumeSet other;
            AssertTrue(VolumeFamily.TryResolve(TestEnv.F("m.part1.rar", "m.part2.rar"), "m.part2.rar", out other));
            AssertEq(other.AuthoritativeMember, "m.part1.rar"); });

        // ---- 独立性开关：置真即按独立文件处理，绝不报缺卷（Review Focus #4）----
        H.Run("Volume.IndependentFlagSuppressesMissingReport", delegate {
            VolumeSet s;
            AssertFalse(VolumeFamily.TryResolve(TestEnv.F("c.7z.001", "c.7z.003"), "c.7z.001", out s,
                allMembersHaveFullSignature: true));
            AssertTrue(s == null); });
    }
}
