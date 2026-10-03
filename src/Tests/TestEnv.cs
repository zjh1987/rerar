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
using System.IO.Compression;
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

    // ------------------------------------------------------------------
    // 报告 fixture（Task 8 起）：Reporter 的样本结果。
    //
    // 刻意**不做静态缓存**：每次调用都返回一个全新的 ArchiveResult，里面的路径由 Tmp/OutRoot
    // 在调用时现算 —— H.Run 在每个用例开始前都会 Cleanup() 清空整个临时根，任何缓存下来的路径
    // 到下一个用例就指向不存在的目录了（惰性重建是 TestEnv 的通用约定）。
    // 归档名刻意用中文（用例里的「中文名.zip」）：Reporter 的 BOM/乱码回归靠它钉住
    // 「中文名逐字节原样写进报告」。
    // ------------------------------------------------------------------
    public static ArchiveResult SampleResult(string archiveName)
    {
        if (archiveName == null) { throw new ArgumentNullException("archiveName"); }

        ArchiveResult result = new ArchiveResult();
        result.Path = Path.Combine(Tmp, archiveName);          // 源归档路径
        result.Status = ArchiveStatus.CompletedWithFailures;   // 「归档已解出，但内部有成员失败」
        result.Layers = 2;
        result.Files = 5;
        result.Failed = 1;
        result.OutputDir = Path.Combine(OutRoot, archiveName);  // 解压去向
        result.Message = "第 2 层有 1 个成员失败";
        return result;
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

        // 进程外的残留也一并收拾：上一次运行若在 subst 夹具用例中途被杀，映射会留在机器上
        //（Cleanup 删得掉目录，删不掉映射）。放在这里而不是只放在夹具里，是因为它属于
        // 「每个用例开始前让机器回到干净状态」这件事：
        //   * 僵留映射（目标目录已被删）实测 GetDriveType=1，不干扰任何用例，但它是**机器上的残留**，
        //     必须由测试自己收掉；
        //   * 若目标目录还在（映射是"活"的）实测 GetDriveType=3（DRIVE_FIXED），会让
        //     `Guard.RefusesOnRemovableOrRemote` 的字面量 Z:\ 判成 Quarantine 而误 FAIL ——
        //     而那条用例比 subst 夹具更早执行，所以自愈必须发生在这里。
        ReleaseStaleSubstMappings(Path.Combine(_root, "substroot"));
    }

    // ------------------------------------------------------------------
    // 超配额夹具（Task 9 起）：一个「逻辑大小超过本卷回收站配额」的文件。
    //
    // 为什么不是真写一个大文件：本机临时目录所在卷（C:）的回收站配额是 3245 MB，真要造一个
    // 超过它的普通文件就得实占 3 GB 以上磁盘。改用 NTFS **稀疏文件**：FSCTL_SET_SPARSE 之后
    // SetEndOfFile 到 64 GiB —— FileInfo.Length（也就是 RecycleBinGuard.Plan 判定用的量）报的
    // 是逻辑大小 64 GiB，而磁盘占用接近 0。对「体积 vs 配额」这个判定而言与真文件同形。
    //
    // brief 给的两种造法是「临时把 MaxCapacity 改小」或「如实跳过」。这里都不采用：改配额要动
    // 用户注册表（留着没恢复就是真的坑），而稀疏文件一个字节的注册表都不碰 —— 偏离理由见
    // task-9-report.md。造不出来（非 NTFS / 稀疏不支持）时抛 InvalidOperationException，
    // 由用例 H.Skip(name, reason) 记一条 SKIPPED（带原因、不计入 PASS），绝不假 PASS。
    //
    // H.Run 每用例前都 Cleanup()，故这里绝不静态缓存，访问时按需重建（与 TestEnv 其余 fixture 一致）。
    // ------------------------------------------------------------------
    private const long OversizedFileLength = 64L * 1024 * 1024 * 1024;   // 64 GiB，远超任何常见回收站配额
    private const uint FsctlSetSparse = 0x000900C4;

    public static string OversizedFile
    {
        get
        {
            string path = Path.Combine(Tmp, "oversized_over_quota.bin");
            if (File.Exists(path)) { return path; }

            string root = Path.GetPathRoot(path);
            try
            {
                DriveInfo drive = new DriveInfo(root);
                if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "临时目录所在卷 " + root + " 的文件系统是 " + drive.DriveFormat + "，不支持稀疏文件");
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "无法确认临时目录所在卷 " + root + " 的文件系统（" + ex.GetType().Name + "）：" + ex.Message, ex);
            }

            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    uint returned;
                    bool ok = DeviceIoControl(
                        fs.SafeFileHandle.DangerousGetHandle(),
                        FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero);
                    if (!ok)
                    {
                        throw new InvalidOperationException(
                            "FSCTL_SET_SPARSE 失败（Win32 错误 " + Marshal.GetLastWin32Error() + "）");
                    }
                    fs.SetLength(OversizedFileLength);
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("在 " + root + " 上造稀疏文件失败：" + ex.Message, ex);
            }

            long actual = new FileInfo(path).Length;
            if (actual != OversizedFileLength)
            {
                throw new InvalidOperationException("稀疏文件逻辑大小是 " + actual + "，期望 " + OversizedFileLength);
            }
            return path;
        }
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

    // ---- 以下 8 个夹具属于「规格 §6.4 表被裁定为欠包含」之后补的规则（不变式 I4 是权威）----
    // 全部都是**一个标记就够**的形状：表里原来只写了常见形状（`+`）或干脆没写（其余 ⇒ 放行），
    // 于是这些真容器会一路走到 Allow —— 开了「解压成功后删除原包」就是用户文档被销毁且全程 exit 0。

    // Apple iWork（Pages/Numbers/Keynote）：主条目 Index/Document.iwa，且**不带**任何别的标记
    //（没有 [Content_Types].xml、没有 mimetype），所以它只能靠 iWork 这条规则被认出来。
    public static ArchiveIndex IndexIWork
    {
        get { return GaterIndex("gater-iwork.zip", BuildIWorkZip); }
    }

    // 同一族容器的另一个标记 Metadata/DocumentIdentifier（Keynote/Numbers 包里也有它）。
    // 单列一个夹具，好让两条规则各自被真包钉住，而不是靠一个包「顺便」带过。
    public static ArchiveIndex IndexIWorkIdentifier
    {
        get { return GaterIndex("gater-iwork-identifier.zip", BuildIWorkIdentifierZip); }
    }

    // Ant `<zip>` / 裸 `zip` 打出来的 jar：只有 .class，**没有** META-INF/MANIFEST.MF。
    public static ArchiveIndex IndexJarClassesOnly
    {
        get { return GaterIndex("gater-jar-classes-only.zip", BuildJarClassesOnlyZip); }
    }

    // 只有 META-INF/MANIFEST.MF、一个 .class 都没有的资源 jar（classpath jar / OSGi 等）。
    public static ArchiveIndex IndexJarManifestOnly
    {
        get { return GaterIndex("gater-jar-manifest-only.zip", BuildJarManifestOnlyZip); }
    }

    // 只有 AndroidManifest.xml、没有 classes.dex 的 APK 形状（无 dex 的 split APK / 资源包）。
    public static ArchiveIndex IndexApkManifestOnly
    {
        get { return GaterIndex("gater-apk-manifest-only.zip", BuildApkManifestOnlyZip); }
    }

    // Python wheel：`<name>-<ver>.dist-info` 是 wheel 规范要求的路径段。
    // 注意这一份是 `7z a <seed>\*` 打出来的：7-Zip 会给目录树补写目录条目（`Folder = +`），
    // 所以它只代表「带目录条目的 zip 形状」，**不代表真实 wheel**（见下面 IndexWheelNoDirEntries）。
    public static ArchiveIndex IndexWheel
    {
        get { return GaterIndex("gater-wheel.zip", BuildWheelZip); }
    }

    // Python wheel 的**真实**布局（pip / setuptools 用 python zipfile 打出来的包）：
    // 包里**一个目录条目都没有**，`.dist-info` 只作为**文件路径**里的一个路径段出现
    //（`mypkg-1.0.dist-info/METADATA`、`…/RECORD`、`…/WHEEL`）。7-Zip 打目录树时会补写目录条目，
    // 造不出这个形状，所以这份夹具逐条写文件、不写父目录条目（见 BuildWheelNoDirEntriesZip）。
    // 上一版规则只看目录条目，对这种形状永不触发 ⇒ 落到「其余 ⇒ 放行」；这份夹具就是那个缺口的回归。
    public static ArchiveIndex IndexWheelNoDirEntries
    {
        get { return GaterIndex("gater-wheel-flat.zip", BuildWheelNoDirEntriesZip); }
    }

    // VSIX（VS 扩展包）：根级 extension.vsixmanifest。
    public static ArchiveIndex IndexVsix
    {
        get { return GaterIndex("gater-vsix.zip", BuildVsixZip); }
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

    // Apple iWork 包：主条目 Index/Document.iwa（外加一个同样以 .iwa 结尾但**不叫** Document.iwa
    // 的成员，确保匹配的是完整路径段而不是「后缀是 .iwa」这种过宽的判据）。
    private static void BuildIWorkZip(string targetPath)
    {
        string src = SeedDir("gater-iwork-src");
        SeedText(src, "Index/Document.iwa", "iwa fixture payload");
        SeedText(src, "Index/Document.iwa.bak", "not the primary entry");
        SeedText(src, "Metadata/Properties.plist", "<?xml version=\"1.0\"?><plist/>");
        ZipSeedDir(targetPath, src);
    }

    private static void BuildIWorkIdentifierZip(string targetPath)
    {
        string src = SeedDir("gater-iwork-identifier-src");
        SeedText(src, "Metadata/DocumentIdentifier", "iwork document identifier");
        SeedText(src, "Index/Document.iwa.bak", "not the primary entry");
        ZipSeedDir(targetPath, src);
    }

    private static void BuildJarClassesOnlyZip(string targetPath)
    {
        string src = SeedDir("gater-jar-classes-only-src");
        SeedText(src, "com/example/App.class", "fixture class payload");
        SeedText(src, "com/example/Helper.class", "fixture class payload");
        ZipSeedDir(targetPath, src);
    }

    private static void BuildJarManifestOnlyZip(string targetPath)
    {
        string src = SeedDir("gater-jar-manifest-only-src");
        SeedText(src, "META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\n\r\n");
        ZipSeedDir(targetPath, src);
    }

    private static void BuildApkManifestOnlyZip(string targetPath)
    {
        string src = SeedDir("gater-apk-manifest-only-src");
        SeedText(src, "AndroidManifest.xml", "<?xml version=\"1.0\"?><manifest/>");
        ZipSeedDir(targetPath, src);
    }

    private static void BuildWheelZip(string targetPath)
    {
        string src = SeedDir("gater-wheel-src");
        SeedText(src, "mypkg/__init__.py", "# fixture package\r\n");
        SeedText(src, "mypkg-1.0.dist-info/METADATA", "Name: mypkg\r\nVersion: 1.0\r\n");
        SeedText(src, "mypkg-1.0.dist-info/RECORD", "mypkg/__init__.py,,\r\n");
        ZipSeedDir(targetPath, src);
    }

    // 真实 wheel 形状：用 zip 写库（.NET 的 System.IO.Compression）逐条添加**文件**，不添加任何父目录
    // 条目 —— 这正是 pip / setuptools 用 python zipfile 打 wheel 时的行为（实测真实 wheel 里
    // `Folder = +` 行数为 0）。绝不能用 `7z a <seed>\*` 造这份夹具：7-Zip 会替目录树补写目录条目，
    // 那样造出来的就不是 wheel 的形状 —— 前一轮的覆盖用例正是因此变成假通过。
    private static void BuildWheelNoDirEntriesZip(string targetPath)
    {
        string[] names = new string[] {
            "mypkg/__init__.py",
            "mypkg-1.0.dist-info/METADATA",
            "mypkg-1.0.dist-info/RECORD",
            "mypkg-1.0.dist-info/WHEEL" };
        string[] contents = new string[] {
            "# fixture package\r\n",
            "Name: mypkg\r\nVersion: 1.0\r\n",
            "mypkg/__init__.py,,\r\n",
            "Wheel-Version: 1.0\r\n" };

        if (File.Exists(targetPath)) { File.Delete(targetPath); }
        using (FileStream stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
        using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            for (int i = 0; i < names.Length; i++) { AddZipFile(zip, names[i], contents[i]); }
        }

        // 自检 1：夹具必须真的**没有目录条目**。这正是本轮回归要钉住的形状；一旦写库版本替我们补上
        // 父目录条目，这份夹具就退化成 IndexWheel（旧规则也能拒它），回归用例又变假通过 —— 所以这里
        // 拿 7-Zip 自己的清单当场核一遍，不合格就报错，绝不让用例悄悄通过。
        string[] listArgs = new string[] { "l", "-slt", targetPath, "-p", "-y" };
        RunResult listed = RunSevenZip(listArgs);
        if (!SevenZipRunner.IsSuccess(listed.ExitCode)) { FixtureFailed("校验 wheel（无目录条目）fixture", listArgs, listed); }

        int dirEntries = CountOccurrences(listed.StdOut, "Folder = +");
        if (dirEntries != 0)
        {
            throw new InvalidOperationException(
                "wheel（无目录条目）fixture 构造失败：清单里出现了 " + dirEntries +
                " 个目录条目（Folder = +），它已经不是真实 wheel 的形状；stdout=[" + Head(listed.StdOut) + "]");
        }

        // 自检 2：.dist-info 段只出现在**文件路径**里 —— 没有任何一条 `Path = ` 行的末段就是它本身。
        if (HasPathEndingWithSegment(listed.StdOut, "mypkg-1.0.dist-info"))
        {
            throw new InvalidOperationException(
                "wheel（无目录条目）fixture 构造失败：清单里出现了以 .dist-info 段结尾的 Path 行；stdout=[" +
                Head(listed.StdOut) + "]");
        }
    }

    // 往 zip 里写一个**文件**条目（不写父目录条目）。条目名用 '/'（wheel 规范/zip 的写法），
    // 7-Zip 的清单会按本机习惯回显成 '\' —— Judge 两种分隔符都切，所以两边都对得上。
    private static void AddZipFile(ZipArchive zip, string entryName, string content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(entryName);
        using (Stream stream = entry.Open())
        using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(content);
        }
    }

    private static void BuildVsixZip(string targetPath)
    {
        string src = SeedDir("gater-vsix-src");
        SeedText(src, "extension.vsixmanifest", "<?xml version=\"1.0\"?><PackageManifest/>");
        SeedText(src, "extension/readme.txt", "fixture extension payload");
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

    // 一个子串在文本里出现了几次（夹具自检用：数 `l -slt` 清单里的目录条目行 `Folder = +`）。
    private static int CountOccurrences(string text, string needle)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) { return 0; }

        int count = 0;
        int at = text.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }

    // `l -slt` 清单里有没有一条 `Path = ` 行的**末段**就是这个段名（用来证明 .dist-info 只出现在
    // 文件路径的中间位置，而不是作为一条目录条目/以它结尾的路径出现）。
    private static bool HasPathEndingWithSegment(string listing, string segment)
    {
        if (string.IsNullOrEmpty(listing)) { return false; }
        foreach (string rawLine in listing.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("Path = ", StringComparison.Ordinal)) { continue; }

            string value = line.Substring("Path = ".Length).Trim().TrimEnd('\\', '/');
            if (value.EndsWith(segment, StringComparison.OrdinalIgnoreCase)) { return true; }
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

    // OversizedFile 用：把新建的文件标记为稀疏文件，之后 SetEndOfFile 到 64 GiB 也不占磁盘。
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

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

    // ------------------------------------------------------------------
    // 「卷上没有 $Recycle.Bin」夹具（Task 9 fix 轮）：用 subst 把临时目录映射成一个盘符。
    //
    // 为什么用 subst：真造一个「没有 $Recycle.Bin 的卷」需要新建/挂载 VHD（要管理员），
    // 而 subst 出来的盘符本机实测 —— GetDriveType=3（固定卷）、GetVolumePathName 失败
    //（RecycleBinGuard.VolumeRoot 于是退回盘符根）、<盘符>:\$Recycle.Bin 不存在 —— 正好落在
    //「固定卷 + 没有回收站」这条分支上，且 subst /d 即可完全复原，全程无需提权。
    //
    // 用例必须用 try/finally 调 ReleaseNoRecycleBinVolume()：subst 映射是**进程外**状态，
    // H.Run 每用例前的 TestEnv.Cleanup() 删得掉目标目录却删不掉映射。本类只撤自己建的映射：
    //   * Cleanup() 每用例前都会跑一次自愈（ReleaseStaleSubstMappings）—— 上一次运行若在用例
    //     中途被杀，僵留的映射（任何盘符，包括 Z:）都会在那里被撤掉；
    //   * 挑盘符从 Y 起（**刻意避开 Z**）：`Guard.RefusesOnRemovableOrRemote` 用字面量
    //     `Z:\remote\x.zip` 钉「盘符没有卷」这条分支，夹具不该去占那个盘符；
    //   * 只用 QueryDosDevice 认领映射（目标是本类的 substroot 才撤），绝不 subst /d 别人的盘。
    // ------------------------------------------------------------------
    private static string _noBinLetter;
    private const string SubstMarkerName = ".rerar_subst_marker";
    private const uint DriveNoRootDir = 1;

    public static string NoRecycleBinVolumeRoot
    {
        get
        {
            if (_noBinLetter == null) { _noBinLetter = CreateNoBinVolume(); }

            // Cleanup() 会删掉整个临时根，subst 的目标目录可能被连带删掉；
            // 映射本身只是路径，补建目录即恢复。
            string target = Path.Combine(_root, "substroot");
            if (!Directory.Exists(target)) { Directory.CreateDirectory(target); }

            string root = _noBinLetter + @":\";
            // 自检：这个盘下必须真的没有回收站目录，否则夹具名不副实（用例会退化成假的「无回收站」）。
            if (Directory.Exists(Path.Combine(root, "$Recycle.Bin")))
            {
                throw new InvalidOperationException(
                    "subst 映射盘 " + root + " 下竟然出现了 $Recycle.Bin，夹具已失效");
            }
            return root;
        }
    }

    public static string NoRecycleBinFile
    {
        get
        {
            string path = Path.Combine(NoRecycleBinVolumeRoot, "no_recycle_bin_probe.txt");
            if (!File.Exists(path)) { File.WriteAllText(path, "x", new UTF8Encoding(false)); }
            return path;
        }
    }

    // 撤销我们自己建的 subst 映射（幂等：没建过、或已经撤掉时什么都不做）。
    public static void ReleaseNoRecycleBinVolume()
    {
        string letter = _noBinLetter;
        _noBinLetter = null;
        if (letter == null) { return; }

        try { RunSubst(letter + ": /d"); }
        catch (Exception) { }
    }

    private static string CreateNoBinVolume()
    {
        string target = Path.Combine(_root, "substroot");
        if (!Directory.Exists(target)) { Directory.CreateDirectory(target); }

        // 僵留映射已由 Cleanup()（每用例前都跑）撤掉了，这里只管找盘符。
        List<string> tried = new List<string>();
        for (char c = 'Y'; c >= 'P'; c--)
        {
            string letter = c.ToString();
            // 只挑「没有卷」的盘符：有卷的（包括空光驱）绝不碰，也绝不会去动别人的映射。
            if (GetDriveType(letter + @":\") != DriveNoRootDir) { continue; }
            tried.Add(letter);
            if (TrySubst(letter, target)) { return letter; }
        }

        throw new InvalidOperationException(
            "找不到可用盘符来 subst 出「没有 $Recycle.Bin 的卷」夹具（已试：" + string.Join("、", tried.ToArray()) + "）");
    }

    // 撤掉「目标正好是本夹具的 substroot」的僵留映射（上一次运行被中断留下的进程外状态）。
    // 只认自己人：别人的映射目标不是这个路径，QueryDosDevice 也读不出匹配 —— 绝不碰。
    // 任何失败都吞掉：自愈失败只意味着夹具可能挑到别的盘符，不该让用例失败。
    private static void ReleaseStaleSubstMappings(string target)
    {
        for (char c = 'Z'; c >= 'P'; c--)
        {
            string letter = c.ToString();
            string mapped = SubstTarget(letter + ":");
            if (mapped == null) { continue; }
            if (!string.Equals(TrimTrailingSlash(mapped), TrimTrailingSlash(target), StringComparison.OrdinalIgnoreCase)) { continue; }

            try { RunSubst(letter + ": /d"); }
            catch (Exception) { }
        }
    }

    // 盘符的设备映射目标。subst 映射形如 "\??\C:\...\substroot"；真卷是 "\Device\HarddiskVolume3"
    //（与夹具目标不可能相等）。查不到返回 null。
    private static string SubstTarget(string deviceName)
    {
        try
        {
            StringBuilder sb = new StringBuilder(1024);
            if (QueryDosDevice(deviceName, sb, sb.Capacity) <= 0) { return null; }

            string path = sb.ToString();
            const string dosPrefix = @"\??\";
            if (path.StartsWith(dosPrefix, StringComparison.Ordinal)) { path = path.Substring(dosPrefix.Length); }
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string TrimTrailingSlash(string path)
    {
        if (path == null) { return null; }
        return path.TrimEnd('\\');
    }

    // 成功判据是**可观察的事实**而不是 subst 的退出码：映射之后该盘下必须能看到我们写的身份标记。
    private static bool TrySubst(string letter, string target)
    {
        File.WriteAllText(Path.Combine(target, SubstMarkerName), "rerar subst fixture", new UTF8Encoding(false));
        RunSubst(letter + ": " + Quote(target));
        return File.Exists(Path.Combine(letter + @":\", SubstMarkerName));
    }

    private static void RunSubst(string arguments)
    {
        string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string exe = string.IsNullOrEmpty(systemDir) ? "subst.exe" : Path.Combine(systemDir, "subst.exe");

        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = exe;
        psi.Arguments = arguments;              // 参数由本类拼装，路径已加引号
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        Process process = new Process();
        process.StartInfo = psi;
        try
        {
            process.Start();
            string stdOut = process.StandardOutput.ReadToEnd();
            string stdErr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(); } catch (Exception) { }
                process.WaitForExit();
                throw new InvalidOperationException(
                    "subst 15 秒未返回（已杀掉）：subst " + arguments);
            }
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "subst 失败（退出码 " + process.ExitCode + "）：subst " + arguments +
                    "；stdout=[" + Head(stdOut) + "]；stderr=[" + Head(stdErr) + "]");
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value + "\"";
    }

    // subst 夹具用：查盘符类型（1 = DRIVE_NO_ROOT_DIR，即该盘符没有卷）。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetDriveType(string lpRootPathName);

    // subst 夹具自愈用：把盘符解析成设备映射目标（QueryDosDevice，UTF-16，无编码坑）。
    // 实测：subst 映射的 Y: → "\??\C:\Users\…\rerar-tests\substroot"；真卷 C: → "\Device\HarddiskVolume3"。
    // 注意 subst 不带参数时**不打印**映射表（本机实测 stdout 为空），所以只能走这个 API，不能解析输出。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);

    // ------------------------------------------------------------------
    // 「回环管理共享」夹具（Task 9 fix 轮）：把本地路径换成同一台机器经**未映射 UNC** 的写法
    //（C:\a\b → \\localhost\C$\a\b），从而拿到一条 GetDriveType=DRIVE_REMOTE 的真实路径。
    //
    // 为什么必须用它：本机没有真实远端主机，也不允许测试去 `net share`（要提权、还会改机器状态），
    // 未映射 UNC 是唯一能在不改机器状态的前提下得到 DRIVE_REMOTE 的办法（探针实测 GetDriveType=4）。
    //
    // 代价：`<盘符>$` 是**管理共享**，只有提权进程连得上；未提权时访问不到。此时本方法抛
    // InvalidOperationException，用例 H.Skip(name, reason) 记一条 SKIPPED（带原因、不计入 PASS）
    // —— 绝不假装测过远程卷。
    // ------------------------------------------------------------------
    public static string UncViewOf(string localPath)
    {
        if (string.IsNullOrEmpty(localPath)) { throw new ArgumentNullException("localPath"); }

        string root;
        try { root = Path.GetPathRoot(localPath); }
        catch (Exception) { root = null; }

        if (string.IsNullOrEmpty(root) || root.Length < 3 || root[1] != ':')
        {
            throw new InvalidOperationException("无法把「" + localPath + "」换成 UNC 视图：路径不在盘符根下");
        }

        string share = @"\\localhost\" + char.ToUpperInvariant(root[0]) + "$";
        string unc = share + localPath.Substring(2);   // "C:\a\b" → "\\localhost\C$\a\b"

        string dir;
        try { dir = Path.GetDirectoryName(unc); }
        catch (Exception) { dir = null; }

        if (dir == null || !Directory.Exists(dir))
        {
            throw new InvalidOperationException(
                "回环管理共享 " + share + " 不可达（管理共享只有提权进程连得上），拿不到 DRIVE_REMOTE 路径：" + unc);
        }
        return unc;
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

    // ==================================================================
    // Task 10：Preflight + Extractor 的 fixture 与驱动。
    //
    // 为什么这几个 zip 用 .NET 的 zip 写库逐条写文件，而不是用 7-Zip 现做：
    // 不变式 I5 逼着本项目的 SevenZipRunner 给**每一个** 7z 参数表都带 -p，而 7z 的 a 命令
    // 拿到空 -p 会真的弹密码提示（本轮实测 `a -p` → 打印 "Enter password"、退出码 255、
    // 包根本没生成），给真密码则**真的加密**。于是「不带密码的普通归档」用 7-Zip 造不出来 ——
    // 而递归解压的主用例恰恰就是普通无密码归档（NestedZip / DeepNestedZip / PlainZip / Docx）。
    // 实测对照：`-ttar` / `-tbzip2` 会忽略 -p（包不加密、能造），但它们的清单形状满足不了
    // 「条目数 + 总字节」这条基线（bz2 连条目名都没有），所以这里走 zip 写库。
    //
    // 唯一的例外是 BigSevenZip：它是真 .7z，因此按 I5 的约束它必然是加密的（头部明文、密码
    // SECRET，自检见 BuildBigSevenZip）。它只服务磁盘空间预检用例，而空间预检排在密码阶梯
    // **之前** —— 用例本身就钉住了这个顺序（否则会得到 SkippedNeedsPassword 而不是 Failed）。
    //
    // 与其它 fixture 一致：全部惰性重建，绝不做静态缓存（H.Run 每用例前 Cleanup()）。
    // ==================================================================

    // 外层 zip 里含 inner.zip，inner.zip 里含 hello.txt（规格 §9.1 用例 1 的形状）。
    public static string NestedZip
    {
        get { return Fixture("nested.zip", BuildNestedZip); }
    }

    // 三层嵌套：outer.zip → mid.zip → inner.zip → hello.txt。
    // 深度上限用例（MaxDepth=1）用它，保证「触顶未处理」的那一项必然存在。
    public static string DeepNestedZip
    {
        get { return Fixture("deep-nested.zip", BuildDeepNestedZip); }
    }

    // 普通 zip（三个普通文件，无加密）：非空目标目录改名用例的输入。
    public static string PlainZip
    {
        get { return Fixture("plain.zip", BuildExtractPlainZip); }
    }

    // 真 OOXML 形状的 .docx（[Content_Types].xml + _rels/.rels + word/document.xml）：I4 门控用例。
    public static string Docx
    {
        get { return Fixture("docx.docx", BuildExtractDocx); }
    }

    // 大 .7z（约 2 MB 不可压缩内容，AES 加密、头部明文、密码 SECRET）：磁盘空间预检用例。
    // 约 2 MB 是刻意的：够大到「1024 字节可用空间」的预检必然拒绝，又不至于让整套测试变慢。
    public static string BigSevenZip
    {
        get { return Fixture("big.7z", BuildBigSevenZip); }
    }

    // 高压缩比「zip 炸弹」形状（规格 §9.1 用例 10；审计 F2）：16 MiB 全零被 deflate 压到几十 KB。
    // 用例要的是「**任何写入之前**就被预检拒绝」——所以它既不会真的写出 16 MiB，也不会拖慢套件。
    public static string BombZip
    {
        get { return Fixture("bomb-zeros.zip", BuildBombZip); }
    }

    // Sniffer **不认识**、7-Zip 却能解的**真归档**（.wim）：格式门控「强制按压缩包尝试」的输入。
    public static string UnknownFormatWim
    {
        get { return Fixture("unknown-format.wim", BuildUnknownFormatWim); }
    }

    // Sniffer 不认识的**非归档**（一段全零字节）：强制后必须如实失败，绝不能被读成成功。
    public static string UnknownGarbage
    {
        get { return Fixture("unknown-garbage.bin", BuildUnknownGarbage); }
    }

    // 条目数上限用的**真归档**：10 万 + 1 条零长度条目（数量就是它存在的全部意义）。
    // 只服务一条用例，所以构造成本（约 10 万次 CreateEntry）每轮只付一次。
    public static string EntryFloodZip
    {
        get { return Fixture("entry-flood.zip", BuildEntryFloodZip); }
    }

    // ------------------------------------------------------------------
    // zip 写库：逐条写**文件**，不写父目录条目（与真实 wheel 夹具同一套做法）。
    // ------------------------------------------------------------------

    private static void WriteZipFile(string targetPath, Action<ZipArchive> fill)
    {
        string parent = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) { Directory.CreateDirectory(parent); }

        using (FileStream stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
        using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            fill(zip);
        }
    }

    private static byte[] ZipBytes(Action<ZipArchive> fill)
    {
        using (MemoryStream memory = new MemoryStream())
        {
            using (ZipArchive zip = new ZipArchive(memory, ZipArchiveMode.Create, true)) { fill(zip); }
            return memory.ToArray();
        }
    }

    private static void AddZipText(ZipArchive zip, string entryName, string content)
    {
        AddZipBytes(zip, entryName, new UTF8Encoding(false).GetBytes(content));
    }

    private static void AddZipBytes(ZipArchive zip, string entryName, byte[] bytes)
    {
        ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using (Stream stream = entry.Open()) { stream.Write(bytes, 0, bytes.Length); }
    }

    private static void BuildNestedZip(string targetPath)
    {
        byte[] inner = ZipBytes(delegate(ZipArchive z)
        {
            AddZipText(z, "hello.txt", "Rerar NestedZip fixture: hello from inner.zip\r\n");
        });

        WriteZipFile(targetPath, delegate(ZipArchive z)
        {
            AddZipBytes(z, "inner.zip", inner);
            AddZipText(z, "readme.txt", "Rerar NestedZip fixture: outer layer\r\n");
        });
    }

    private static void BuildDeepNestedZip(string targetPath)
    {
        byte[] inner = ZipBytes(delegate(ZipArchive z)
        {
            AddZipText(z, "hello.txt", "Rerar DeepNestedZip fixture: innermost\r\n");
        });
        byte[] mid = ZipBytes(delegate(ZipArchive z)
        {
            AddZipBytes(z, "inner.zip", inner);
        });

        WriteZipFile(targetPath, delegate(ZipArchive z)
        {
            AddZipBytes(z, "mid.zip", mid);
        });
    }

    private static void BuildExtractPlainZip(string targetPath)
    {
        WriteZipFile(targetPath, delegate(ZipArchive z)
        {
            AddZipText(z, "a.txt", "alpha");
            AddZipText(z, "b.txt", "bravo");
            AddZipText(z, "docs/readme.md", "# readme");
        });
    }

    private static void BuildExtractDocx(string targetPath)
    {
        WriteZipFile(targetPath, delegate(ZipArchive z)
        {
            AddZipText(z, "[Content_Types].xml", "<?xml version=\"1.0\"?><Types/>");
            AddZipText(z, "_rels/.rels", "<?xml version=\"1.0\"?><Relationships/>");
            AddZipText(z, "word/document.xml", "<?xml version=\"1.0\"?><document/>");
        });
    }

    // 大 7z：2 MB 固定种子伪随机内容（7-Zip 压不动它）+ `-pSECRET`（I5 逼出来的）。
    // 三条自检缺一不可，否则用例会在一个「其实不是加密包」或「其实不加密」的夹具上假通过：
    //   1) `l -slt -p` 必须**成功**（头部明文 ⇒ 清单可读 ⇒ 空间预检才有 TotalBytes 可比）；
    //   2) 该清单必须带 `Encrypted = +`（成员确实加密）；
    //   3) `t -p` 必须失败、`t -pSECRET` 必须成功（密码确实是 SECRET）。
    private static void BuildBigSevenZip(string targetPath)
    {
        string seed = SeedBinary("big-7z-payload.bin", 2 * 1024 * 1024);
        string[] args = new string[] { "a", "-t7z", "-mx1", targetPath, seed, "-pSECRET", "-y" };
        RunResult built = RunSevenZip(args);
        if (!SevenZipRunner.IsSuccess(built.ExitCode)) { FixtureFailed("构造 BigSevenZip fixture", args, built); }

        string[] listArgs = new string[] { "l", "-slt", targetPath, "-p", "-y" };
        RunResult listed = RunSevenZip(listArgs);
        if (!SevenZipRunner.IsSuccess(listed.ExitCode))
        {
            FixtureFailed("校验 BigSevenZip fixture（头部必须明文、清单必须可读）", listArgs, listed);
        }
        // 直接找 `Encrypted = +` 这一整行：HasNonEmptyValue 是「键 + 非空值」的判据，
        // 而这里的值恰好是 "+"，用它会把正确的清单误判成失败（Task 4 的软链自检才用它）。
        if (listed.StdOut == null || listed.StdOut.IndexOf("Encrypted = +", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                "BigSevenZip fixture 构造失败：清单里没有 `Encrypted = +`（成员没被加密）；stdout=[" + Head(listed.StdOut) + "]");
        }

        string[] wrongArgs = new string[] { "t", targetPath, "-p", "-y" };
        RunResult wrong = RunSevenZip(wrongArgs);
        if (SevenZipRunner.IsSuccess(wrong.ExitCode))
        {
            FixtureFailed("校验 BigSevenZip fixture（空密码竟然解开了）", wrongArgs, wrong);
        }
        string[] rightArgs = new string[] { "t", targetPath, "-pSECRET", "-y" };
        RunResult right = RunSevenZip(rightArgs);
        if (!SevenZipRunner.IsSuccess(right.ExitCode)) { FixtureFailed("校验 BigSevenZip fixture（SECRET 打不开）", rightArgs, right); }
    }

    // ==================================================================
    // Task 10 修复轮（预检安全上限 + 「强制按压缩包尝试」）的夹具
    // ==================================================================

    // 高压缩比包：16 MiB 全零（deflate 对全零文件能压到几十 KB）。
    // 用 .NET 的 zip 写库造（理由同本节的其它 zip）：7-Zip 的 `a` 在 I5 约束下必然带 `-p`，
    // 而带密码的 zip 是**加密**的 —— 这个用例要的形状是「可读清单 + 巨大解压后体积」，
    // 加密会先把流程推到密码阶梯上去。
    private static void BuildBombZip(string targetPath)
    {
        WriteZipFile(targetPath, delegate(ZipArchive z)
        {
            ZipArchiveEntry entry = z.CreateEntry("zeros.bin", CompressionLevel.Optimal);
            using (Stream stream = entry.Open())
            {
                byte[] chunk = new byte[64 * 1024];
                for (long written = 0; written < BombPayloadBytes; written += chunk.Length)
                {
                    stream.Write(chunk, 0, chunk.Length);
                }
            }
        });

        // 自检：这个夹具必须真的越过解压比上限。否则用例是在一块「其实不高压缩比」的数据上
        // 假通过 —— 而「高压缩比必须被拒」正是这条安全上限唯一要挡住的方向。
        long pack = new FileInfo(targetPath).Length;
        long basis = pack > Preflight.ExpansionRatioBasisFloorBytes ? pack : Preflight.ExpansionRatioBasisFloorBytes;
        double ratio = (double)BombPayloadBytes / (double)basis;
        if (ratio <= Preflight.MaxExpansionRatio)
        {
            throw new InvalidOperationException(
                "BombZip fixture 构造失败：包 " + pack + " 字节、解压后 " + BombPayloadBytes +
                " 字节，解压比只有 " + ((long)ratio) + " 倍，没超过上限 " + Preflight.MaxExpansionRatio + " 倍");
        }
    }

    private const int BombPayloadBytes = 16 * 1024 * 1024;

    // Sniffer 不认识的真归档：`.wim`（MSWIM 魔数不在 Sniffer 的签名表里）而 7-Zip 26.01 能造能解。
    //
    // 为什么不用残余风险里点名的 `.cab`：7-Zip 只能**解**不能**造**（实测 `7z a -tcab` 报「未实现」），
    // 而用系统自带的 makecab 会让这套件多一个工具依赖；wim 用**已经必须存在的** 7-Zip 就能造
    //（实测 26.01 `a -twim` 成功，且 `-p` 被忽略、不弹密码提示 —— 与 tar/bzip2 同形）。
    private static void BuildUnknownFormatWim(string targetPath)
    {
        // 源文件放在自己的短目录里，用 `目录\*` 通配喂给 7-Zip：这样存进包里的成员名就是裸文件名
        //（绝不能用 fixture-seed 目录，那里面还有别的夹具的种子文件）。
        string sourceDir = Path.Combine(_root, "unknown-format-src");
        if (!Directory.Exists(sourceDir)) { Directory.CreateDirectory(sourceDir); }
        File.WriteAllText(Path.Combine(sourceDir, "hello.txt"),
            "Rerar forced-as-archive fixture: hello\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(sourceDir, "second.dat"),
            new string('F', 512), new UTF8Encoding(false));

        string[] args = new string[] { "a", "-twim", targetPath, sourceDir + @"\*", "-p", "-y" };
        RunResult built = RunSevenZip(args);
        if (!SevenZipRunner.IsSuccess(built.ExitCode)) { FixtureFailed("构造 UnknownFormatWim fixture", args, built); }

        // 自检 1：它必须真的**不被 Sniffer 认识** —— 否则「强制」这条用例根本没走到格式门控。
        long length = new FileInfo(targetPath).Length;
        byte[] head = new byte[512];
        using (FileStream stream = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            int read = stream.Read(head, 0, head.Length);
            if (read != head.Length) { Array.Resize(ref head, read); }
        }
        SniffKind kind = Sniffer.Classify(head, length, Path.GetFileName(targetPath), null);
        if (kind != SniffKind.Unknown)
        {
            throw new InvalidOperationException(
                "UnknownFormatWim fixture 构造失败：Sniffer 把它判成了 " + kind + "（本条用例要的是 Unknown）");
        }

        // 自检 2：7-Zip 必须读得出它的清单 —— 否则「强制后 Completed」这个期望是错的。
        string[] listArgs = new string[] { "l", "-slt", targetPath, "-p", "-y" };
        RunResult listed = RunSevenZip(listArgs);
        if (!SevenZipRunner.IsSuccess(listed.ExitCode)) { FixtureFailed("校验 UnknownFormatWim fixture（清单读不出来）", listArgs, listed); }
        if (listed.StdOut == null || listed.StdOut.IndexOf("hello.txt", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                "UnknownFormatWim fixture 构造失败：清单里没有 hello.txt；stdout=[" + Head(listed.StdOut) + "]");
        }
    }

    // 非归档：一段全零字节。Sniffer 判 Unknown（不是空文件，也没有任何签名/尾部标记），
    // 7-Zip 读不出清单 ⇒ 强制后必须 Failed（顺带钉住「强制不绕过 I1」）。
    private static void BuildUnknownGarbage(string targetPath)
    {
        File.WriteAllBytes(targetPath, new byte[4096]);
    }

    // 条目数上限的夹具：条目数 = MaxEntriesPerArchive + 1（**刚好越界一条**，这样它也顺带钉住边界）。
    // 每条都是零长度文件：伤害不在字节（总共 0 字节，空间预检完全拦不住），而在 MFT/配额 —— 正是这条上限的理由。
    private static void BuildEntryFloodZip(string targetPath)
    {
        WriteZipFile(targetPath, delegate(ZipArchive z)
        {
            for (int i = 0; i <= Preflight.MaxEntriesPerArchive; i++)
            {
                AddZipBytes(z, "e" + i + ".bin", new byte[0]);
            }
        });

        // 自检：清单必须真的读得出来（否则这条用例会退化成「清单坏了」而不是「条目数超限」）。
        string[] listArgs = new string[] { "l", "-slt", targetPath, "-p", "-y" };
        RunResult listed = RunSevenZip(listArgs);
        if (!SevenZipRunner.IsSuccess(listed.ExitCode)) { FixtureFailed("构造 EntryFloodZip fixture", listArgs, listed); }
    }

    // ------------------------------------------------------------------
    // Task 10 驱动：把 Extractor 用固定选项跑一遍，返回 RunSummary。
    // 输出根固定为 OutRoot（每个用例前被 Cleanup 清空），于是
    // 「暂存 → 校验 → 提交」全部落在临时目录里，用例之间互不干扰。
    // ------------------------------------------------------------------

    public static string OutOf(string archive, params string[] parts)
    {
        string path = Path.Combine(OutRoot, PathSanitizer.Sanitize(Path.GetFileName(archive)));
        if (parts != null)
        {
            foreach (string part in parts) { path = Path.Combine(path, part); }
        }
        return path;
    }

    public static RunSummary RunExtract(string archive)
    {
        return RunExtractCore(archive, null, 10, null, false, 2);
    }

    public static RunSummary RunExtractWithPassword(string archive, string password)
    {
        return RunExtractCore(archive, password, 10, null, false, 2);
    }

    public static RunSummary RunExtractWithDepth(int depth, string archive)
    {
        return RunExtractCore(archive, null, depth, null, false, 2);
    }

    // 固定可用空间（brief 的 freeBytes 形参）：预检必然按该值判定。
    public static RunSummary RunExtractWithFakeDisk(long freeBytes, string archive)
    {
        return RunExtractCore(archive, null, 10, new long[] { freeBytes }, false, 2);
    }

    // 可用空间按时序变化（第 1 次调用 = 预检，之后 = 运行中轮询）+ 轮询间隔可调：
    // 用来钉住「解压中途盘满 → 干净中止」这条路径（Review Focus #1）。
    public static RunSummary RunExtractWithDroppingDisk(string archive, string password, long beforeBytes, long afterBytes, int pollSeconds)
    {
        return RunExtractCore(archive, password, 10, new long[] { beforeBytes, afterBytes }, false, pollSeconds);
    }

    // 删除开关打开（I3 的默认值是 false，这里显式打开）。名字偏长是为了让用例读起来就是
    // 「这次开了删除」——「失败/跳过也不许删」的用例全靠它反向钉住。
    public static RunSummary RunExtractWithDelete(string archive)
    {
        return RunExtractCore(archive, null, 10, null, true, 2);
    }

    public static RunSummary RunExtractWithExistingTarget(string archive)
    {
        // 在目标名上先放一个**非空**目录：Uniquify 必须改名为 "<名字> (2)"（规格 §6.11）。
        string occupied = Path.Combine(OutRoot, PathSanitizer.Sanitize(Path.GetFileName(archive)));
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "blocker.txt"), "occupied", new UTF8Encoding(false));
        return RunExtractCore(archive, null, 10, null, false, 2);
    }

    // 规格 §6.11 的**默认**布局：不指定输出根 ⇒ 产物落在归档所在目录（原地）。
    // 其余用例都显式指定 OutRoot，所以这条是唯一钉住「原地」这个默认行为的地方。
    public static RunSummary RunExtractInPlace(string archive)
    {
        RunOptions options = new RunOptions();
        options.SevenZipPath = SevenZip;
        options.OutputRoot = "";                 // 空 = 原地（这是 RunOptions 的默认值）
        return new Extractor(options, new DriveSpaceProvider(), null).Run(new string[] { archive });
    }

    // 用户取消（两段式取消的入口）：预置一个**已取消**的令牌。
    public static RunSummary RunExtractCancelled(string archive)
    {
        CancellationTokenSource source = new CancellationTokenSource();
        source.Cancel();

        RunOptions options = new RunOptions();
        options.SevenZipPath = SevenZip;
        options.OutputRoot = OutRoot;
        options.Cancellation = source.Token;
        return new Extractor(options, new DriveSpaceProvider(), null).Run(new string[] { archive });
    }

    private static RunSummary RunExtractCore(string archive, string password, int depth, long[] freeSequence, bool deleteOriginals, int pollSeconds)
    {
        return RunExtractCore(archive, password, depth, freeSequence, deleteOriginals, pollSeconds, null);
    }

    // 「强制按压缩包尝试」的逐项覆盖（规格 §6.1 的逐项动作）：把该路径加进 RunOptions 的清单。
    // 只覆盖格式门控 —— 校验/暂存/不删/安全上限全部照旧（用例逐个钉住）。
    public static RunSummary RunExtractForced(string archive)
    {
        return RunExtractCore(archive, null, 10, null, false, 2, archive);
    }

    // 一次 Run 跑多个目标：钉住「预检安全上限只跳过**那一个**归档、整批继续」（不是致命中止）。
    public static RunSummary RunExtractMany(params string[] archives)
    {
        RunOptions options = NewOptions(null, 10, false, 2);
        return new Extractor(options, new DriveSpaceProvider(), null).Run(archives);
    }

    private static RunOptions NewOptions(string password, int depth, bool deleteOriginals, int pollSeconds)
    {
        RunOptions options = new RunOptions();
        options.SevenZipPath = SevenZip;
        options.OutputRoot = OutRoot;
        options.Password = password;
        options.MaxDepth = depth;
        options.DeleteOriginals = deleteOriginals;
        options.DiskPollSeconds = pollSeconds;
        return options;
    }

    private static RunSummary RunExtractCore(string archive, string password, int depth, long[] freeSequence, bool deleteOriginals, int pollSeconds, string force)
    {
        RunOptions options = NewOptions(password, depth, deleteOriginals, pollSeconds);
        if (force != null) { options.ForceTreatAsArchive.Add(force); }

        IDiskSpaceProvider disk = freeSequence == null
            ? (IDiskSpaceProvider)new DriveSpaceProvider()
            : new SequenceDisk(freeSequence);

        Extractor extractor = new Extractor(options, disk, null);
        return extractor.Run(new string[] { archive });
    }

    // 按时序返回可用空间：第 N 次调用返回 values[min(N, len-1)]。预检是第 1 次调用，
    // 之后的运行中轮询一直拿最后一个值 —— 于是「预检够、运行中不够」可以精确构造。
    private sealed class SequenceDisk : IDiskSpaceProvider
    {
        private readonly long[] _values;
        private int _calls;

        public SequenceDisk(long[] values)
        {
            _values = (values == null || values.Length == 0) ? new long[] { 0 } : values;
        }

        public long FreeBytes(string path)
        {
            int index = _calls < _values.Length ? _calls : _values.Length - 1;
            _calls++;
            return _values[index];
        }
    }

    // 暂存树里有没有 reparse point（软链/联接）。**绝不进入** reparse point 目录：
    // 否则一个指回父目录的链接就能让本方法死循环，或枚举到输出根之外去。
    public static bool AnyReparsePointUnder(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { return false; }

        Stack<string> pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch (Exception) { continue; }

            foreach (string entry in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception) { continue; }

                if ((attributes & FileAttributes.ReparsePoint) != 0) { return true; }
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); }
            }
        }
        return false;
    }

    // ==================================================================
    // Task 10 修复轮 #3（Finding 1–4）的夹具与驱动
    // ==================================================================

    // ---------------- Finding 1：致命/触顶之后「没轮到」的那些候选 ----------------

    // 固定可用空间（第 1 个目标就因空间不足成为致命档）+ 多个目标：用来钉住
    // 「致命中止之后，本轮**还没轮到处理**的候选全部进 NotAttempted（且没有 Results 条目）」。
    public static RunSummary RunExtractManyWithFakeDisk(long freeBytes, params string[] archives)
    {
        RunOptions options = NewOptions(null, 10, false, 2);
        return new Extractor(options, new SequenceDisk(new long[] { freeBytes }), null).Run(archives);
    }

    // ---------------- Finding 2：一次**跑得久**的密码验证 ----------------

    // 形状：512 MiB 全零（NTFS 稀疏文件，实占近 0）+ 8 MiB 随机填充，用 7-Zip 压成**加密 zip**
    //（ZipCrypto，密码 SECRET，头部明文 ⇒ 清单读得出来 ⇒ 流程会走到密码阶梯）。
    //
    // 为什么必须这么大：这条用例要证明的命题是「取消能打断**正在跑的那一次** `t`」。小夹具上
    // 一次 `t` 只有几十毫秒，与「下一个候选之前才发现取消」在时间上完全无法区分 —— 那个区分只能
    // 建立在「一次调用本身足够长」上。本机实测（7-Zip 26.01）：
    //     t -pSECRET  ≈ 0.85 s（要解压 512 MiB）
    //     t -pWRONG   ≈ 0.02 s（ZipCrypto 的头校验立刻失败）
    //     l -slt -p   ≈ 0.05 s（只读中央目录，不解压）
    // 8 MiB 随机填充的作用是把**包体**抬到解压后总量的 1% 以上：否则解压比会超过
    // Preflight.MaxExpansionRatio(100)，这个包会在**密码阶梯之前**就被安全上限拒掉，用例白测
    //（自检 2 钉住这一点）。
    private const long SlowVerifyZeroBytes = 512L * 1024 * 1024;
    private const int SlowVerifyPadBytes = 8 * 1024 * 1024;

    public static string SlowEncryptedZip
    {
        get { return Fixture("slow-verify.zip", BuildSlowEncryptedZip); }
    }

    private static void BuildSlowEncryptedZip(string targetPath)
    {
        string sourceDir = Path.Combine(_root, "slow-verify-src");
        if (!Directory.Exists(sourceDir)) { Directory.CreateDirectory(sourceDir); }

        // 稀疏零文件：逻辑大小 512 MiB、实占接近 0（与 OversizedFile 同一套 FSCTL_SET_SPARSE 做法）。
        string zeros = Path.Combine(sourceDir, "zeros.bin");
        if (!File.Exists(zeros) || new FileInfo(zeros).Length != SlowVerifyZeroBytes)
        {
            using (FileStream fs = new FileStream(zeros, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                uint returned;
                bool ok = DeviceIoControl(fs.SafeFileHandle.DangerousGetHandle(), FsctlSetSparse,
                    IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero);
                if (!ok)
                {
                    throw new InvalidOperationException("FSCTL_SET_SPARSE 失败（Win32 错误 " + Marshal.GetLastWin32Error() + "）");
                }
                fs.SetLength(SlowVerifyZeroBytes);
            }
        }

        string pad = SeedBinary("slow-verify-pad.bin", SlowVerifyPadBytes);

        string[] args = new string[] { "a", "-tzip", "-mx1", targetPath, zeros, pad, "-pSECRET", "-y" };
        RunResult built = RunSevenZip(args);
        if (!SevenZipRunner.IsSuccess(built.ExitCode)) { FixtureFailed("构造 SlowEncryptedZip fixture", args, built); }

        // 自检 1：清单必须读得出来、且带 `Encrypted = +`（否则永远走不到密码阶梯）。
        string[] listArgs = new string[] { "l", "-slt", targetPath, "-p", "-y" };
        RunResult listed = RunSevenZip(listArgs);
        if (listed.StdOut == null || listed.StdOut.IndexOf("Encrypted = +", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException(
                "SlowEncryptedZip fixture 构造失败：清单里没有 `Encrypted = +`；stdout=[" + Head(listed.StdOut) + "]");
        }

        // 自检 2：解压比必须**明显低于**安全上限（否则会被 caps 拦在密码阶梯之前）。
        long pack = new FileInfo(targetPath).Length;
        double ratio = (double)(SlowVerifyZeroBytes + SlowVerifyPadBytes) / (double)pack;
        if (ratio > Preflight.MaxExpansionRatio * 0.8)
        {
            throw new InvalidOperationException("SlowEncryptedZip fixture 构造失败：解压比约 " + (long)ratio +
                " 倍，太接近安全上限 " + Preflight.MaxExpansionRatio + " 倍（会被拦在密码阶梯之前）");
        }

        // 自检 3：密码确实是 SECRET（否则「第一次 `t` 就是慢的那一次」不成立）。
        string[] verifyArgs = new string[] { "t", targetPath, "-pSECRET", "-y" };
        RunResult verified = RunSevenZip(verifyArgs);
        if (!SevenZipRunner.IsSuccess(verified.ExitCode)) { FixtureFailed("校验 SlowEncryptedZip fixture（SECRET 打不开）", verifyArgs, verified); }
    }

    // 有没有 7z 子进程已经烧掉 >= milliseconds 的 CPU 时间。用例用它把「取消」精确地落进一次
    // **完整校验**（`t`）里：`l -slt` 只读中央目录、CPU 时间几乎为 0，而 `t` 要解压整个包。
    // 判据用 CPU 时间而不是墙钟：机器慢只会让同一次调用更慢，不会把一次清单读取变成一次校验。
    public static bool AnySevenZipBurningCpu(long milliseconds)
    {
        Process[] running = Process.GetProcessesByName("7z");
        try
        {
            foreach (Process p in running)
            {
                try
                {
                    if (!p.HasExited && p.TotalProcessorTime.TotalMilliseconds >= milliseconds) { return true; }
                }
                catch (Exception)
                {
                    // 拿不到某个进程的 CPU 时间（权限等）就跳过它，不因为「看不见」而误判。
                }
            }
        }
        finally
        {
            foreach (Process p in running) { p.Dispose(); }
        }
        return false;
    }

    // 带真实取消令牌跑一遍（Finding 2 的驱动）。
    public static RunSummary RunExtractWithToken(string archive, string password, CancellationToken token)
    {
        RunOptions options = NewOptions(password, 10, false, 2);
        options.Cancellation = token;
        return new Extractor(options, new DriveSpaceProvider(), null).Run(new string[] { archive });
    }

    // 一次候选验证（`t`）要多久。用例拿它当基线：取消之后必须**远早于**一整次调用就跑完。
    public static long TimeSingleVerification(string archive, string password)
    {
        Stopwatch watch = Stopwatch.StartNew();
        RunSevenZip(new string[] { "t", archive, "-p" + password, "-y" });
        watch.Stop();
        return watch.ElapsedMilliseconds;
    }

    // 预检放行、之后每次查询都抛的 provider（Finding 2 的第二半：解压后那次 FreeBytes 必须被包住，
    // 让「查不出来」落进「按空间不足处理」的不提交路径，而不是降级成「内部错误」）。
    public static RunSummary RunExtractWithThrowingDisk(string archive)
    {
        RunOptions options = NewOptions(null, 10, false, 2);
        return new Extractor(options, new ThrowingDisk(), null).Run(new string[] { archive });
    }

    private sealed class ThrowingDisk : IDiskSpaceProvider
    {
        private int _calls;

        public long FreeBytes(string path)
        {
            _calls++;
            if (_calls <= 1) { return long.MaxValue; }     // 第 1 次 = 预检：放行
            throw new InvalidOperationException("注入的磁盘查询失败（用例构造）");
        }
    }

    // ---------------- Finding 3：长路径 / 未完成标记 ----------------

    // 目标目录名最长的归档副本：120 个 'q' + ".zip" ⇒ 消毒后的目标目录名正好 120 字符
    //（PathSanitizer 的长度上限）。于是「裸目标名 + 条目」正好可以顶到 MaxPathLength。
    public static string LongNamedArchiveCopy()
    {
        string path = TmpFile(new string('q', 120) + ".zip");
        File.Copy(LongEntryZip, path, true);
        return path;
    }

    // 让**旧度量**（裸目标名 + 条目名）正好等于 Preflight.MaxPathLength 的条目名长度。
    // 旧度量放行、**新度量**（目标名 + " (未完成) (2)" + 条目名 = 旧度量 + 17）必然拒绝 ——
    // 这条用例钉的就是这 17 个字符的差。长度随 OutRoot 现算，机器不同也成立。
    public static int LongEntryNameLength
    {
        get { return 258 - (OutRoot.Length + 1 + 120); }
    }

    public static string LongEntryName
    {
        get { return new string('e', LongEntryNameLength - 4) + ".txt"; }
    }

    public static string LongEntryZip
    {
        get { return Fixture("long-entry.zip", BuildLongEntryZip); }
    }

    private static void BuildLongEntryZip(string targetPath)
    {
        int length = LongEntryNameLength;
        if (length < 8 || length > 200)
        {
            throw new InvalidOperationException("长路径夹具的前提不成立：条目名长度 " + length +
                "（临时目录太深或太浅，见 LongEntryNameLength）");
        }
        string entryName = LongEntryName;
        WriteZipFile(targetPath, delegate(ZipArchive z) { AddZipText(z, entryName, "Rerar long-path fixture\r\n"); });
    }

    // 「清单读得出来、解压一定失败（CRC 错）」的 zip：唯一成员是 stored 的短名字 `payload`，
    // 数据里的标记字节被翻掉一位 ⇒ `l -slt` 成功（基线可用）、`x` 退出码 2 ⇒ 走 MarkIncomplete。
    // 失败路径的长度用例需要一个「写到盘上、然后失败」的输入，这是最便宜、最确定的一种。
    private const string CorruptPayloadMarker = "RERAR-CORRUPT-ME-0123456789-ABCDEFGHIJ";

    public static string CorruptPayloadZip
    {
        get { return Fixture("corrupt-payload.zip", BuildCorruptPayloadZip); }
    }

    // 该夹具唯一的成员名（长度就是它 —— 用例拿它算路径长度）。
    public const string CorruptPayloadEntryName = "payload";

    private static void BuildCorruptPayloadZip(string targetPath)
    {
        WriteZipFile(targetPath, delegate(ZipArchive z)
        {
            ZipArchiveEntry entry = z.CreateEntry(CorruptPayloadEntryName, CompressionLevel.NoCompression);
            using (Stream stream = entry.Open())
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(CorruptPayloadMarker);
                stream.Write(bytes, 0, bytes.Length);
            }
        });

        byte[] file = File.ReadAllBytes(targetPath);
        byte[] marker = new UTF8Encoding(false).GetBytes(CorruptPayloadMarker);
        int at = IndexOfBytes(file, marker);
        if (at < 0)
        {
            throw new InvalidOperationException("CorruptPayloadZip fixture 构造失败：找不到数据标记（stored 成员应当原样落在文件里）");
        }
        file[at + 5] = (byte)(file[at + 5] ^ 0xFF);
        File.WriteAllBytes(targetPath, file);

        // 自检 1：清单仍读得出来（⇒ 有可用基线 ⇒ 流程会真的写盘，然后才在 CRC 上失败）。
        string[] listArgs = new string[] { "l", "-slt", targetPath, "-p", "-y" };
        RunResult listed = RunSevenZip(listArgs);
        if (!SevenZipRunner.IsSuccess(listed.ExitCode))
        {
            FixtureFailed("校验 CorruptPayloadZip fixture（清单必须可读）", listArgs, listed);
        }
        // 自检 2：解压必须失败（否则这条用例根本没走到 MarkIncomplete）。
        string[] testArgs = new string[] { "t", targetPath, "-p", "-y" };
        RunResult tested = RunSevenZip(testArgs);
        if (SevenZipRunner.IsSuccess(tested.ExitCode))
        {
            FixtureFailed("校验 CorruptPayloadZip fixture（必须解压失败）", testArgs, tested);
        }
    }

    private static int IndexOfBytes(byte[] haystack, byte[] needle)
    {
        if (haystack == null || needle == null || needle.Length == 0) { return -1; }
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) { return i; }
        }
        return -1;
    }

    // 「哨兵的名字已经被占住」的 zip：除了一份 CRC 坏掉的成员（必然走失败路径），还有一个
    // **同名目录条目** `_RERAR_INCOMPLETE.txt/`。于是 MarkIncomplete 无论先写还是后写都不可能把
    // 哨兵文件放进去 —— 这正是「标记真的放不下」这一档：必须**如实写进判词**，绝不像旧实现那样
    // 静默留下一个没有标记的目录。恶意/畸形归档里出现这个形状是现实的（成员名由归档作者决定）。
    public static string SentinelNameClashZip
    {
        get { return Fixture("sentinel-clash.zip", BuildSentinelNameClashZip); }
    }

    private static void BuildSentinelNameClashZip(string targetPath)
    {
        WriteZipFile(targetPath, delegate(ZipArchive z)
        {
            z.CreateEntry("_RERAR_INCOMPLETE.txt/");     // 与哨兵同名的**目录**条目
            ZipArchiveEntry entry = z.CreateEntry(CorruptPayloadEntryName, CompressionLevel.NoCompression);
            using (Stream stream = entry.Open())
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(CorruptPayloadMarker);
                stream.Write(bytes, 0, bytes.Length);
            }
        });

        byte[] file = File.ReadAllBytes(targetPath);
        byte[] marker = new UTF8Encoding(false).GetBytes(CorruptPayloadMarker);
        int at = IndexOfBytes(file, marker);
        if (at < 0) { throw new InvalidOperationException("SentinelNameClashZip fixture 构造失败：找不到数据标记"); }
        file[at + 5] = (byte)(file[at + 5] ^ 0xFF);
        File.WriteAllBytes(targetPath, file);

        // 自检 1：清单读得出来（目录条目不计入文件数，基线仍在）。
        string[] listArgs = new string[] { "l", "-slt", targetPath, "-p", "-y" };
        RunResult listed = RunSevenZip(listArgs);
        if (!SevenZipRunner.IsSuccess(listed.ExitCode)) { FixtureFailed("校验 SentinelNameClashZip fixture（清单必须可读）", listArgs, listed); }
        // 自检 2：解压必须失败（CRC 错 ⇒ 走 MarkIncomplete 那条失败路径）。
        string[] testArgs = new string[] { "t", targetPath, "-p", "-y" };
        RunResult tested = RunSevenZip(testArgs);
        if (SevenZipRunner.IsSuccess(tested.ExitCode)) { FixtureFailed("校验 SentinelNameClashZip fixture（必须解压失败）", testArgs, tested); }
        // 自检 3：7-Zip 解压后必须真的在暂存树里造出那个**同名目录**（否则「标记放不下」这个前提是假的）。
        string probe = Path.Combine(_root, "sentinel-clash-probe");
        if (Directory.Exists(probe)) { Directory.Delete(probe, true); }
        Directory.CreateDirectory(probe);
        RunSevenZip(new string[] { "x", targetPath, "-o" + probe, "-p", "-y" });
        bool clash = Directory.Exists(Path.Combine(probe, "_RERAR_INCOMPLETE.txt"));
        try { Directory.Delete(probe, true); }
        catch (Exception) { }
        if (!clash)
        {
            throw new InvalidOperationException("SentinelNameClashZip fixture 构造失败：7-Zip 没有把同名条目解成目录");
        }
    }

    // 长度 >= atLeastLength 的嵌套输出根（每次加一个目录段，最多只超 1 个字符）。
    // Finding 3 的用例要控制「目标目录名 + (未完成) + 哨兵名」的长度，于是输出根必须够深；
    // 具体长度由调用方用返回值的 Length 现算（机器不同 Tmp 长度不同，绝不写死）。
    public static string LongOutputRoot(int atLeastLength)
    {
        string path = Tmp;
        while (path.Length < atLeastLength)
        {
            int need = atLeastLength - path.Length;
            if (need < 2) { need = 2; }
            if (need > 100) { need = 100; }
            path = Path.Combine(path, new string('d', need - 1));
        }
        if (!Directory.Exists(path)) { Directory.CreateDirectory(path); }
        return path;
    }

    // 指定输出根跑一遍（Finding 3 的长度用例要自己控制目标目录的长度）。
    public static RunSummary RunExtractWithOutputRoot(string archive, string outputRoot)
    {
        RunOptions options = NewOptions(null, 10, false, 2);
        options.OutputRoot = outputRoot;
        return new Extractor(options, new DriveSpaceProvider(), null).Run(new string[] { archive });
    }

    // ---------------- Finding 4：密码来源类别 ----------------

    // 用给定口令造的加密 zip（ZipCrypto；头部明文 ⇒ 清单读得出来 ⇒ 流程会走到密码阶梯）。
    // 两个口令分别服务「字典层命中」的两条标签：内置字典（123456 在内置表里）与导入字典。
    public static string BuiltInDictZip
    {
        get { return Fixture("dict-builtin.zip", delegate(string p) { BuildDictZip(p, "123456"); }); }
    }

    public static string ImportedDictZip
    {
        get { return Fixture("dict-imported.zip", delegate(string p) { BuildDictZip(p, "ImportedSecret"); }); }
    }

    private static void BuildDictZip(string targetPath, string password)
    {
        string sourceDir = Path.Combine(_root, "dict-src");
        if (!Directory.Exists(sourceDir)) { Directory.CreateDirectory(sourceDir); }
        string source = Path.Combine(sourceDir, "dict-payload.txt");
        File.WriteAllText(source, "Rerar dictionary-label fixture\r\n", new UTF8Encoding(false));

        string[] args = new string[] { "a", "-tzip", "-mx1", targetPath, source, "-p" + password, "-y" };
        RunResult built = RunSevenZip(args);
        if (!SevenZipRunner.IsSuccess(built.ExitCode)) { FixtureFailed("构造字典口令夹具", args, built); }

        // 自检 1：清单必须读得出来、且带 `Encrypted = +`（否则走不到密码阶梯）。
        RunResult listed = RunSevenZip(new string[] { "l", "-slt", targetPath, "-p", "-y" });
        if (listed.StdOut == null || listed.StdOut.IndexOf("Encrypted = +", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("字典口令夹具构造失败：清单里没有 `Encrypted = +`");
        }
        // 自检 2：口令确实有效、空口令确实无效（否则用例断言的「命中了字典层」是假的）。
        if (!SevenZipRunner.IsSuccess(RunSevenZip(new string[] { "t", targetPath, "-p" + password, "-y" }).ExitCode))
        {
            throw new InvalidOperationException("字典口令夹具构造失败：`t -p<口令>` 没成功");
        }
        if (SevenZipRunner.IsSuccess(RunSevenZip(new string[] { "t", targetPath, "-p", "-y" }).ExitCode))
        {
            throw new InvalidOperationException("字典口令夹具构造失败：空口令竟然解开了");
        }
    }

    // 带导入字典跑一遍（Finding 4 的「导入字典」那一半）。
    public static RunSummary RunExtractWithDictionary(string archive, string[] dictLines)
    {
        RunOptions options = NewOptions(null, 10, false, 2);
        options.DictLines = new List<string>(dictLines);
        return new Extractor(options, new DriveSpaceProvider(), null).Run(new string[] { archive });
    }
}
