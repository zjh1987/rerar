// Task 9 单元测试：RecycleBinGuard（回收站可用性前置检查 + 删后核实 + 隔离兜底）。
//
// 前 3 条用例的名字逐字来自 task-9-brief.md Step 1；其余用例覆盖 brief 的 Safety requirements：
//   * 不确定输入（缺注册表项、盘符不存在、路径本身不合法）必须降级到 Refuse/Quarantine，
//     绝不降级到「永久删除」；
//   * Recycle 任何失败都返回 false、不抛给调用方、绝不退化成永久删除；
//   * VerifyInBin 必须真的枚举回收站，并证实**本次删除新增了条目**（I3 的诚实层）。
//
// Task 9 fix 轮（控制方裁定）改了三处**期望值/接口**，都在这份文件里有明确痕迹：
//   1. 非可回收卷的兜底从 Refuse 改成 Quarantine（规格 §6.8），Refuse 只留给「连同卷移动都不安全」：
//      超配额（`Guard.PlansQuarantineWhenTooLargeForQuota`）、卷上没有 $Recycle.Bin
//      （`Guard.QuarantinesWhenVolumeHasNoRecycleBin`）、源目录不可写（`Guard.RefusesWhenVolumeNotWritable`）、
//      远程卷的两面（`Guard.QuarantinesOnWritableRemoteVolume` / `Guard.RefusesOnRemoteVolumeWhenUnwritable`）。
//   2. VerifyInBin 由「按名匹配」改成「比对删除前后是否新增条目」，签名随之变为
//      `VerifyInBin(fileName, before)`（基线由 `CaptureBin(fileName)` 在删除前取得）：
//      `Guard.RecyclesAndVerifies`、`Guard.VerifyInBinRejectsUnknownName` 随之更新；
//      `Guard.VerifyInBinRequiresNewEntry` 是这次改造的钉子（旧的按名实现会假 PASS）。
//   3. `Guard.RefusesOnUncPath`：探针实测那条路径上的文件根本不存在，Plan 在「文件不存在」这一关
//      就返回 Refuse、**没走到卷类型分支**，故期望值改回 Refuse 并把这一点写进注释；
//      远程卷本身的两条裁定另用两条用例覆盖（需要提权会话的回环管理共享，拿不到就如实 SKIPPED）。
//
// 与 brief 示例的另一处偏离（上一轮已写进 task-9-report.md）：回收站核实不再靠「名字在不在里面」，
// 所以固定文件名不再会造成假 PASS；但回收用例仍用带随机后缀的文件名，避免在用户回收站里
// 反复堆积同名残留。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Rerar.Core;

