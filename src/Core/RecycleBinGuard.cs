// Rerar 回收站守卫（规格 §6.8 SafeDeleter，不变量 I3）—— 本工具唯一的不可逆动作。
//
// I3 是硬约束：删除默认关闭；删前查卷类型与回收站配额；删后枚举回收站核实；核实不到就如实报
// 「已永久删除」，绝不谎称「已移入回收站」；不满足回收条件时提供「隔离文件夹」替代。
//
// 本任务的两项实测（原始输出见 task-9-report.md；结论已回写 docs/research/2026-10-02-edge-case-audit.md 第三节）：
//   * DRIVE_REMOTE：映射盘 Z: → \\localhost\C$ 与 UNC 形式 \\localhost\C$\ 上，
//     FileSystem.DeleteFile(..., SendToRecycleBin) **不抛异常、也不报失败**，但文件真的没了，
//     回收站计数不变、按名核实不到 —— 就是「静默永久删除」。故远程卷一律 Refuse。
//   * 超配额：在一次性挂载的 64 MB 临时 NTFS 卷上把 MaxCapacity 设为 2 MB 再删 8 MB 文件，
//     同样是**不抛异常 + 静默永久删除**（计数不变、核实不到）。故配额必须先于删除检查。
//   * 本机 7 个 BitBucket Volume 项 NukeOnDelete 全为 0（无「删除时不回收」卷）。
//
// 设计要点（安全方向）：
//   * Plan 的任何不确定输入（空/非法路径、盘符不存在、卷不可读、$Recycle.Bin 缺失、
//     注册表项缺失、文件读不到）都降级到 Refuse 或 Quarantine，绝不降级到「照删」；
//   * 读不到配额时用**保守默认**（1024 MB）而不是无限大 —— 宁可多隔离，不可静默永久删除；
//   * Recycle 先过 Plan：Plan != Recycle 一律返回 false，绝不越过前置检查去冒永久删除的风险；
//     任何异常都被吞掉并返回 false，绝不抛给调用方，也绝不退化成永久删除；
//   * Recycle 返回 true 只是「API 调用没报错」，**不是**可恢复的证明 —— 可恢复性只能由
//     VerifyInBin 枚举回收站后回答（I3 的诚实层）。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
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

        // 卷的回收站设置。Documented=false 表示注册表里没有可用记录（缺项/读失败）。
        private sealed class Quota
        {
            public bool Documented;
            public bool NukeOnDelete;
            public long MaxCapacityMb;
        }

        // ------------------------------------------------------------------
        // Plan：决定用哪种机制（Recycle / Quarantine / Refuse），并给出中文判词。
        // reason 一定会被赋值（调用方可以直接显示），且一定具体到「为什么」。
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
                reason = "无法查询卷类型（" + ex.GetType().Name + "），保守拒绝删除：" + Shorten(full);
                return DeletePlan.Refuse;
            }

            if (driveType == DriveRemote)
            {
                // 实测：远程卷上 SendToRecycleBin 不报错却永久删除，且本机回收站里核实不到。
                reason = "文件在网络位置（UNC/映射盘），删除不会进本机回收站且无法核实；拒绝删除";
                return DeletePlan.Refuse;
            }
            if (driveType == DriveRemovable)
            {
                reason = "文件在可移动介质上，回收站不可靠（换机/换卷后无法还原）；拒绝删除";
                return DeletePlan.Refuse;
            }
            if (driveType == DriveCdrom)
            {
                reason = "文件在只读光驱上，无法删除；拒绝";
                return DeletePlan.Refuse;
            }
            if (driveType != DriveFixed && driveType != DriveRamdisk)
            {
                reason = "盘符不存在或卷不可用（GetDriveType=" + driveType + "）；拒绝删除：" + Shorten(full);
                return DeletePlan.Refuse;
            }

            if (!HasRecycleBinFolder(root))
            {
                reason = "该卷根目录下没有 $Recycle.Bin，无法移入回收站；拒绝删除";
                return DeletePlan.Refuse;
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
                reason = "无法读取文件信息（" + ex.GetType().Name + "），保守拒绝删除：" + Shorten(full);
                return DeletePlan.Refuse;
            }

            Quota quota = ReadQuota(root);
            long limitMb = quota.Documented ? quota.MaxCapacityMb : DefaultQuotaMb;

            if (quota.NukeOnDelete)
            {
                reason = "该卷回收站被设为「删除时不回收」（NukeOnDelete=1），删除会绕过回收站；改用隔离文件夹";
                return DeletePlan.Quarantine;
            }
            if (quota.Documented && quota.MaxCapacityMb <= 0)
            {
                reason = "该卷回收站配额为 0 MB（等于不回收）；改用隔离文件夹";
                return DeletePlan.Quarantine;
            }

            long fileMb = Mb(length);
            if (length > limitMb * BytesPerMb)
            {
                if (quota.Documented)
                {
                    reason = "文件 " + fileMb + " MB 超过该卷回收站配额 " + limitMb +
                             " MB，超配额会被永久删除而不进回收站；改用隔离文件夹";
                }
                else
                {
                    // 注册表没有该卷的记录：配额未知，按保守默认判定，绝不假设「装得下」。
                    reason = "读不到该卷回收站配额（按保守默认 " + limitMb + " MB 判定）：文件 " + fileMb +
                             " MB 超过保守默认，可能在回收站里被永久删除；改用隔离文件夹";
                }
                return DeletePlan.Quarantine;
            }

            reason = quota.Documented
                ? "该卷回收站可用（配额 " + limitMb + " MB ≥ 文件 " + fileMb + " MB）"
                : "该卷回收站可用（配额未知，按保守默认 " + limitMb + " MB 判定）；删除后仍需核实";
            return DeletePlan.Recycle;
        }

        // ------------------------------------------------------------------
        // Recycle：真正调用回收站删除。契约：只在 Plan 判定为 Recycle 时才动手；
        // 任何失败/异常都返回 false，绝不抛给调用方，绝不退化成永久删除。
        // 返回 true 只代表「API 没报错」——是否真的可恢复必须再问 VerifyInBin。
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
        // VerifyInBin：真的枚举回收站（Shell.Application + ssfBITBUCKET），按文件名核实。
        // 这是 I3 的诚实层：枚举不了/名字不在里面一律回 false —— 调用方据此如实报「已永久删除」。
        // fileName 可以是文件名，也可以带路径（只取其中的文件名）。
        // ------------------------------------------------------------------
        public static bool VerifyInBin(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) { return false; }

            string wanted;
            try
            {
                wanted = Path.GetFileName(fileName);
            }
            catch (Exception)
            {
                return false;
            }
            if (string.IsNullOrEmpty(wanted)) { return false; }

            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) { return false; }

                object shell = Activator.CreateInstance(shellType);
                if (shell == null) { return false; }

                dynamic shellApp = shell;
                dynamic bin = shellApp.Namespace(SsfBitBucket);
                if (bin == null) { return false; }

                dynamic items = bin.Items();
                if (items == null) { return false; }

                int count = items.Count;
                for (int i = 0; i < count; i++)
                {
                    dynamic item = items.Item(i);
                    if (item == null) { continue; }
                    string name = item.Name;
                    if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) { return true; }
                }
                return false;
            }
            catch (Exception)
            {
                // 枚举不了就不能声称「已在回收站里」。
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 内部：卷根、$Recycle.Bin、注册表配额
        // ------------------------------------------------------------------

        // 文件所在卷的挂载点（优先 GetVolumePathName：挂载文件夹场景下比盘符根更准），
        // 失败退回 Path.GetPathRoot（盘符不存在时 GetVolumePathName 会失败，此时靠它得到 "Z:\"）。
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
