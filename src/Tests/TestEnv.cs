// Rerar 共享测试环境（TestEnv）：整个项目的 fixture 注册表。
//
// 约定：
//   * fixture 由「第一个需要它的任务」加入本类，后续任务复用，绝不重复定义；
//   * H.Run 在每个用例开始前调用 Cleanup()，所以 Tmp/OutRoot 对每个用例都是全新的空目录；
//     需要跨用例使用的 fixture 必须写成「访问时若不存在则重建」的惰性形式；
//   * 临时根目录固定为 %TEMP%\rerar-tests（崩溃残留会被下一轮的 Cleanup 清掉）。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

internal static class TestEnv
{
    private static readonly string _root = Path.Combine(Path.GetTempPath(), "rerar-tests");
    private static string _sevenZip;

    // 每用例独立的临时目录（H.Run 在每个用例开始前清空重建）。
    public static string Tmp
    {
        get
        {
            EnsureDirs();
            return Path.Combine(_root, "tmp");
        }
    }

    // 解压输出根目录（Extractor 等后续任务的测试用），与 Tmp 一样每用例重置。
    public static string OutRoot
    {
        get
        {
            EnsureDirs();
            return Path.Combine(_root, "out");
        }
    }

    // 本机 7-Zip 的绝对路径。缺失时抛异常并给出明确提示：
    // H.Main 在开跑前就访问它，于是整轮测试以一条 FAIL 结束，而不是逐个用例莫名其妙地失败。
    public static string SevenZip
    {
        get
        {
            if (_sevenZip == null) { _sevenZip = LocateSevenZip(); }
            return _sevenZip;
        }
    }

    // 转义串 → 字节（每字符 1 字节），供 Sniffer 之类的二进制前缀测试使用。
    public static byte[] B(string s)
    {
        if (s == null) { return new byte[0]; }

        byte[] bytes = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) { bytes[i] = (byte)s[i]; }
        return bytes;
    }

    // 把若干名字打包成数组：给以 IEnumerable<string> 为参数的 API 提供简洁的数组字面量。
    public static string[] F(params string[] names)
    {
        if (names == null) { return new string[0]; }
        return names;
    }

    // 在 Tmp 下创建文件（自动补中间目录），返回绝对路径。
    public static string MakeFile(string name, string content)
    {
        string path = TmpFile(name);
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
        File.WriteAllText(path, content == null ? string.Empty : content, new UTF8Encoding(false));
        return path;
    }

    // Tmp 下某个名字的绝对路径（只算路径，不创建文件）。
    public static string TmpFile(string name)
    {
        if (name == null) { throw new ArgumentNullException("name"); }
        return Path.Combine(Tmp, name);
    }

    // 删除并重建临时目录（Tmp + OutRoot）。由 H.Run 每用例调用，H.Main 收尾时再调一次。
    // 尽力而为、永不抛异常：清不掉的残留文件不该让整轮测试失败。
    public static void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root)) { Directory.Delete(_root, true); }
        }
        catch (Exception)
        {
        }
        EnsureDirs();
    }

    private static void EnsureDirs()
    {
        string tmp = Path.Combine(_root, "tmp");
        string outRoot = Path.Combine(_root, "out");
        if (!Directory.Exists(tmp)) { Directory.CreateDirectory(tmp); }
        if (!Directory.Exists(outRoot)) { Directory.CreateDirectory(outRoot); }
    }

    private static string LocateSevenZip()
    {
        List<string> candidates = new List<string>();

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (programFiles.Length > 0) { candidates.Add(Path.Combine(programFiles, @"7-Zip\7z.exe")); }

        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (programFilesX86.Length > 0) { candidates.Add(Path.Combine(programFilesX86, @"7-Zip\7z.exe")); }

        // 32 位进程看不到 64 位的 Program Files，补一个环境变量候补。
        string programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
        if (!string.IsNullOrEmpty(programW6432)) { candidates.Add(Path.Combine(programW6432, @"7-Zip\7z.exe")); }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (localAppData.Length > 0) { candidates.Add(Path.Combine(localAppData, @"Programs\7-Zip\7z.exe")); }

        List<string> tried = new List<string>();
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate)) { return candidate; }
            tried.Add(candidate);
        }

        string pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (string rawDir in pathEnv.Split(';'))
            {
                string dir = rawDir.Trim();
                if (dir.Length == 0) { continue; }
                string candidate = Path.Combine(dir, "7z.exe");
                if (File.Exists(candidate)) { return candidate; }
                tried.Add(candidate);
            }
        }

        throw new InvalidOperationException(
            "未找到本机 7-Zip（7z.exe）：测试需要真实的 7-Zip 来构造与校验归档。" +
            "请安装 7-Zip（https://www.7-zip.org/）或把 7z.exe 所在目录加入 PATH。已查找：" +
            string.Join("；", tried.ToArray()));
    }
}
