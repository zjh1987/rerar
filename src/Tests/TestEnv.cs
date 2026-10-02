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
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

    // 只含两个文件的 zip（Task 4 的完整性基线用例 Index.ReadsEntryCountAndBytes）。
    // 成员数据是 7-Zip 用 -pSECRET 加密的（ZipCrypto，实测）：Runner 的 I5 强制参数表必须带 -p，
    // 而 7z 的 a 命令在 -p 为空值时会弹密码提示，所以构造夹具时躲不开 -p；
    // zip 的中央目录是明文，`l` 不需要密码 —— 下面自检里用空 -p 真列一次来钉住这点。
    public static string TwoFileZip
    {
        get { return Fixture("two-file.zip", BuildTwoFileZip); }
    }

    // 头部加密（-mhe=on）的 7z，密码 SECRET（Task 4 的 Index.EncryptedHeadersReported）。
    // 控制方裁定：7-Zip 造不出 rar（只有 WinRAR 能），用头部加密的 7z 复现同一个歧义 ——
    // 错密码 / 损坏 / 非归档在 `l` 的输出上无法区分，正是 HasEncryptedHeaders 要消解的那件事。
    public static string HeaderEncrypted7z
    {
        get { return Fixture("header-encrypted.7z", BuildHeaderEncrypted7z); }
    }

    // 含真实软链（reparse point）条目的 tar（Task 4 的 Index.DetectsReparsePointEntry）。
    // 必须用真软链：7-Zip 只有在 tar 里存了「符号链接」条目时才打印非空的 `Symbolic Link = …`。
    // 本机实测可用：Developer Mode 已开启，建软链无需管理员；归档用 Git 自带的 GNU tar。
    // 注意 GNU tar 是 MSYS 程序，它把 `C:\...` 当远程主机（`C:` 被解析成 host），
    // 所以下面一律用相对名字 + WorkingDirectory 建包，成功后再搬到 fixture 路径。
    public static string SymlinkTar
    {
        get { return Fixture("symlink.tar", BuildSymlinkTar); }
    }

    // 分卷（split）的 7z：`-v1k` + 5120 字节不可压缩内容 → 真的切成多个卷；返回 **.001** 这个
    // 权威成员（规格 §6.6 / 计划 Task 6 交给 7-Zip 的就是它）。Task 4 用它钉住 Finding 1：
    // 分卷清单里 7-Zip 会多打印两个段标记（`----` 与第二个 `--`），把「卷/包自己的属性块」当成
    // 条目，基线就会凭空多出 2 个文件、多算一个包的字节数。
    //
    // 不能走 Fixture()：分卷是**多个文件**，而 Fixture() 只搬迁单个 .building 文件。
    public static string SplitVolume7z
    {
        get
        {
            string dir = Path.Combine(_root, "fixtures", "split");
            string first = Path.Combine(dir, "vol.7z.001");
            string second = Path.Combine(dir, "vol.7z.002");

            if (File.Exists(first) && File.Exists(second)) { return first; }

            // 半成品/残留一律先清掉，避免上一轮失败留下的卷被当成可复用 fixture。
            if (Directory.Exists(dir)) { Directory.Delete(dir, true); }
            Directory.CreateDirectory(dir);

            string seed = SeedBinary("big.bin", 5120);
            string baseName = Path.Combine(dir, "vol.7z");
            string[] args = new string[] { "a", "-t7z", "-v1k", baseName, seed, "-pSECRET", "-y" };
            RunResult r = RunSevenZip(args);
            if (!SevenZipRunner.IsSuccess(r.ExitCode)) { FixtureFailed("构造 SplitVolume7z fixture", args, r); }

            // 自检 1：真的切开了（至少两个卷）。没切开就测不到分卷形状，必须当场报错，
            // 绝不能让用例在一个「其实是单卷」的 fixture 上悄悄变成弱断言。
            if (!File.Exists(first) || !File.Exists(second))
            {
                throw new InvalidOperationException(
                    "SplitVolume7z fixture 构造失败：没有切成多个卷（" + first + " 与 " + second + " 不都存在）");
            }

            // 自检 2：.001 必须列得出来，且带条目段标记（Task 4 就用 password: null 读它）。
            string[] listArgs = new string[] { "l", "-slt", first, "-p", "-y" };
            RunResult listed = RunSevenZip(listArgs);
            if (!SevenZipRunner.IsSuccess(listed.ExitCode) ||
                listed.StdOut == null || listed.StdOut.IndexOf("----------", StringComparison.Ordinal) < 0)
            {
                FixtureFailed("校验 SplitVolume7z fixture", listArgs, listed);
            }

            return first;
        }
    }

    // 单流格式（bzip2）样例：这种归档**不存成员名**，`l -slt` 的条目块连 Path 行都没有
    //（实测 `Size = ` 也是空的），唯一可用的基线是 `x`/`t` 尾部的汇总。Task 4 用它钉住
    // 「0 条目 ≠ 已完整」与「汇总里只有 Size: 是必然出现的」这两件事。
    public static string SingleStreamBz2
    {
        get { return Fixture("single-stream.bz2", BuildSingleStreamBz2); }
    }

    // ------------------------------------------------------------------
    // 容器文档 fixture（Task 5 起）：ArchiveGater.Judge 的输入。
    //
    // 每个都是一份**真 zip**（7-Zip 打出来的）经 SevenZipIndex.Read 得到的 ArchiveIndex ——
    // 内容身份（条目名）就是判定依据本身，所以夹具必须由真打包器产生，不能手搓索引。
    // H.Run 每用例清空整个临时根，故与其它 fixture 一样：惰性 + 不存在则重建。
    // ------------------------------------------------------------------

    // 规格 §6.4 最典型的 OOXML：根级 [Content_Types].xml + 根级 _rels/.rels + word/document.xml。
    public static ArchiveIndex IndexDocx
    {
        get { return GaterIndex("gater-docx.zip", BuildDocxZip); }
    }

    // 同样的 OOXML 身份，外加一个**真**嵌套 zip（word/embeddings/embedding1.zip）：
    // 钉住「包里有包」绝不能成为放行理由 —— 那是把 Word 文档交给递归拆散的最短路径。
    public static ArchiveIndex IndexDocxWithNestedZip
    {
        get { return GaterIndex("gater-docx-nested.zip", BuildDocxWithNestedZip); }
    }

    // 身份标记全大写（[CONTENT_TYPES].XML / _RELS/.RELS）：zip 条目名是大小写敏感的字节串，
    // 各家打包工具的大小写五花八门，而标记名的大小写不携带语义。
    public static ArchiveIndex IndexDocxUpperCase
    {
        get { return GaterIndex("gater-docx-upper.zip", BuildDocxUpperCaseZip); }
    }

    // APK：AndroidManifest.xml + classes.dex（规格 §6.4 第 2 行）。
    public static ArchiveIndex IndexApk
    {
        get { return GaterIndex("gater-apk.zip", BuildApkZip); }
    }

    // JAR：META-INF/MANIFEST.MF + 任一 .class（规格 §6.4 第 3 行）。
    public static ArchiveIndex IndexJar
    {
        get { return GaterIndex("gater-jar.zip", BuildJarZip); }
    }

    // 普通 zip（几个普通文件）：判定必须放行，否则递归功能整体失效。
    public static ArchiveIndex IndexPlain
    {
        get { return GaterIndex("gater-plain.zip", BuildPlainZip); }
    }

    // 普通 zip + 真嵌套 zip：本工具的主用例就是「包里的包」，
    //「含 .zip 条目」本身绝不能成为拒绝理由。
    public static ArchiveIndex IndexPlainWithNestedZip
    {
        get { return GaterIndex("gater-plain-nested.zip", BuildPlainWithNestedZip); }
    }

    // EPUB/ODF：根级 mimetype 标记条目（OCF 容器规范要求它是本包根级的成员）。
    public static ArchiveIndex IndexEpub
    {
        get { return GaterIndex("gater-epub.zip", BuildEpubZip); }
    }

    // 惰性「真 zip → ArchiveIndex」通用入口。
    //
    // 自检是必须的，不是装饰：夹具要是没被列出来（ListingFailed，或 0 个文件条目），Judge 会按
    //「清单不可用 ⇒ 拒绝递归」直接返回 ContainerDocument —— 于是**所有拒绝类用例都会在一个坏
    // 夹具上假通过**。这里当场把这种静默假通过变成一条明确的 FAIL。
    private static ArchiveIndex GaterIndex(string fileName, Action<string> build)
    {
        string path = Fixture(fileName, build);
        ArchiveIndex index = SevenZipIndex.Read(SevenZip, path, null);
        if (index.ListingFailed || index.FileCount == 0)
        {
            throw new InvalidOperationException(
                "容器文档夹具 " + fileName + " 的清单不可用：ListingFailed=" + index.ListingFailed +
                "，FileCount=" + index.FileCount + "，ExitCode=" + index.ExitCode +
                "（那种形状会让拒绝类用例假通过）");
        }
        return index;
    }

    private static void BuildDocxZip(string targetPath)
    {
        string src = SeedDir("gater-docx-src");
        SeedDocxMarkers(src, false);
        ZipSeedDir(targetPath, src);
    }

    private static void BuildDocxWithNestedZip(string targetPath)
    {
        string src = SeedDir("gater-docx-nested-src");
        SeedDocxMarkers(src, false);

        // 真嵌套 zip（不是改个名的空壳）：先真造一个 zip 放进 seed 目录，再连同外层一起打包。
        string innerSrc = SeedDir("gater-inner-docx-src");
        SeedText(innerSrc, "payload.txt", "Rerar gater fixture: nested payload\r\n");
        ZipSeedDir(Path.Combine(src, @"word\embeddings\embedding1.zip"), innerSrc);

        ZipSeedDir(targetPath, src);
    }

    private static void BuildDocxUpperCaseZip(string targetPath)
    {
        string src = SeedDir("gater-docx-upper-src");
        SeedDocxMarkers(src, true);
        ZipSeedDir(targetPath, src);
    }

    private static void BuildApkZip(string targetPath)
    {
        string src = SeedDir("gater-apk-src");
        SeedText(src, "AndroidManifest.xml", "<?xml version=\"1.0\"?><manifest/>");
        SeedText(src, "classes.dex", "dex 035 fixture payload");
        SeedText(src, "META-INF/CERT.RSA", "not-a-real-certificate");
        SeedText(src, "res/layout/main.xml", "<?xml version=\"1.0\"?><layout/>");
        ZipSeedDir(targetPath, src);
    }

    private static void BuildJarZip(string targetPath)
    {
        string src = SeedDir("gater-jar-src");
        SeedText(src, "META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\n\r\n");
        SeedText(src, "com/example/App.class", "fixture class payload");
        SeedText(src, "com/example/App.java", "class App {}");
        ZipSeedDir(targetPath, src);
    }

    private static void BuildPlainZip(string targetPath)
    {
        string src = SeedDir("gater-plain-src");
        SeedText(src, "a.txt", "alpha");
        SeedText(src, "b.txt", "bravo");
        SeedText(src, "docs/readme.md", "# readme");
        ZipSeedDir(targetPath, src);
    }

    private static void BuildPlainWithNestedZip(string targetPath)
    {
        // 与 IndexPlain 同样的三个普通文件，只多一个真嵌套 zip：于是用例可以断言
        //「同一份输入 + 一个嵌套包」依然放行，差别确实只在那一个成员上。
        string src = SeedDir("gater-plain-nested-src");
        SeedText(src, "a.txt", "alpha");
        SeedText(src, "b.txt", "bravo");
        SeedText(src, "docs/readme.md", "# readme");

        string innerSrc = SeedDir("gater-inner-plain-src");
        SeedText(innerSrc, "inner.txt", "inner");
        ZipSeedDir(Path.Combine(src, "inner.zip"), innerSrc);

        ZipSeedDir(targetPath, src);
    }

    private static void BuildEpubZip(string targetPath)
    {
        string src = SeedDir("gater-epub-src");
        SeedText(src, "mimetype", "application/epub+zip");
        SeedText(src, "META-INF/container.xml", "<?xml version=\"1.0\"?><container/>");
        SeedText(src, "OEBPS/content.opf", "<?xml version=\"1.0\"?><package/>");
        ZipSeedDir(targetPath, src);
    }

    // OOXML 的身份标记。upper = true 时全部大写（大小写不敏感用例）。
    private static void SeedDocxMarkers(string srcDir, bool upper)
    {
        string contentTypes = "[Content_Types].xml";
        string rels = "_rels/.rels";
        string document = "word/document.xml";
        if (upper)
        {
            contentTypes = "[CONTENT_TYPES].XML";
            rels = "_RELS/.RELS";
            document = "WORD/DOCUMENT.XML";
        }

        SeedText(srcDir, contentTypes, "<?xml version=\"1.0\"?><Types/>");
        SeedText(srcDir, rels, "<?xml version=\"1.0\"?><Relationships/>");
        SeedText(srcDir, document, "<?xml version=\"1.0\"?><document/>");
    }

    // 造夹具用的 seed 目录：每次构造前清空重建，半成品不会被下一轮当成有效输入。
    private static string SeedDir(string name)
    {
        string dir = Path.Combine(_root, "fixture-seed", name);
        if (Directory.Exists(dir)) { Directory.Delete(dir, true); }
        Directory.CreateDirectory(dir);
        return dir;
    }

    // 在 seed 目录里写一个成员（自动补中间目录）。
    private static void SeedText(string srcDir, string relativePath, string content)
    {
        string path = Path.Combine(srcDir, relativePath);
        string parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) { Directory.CreateDirectory(parent); }
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    // 把一个 seed 目录打成 zip：条目名 = 相对 seed 目录的路径（正是身份判定要的形状）。
    //
    // 为什么用 `"<seed>\*"` 而不是把每个文件当参数（本机 7-Zip 26.01 实测）：
    //   * 逐个传绝对路径会丢掉目录部分 —— `_rels\.rels` 被存成 `.rels`、`word\document.xml`
    //     被存成 `document.xml`，OOXML/JAR 的路径特征就没了；
    //   * `[Content_Types].xml` 直接当参数会被 7z 当成通配符（方括号是它的通配语法），
    //     报「系统找不到指定的文件」。
    // 通配符让 7z 自己枚举目录，条目名恰好是相对路径，方括号原样保留。
    //
    // -pSECRET 是被不变式 I5 逼出来的：Runner 硬拒不带 -p 的参数表，而 7-Zip 的 a 命令拿到
    // 空 -p 会弹密码提示（实测）。zip 的成员名在中央目录里是明文（TwoFileZip 已自检过这一点），
    // 所以加密不影响 SevenZipIndex.Read 用空 -p 读清单。
    private static void ZipSeedDir(string targetPath, string seedDir)
    {
        string parent = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) { Directory.CreateDirectory(parent); }
        if (File.Exists(targetPath)) { File.Delete(targetPath); }

        string[] args = new string[] { "a", "-tzip", targetPath, seedDir + @"\*", "-pSECRET", "-y" };
        RunResult r = RunSevenZip(args);
        if (!SevenZipRunner.IsSuccess(r.ExitCode)) { FixtureFailed("构造 zip 夹具", args, r); }

        if (!File.Exists(targetPath)) { throw new InvalidOperationException("zip 夹具构造失败，未生成 " + targetPath); }
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

    // 两个成员的 zip：内容故意取 3 字节与 5 字节，Task 4 因此可以断言总字节「精确等于 8」。
    private static void BuildTwoFileZip(string targetPath)
    {
        string first = SeedFile("two-file-a.txt", "abc");
        string second = SeedFile("two-file-b.txt", "12345");
        string[] args = new string[] { "a", "-tzip", targetPath, first, second, "-pSECRET", "-y" };
        RunResult r = RunSevenZip(args);
        if (!SevenZipRunner.IsSuccess(r.ExitCode)) { FixtureFailed("构造 TwoFileZip fixture", args, r); }

        // 自检：不带密码（空 -p）也必须列得出条目来 —— Task 4 就是用 password: null 读这个包的。
        string[] listArgs = new string[] { "l", "-slt", targetPath, "-p", "-y" };
        RunResult listed = RunSevenZip(listArgs);
        if (!SevenZipRunner.IsSuccess(listed.ExitCode) ||
            listed.StdOut == null || listed.StdOut.IndexOf("----------", StringComparison.Ordinal) < 0)
        {
            FixtureFailed("校验 TwoFileZip fixture（不带密码也必须列得出来）", listArgs, listed);
        }
    }

    private static void BuildHeaderEncrypted7z(string targetPath)
    {
        string seed = SeedFile("header-encrypted-seed.txt", "Rerar HeaderEncrypted7z fixture (password is SECRET)\r\n");
        string[] args = new string[] { "a", "-t7z", "-mhe=on", targetPath, seed, "-pSECRET", "-y" };
        RunResult r = RunSevenZip(args);
        if (!SevenZipRunner.IsSuccess(r.ExitCode)) { FixtureFailed("构造 HeaderEncrypted7z fixture", args, r); }

        // 自检 1：错密码必须打不开（否则它就不是「头部加密」，Task 4 的用例会失去意义）。
        string[] wrongArgs = new string[] { "l", "-slt", targetPath, "-pWRONG", "-y" };
        RunResult wrong = RunSevenZip(wrongArgs);
        if (SevenZipRunner.IsSuccess(wrong.ExitCode)) { FixtureFailed("校验 HeaderEncrypted7z fixture（错密码竟然列出来了）", wrongArgs, wrong); }

        // 自检 2：正确密码必须列得出来。
        string[] rightArgs = new string[] { "l", "-slt", targetPath, "-pSECRET", "-y" };
        RunResult right = RunSevenZip(rightArgs);
        if (!SevenZipRunner.IsSuccess(right.ExitCode)) { FixtureFailed("校验 HeaderEncrypted7z fixture（正确密码打不开）", rightArgs, right); }
    }

    private static void BuildSymlinkTar(string targetPath)
    {
        string tarExe = LocateTar();
        string srcDir = Path.Combine(_root, "fixtures", "symlink-src");
        if (Directory.Exists(srcDir)) { Directory.Delete(srcDir, true); }
        Directory.CreateDirectory(srcDir);

        string target = Path.Combine(srcDir, "target.txt");
        string link = Path.Combine(srcDir, "link.txt");
        File.WriteAllText(target, "Rerar SymlinkTar fixture\r\n", new UTF8Encoding(false));
        CreateFileSymlink(link, "target.txt");

        // 自检：软链真的建出来了。Developer Mode 关掉时 CreateSymbolicLink 会失败，
        // 那时这里抛出的异常就是使用该 fixture 的用例的 FAIL 原因 —— 绝不悄悄退化成普通文件。
        FileAttributes attributes = File.GetAttributes(link);
        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            throw new InvalidOperationException("SymlinkTar fixture 构造失败：" + link + " 不是 reparse point（软链）");
        }

        // 相对名字 + WorkingDirectory：MSYS 版 GNU tar 不接受 `C:\` 形式的绝对路径。
        string built = Path.Combine(srcDir, "symlink.tar");
        RunTar(tarExe, srcDir, new string[] { "-cf", "symlink.tar", "link.txt", "target.txt" });
        if (!File.Exists(built)) { throw new InvalidOperationException("SymlinkTar fixture 构造失败：GNU tar 未生成 " + built); }

        if (File.Exists(targetPath)) { File.Delete(targetPath); }
        File.Move(built, targetPath);

        // 自检：7-Zip 必须把它报成符号链接条目。tar 若把软链当普通文件存了（例如 -h），
        // fixture 就名不副实，当场报错而不是让 Task 4 的用例以「找不到 reparse point」失败。
        string[] listArgs = new string[] { "l", "-slt", targetPath, "-p", "-y" };
        RunResult listed = RunSevenZip(listArgs);
        if (!SevenZipRunner.IsSuccess(listed.ExitCode)) { FixtureFailed("校验 SymlinkTar fixture", listArgs, listed); }
        if (!HasNonEmptyValue(listed.StdOut, "Symbolic Link ="))
        {
            throw new InvalidOperationException(
                "SymlinkTar fixture 构造失败：l -slt 里没有非空的 Symbolic Link 行；stdout=[" + Head(listed.StdOut) + "]");
        }
    }

    // 单流格式（bzip2）：-tbzip2 根本不支持加密，但 I5 逼着 a 命令也必须带 -p，
    // 实测 `-pSECRET` 被 bzip2 忽略（包照常生成、`l -slt -p` 也照常成功），所以这里给真密码。
    private static void BuildSingleStreamBz2(string targetPath)
    {
        string seed = SeedFile("single-stream-seed.txt", "xyz");
        string[] args = new string[] { "a", "-tbzip2", targetPath, seed, "-pSECRET", "-y" };
        RunResult r = RunSevenZip(args);
        if (!SevenZipRunner.IsSuccess(r.ExitCode)) { FixtureFailed("构造 SingleStreamBz2 fixture", args, r); }

        // 自检：它必须真的是「条目段里没有 Path 行」的形状。注意属性段里有 `Path = <包自己>`，
        // 所以只看条目段标记之后的部分 —— 否则这条自检会把正常输出误判成失败。
        string[] listArgs = new string[] { "l", "-slt", targetPath, "-p", "-y" };
        RunResult listed = RunSevenZip(listArgs);
        if (!SevenZipRunner.IsSuccess(listed.ExitCode)) { FixtureFailed("校验 SingleStreamBz2 fixture", listArgs, listed); }

        int marker = listed.StdOut == null ? -1 : listed.StdOut.IndexOf("----------", StringComparison.Ordinal);
        if (marker < 0 || listed.StdOut.IndexOf("Path =", marker, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                "SingleStreamBz2 fixture 构造失败：条目段里出现了 Path 行，它不再是「无成员名」的形状；stdout=[" +
                Head(listed.StdOut) + "]");
        }
    }

    // tar 只是**测试夹具的构造工具**，不是产品路径：SevenZipRunner 是「7-Zip 的唯一进程入口」，
    // 它钉死了 7-Zip 的契约（强制 -p、注入 -sccUTF-8、7-Zip 的退出码语义），无法用来启动 GNU tar
    //（tar 既不认 -scc* 开关，也无法接受 -p）。所以这里直接起进程，但把边界收死：
    // 固定超时、超时即杀、异步读干两个管道、不碰 stdin、失败时把退出码与输出带进异常。
    private static void RunTar(string tarExe, string workingDir, string[] args)
    {
        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = tarExe;
        psi.WorkingDirectory = workingDir;
        psi.Arguments = string.Join(" ", args);      // 本类自己给的参数都不含空白，无需引号转义
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        StringBuilder stdOut = new StringBuilder();
        StringBuilder stdErr = new StringBuilder();
        Process process = new Process();
        process.StartInfo = psi;
        // 读事件的挂接放在 Start 之前：BeginOutputReadLine 一开就要能收到数据（进程一起就跑）。
        process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) { stdOut.Append(e.Data).Append('\n'); } };
        process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) { stdErr.Append(e.Data).Append('\n'); } };
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(30000))
            {
                try { process.Kill(); } catch (Exception) { }
                process.WaitForExit();
                throw new InvalidOperationException("SymlinkTar fixture 构造失败：GNU tar 30 秒未返回（已杀掉）");
            }
            process.WaitForExit();     // 无参重载：等异步读事件处理完，stdout/stderr 才是完整的

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "SymlinkTar fixture 构造失败：tar 退出码 " + process.ExitCode +
                    "；stdout=[" + Head(stdOut.ToString()) + "]；stderr=[" + Head(stdErr.ToString()) + "]");
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    // `-slt` 输出里有没有「<键> = <非空值>」的行。Symbolic Link / Hard Link 这些键对普通文件是
    // 「键 = 空」，只有真软链才有值，所以必须看值而不是只看键。
    private static bool HasNonEmptyValue(string text, string keyPrefix)
    {
        if (text == null) { return false; }
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith(keyPrefix, StringComparison.Ordinal)) { continue; }
            if (line.Substring(keyPrefix.Length).Trim().Length > 0) { return true; }
        }
        return false;
    }

    // .NET Framework 的 BCL 没有 File.CreateSymbolicLink（那是 .NET 6+ 才有的），所以直接调 Win32。
    // 先带 ALLOW_UNPRIVILEGED_CREATE（Developer Mode）；老系统不认这个标志（ERROR_INVALID_PARAMETER）
    // 时退回 0（进程持有 SeCreateSymbolicLinkPrivilege 时同样能建）。绝不提权。
    private static void CreateFileSymlink(string linkPath, string targetPath)
    {
        if (CreateSymbolicLink(linkPath, targetPath, SymlinkFlagAllowUnprivilegedCreate)) { return; }

        int firstError = Marshal.GetLastWin32Error();
        if (CreateSymbolicLink(linkPath, targetPath, 0)) { return; }

        throw new InvalidOperationException(
            "SymlinkTar fixture 构造失败：创建软链 " + linkPath + " -> " + targetPath +
            " 失败（Win32 错误 " + firstError + " / " + Marshal.GetLastWin32Error() +
            "）。需要 Developer Mode 或 SeCreateSymbolicLinkPrivilege。");
    }

    private const uint SymlinkFlagAllowUnprivilegedCreate = 0x2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, uint dwFlags);

    // Git 自带 GNU tar 的常见位置（本机实测 C:\Program Files\Git\usr\bin\tar.exe），
    // 最后再查 PATH 兜底；能不能用由 BuildSymlinkTar 的自检决定，不靠猜。
    private static string LocateTar()
    {
        List<string> candidates = new List<string>();

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (programFiles.Length > 0) { candidates.Add(Path.Combine(programFiles, @"Git\usr\bin\tar.exe")); }

        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (programFilesX86.Length > 0) { candidates.Add(Path.Combine(programFilesX86, @"Git\usr\bin\tar.exe")); }

        string programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
        if (!string.IsNullOrEmpty(programW6432)) { candidates.Add(Path.Combine(programW6432, @"Git\usr\bin\tar.exe")); }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (localAppData.Length > 0) { candidates.Add(Path.Combine(localAppData, @"Programs\Git\usr\bin\tar.exe")); }

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
                string candidate = Path.Combine(dir, "tar.exe");
                if (File.Exists(candidate)) { return candidate; }
                tried.Add(candidate);
            }
        }

        throw new InvalidOperationException(
            "未找到 GNU tar（SymlinkTar fixture 需要它把真软链存进 tar）：" +
            "请安装 Git for Windows（https://git-scm.com/）或把 tar.exe 所在目录加入 PATH。已查找：" +
            string.Join("；", tried.ToArray()));
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

    // 固定种子的伪随机二进制内容：7-Zip 压不动它，5120 字节才会真的被 `-v1k` 切成多个卷
    //（内容取重复模式的话包会小到只剩一个卷，分卷形状就测不到了）。固定种子保证每台机器
    // 每次构造出的包形状一致；用例只断言条目的条目数与字节数，不依赖包的大小。
    private static string SeedBinary(string fileName, int length)
    {
        string dir = Path.Combine(_root, "fixture-seed");
        if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
        string path = Path.Combine(dir, fileName);
        byte[] bytes = new byte[length];
        new Random(20261003).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
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
