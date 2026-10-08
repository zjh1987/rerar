// Rerar 磁盘可用空间提供者（Task 10；规格 §6.7 暂存/校验、Review Focus #1）。
//
// 为什么把它做成可注入的接口：规格只写了「启动前预检」，而真正会伤到用户的是**解压中途盘写满**
// —— 那时用户期望的是「干净中止并明确告知」，而不是几百条级联 I/O 报错之后留下一个貌似完整的
// 产物目录。要让这条路径可测，就必须能在测试里把「可用空间」变成一个可控输入（模拟盘满），
// 而不是真去写满一块盘。于是空间查询被收在一个单方法接口后面。
//
// 语义约定（Extractor 依赖它）：
//   * 返回值单位是字节；
//   * **任何查询失败都返回 0**，绝不返回「很大」：0 可用空间会让预检拒绝该归档，
//     而返回一个大数字会让预检放行、随后在写入时才炸。安全方向只有一个 —— 宁可少解一个包。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.IO;

namespace Rerar.Core
{
    // 目标路径所在卷还剩多少字节可用。path 可以是文件或目录（可以是还不存在的目标目录）。
    public interface IDiskSpaceProvider
    {
        long FreeBytes(string path);
    }

    // 默认实现：走 DriveInfo。绝不需要提权、不弹窗。
    public sealed class DriveSpaceProvider : IDiskSpaceProvider
    {
        public long FreeBytes(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) { return 0; }

                string root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) { return 0; }

                DriveInfo drive = new DriveInfo(root);
                if (!drive.IsReady) { return 0; }

                // AvailableFreeSpace 而不是 TotalFreeSpace：前者把配额（磁盘配额/卷影副本预留）
                // 也算进去，与「我这个进程还能写多少」一致；后者在小配额卷上会乐观地多报。
                long available = drive.AvailableFreeSpace;
                return available > 0 ? available : 0;
            }
            catch (Exception)
            {
                // 卷不存在 / 未就绪 / 权限拒绝 / 路径非法：一律 0（保守方向，见文件头）。
                return 0;
            }
        }
    }
}
