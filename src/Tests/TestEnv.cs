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
using System.Threading;
using Rerar.Core;

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

    // ------------------------------------------------------------------
    // 归档 fixture（Task 3 起）。两个都用 SevenZipRunner 调本机 7-Zip 现做，
    // 放在 %TEMP%\rerar-tests\fixtures 下；H.Run 每用例清空整个临时根，所以这里
    // 每次访问都会重建（惰性 + File.Exists 检查），绝不做静态缓存。
    // ------------------------------------------------------------------

    // 损坏的 zip（7z 打不开，退出码 2）：Runner.CorruptArchiveExitsTwo 的输入。
    public static string CorruptZip
    {
        get { return Fixture("corrupt.zip", BuildCorruptZip); }
    }

    // AES-256 加密的 zip，密码 SECRET（Task 10 断言 SECRET 成功、WRONG 得到 SkippedNeedsPassword）。
    public static string AesZip
    {
        get { return Fixture("aes.zip", BuildAesZip); }
    }

    // 惰性 fixture 统一入口：先造到 <名字>.building，成功后再改名到位，
    // 这样半成品绝不会被后续用例当成可复用的 fixture。
    private static string Fixture(string fileName, Action<string> build)
    {
        string finalPath = FixturePath(fileName);
        if (File.Exists(finalPath)) { return finalPath; }

        string tempPath = finalPath + ".building";
        if (File.Exists(tempPath)) { File.Delete(tempPath); }

        build(tempPath);

        if (!File.Exists(tempPath)) { throw new InvalidOperationException("fixture 构造失败，未生成 " + tempPath); }
        if (File.Exists(finalPath)) { File.Delete(finalPath); }
        File.Move(tempPath, finalPath);
        return finalPath;
    }

    private static string FixturePath(string fileName)
    {
        string dir = Path.Combine(_root, "fixtures");
        if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
        return Path.Combine(dir, fileName);
    }

    // 先用 7-Zip 造一个真 zip，再把文件截断到只剩开头 40 字节（局部头没写完）。
    // 结果是一个「PK\x03\x04 开头但打不开」的文件：7z x 对它返回退出码 2。
    private static void BuildCorruptZip(string targetPath)
    {
        string seed = SeedFile("corrupt-seed.txt", "Rerar CorruptZip fixture\r\n");
        string full = targetPath + ".full.zip";
        CreateZipWithPassword(full, seed);

        byte[] bytes = File.ReadAllBytes(full);
        int keep = bytes.Length < 40 ? bytes.Length : 40;
        if (keep <= 0) { throw new InvalidOperationException("7-Zip 未生成 " + full); }

        byte[] head = new byte[keep];
        Array.Copy(bytes, head, keep);
        File.WriteAllBytes(targetPath, head);
    }

    private static void BuildAesZip(string targetPath)
    {
        string seed = SeedFile("aes-seed.txt", "Rerar AesZip fixture (password is SECRET)\r\n");
        CreateZipWithPassword(targetPath, seed);

        // 自检：拿 SECRET 真跑一次 t。fixture 若是「没加密」或「密码不对」，Task 10 会莫名其妙地失败，
        // 所以在这里就如实报错（惰性构造的异常会成为使用该 fixture 的用例的 FAIL 原因）。
        RunResult check = RunSevenZip(new string[] { "t", targetPath, "-pSECRET", "-y" });
        if (!SevenZipRunner.IsSuccess(check.ExitCode)) { FixtureFailed("校验 AesZip fixture", new string[] { "t", targetPath, "-pSECRET", "-y" }, check); }
    }

    // 注意：Runner 强制要求参数里带 -p（不变式 I5），而 7-Zip 的 a 命令在 -p 为空值时会
    // 真的弹密码提示（实测），所以构造 fixture 一律给真密码 SECRET + AES256。
    private static void CreateZipWithPassword(string archivePath, string seedPath)
    {
        string[] args = new string[] { "a", "-tzip", archivePath, seedPath, "-pSECRET", "-mem=AES256", "-y" };
        RunResult r = RunSevenZip(args);
        if (!SevenZipRunner.IsSuccess(r.ExitCode)) { FixtureFailed("构造 fixture", args, r); }
    }

    private static RunResult RunSevenZip(string[] args)
    {
        return SevenZipRunner.Run(SevenZip, args, null, CancellationToken.None);
    }

    private static void FixtureFailed(string what, string[] args, RunResult r)
    {
        throw new InvalidOperationException(
            what + "失败：7z " + string.Join(" ", args) + " 退出码 " + r.ExitCode +
            "；stdout=[" + Head(r.StdOut) + "]；stderr=[" + Head(r.StdErr) + "]");
    }

    private static string Head(string s)
    {
        if (string.IsNullOrEmpty(s)) { return ""; }
        string flat = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= 300 ? flat : flat.Substring(0, 300) + "…";
    }

    private static string SeedFile(string fileName, string content)
    {
        string dir = Path.Combine(_root, "fixture-seed");
        if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
        string path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
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