internal sealed class RecycleBinGuardTests : TestBase
{
    public static void Run()
    {
        // brief Step 1 用例 1：回收站删除后必须能从回收站里核实到**新出现的**条目。
        H.Run("Guard.RecyclesAndVerifies", delegate {
            string name = UniqueName("guard_me");
            string f = TestEnv.MakeFile(name, "x");
            string why;
            AssertEq(RecycleBinGuard.Plan(f, out why), DeletePlan.Recycle);

            // 基线必须在删除**之前**取：这样「回收站里本来就有同名项」不会被误判成本次删除成功。
            RecycleBinSnapshot before = RecycleBinGuard.CaptureBin(name);
            AssertEq(before.MatchCount, 0);          // 前置：这个名字本来不在回收站里

            AssertTrue(RecycleBinGuard.Recycle(f));
            AssertFalse(File.Exists(f));
            AssertTrue(RecycleBinGuard.VerifyInBin(name, before));
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
            AssertTrue(why.IndexOf("隔离") >= 0);                // 且必须说清替代机制
        });

        // brief Step 1 用例 3 的字面路径（Z:\remote\x.zip；本机没有 Z: 卷 → DRIVE_NO_ROOT_DIR）。
        // fix 轮核对：这条**不是**在测「远程卷」（远程卷按裁定走 Quarantine），而是在测
        // 「盘符/卷不存在」—— 没有卷就既没有回收站、也没有地方建隔离文件夹，正落在裁定里
        // 「连同卷移动都不安全」的 Refuse 上。所以除了 Refuse，还要断言判词点明了被堵死的替代机制，
        // 否则这条就只是「碰巧 Refuse」。
        // 已知脆弱点（如实记录）：若本机恰好有 Z: 卷（真实盘或别人的映射），这条会失效；本机实测没有。
        H.Run("Guard.RefusesOnRemovableOrRemote", delegate {
            string why;
            AssertEq(RecycleBinGuard.Plan(@"Z:\remote\x.zip", out why), DeletePlan.Refuse);
            AssertTrue(why != null && why.Length > 0);
            AssertTrue(why.IndexOf("隔离文件夹") >= 0);      // 判词必须说清「连隔离都做不了」
            AssertTrue(why.IndexOf("Z:") >= 0);             // 且必须回显是哪个盘符
        });

        // 「真 DRIVE_REMOTE 路径」用例（brief 时期加入，fix 轮改正）。fix 轮探针实测：
        //   * GetDriveType(\\localhost\C$\) = 4 —— 这条路径确实被判为远程卷；
        //   * 但该路径上的文件**根本不存在**（未提权时连这个管理共享都看不见），Plan 在
        //     「文件不存在」这一关就返回 Refuse，压根没走到卷类型那一支。
        // 所以这条钉的是「远程路径 + 文件不在 → Refuse」——任何权限下都成立、不需要提权；
        // 远程卷本身的两条裁定（可写 → Quarantine、不可写 → Refuse）由下面两条用例覆盖。
        H.Run("Guard.RefusesOnUncPath", delegate {
            string path = @"\\localhost\C$\Users\Public\remote_archive.zip";
            AssertFalse(File.Exists(path));                  // 前置：正因文件不在，才轮不到卷类型分支
            string why;
            AssertEq(RecycleBinGuard.Plan(path, out why), DeletePlan.Refuse);
            AssertTrue(why != null && why.Length > 0);
        });

        // 裁定 §6.8 的一半：远程卷不可回收 → **不删**，改提供同卷隔离文件夹（Quarantine）。
        // 要真的走到这一支，路径必须指向一个确实存在的文件：用回环管理共享把 Tmp 换成未映射 UNC
        // 写法即可（同一批文件，卷类型变成 DRIVE_REMOTE）。
        // 需要**提权**会话（`<盘符>$` 是管理共享，未提权连不上）；拿不到就如实 SKIPPED，不假装测过。
        H.Run("Guard.QuarantinesOnWritableRemoteVolume", delegate {
            string local = TestEnv.MakeFile(UniqueName("remote_writable"), "x");

            string unc;
            try { unc = TestEnv.UncViewOf(local); }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine("SKIPPED Guard.QuarantinesOnWritableRemoteVolume：" + ex.Message);
                return;
            }

            string why;
            AssertEq(RecycleBinGuard.Plan(unc, out why), DeletePlan.Quarantine);
            AssertTrue(why.IndexOf("网络") >= 0);            // 判词要点明是网络位置
            AssertTrue(why.IndexOf("隔离") >= 0);            // 且要说清替代机制

            // 正面拦截：远程卷上**根本不调删除 API**（实测该 API 在这种卷上不报错却静默永久删除）。
            AssertFalse(RecycleBinGuard.Recycle(unc));
            AssertTrue(File.Exists(unc));                    // 文件原地未动（UNC 视图）
            AssertTrue(File.Exists(local));                  // 本地同一份文件
        });

