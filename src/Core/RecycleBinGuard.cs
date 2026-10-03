// Rerar 回收站守卫（规格 §6.8 SafeDeleter，不变量 I3）—— 本工具唯一的不可逆动作。
//
// I3 是硬约束：删除默认关闭；删前查卷类型与回收站配额；删后枚举回收站核实；核实不到就如实报
// 「已永久删除」，绝不谎称「已移入回收站」；不满足回收条件时提供「隔离文件夹」替代。
//
// 本任务的两项实测（原始输出见 task-9-report.md；结论已回写 docs/research/2026-10-02-edge-case-audit.md 第三节）：
//   * DRIVE_REMOTE：映射盘 Z: → \\localhost\C$ 与 UNC 形式 \\localhost\C$\ 上，
//     FileSystem.DeleteFile(..., SendToRecycleBin) **不抛异常、也不报失败**，但文件真的没了，
//     回收站计数不变、按名核实不到 —— 就是「静默永久删除」。故远程卷一律不走回收站。
//   * 超配额：在一次性挂载的 64 MB 临时 NTFS 卷上把 MaxCapacity 设为 2 MB 再删 8 MB 文件，
//     同样是**不抛异常 + 静默永久删除**（计数不变、核实不到）。故配额必须先于删除检查。
//   * 本机 7 个 BitBucket Volume 项 NukeOnDelete 全为 0（无「删除时不回收」卷）。
//
// 三种机制（控制方在 Task 9 fix 轮的裁定）：
//   * Recycle    —— 卷可回收（固定卷 + 有 $Recycle.Bin + 配额够 + 未设 NukeOnDelete）时的首选；
//   * Quarantine —— **每一个不可回收的卷**（无 $Recycle.Bin / 超配额 / 远程 / 可移动 / 卷类型未知），
//                   只要「同卷移动到隔离文件夹」这一步可行，就走这里：不删，改提供
//                   同卷 `_originals_<时间戳>\`（规格 §6.8「不满足 3/4 时」）；
//   * Refuse     —— 只留给「连同卷移动都不安全」：源目录不可写（隔离文件夹无处可建）、卷是只读卷、
//                   盘符/卷不存在、只读介质、路径本身非法/文件不存在。Refuse 也是非破坏性的。
//
// 设计要点（安全方向）：
//   * Plan 的任何不确定输入（空/非法路径、盘符不存在、卷不可读、$Recycle.Bin 缺失、
//     注册表项缺失、文件读不到、目录访问权查不出来）都降级到 Refuse 或 Quarantine，绝不降级到「照删」；
//   * 读不到配额时用**保守默认**（1024 MB）而不是无限大 —— 宁可多隔离，不可静默永久删除；
//   * Recycle 先过 Plan：Plan != Recycle 一律返回 false，绝不越过前置检查去冒永久删除的风险；
//     任何异常都被吞掉并返回 false，绝不抛给调用方，也绝不退化成永久删除；
//   * Recycle 返回 true 只是「API 调用没报错」，**不是**可恢复的证明。实测已证明这个 API 会在
//     不该调用它的卷上静默永久删除，所以返回值一律不作数：可恢复性只能由 VerifyInBin **比对
//     删除前后的回收站条目**来回答（I3 的诚实层，见下方 VerifyInBin 的说明）。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Rerar.Core
{
    // 删除机制的三选一：能真进回收站 / 只能进隔离文件夹 / 连隔离都不安全。
    public enum DeletePlan
    {
        Recycle,
        Quarantine,
        Refuse
    }

    // 回收站里「与某个文件名匹配的条目」的快照 —— VerifyInBin 判断「这次删除有没有**新增**条目」
    // 的基线。必须由调用方在 Recycle **之前** CaptureBin() 拿到，删完再交给 VerifyInBin。
    //
    // 为什么需要它（Task 9 fix 轮 Finding 2）：只看「回收站里有没有同名条目」会产生假核实 ——
    // 回收站里本来躺着一个同名文件（用户以前删过同名文件）时，即使本次删除静默永久删除了，
    // 按名匹配依然会回 true，于是 UI 谎称「已移入回收站」。那是 I3 明令禁止的不诚实。
    public sealed class RecycleBinSnapshot
    {
        private readonly bool _readable;
        private readonly int _matchCount;
        private readonly List<string> _keys;

        internal RecycleBinSnapshot(bool readable, int matchCount, List<string> keys)
        {
            _readable = readable;
            _matchCount = matchCount;
            _keys = keys == null ? new List<string>() : keys;
        }

        // 枚举得出回收站（COM 建不起来 / 枚举抛异常时为 false）。false 时一切核实都回 false。
        public bool Readable { get { return _readable; } }

        // 快照时刻匹配到几个条目。
        public int MatchCount { get { return _matchCount; } }

        // 条目身份键是否已在这个快照里（键 = 回收站里的 $R 文件路径，取不到时退回显示名）。
        internal bool ContainsKey(string key)
        {
            for (int i = 0; i < _keys.Count; i++)
            {
                if (string.Equals(_keys[i], key, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }
    }

    public static class RecycleBinGuard
    {
        // ---- GetDriveType 返回值（winbase.h）----
        private const uint DriveUnknown = 0;
        private const uint DriveNoRootDir = 1;
        private const uint DriveRemovable = 2;
        private const uint DriveFixed = 3;
        private const uint DriveRemote = 4;
        private const uint DriveCdrom = 5;
        private const uint DriveRamdisk = 6;

        // ---- GetVolumeInformation 的 lpFileSystemFlags 位（winbase.h）----
        private const uint FileReadOnlyVolume = 0x00080000;

        // ---- CreateFile（访问检查用）----
        private const uint FileAddFile = 0x0002;          // 对目录句柄 = FILE_ADD_FILE
        private const uint FileAddSubdirectory = 0x0004;
        private const uint FileShareAll = 0x00000007;     // read | write | delete
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;   // 打开**目录**句柄必须带

        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        // Shell.Application 的 ssfBITBUCKET（回收站虚拟文件夹）；本机实测可用。
        private const int SsfBitBucket = 0xA;

        // BitBucket\Volume 项名就是卷 GUID（GetVolumeNameForVolumeMountPoint 的 {…} 部分）。
        private const string BitBucketVolumeKey =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume";

        // 读不到配额时使用的**保守**默认值：宁可把大文件判成「要隔离」，也绝不在配额未知时
        // 假设回收站装得下（装不下的后果是静默永久删除）。
        private const long DefaultQuotaMb = 1024;

        private const long BytesPerMb = 1024L * 1024L;
        private const int ReasonPathLimit = 120;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetDriveType(string lpRootPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumePathName(string lpszFileName, StringBuilder lpszVolumePathName, uint cchBufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeNameForVolumeMountPoint(string lpszVolumeMountPoint, StringBuilder lpszVolumeName, uint cchBufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeInformation(
            string lpRootPathName, StringBuilder lpVolumeNameBuffer, uint nVolumeNameSize,
            out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength, out uint lpFileSystemFlags,
            StringBuilder lpFileSystemNameBuffer, uint nFileSystemNameSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        // 卷的回收站设置。Documented=false 表示注册表里没有可用记录（缺项/读失败）。
        private sealed class Quota
        {
            public bool Documented;
            public bool NukeOnDelete;
            public long MaxCapacityMb;
        }

        // ------------------------------------------------------------------
        // Plan：决定用哪种机制（Recycle / Quarantine / Refuse），并给出中文判词。
        // reason 一定会被赋值（调用方可以直接显示），且一定具体到「为什么」——
        // 用户要能分清「这次走的是哪条路、为什么没走回收站」。
        // ------------------------------------------------------------------
        public static DeletePlan Plan(string filePath, out string reason)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                reason = "未提供待删除的文件路径，无法判断回收站是否可用";
                return DeletePlan.Refuse;
            }

            string full;
            try
            {
                full = Path.GetFullPath(filePath);
            }
            catch (Exception ex)
            {
                reason = "路径无效（" + ex.GetType().Name + "），拒绝删除：" + Shorten(filePath);
                return DeletePlan.Refuse;
            }

            string root = VolumeRoot(full);
            if (root.Length == 0)
            {
                reason = "无法确定文件所在卷（路径不含盘符），拒绝删除：" + Shorten(full);
                return DeletePlan.Refuse;
            }

            uint driveType;
            try
            {
                driveType = GetDriveType(root);
            }
            catch (Exception ex)
            {
                reason = "无法查询卷类型（" + ex.GetType().Name + "），拒绝删除：" + Shorten(full);
                return DeletePlan.Refuse;
            }

            // 卷/盘符不存在：既不能回收，也没有地方建隔离文件夹 —— 连同卷移动都不可能。
            if (driveType == DriveUnknown || driveType == DriveNoRootDir)
            {
                reason = "盘符不存在或卷不可用（GetDriveType=" + driveType + "），连同卷的隔离文件夹都无处可建；拒绝删除：" + Shorten(full);
                return DeletePlan.Refuse;
            }
            // 只读介质（光驱）：回收与同卷移动都不可能。
            if (driveType == DriveCdrom)
            {
                reason = "文件在只读介质（光驱）上，既不能回收也不能同卷移动；拒绝自动处理：" + Shorten(full);
                return DeletePlan.Refuse;
            }
            // 只读卷（例如写保护的移动介质 / 以只读挂载的卷）：同上。
            if (IsReadOnlyVolume(root))
            {
                reason = "该卷是只读卷（FILE_READ_ONLY_VOLUME），既不能回收也不能同卷移动；拒绝自动处理：" + Shorten(full);
                return DeletePlan.Refuse;
            }

            // 卷层面的不可回收原因（与文件是否存在无关，先定下来）：回收站这条路走不通时把
            // 「为什么」记在这里；为 null 表示回收站仍有希望。判词要能说清是哪一条。
            string notRecyclable = null;

            if (driveType == DriveRemote)
            {
                // 实测：远程卷上 SendToRecycleBin 不报错却永久删除，且本机回收站里核实不到。
                notRecyclable = "文件在网络位置（UNC/映射盘）：回收站在对端、本机无从核实，实测此卷上删除不报错却会静默永久删除；不回收";
            }
            else if (driveType == DriveRemovable)
            {
                notRecyclable = "文件在可移动介质上，回收站不可靠（换机/换卷后可能无法还原）；不回收";
            }
            else if (driveType != DriveFixed && driveType != DriveRamdisk)
            {
                notRecyclable = "卷类型未知（GetDriveType=" + driveType + "），不冒险走回收站";
            }

            long length;
            try
            {
                FileInfo info = new FileInfo(full);
                if (!info.Exists)
                {
                    reason = "文件不存在，无法删除：" + Shorten(full);
                    return DeletePlan.Refuse;
                }
                // Length 必须在同一个 try 里读：文件恰好被移走 / 无权限时这里会抛，
                // 抛给调用方就等于把「判断不出来」变成「不知道该怎么办」，只能保守拒绝。
                length = info.Length;
            }
            catch (Exception ex)
            {
                reason = "无法读取文件信息（" + ex.GetType().Name + "），拒绝删除：" + Shorten(full);
                return DeletePlan.Refuse;
            }

            // 卷本身还能回收时，再看回收站自身：目录在不在、是不是被设成「不回收」、装不装得下。
            if (notRecyclable == null)
            {
                if (!HasRecycleBinFolder(root))
                {
                    // brief Step 3 原文判 Refuse；控制方在 fix 轮裁定从规格 §6.8 —— 该卷不可回收，
                    // 但同卷隔离仍然可行且更有用，故走 Quarantine。
                    notRecyclable = "该卷根目录下没有 $Recycle.Bin（外壳还没建出来，或该卷不支持回收站）；不回收";
                }
                else
                {
                    Quota quota = ReadQuota(root);
                    long limitMb = quota.Documented ? quota.MaxCapacityMb : DefaultQuotaMb;
                    long fileMb = Mb(length);

                    if (quota.NukeOnDelete)
                    {
                        notRecyclable = "该卷回收站被设为「删除时不回收」（NukeOnDelete=1），删除会绕过回收站；不回收";
                    }
                    else if (quota.Documented && quota.MaxCapacityMb <= 0)
                    {
                        notRecyclable = "该卷回收站配额为 0 MB（等于不回收）；不回收";
                    }
                    else if (length > limitMb * BytesPerMb)
                    {
                        if (quota.Documented)
                        {
                            notRecyclable = "文件 " + fileMb + " MB 超过该卷回收站配额 " + limitMb +
                                            " MB，超配额会被永久删除而不进回收站；不回收";
                        }
                        else
                        {
                            // 注册表没有该卷的记录：配额未知，按保守默认判定，绝不假设「装得下」。
                            notRecyclable = "读不到该卷回收站配额（按保守默认 " + limitMb + " MB 判定）：文件 " + fileMb +
                                            " MB 超过保守默认，可能在回收站里被永久删除；不回收";
                        }
                    }
                    else
                    {
                        reason = quota.Documented
                            ? "该卷回收站可用（配额 " + limitMb + " MB ≥ 文件 " + fileMb + " MB）"
                            : "该卷回收站可用（配额未知，按保守默认 " + limitMb + " MB 判定）；删除后仍需核实";
                        return DeletePlan.Recycle;
                    }
                }
            }

            // 走到这里：回收站这条路走不通（规格 §6.8 的「不满足 3/4」），唯一剩下的机制是
            // 「同卷移动到隔离文件夹 _originals_<时间戳>\」。只有连这个都做不了时才 Refuse。
            string moveBlocked;
            if (!CanWriteSourceDirectory(full, out moveBlocked))
            {
                reason = moveBlocked + "（回收站同样不可用：" + notRecyclable + "）；连同卷移动都不安全，拒绝自动处理";
                return DeletePlan.Refuse;
            }

            reason = notRecyclable + "；改用同卷隔离文件夹 _originals_<时间戳>\\（不删除）";
            return DeletePlan.Quarantine;
        }

        // ------------------------------------------------------------------
        // Recycle：真正调用回收站删除。契约：只在 Plan 判定为 Recycle 时才动手；
        // 任何失败/异常都返回 false，绝不抛给调用方，绝不退化成永久删除。
        //
        // 返回 true 只代表「API 没报错」——实测这个 API 在远程卷/超配额时会**不报错地永久删除**，
        // 所以返回值不构成「已移入回收站」的任何证据：必须再用 VerifyInBin 比对删除前后的条目。
        // ------------------------------------------------------------------
        public static bool Recycle(string filePath)
        {
            try
            {
                string reason;
                if (Plan(filePath, out reason) != DeletePlan.Recycle)
                {
                    // 不在「能真进回收站」的情况下绝不删：超配额/远程卷上这个 API 会静默永久删除。
                    return false;
                }

                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    filePath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // CaptureBin：在 Recycle **之前**取该文件名的回收站条目基线（VerifyInBin 的必需输入）。
        // 枚举不了时返回 Readable=false 的快照（调用方据此只能如实报「已永久删除」）。
        // ------------------------------------------------------------------
        public static RecycleBinSnapshot CaptureBin(string fileName)
        {
            List<string> keys = new List<string>();
            bool readable;
            int count = EnumerateMatches(fileName, keys, out readable);
            return new RecycleBinSnapshot(readable, count, keys);
        }

        // ------------------------------------------------------------------
        // VerifyInBin：核实**本次删除新增了一个条目**，而不是「回收站里存在同名条目」。
        //
        // 为什么必须带 before（Task 9 fix 轮 Finding 2）：回收站里可能本来就有一个同名文件
        //（用户以前删过同名文件），按名匹配会让「静默永久删除」也被核实成 true —— 于是 UI 谎称
        //「已移入回收站」。这里要求「条目数变多」或「出现了一个 before 里没有的条目身份键」。
        //
        // 偏保守是刻意的：文件真的进了回收站、但比对没能确认（枚举失败 / before 为 null /
        // 并发清空导致条目数没变）时，本方法回 false —— 调用方会如实报「已永久删除」。
        // 少报可恢复性是安全的；多报（谎称可恢复）是 I3 明令禁止的。
        // ------------------------------------------------------------------
        public static bool VerifyInBin(string fileName, RecycleBinSnapshot before)
        {
            // 没有可信的基线就没有「新增」可言：不猜、不作数。
            if (before == null || !before.Readable) { return false; }

            List<string> keys = new List<string>();
            bool readable;
            int count = EnumerateMatches(fileName, keys, out readable);
            if (!readable) { return false; }

            if (count > before.MatchCount) { return true; }

            // 条目数没变也可能真的新增了（同时被清掉一个）：看身份键有没有 before 里没有的。
            for (int i = 0; i < keys.Count; i++)
            {
                if (!before.ContainsKey(keys[i])) { return true; }
            }
            return false;
        }

        // ------------------------------------------------------------------
        // 内部：回收站枚举（Shell.Application + ssfBITBUCKET）
        // ------------------------------------------------------------------

        // 枚举回收站里与 fileName 同名的条目，返回匹配数；keys 收下每个匹配条目的身份键。
        // readable=false 表示**枚举不出来**（COM 建不起来 / 枚举抛异常），此时调用方必须回 false。
        private static int EnumerateMatches(string fileName, List<string> keys, out bool readable)
        {
            readable = false;
            if (string.IsNullOrEmpty(fileName)) { return 0; }

            string wanted;
            try
            {
                wanted = Path.GetFileName(fileName);
            }
            catch (Exception)
            {
                return 0;
            }
            if (string.IsNullOrEmpty(wanted)) { return 0; }

            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) { return 0; }

                object shell = Activator.CreateInstance(shellType);
                if (shell == null) { return 0; }

                dynamic shellApp = shell;
                dynamic bin = shellApp.Namespace(SsfBitBucket);
                if (bin == null) { return 0; }

                dynamic items = bin.Items();
                if (items == null) { return 0; }

                int total = items.Count;
                int matched = 0;

                // 枚举本身可用即置 readable：单个条目读不动只是少一个键（更保守），
                // 不改变「我们确实看到了回收站」这个事实。循环中抛异常会落到外层 catch，
                // 那时 readable 会被改回 false —— 一样是保守方向。
                readable = true;

                for (int i = 0; i < total; i++)
                {
                    string name;
                    string key;
                    if (!TryDescribeItem(items, i, out name, out key)) { continue; }
                    if (!string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) { continue; }

                    matched++;
                    keys.Add(key);
                }
                return matched;
            }
            catch (Exception)
            {
                // 枚举不了就不能声称「已在回收站里」。
                readable = false;
                return 0;
            }
        }

        // 取一个回收站条目的显示名与身份键。任何一项读不动都只是跳过这一项（保守方向）。
        private static bool TryDescribeItem(dynamic items, int index, out string name, out string key)
        {
            name = null;
            key = null;
            try
            {
                dynamic item = items.Item(index);
                if (item == null) { return false; }

                name = (string)item.Name;
                if (name == null) { return false; }

                // 身份键优先用 $R 文件路径：每个回收条目一份，天然唯一（同名文件也是两个不同的键）。
                // 取不到就退回显示名 —— 此时同名条目无法区分，只会让核实更保守（可能少判一次「新增」）。
                key = null;
                try { key = (string)item.Path; }
                catch (Exception) { key = null; }
                if (string.IsNullOrEmpty(key)) { key = name; }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 内部：卷根、$Recycle.Bin、只读卷、目录访问权、注册表配额
        // ------------------------------------------------------------------

        // 文件所在卷的挂载点（优先 GetVolumePathName：挂载文件夹场景下比盘符根更准），
        // 失败退回 Path.GetPathRoot（盘符不存在 / subst 映射盘时 GetVolumePathName 会失败，
        // 此时靠它得到 "Z:\"、"L:\"）。
        private static string VolumeRoot(string fullPath)
        {
            try
            {
                StringBuilder sb = new StringBuilder(400);
                if (GetVolumePathName(fullPath, sb, (uint)sb.Capacity))
                {
                    string volumePath = sb.ToString();
                    if (volumePath.Length > 0) { return volumePath; }
                }
            }
            catch (Exception)
            {
            }

            try
            {
                return Path.GetPathRoot(fullPath);
            }
            catch (Exception)
            {
                return "";
            }
        }

        // 卷根下有没有 $Recycle.Bin。读不了（权限/异常）按「没有」处理 —— 保守方向。
        private static bool HasRecycleBinFolder(string root)
        {
            try
            {
                return Directory.Exists(Path.Combine(root, "$Recycle.Bin"));
            }
            catch (Exception)
            {
                return false;
            }
        }

        // 卷本身是不是只读卷。查不到（返回 false）时不作判断 —— 后面的 $Recycle.Bin、
        // 配额与「连移动都不安全」的目录访问权检查仍然会把关。
        private static bool IsReadOnlyVolume(string root)
        {
            try
            {
                uint serial;
                uint maxComponentLength;
                uint flags;
                StringBuilder volumeName = new StringBuilder(300);
                StringBuilder fileSystemName = new StringBuilder(300);

                if (!GetVolumeInformation(
                        root, volumeName, (uint)volumeName.Capacity,
                        out serial, out maxComponentLength, out flags,
                        fileSystemName, (uint)fileSystemName.Capacity))
                {
                    return false;
                }
                return (flags & FileReadOnlyVolume) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // 「连同卷移动到隔离文件夹都不安全」的判据：源文件所在目录能不能被写入
        // （FILE_ADD_FILE | FILE_ADD_SUBDIRECTORY —— 建 `_originals_<时间戳>\` 并把文件移进去所需的访问权）。
        // 用 CreateFile 打开目录句柄**只做访问检查**，不创建任何文件：比「真的建一个探针文件」干净。
        // 目录不存在 / 打不开 / 判不出来一律 false（保守方向）。
        private static bool CanWriteSourceDirectory(string fullPath, out string whyNot)
        {
            whyNot = null;

            string dir;
            try
            {
                dir = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(dir)) { dir = Path.GetPathRoot(fullPath); }
            }
            catch (Exception)
            {
                dir = null;
            }
            if (string.IsNullOrEmpty(dir))
            {
                whyNot = "无法确定源文件所在目录，隔离文件夹无处可建";
                return false;
            }

            IntPtr handle = IntPtr.Zero;
            try
            {
                if (!Directory.Exists(dir))
                {
                    whyNot = "源文件所在目录不存在（" + Shorten(dir) + "），隔离文件夹无处可建";
                    return false;
                }

                handle = CreateFile(dir, FileAddFile | FileAddSubdirectory, FileShareAll,
                    IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
                if (handle == InvalidHandleValue)
                {
                    whyNot = "源文件所在目录「" + Shorten(dir) + "」不可写（无法创建隔离文件夹 _originals_<时间戳>\\，Win32 错误 " +
                             Marshal.GetLastWin32Error() + "）";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                whyNot = "无法确认源文件所在目录「" + Shorten(dir) + "」可否写入（" + ex.GetType().Name + "）";
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero && handle != InvalidHandleValue)
                {
                    try { CloseHandle(handle); } catch (Exception) { }
                }
            }
        }

        // 读该卷的回收站设置。缺项/读失败 → Documented=false（调用方用保守默认配额）。
        private static Quota ReadQuota(string root)
        {
            Quota quota = new Quota();
            quota.Documented = false;
            quota.NukeOnDelete = false;
            quota.MaxCapacityMb = DefaultQuotaMb;

            string guid = VolumeGuid(root);
            if (guid == null) { return quota; }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(BitBucketVolumeKey + "\\" + guid))
                {
                    if (key == null) { return quota; }

                    object nuke = key.GetValue("NukeOnDelete");
                    if (nuke != null)
                    {
                        if (nuke is int) { if ((int)nuke != 0) { quota.NukeOnDelete = true; } }
                        // 值在、但读不成 int（异常类型/损坏）：按「不回收」处理 —— 保守方向。
                        else { quota.NukeOnDelete = true; }
                    }

                    object capacity = key.GetValue("MaxCapacity");
                    if (capacity is int && (int)capacity >= 0)
                    {
                        quota.MaxCapacityMb = (int)capacity;
                        quota.Documented = true;
                    }
                }
            }
            catch (Exception)
            {
                // 注册表读失败 → 保持保守默认。
            }

            return quota;
        }

        // "\\?\Volume{bc270a2e-…}\" → "{bc270a2e-…}"（注册表项名）。取不到返回 null。
        private static string VolumeGuid(string root)
        {
            try
            {
                StringBuilder sb = new StringBuilder(400);
                if (!GetVolumeNameForVolumeMountPoint(root, sb, (uint)sb.Capacity)) { return null; }

                string name = sb.ToString();
                int open = name.IndexOf('{');
                int close = name.IndexOf('}');
                if (open < 0 || close <= open) { return null; }

                return name.Substring(open, close - open + 1);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // 字节 → MB（向上取整：判词里宁可说大一点，也不要让用户以为「差不多刚好」）。
        private static long Mb(long bytes)
        {
            if (bytes <= 0) { return 0; }
            return (bytes + BytesPerMb - 1) / BytesPerMb;
        }

        // 判词里回显路径要短：压平换行并截断，避免把长路径灌进 UI。
        private static string Shorten(string path)
        {
            if (path == null) { return ""; }
            string flat = path.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (flat.Length <= ReasonPathLimit) { return flat; }
            return flat.Substring(0, ReasonPathLimit) + "…";
        }
    }
}
