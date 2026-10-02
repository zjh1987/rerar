// Task 9 单元测试：RecycleBinGuard（回收站可用性前置检查 + 删后核实 + 隔离兜底）。
//
// 前 3 条用例的名字逐字来自 task-9-brief.md Step 1；其余用例覆盖 brief 的 Safety requirements：
//   * 不确定输入（缺注册表项、盘符不存在、路径本身不合法）必须降级到 Refuse/Quarantine，
//     绝不降级到「永久删除」；
//   * Recycle 任何失败都返回 false、不抛给调用方、绝不退化成永久删除；
//   * VerifyInBin 必须真的枚举回收站按名匹配（I3 的诚实层）。
//
// 与 brief 示例的唯一偏离（已写进 task-9-report.md）：回收站里是按「文件名」核实的，
// 若用例每次都用固定名 guard_me.txt，第二遍运行时上一轮遗留的同名项会让「静默永久删除」
// 也核实为 true —— 那是假 PASS。故每次运行用带随机后缀的文件名，并在删前断言该名不在回收站里。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.IO;
using Rerar.Core;

internal sealed class RecycleBinGuardTests : TestBase
{
    public static void Run()
    {
        // brief Step 1 用例 1：回收站删除后必须能从回收站里按名核实到。
        H.Run("Guard.RecyclesAndVerifies", delegate {
            string name = UniqueName("guard_me");
            string f = TestEnv.MakeFile(name, "x");
            string why;
            AssertEq(RecycleBinGuard.Plan(f, out why), DeletePlan.Recycle);
            AssertFalse(RecycleBinGuard.VerifyInBin(name));      // 前置：这个名字本来不在回收站里
            AssertTrue(RecycleBinGuard.Recycle(f));
            AssertFalse(File.Exists(f));
            AssertTrue(RecycleBinGuard.VerifyInBin(name));
        });

        // brief Step 1 用例 2：体积超过本卷回收站配额 → 不走回收站，给隔离文件夹。
        H.Run("Guard.PlansQuarantineWhenTooLargeForQuota", delegate {
            string path;
            try
            {
                path = TestEnv.OversizedFile;
            }
            catch (InvalidOperationException ex)
            {
                // 造不出超配额夹具时如实打印 SKIPPED（绝不假 PASS），并写进报告。
                Console.WriteLine("SKIPPED Guard.PlansQuarantineWhenTooLargeForQuota：" + ex.Message);
                return;
            }

            string why;
            AssertEq(RecycleBinGuard.Plan(path, out why), DeletePlan.Quarantine);
            AssertTrue(why.IndexOf("配额") >= 0);                // reason 必须具体到「配额」
        });

        // brief Step 1 用例 3：远程/可移动/不存在的卷一律拒绝（Z: 在本机不存在 → DRIVE_NO_ROOT_DIR）。
        H.Run("Guard.RefusesOnRemovableOrRemote", delegate {
            string why;
            AssertEq(RecycleBinGuard.Plan(@"Z:\remote\x.zip", out why), DeletePlan.Refuse);
            AssertTrue(why != null && why.Length > 0);
        });

        // 真·DRIVE_REMOTE 路径（本机实测 GetDriveType(\\localhost\C$\) = 4）：必须拒绝，
        // 且判词要点明「网络」—— 实测这种路径上 SendToRecycleBin 不报错却静默永久删除。
        H.Run("Guard.RefusesOnUncPath", delegate {
            string why;
            AssertEq(RecycleBinGuard.Plan(@"\\localhost\C$\Users\Public\remote_archive.zip", out why), DeletePlan.Refuse);
            if (why == null || why.IndexOf("网络") < 0)
            {
                throw new Exception("UNC 路径的判词没点明网络位置（本机实测该卷 GetDriveType=DRIVE_REMOTE）：" + why);
            }
        });

        // 普通临时文件 → Recycle，且判词非空（会直接显示给用户）。
        H.Run("Guard.PlansRecycleForOrdinaryTempFile", delegate {
            string f = TestEnv.MakeFile(UniqueName("ordinary"), "x");
            string why;
            AssertEq(RecycleBinGuard.Plan(f, out why), DeletePlan.Recycle);
            AssertTrue(why != null && why.Length > 0);
            AssertTrue(why.IndexOf("回收站") >= 0);
        });

        // 空路径 / 垃圾路径 / 超长路径：一律 Refuse，且绝不抛异常。
        H.Run("Guard.PlanRefusesMissingOrGarbagePath", delegate {
            string why;
            AssertEq(RecycleBinGuard.Plan(null, out why), DeletePlan.Refuse);
            AssertTrue(why != null && why.Length > 0);
            AssertEq(RecycleBinGuard.Plan("", out why), DeletePlan.Refuse);
            AssertEq(RecycleBinGuard.Plan("::::", out why), DeletePlan.Refuse);
            AssertEq(RecycleBinGuard.Plan(new string('x', 4000), out why), DeletePlan.Refuse);
            AssertEq(RecycleBinGuard.Plan(TestEnv.TmpFile("no_such_file_here.txt"), out why), DeletePlan.Refuse);
        });

        // Recycle 的失败路径：文件不存在 / 空路径 → false，且不生成任何东西。
        H.Run("Guard.RecycleReturnsFalseForMissingFile", delegate {
            AssertFalse(RecycleBinGuard.Recycle(TestEnv.TmpFile("no_such_file_here.txt")));
            AssertFalse(RecycleBinGuard.Recycle(""));
            AssertFalse(RecycleBinGuard.Recycle(null));
        });

        // Recycle 的失败路径：Plan 判定不可回收时直接拒绝，绝不退化成永久删除。
        H.Run("Guard.RecycleRefusesNonRecyclablePath", delegate {
            AssertFalse(RecycleBinGuard.Recycle(@"Z:\remote\x.zip"));
            string why;
            AssertEq(RecycleBinGuard.Plan(@"Z:\remote\x.zip", out why), DeletePlan.Refuse);
        });

        // Recycle 的失败路径：目标是目录 → false，且目录与其中内容原样保留。
        H.Run("Guard.RecycleNeverTouchesDirectory", delegate {
            TestEnv.MakeFile(@"must_survive\inner.txt", "keep");
            string dir = Path.Combine(TestEnv.Tmp, "must_survive");
            AssertFalse(RecycleBinGuard.Recycle(dir));
            AssertTrue(Directory.Exists(dir));
            AssertTrue(File.Exists(Path.Combine(dir, "inner.txt")));
        });

        // VerifyInBin 的诚实性：名字不在回收站里就必须回 false（不能用 Recycle 的返回值代替核实）。
        H.Run("Guard.VerifyInBinRejectsUnknownName", delegate {
            AssertFalse(RecycleBinGuard.VerifyInBin(UniqueName("never_recycled")));
            AssertFalse(RecycleBinGuard.VerifyInBin(null));
            AssertFalse(RecycleBinGuard.VerifyInBin(""));
        });
    }

    // 回收站按名核实，同名项会跨运行残留 —— 每次运行都必须用新名字，否则「静默永久删除」
    // 会被上一轮的残留项核实成 true（假 PASS）。
    private static string UniqueName(string prefix)
    {
        return prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 10) + ".txt";
    }
}