        // 裁定 §6.8 的另一半：远程卷 + 源目录不可写（隔离文件夹建不出来）→ Refuse，判词必须
        // 同时点明「网络」与「不可写」—— 这才是「连同卷移动都不安全」。
        // 构造：同一个 Tmp 目录的 UNC 视图 + 本地 ACL 对当前用户 Deny CreateFiles|CreateDirectories，
        // 正是建 `_originals_<时间戳>\` 并把文件移进去所需的权限（与 `Guard.RefusesWhenVolumeNotWritable` 同一手法）。
        // 探针实测：Deny 之后走 UNC 的 CreateFile(FILE_ADD_FILE) 报 Win32 错误 5 —— 提权也挡得住
        //（本进程 SeBackupPrivilege 是 Disabled，FILE_FLAG_BACKUP_SEMANTICS 不会绕过 ACL）。
        H.Run("Guard.RefusesOnRemoteVolumeWhenUnwritable", delegate {
            string dir = Path.Combine(TestEnv.Tmp, "remote_denied");
            Directory.CreateDirectory(dir);
            string local = Path.Combine(dir, "payload.txt");
            File.WriteAllText(local, "x", new UTF8Encoding(false));

            string unc;
            try { unc = TestEnv.UncViewOf(local); }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine("SKIPPED Guard.RefusesOnRemoteVolumeWhenUnwritable：" + ex.Message);
                return;
            }

            DenyCreateIn(dir);
            try
            {
                string why;
                AssertEq(RecycleBinGuard.Plan(unc, out why), DeletePlan.Refuse);
                AssertTrue(why.IndexOf("网络") >= 0);
                AssertTrue(why.IndexOf("不可写") >= 0);
                AssertTrue(why.IndexOf("隔离文件夹") >= 0);

                AssertFalse(RecycleBinGuard.Recycle(unc));   // 不删、不动
                AssertTrue(File.Exists(unc));
                AssertTrue(File.Exists(local));
            }
            finally
            {
                AllowCreateIn(dir);
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

        // VerifyInBin 的诚实性：没有基线、名字不在回收站里、参数为空 → 一律 false
        //（不能用 Recycle 的返回值代替核实）。
        H.Run("Guard.VerifyInBinRejectsUnknownName", delegate {
            string name = UniqueName("never_recycled");
            RecycleBinSnapshot before = RecycleBinGuard.CaptureBin(name);
            AssertEq(before.MatchCount, 0);

            AssertFalse(RecycleBinGuard.VerifyInBin(name, before));
            AssertFalse(RecycleBinGuard.VerifyInBin(null, before));
            AssertFalse(RecycleBinGuard.VerifyInBin("", before));
            AssertFalse(RecycleBinGuard.VerifyInBin(UniqueName("no_baseline"), null));   // 没有基线就不作数
        });

        // Finding 1 的钉子：卷上没有 $Recycle.Bin（刚挂载/不支持回收站的卷）→ 不删，改隔离文件夹。
        // 夹具：subst 映射出来的盘符（本机实测 GetDriveType=3 固定卷、该盘根下没有 $Recycle.Bin），无需提权。
        // 如实说明它是**代理**夹具：底层真卷（C:）自己有回收站，但映射盘的「卷根」是 <Tmp>\substroot，
        // 其下没有 $Recycle.Bin —— 代码正是按卷根判的（GetVolumePathName 对 subst 盘失败，退回盘符根）。
        // 真造一个「没有回收站的真实卷」要挂 VHD（提权、且改机器状态），故用代理；夹具自己还会复查
        // 「该盘下确实没有 $Recycle.Bin」，名不副实就直接抛异常而不是假 PASS。
        H.Run("Guard.QuarantinesWhenVolumeHasNoRecycleBin", delegate {
            try
            {
                string path;
                try
                {
                    path = TestEnv.NoRecycleBinFile;
                }
                catch (InvalidOperationException ex)
                {
                    Console.WriteLine("SKIPPED Guard.QuarantinesWhenVolumeHasNoRecycleBin：" + ex.Message);
                    return;
                }

                string why;
                AssertEq(RecycleBinGuard.Plan(path, out why), DeletePlan.Quarantine);
                AssertTrue(why.IndexOf("$Recycle.Bin") >= 0);      // 判词要点明是哪一条不满足
                AssertTrue(why.IndexOf("隔离") >= 0);              // 且要说清替代机制

                AssertFalse(RecycleBinGuard.Recycle(path));        // 前置门生效：根本不调删除 API
                AssertTrue(File.Exists(path));                     // 文件原地未动
            }
            finally
            {
                // 映射是**进程外**状态：TestEnv.Cleanup() 删得掉目标目录、并在下一个用例前撤掉
                // 僵留映射，但**当前用例正在用的**这条映射必须由用例自己撤。
                TestEnv.ReleaseNoRecycleBinVolume();
            }
        });

        // Finding 1 的钉子：连「同卷移动到隔离文件夹」都不安全（源目录不可写）→ Refuse。
        // 构造：subst 卷（回收站不可用：没有 $Recycle.Bin）+ 源目录对当前用户 Deny
        // CreateFiles|CreateDirectories —— 正是建 `_originals_<时间戳>\` 并把文件移进去所需的权限。
        //
        // 未能构造的部分（如实记录，见报告）：`FILE_READ_ONLY_VOLUME`（写保护卷）那条 Refuse 分支
        // 需要挂载一个只读卷，本机无提权无法造；它与本条同属「连移动都不可能」，走的是同一句判词风格。
        H.Run("Guard.RefusesWhenVolumeNotWritable", delegate {
            try
            {
                string root;
                try
                {
                    root = TestEnv.NoRecycleBinVolumeRoot;
                }
                catch (InvalidOperationException ex)
                {
                    Console.WriteLine("SKIPPED Guard.RefusesWhenVolumeNotWritable：" + ex.Message);
                    return;
                }

                string dir = Path.Combine(root, "unwritable");
                if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
                string file = Path.Combine(dir, "payload.txt");
                File.WriteAllText(file, "x", new UTF8Encoding(false));

                DenyCreateIn(dir);
                try
                {
                    string why;
                    AssertEq(RecycleBinGuard.Plan(file, out why), DeletePlan.Refuse);
                    AssertTrue(why.IndexOf("不可写") >= 0);         // 判词必须说清是「不能移动」
                    AssertTrue(why.IndexOf("隔离文件夹") >= 0);     // 且点明被拒的是隔离文件夹这条路

                    AssertFalse(RecycleBinGuard.Recycle(file));     // 不删、不动
                    AssertTrue(File.Exists(file));
                }
                finally
                {
                    AllowCreateIn(dir);
                }
            }
            finally
            {
                TestEnv.ReleaseNoRecycleBinVolume();
            }
        });

        // Finding 2 的钉子：回收站里**已经有**一个同名项时，「本次删除没有新增条目」也必须核实为 false。
        //
        // 构造：先真回收一个文件（回收站里就此有一个同名项），再造一个同名文件、用 File.Delete
        // 直接永久删除来**模拟**「API 静默永久删除」（实测已证明外壳会在超配额/远程卷上这样干，
        // 但那两种触发方式都要动用户机器状态：改配额或建共享，所以这里用等价的删除结果来模拟）。
        // 旧实现（只按名字匹配）在这一步会回 true —— 那正是「谎称已移入回收站」。
        H.Run("Guard.VerifyInBinRequiresNewEntry", delegate {
            string name = UniqueName("dup_name");
            string first = TestEnv.MakeFile(name, "first");

            RecycleBinSnapshot beforeFirst = RecycleBinGuard.CaptureBin(name);
            AssertEq(beforeFirst.MatchCount, 0);
            AssertTrue(RecycleBinGuard.Recycle(first));
            AssertTrue(RecycleBinGuard.VerifyInBin(name, beforeFirst));   // 真新增 → 核实到

            // 此刻回收站里已有 1 个同名项：这正是旧实现会误判为 true 的情形。
            string second = TestEnv.MakeFile(name, "second");
            RecycleBinSnapshot beforeSecond = RecycleBinGuard.CaptureBin(name);
            AssertEq(beforeSecond.MatchCount, 1);

            // 模拟「静默永久删除」：文件真的没了，但回收站里一个条目都没多。
            File.Delete(second);
            AssertFalse(File.Exists(second));
            AssertFalse(RecycleBinGuard.VerifyInBin(name, beforeSecond));   // 旧实现这里会（错误地）回 true
        });

        // 裁定落地后的优先级：回收站**可用**时，即使源目录不可写（隔离文件夹建不出来）也仍然走
        // Recycle —— 回收站删除只需要文件自身的删除权限，不需要在源目录里创建任何东西。
        // 这条与上一条一起钉死「Refuse 只留给两种机制都不可行」。
        H.Run("Guard.RecyclesWhenQuarantineDirDenied", delegate {
            string dir = Path.Combine(TestEnv.Tmp, "denied_dir");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "keep_or_recycle.txt");
            File.WriteAllText(file, "x", new UTF8Encoding(false));

            DenyCreateIn(dir);
            try
            {
                string why;
                AssertEq(RecycleBinGuard.Plan(file, out why), DeletePlan.Recycle);

                string name = Path.GetFileName(file);
                RecycleBinSnapshot before = RecycleBinGuard.CaptureBin(name);
                AssertTrue(RecycleBinGuard.Recycle(file));
                AssertFalse(File.Exists(file));
                AssertTrue(RecycleBinGuard.VerifyInBin(name, before));
            }
            finally
            {
                AllowCreateIn(dir);
            }
        });
    }

    // ------------------------------------------------------------------
    // ACL 夹具：把目录的「创建文件 / 创建目录」权限对当前用户 Deny 掉 —— 这正是隔离文件夹
    //（同卷 `_originals_<时间戳>\`）所需的两项权限（FILE_ADD_FILE / FILE_ADD_SUBDIRECTORY）。
    //
    // 只 Deny 这两项，不动 Delete / WriteDac：所以即使测试进程中途被杀、这句 Deny 来不及撤销，
    // TestEnv.Cleanup() 的递归删除照样能清掉这棵目录树（本机实测）。
    // ------------------------------------------------------------------
    private static void DenyCreateIn(string dir)
    {
        DirectorySecurity security = Directory.GetAccessControl(dir);
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User,
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories,
            AccessControlType.Deny));
        Directory.SetAccessControl(dir, security);
    }

    private static void AllowCreateIn(string dir)
    {
        DirectorySecurity security = Directory.GetAccessControl(dir);
        security.RemoveAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User,
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories,
            AccessControlType.Deny));
        Directory.SetAccessControl(dir, security);
    }

    // 回收站核实已改为「比对删除前后的条目」，同名残留不再造成假 PASS；
    // 这里仍用随机名，只是避免在用户回收站里反复堆积同名的 1 字节残留。
    private static string UniqueName(string prefix)
    {
        return prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 10) + ".txt";
    }
}
