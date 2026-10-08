// Rerar 的解压引擎定位（规格 §6.2）。Task 13 落地，取代 Program.cs 里那个临时替身。
//
// 【这个类为什么存在】规格承诺「无 7-Zip 的干净机器上双击即用」：本工具**每一个**归档操作都要一个
// 7z.exe，而目标机器上很可能一个都没有。所以本类按固定顺序找一个**经过功能性自检**的 7-Zip，
// 全找不到就用内嵌在这颗 exe 里的便携版兜底 —— 那是「单文件、双击即用」这唯一一条承诺的全部依据。
//
// 【探测顺序（规格 §6.2，逐步短路，先找到先用）】
//   1. exe 同目录下的 7z.exe
//   2. %ProgramFiles%\7-Zip\7z.exe
//   3. %ProgramFiles(x86)%\7-Zip\7z.exe
//   4. 注册表 HKLM\SOFTWARE\7-Zip\Path —— **显式**读 64 位视图与 WOW6432Node（32 位）视图：
//      64 位进程默认看不见 32 位键，反之亦然，只读一个视图就会在「另一个位数装的 7-Zip」上漏掉它。
//   5. 内嵌便携版，释放到 %LOCALAPPDATA%\Rerar\bin\<buildid>\
//
// 【每一步都做功能性自检，而不是「文件存在就算数」】判据是 `7z i` **退出码 0** 且它能报出一个可
// 解析的版本号：一个被 AV 截断、缺 DLL、或者根本不是 7-Zip 的同名文件都会在这一步被拒掉。
// 只查 File.Exists 等于把「跑一个没验证过的二进制」当成成功 —— 而它接过的是**用户不可信的压缩包**。
//
// 【版本下限 25.00】更早的 7-Zip 带已被在野利用的符号链接目录穿越问题，本工具解的又恰恰是不可信
// 的包。低于下限的本机副本一律拒绝并回落到内嵌副本；内嵌副本在**构建时**就被 build\build.ps1 卡在
// 同一下限上（构建脚本自己跑一次 `7z i` 校验，低于下限直接不构建）。实际使用的版本与来源路径由调用
// 方如实报给用户（Program.cs 的无头输出 / Task 14 的界面），绝不闷着用。
//
// 【内嵌副本的落点与校验】
//   * 落点 %LOCALAPPDATA%\Rerar\bin\<buildid>\，**绝不用 %TEMP%**：那是杀软最敏感的「落地并执行」
//     路径，也是临时清理器会随手删掉的地方（下次运行就得多释放一次）。
//   * 7z.exe 与 7z.dll **都**内嵌：老版本 7z.exe 需要同目录的 7z.dll 才能工作，只内嵌 exe 会在那些
//     版本上悄悄退化（26.01 实测可以独立跑，但我们的兜底不能压在某一个版本的脾气上）。
//   * 两份文件的期望 SHA-256 在**构建时**写死成程序常量（见 build\build.ps1 生成的
//     EmbeddedEngine.g.cs 里的 EmbeddedSha256 / EmbeddedDllSha256）。**每一次解析（也就是每一次
//     执行流水线之前）都重新校验盘上那份的实际哈希**：被 AV 隔离、被截断、被替换都会当场暴露，
//     而不会被当成「引擎可用」。
//   * 写入一律先落同目录的临时名 → 校验哈希 → File.Move 改名到位（原子）；已存在且哈希一致则直接
//     复用，不重写（省掉每次启动 ~2.4 MB 的写入，也让杀软少一次「新文件落地」）。
//
// 【不提权、不持久化】只读注册表、只写用户自己的 %LOCALAPPDATA%；不碰 Run 键、服务、计划任务。
// 释放失败时抛的是**可操作的中文判词**（指出路径 + 下一步），绝不静默降级成「凑合用」。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Rerar.Core
{
    // 一次引擎定位的结果（brief 的形状：public 字段，不是属性）。
    public sealed class EngineInfo
    {
        public string Path;        // 可执行的 7z.exe 绝对路径
        public Version Version;    // 该二进制**自报**的真实版本（不是我们猜的）
        public bool IsEmbedded;    // true = 用的是内嵌便携版（从这颗 exe 的资源里释放出来的那份）
    }

    public static partial class EngineLocator
    {
        // 内嵌副本释放根的进程级环境变量覆盖。与 Task 12 的 RERAR_JOURNAL_ROOT 同一套做法：
        //   * 测试用它把释放根指到临时目录 —— 真实的 %LOCALAPPDATA%\Rerar\bin 是**用户的应用数据**，
        //     测试一次都不该往里写（内嵌分支的用例要释放 ~2.4 MB）；
        //   * CLI 用例起的是**子进程**，只有环境变量会被子进程继承（进程内的静态接缝不行），
        //     没装 7-Zip 的机器上那些用例会真的走释放路径；
        //   * 它同时是「可移植 / 调试运行」的正规用法：把便携引擎固定放在某个目录。
        // 未设置（或为空串）时行为与从前**完全一致**：%LOCALAPPDATA%\Rerar\bin。
        public const string EngineRootVariable = "RERAR_ENGINE_ROOT";

        // 「跳过全部本机探测、只用内嵌便携版」的进程级开关（值取 off / 0 / false 时生效，其余值
        // 与未设置同义）。它与 ResolveLocalDisabled() 走的是**同一条**分支，存在的理由有两条：
        //   * 验收要能**端到端**证明「无 7-Zip 的机器上双击即用」这条承诺 —— 只在进程内把本机探测
        //     关掉够不着真正随包发出的 dist\Rerar.exe（CLI 是独立进程），而那才是这条承诺的主体；
        //   * 它也是「本机那份 7-Zip 出了问题（被杀软改过、装了个半截）时的临时规避」，与
        //     RERAR_ENGINE_ROOT 同属可移植 / 排障用法。
        // 默认（未设置）行为与从前**完全一致**：本机 ≥ 25.00 优先，找不到（或低于下限）才用内嵌副本。
        public const string EngineLocalDisabledVariable = "RERAR_ENGINE_LOCAL";

        // 版本下限（规格 §6.2）：更早的版本带已在野利用的符号链接目录穿越问题。
        public static readonly Version MinimumVersion = new Version(25, 0);

        // 内嵌资源的名字（build\build.ps1 里 /resource:<文件>,<名字> 的那个名字）。
        public const string SevenZipResourceName = "sz.7z.exe";
        public const string SevenZipDllResourceName = "sz.7z.dll";

        // 探测用的 7-Zip 参数表。**必须带 -p**：SevenZipRunner 对没有 -p 前缀项的参数表直接抛
        // ArgumentException（不变式 I5 —— 原 .bat 就是在加密包上等 stdin 挂死的）。`7z i` 忽略密码
        // 开关，所以这一项无害；写成静态只读字段是为了让它不会被某次改动悄悄弄丢（探测里不带 -p
        // 会让每一个候选都「验证失败」，然后静默回落到内嵌副本 —— 表面上一切正常）。
        private static readonly string[] InfoArguments = new string[] { "i", "-p" };

        // ------------------------------------------------------------------
        // 对外入口
        // ------------------------------------------------------------------

        // 按规格 §6.2 的顺序定位引擎。全都不可用时抛 InvalidOperationException（中文、可操作），
        // **绝不**返回一个未经验证的路径。
        // RERAR_ENGINE_LOCAL=off 时跳过全部本机探测（与 ResolveLocalDisabled 同一条分支）。
        public static EngineInfo Resolve()
        {
            return ResolveCore(LocalProbingDisabledByEnvironment());
        }

        // 测试接缝（计划要求）：跳过全部本机探测，强制走内嵌兜底分支。
        public static EngineInfo ResolveLocalDisabled()
        {
            return ResolveCore(true);
        }

        // 进程级开关的解析。只认 off / 0 / false（大小写、首尾空白不敏感）；其余任何值（含未设置与
        // 空串）都按「没设」处理 —— 一个意外写错的值绝不该悄悄改变引擎来源。
        private static bool LocalProbingDisabledByEnvironment()
        {
            try
            {
                string value = Environment.GetEnvironmentVariable(EngineLocalDisabledVariable);
                if (string.IsNullOrEmpty(value)) { return false; }

                value = value.Trim();
                return string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(value, "0", StringComparison.Ordinal) ||
                       string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // 读环境变量都可能被宿主拒绝：那种情况下按默认行为（本机优先）继续，绝不因此失败。
                return false;
            }
        }

        // 内嵌副本的**有效**释放根：设了 RERAR_ENGINE_ROOT 就用它，否则 %LOCALAPPDATA%\Rerar\bin。
        // 单独暴露出来是为了让调用方/用例能如实说明「这次把副本释放到哪里去了」。
        public static string EmbeddedRoot
        {
            get
            {
                string overridden = Environment.GetEnvironmentVariable(EngineRootVariable);
                if (!string.IsNullOrEmpty(overridden)) { return overridden; }

                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(localAppData))
                {
                    throw new InvalidOperationException(
                        "无法确定 %LOCALAPPDATA%（内嵌 7-Zip 的释放根）：请设置环境变量 " + EngineRootVariable +
                        " 指向一个可写目录");
                }
                // **绝不**用 %TEMP%：那是杀软最敏感的「落地并执行」路径，也会被临时清理器删掉。
                return Path.Combine(localAppData, "Rerar", "bin");
            }
        }

        // 本次构建的载荷标识（内嵌副本的目录名）：由构建时写死的 exe 哈希前 16 位推出。
        // 内容寻址的好处是「同一份载荷永远落同一个目录」（可复用、不重写），载荷一换目录就换，
        // 于是旧目录里的残留绝不会被当成新载荷。
        public static string BuildId
        {
            get
            {
                string sha = EmbeddedSha256;
                if (string.IsNullOrEmpty(sha) || sha.Length < 16) { return "unknown"; }
                return sha.Substring(0, 16);
            }
        }

        // 版本是否达到下限（低于下限的本机副本一律拒绝）。null 视为不可信。
        public static bool MeetsVersionFloor(Version version)
        {
            return version != null && version >= MinimumVersion;
        }

        // 文件的 SHA-256（小写十六进制）。不返回 byte[]：哈希的比较点一律是**字符串**
        //（byte[] 的相等比较是引用比较，是个静默的假通过陷阱）。
        public static string Sha256Of(string path)
        {
            if (path == null) { throw new ArgumentNullException("path"); }

            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder text = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    text.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }
                return text.ToString();
            }
        }

        // 版本的**用户可见写法**：7-Zip 自己用「主版本.两位次版本」（26.01 / 24.09 / 25.00），而
        // Version.ToString() 会把 26.01 印成 "26.1"、24.09 印成 "24.9" —— 那是用户在下拉页里
        // 找不到的版本号。凡是要把版本如实报给用户的地方（CLI 输出、Task 14 的界面）都用这个写法。
        public static string FormatVersion(Version version)
        {
            if (version == null) { return ""; }
            return version.Major.ToString(CultureInfo.InvariantCulture) + "." +
                version.Minor.ToString("00", CultureInfo.InvariantCulture);
        }

        // 从 `7z i` 的输出里读出真实版本。实测 26.01 的开头是
        //     "7-Zip 26.01 (x64) : Copyright (c) 1999-2026 Igor Pavlov : 2026-04-27"
        // 只在「**行首**就是 7-Zip 横幅」的那一行上取版本，且版本串解析不出来就返回 null
        //（绝不在整段输出里乱找数字：格式列表里也有版本号，那种「找到就算」的解析会把一个不是
        // 7-Zip 的程序认成 7-Zip）。公开出来是为了让用例能拿真实横幅钉住这个解析。
        public static Version ParseVersion(string stdout)
        {
            if (stdout == null) { return null; }

            foreach (string rawLine in stdout.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) { continue; }
                if (!line.StartsWith("7-Zip ", StringComparison.Ordinal)) { continue; }

                int start = "7-Zip ".Length;
                int end = start;
                while (end < line.Length && !char.IsWhiteSpace(line[end])) { end++; }

                Version version;
                if (end > start && Version.TryParse(line.Substring(start, end - start), out version))
                {
                    return version;
                }
                // 横幅在、版本串不合法：不可信，直接判不可用（绝不退回去猜）。
                return null;
            }
            return null;
        }

        // ------------------------------------------------------------------
        // 定位主流程
        // ------------------------------------------------------------------

        private static EngineInfo ResolveCore(bool localDisabled)
        {
            List<string> tried = new List<string>();

            if (!localDisabled)
            {
                foreach (Candidate candidate in LocalCandidates())
                {
                    string reason;
                    EngineInfo info = Probe(candidate.Path, false, out reason);
                    if (info != null) { return info; }

                    // 逐个记下「找过哪里、为什么不用」：全都不行时这句话要原样进错误信息，
                    // 用户（或客服）据此就能判断该装什么、或者该把哪个目录加白名单。
                    tried.Add(candidate.Source + "（" + candidate.Path + "）：" + reason);
                }
            }

            return ResolveEmbedded(tried, localDisabled);
        }

        // 内嵌兜底分支：把两份载荷落到 %LOCALAPPDATA%\Rerar\bin\<buildid>\，校验哈希，然后功能性自检。
        // localDisabled 只影响**判词的措辞**：那两种情况（本机一个都没有 / 本次被要求跳过本机探测）
        // 的原因不同，绝不该用同一句话糊过去。
        private static EngineInfo ResolveEmbedded(List<string> tried, bool localDisabled)
        {
            string directory = Path.Combine(EmbeddedRoot, BuildId);
            string exe = Path.Combine(directory, "7z.exe");
            string dll = Path.Combine(directory, "7z.dll");

            // 1) 把两份文件落到位（已存在且哈希一致则复用；否则写临时名 → 校验 → 原子改名）。
            //    EnsurePayload 结束时一定会核一次**盘上**那份的哈希：这是「每次执行前校验」的落点。
            EnsurePayload(exe, SevenZipResourceName, EmbeddedSha256);
            EnsurePayload(dll, SevenZipDllResourceName, EmbeddedDllSha256);

            // 2) 功能性自检 + 版本下限。注意按构造这里走的还是同一个 Probe：内嵌副本也要真的能自报
            //    版本，而且不能低于下限（构建时会拦一道，这里是运行期的第二道）。
            string reject;
            EngineInfo info = Probe(exe, true, out reject);
            if (info == null)
            {
                string because = localDisabled
                    ? "（本次已按要求跳过本机 7-Zip 的探测：" + EngineLocalDisabledVariable + "）"
                    : "，本机也没有可用的 7-Zip" + TriedSuffix(tried);

                throw new InvalidOperationException(
                    "内嵌的 7-Zip 不可用（" + reject + "）：" + exe +
                    "。请把 " + directory + " 加入杀毒软件白名单后重试（该文件可能被隔离、截断或替换过）" +
                    because + "，因此本次无法解压。");
            }
            return info;
        }

        // ------------------------------------------------------------------
        // 候选清单（规格 §6.2 的前 4 步）
        // ------------------------------------------------------------------

        // 一个候选：路径 + 人读的来源说明（错误信息里要如实说明「找过哪里」）。
        private sealed class Candidate
        {
            public string Path;
            public string Source;
        }

        private static List<Candidate> LocalCandidates()
        {
            List<Candidate> candidates = new List<Candidate>();

            // 1) exe 同目录。这是「便携版放在程序旁边」的形状，也是最容易被忽略的一步。
            string appDirectory = AppDirectory();
            if (!string.IsNullOrEmpty(appDirectory))
            {
                AddCandidate(candidates, Path.Combine(appDirectory, "7z.exe"), "程序所在目录");
            }

            // 2) / 3) 两个 Program Files。
            AddCandidate(candidates, UnderFolder(Environment.SpecialFolder.ProgramFiles, @"7-Zip\7z.exe"),
                "Program Files");
            AddCandidate(candidates, UnderFolder(Environment.SpecialFolder.ProgramFilesX86, @"7-Zip\7z.exe"),
                "Program Files (x86)");

            // 4) 注册表：**两个视图都显式读**（见类头说明）。
            AddRegistryCandidate(candidates, RegistryView.Registry64,
                @"注册表 HKLM\SOFTWARE\7-Zip\Path（64 位视图）");
            AddRegistryCandidate(candidates, RegistryView.Registry32,
                @"注册表 HKLM\SOFTWARE\7-Zip\Path（32 位视图 / WOW6432Node）");

            return candidates;
        }

        private static void AddCandidate(List<Candidate> candidates, string path, string source)
        {
            if (string.IsNullOrEmpty(path)) { return; }

            // 去重：注册表两个视图经常给出同一个目录（也常与 Program Files 那条重复），
            // 而每一次重复探测都要多起一个 7z 进程。
            foreach (Candidate existing in candidates)
            {
                if (string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase)) { return; }
            }

            Candidate candidate = new Candidate();
            candidate.Path = path;
            candidate.Source = source;
            candidates.Add(candidate);
        }

        private static string UnderFolder(Environment.SpecialFolder folder, string relative)
        {
            string root = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(root)) { return null; }
            return Path.Combine(root, relative);
        }

        // 读注册表一个视图里的安装目录。读不出来（权限、键损坏、该视图在本机不存在、值类型不对）
        // 一律**静默跳过这一个视图**：本机副本只是优先项，真正保证可用性的是第 5 步的内嵌兜底 ——
        // 绝不能因为读不到注册表就把整件事判死。
        private static void AddRegistryCandidate(List<Candidate> candidates, RegistryView view, string source)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (RegistryKey key = baseKey.OpenSubKey(@"SOFTWARE\7-Zip"))
                {
                    if (key == null) { return; }
                    string installDirectory = key.GetValue("Path") as string;
                    if (string.IsNullOrEmpty(installDirectory)) { return; }

                    AddCandidate(candidates, Path.Combine(installDirectory, "7z.exe"), source);
                }
            }
            catch (Exception)
            {
                // 见上：不致命，继续下一个候选。
            }
        }

        // ------------------------------------------------------------------
        // 功能性自检
        // ------------------------------------------------------------------

        // 跑一次 `7z i` 并判定这个候选能不能用：
        //   * 起不来（不是可执行文件 / 缺依赖 / 被 AV 拦） → 不可用
        //   * 退出码非 0                                    → 不可用
        //   * 报不出可解析的版本号                          → 不可用（可能根本不是 7-Zip）
        //   * 版本低于下限 25.00                            → 不可用（安全理由，见类头）
        // 通过时返回 EngineInfo，否则返回 null 并把原因写进 rejectReason。
        private static EngineInfo Probe(string path, bool embedded, out string rejectReason)
        {
            rejectReason = null;

            bool exists;
            try
            {
                exists = File.Exists(path);
            }
            catch (Exception ex)
            {
                rejectReason = "路径不可读（" + ex.GetType().Name + "：" + ex.Message + "）";
                return null;
            }
            if (!exists) { rejectReason = "文件不存在"; return null; }

            RunResult result;
            try
            {
                result = SevenZipRunner.Run(path, InfoArguments, null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                rejectReason = "无法运行自检命令 `7z i`（" + ex.GetType().Name + "：" + ex.Message + "）";
                return null;
            }

            if (result.ExitCode != 0)
            {
                rejectReason = "`7z i` 退出码 " + result.ExitCode.ToString(CultureInfo.InvariantCulture) + "（应为 0）";
                return null;
            }

            Version version = ParseVersion(result.StdOut);
            if (version == null)
            {
                rejectReason = "`7z i` 的输出里没有可解析的 7-Zip 版本号（它可能根本不是 7-Zip）";
                return null;
            }
            if (!MeetsVersionFloor(version))
            {
                rejectReason = "版本 " + version + " 低于下限 " + MinimumVersion +
                    "（该版本存在已被利用的符号链接目录穿越问题）";
                return null;
            }

            EngineInfo info = new EngineInfo();
            info.Path = path;
            info.Version = version;
            info.IsEmbedded = embedded;
            return info;
        }

        // ------------------------------------------------------------------
        // 释放 + 哈希校验
        // ------------------------------------------------------------------

        // 保证 target 处存在一份内容 == expectedSha 的载荷：
        //   * 已存在且哈希一致 → 直接复用（**每次调用都真的算一遍哈希**，这就是「每次执行前校验」）；
        //   * 否则：从内嵌资源写到同目录的临时名 → 校验临时文件 → File.Move 原子改名 → 再校验盘上那份。
        // 任何失败都抛可操作的中文判词（绝不留下一个「名字对、内容不对」的文件，也绝不将就使用）。
        private static void EnsurePayload(string target, string resourceName, string expectedSha)
        {
            if (HashMatches(target, expectedSha)) { return; }

            string directory = Path.GetDirectoryName(target);
            string temporary = null;
            try
            {
                if (!Directory.Exists(directory)) { Directory.CreateDirectory(directory); }

                // 临时名带进程号：同一个目录被两个实例同时释放时不会互相踩（最后一个改名者赢，
                // 而两份内容本来就一模一样）。名字必须在**同一个目录**里，File.Move 才是原子的。
                temporary = target + ".tmp-" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);

                using (Stream source = OpenResource(resourceName))
                using (FileStream destination = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(destination);
                    destination.Flush(true);   // 先落盘再改名：崩溃/断电不会留下「名字对、内容是半截」
                }

                string actual = Sha256Of(temporary);
                if (!HashEquals(actual, expectedSha))
                {
                    throw new InvalidOperationException(
                        "内嵌 " + resourceName + " 的内容与构建时记录的哈希不一致（期望 " + expectedSha +
                        "，实际 " + actual + "）：程序文件可能已损坏，本次拒绝使用内嵌引擎");
                }

                // 目标可能是一个被 AV 换掉的坏文件：File.Move 在目标存在时会抛 IOException，
                // 所以先删掉它（这里的窗口很短，且紧接着的哈希校验会兜住任何意外）。
                if (File.Exists(target)) { File.Delete(target); }
                File.Move(temporary, target);
                temporary = null;
            }
            catch (InvalidOperationException)
            {
                throw;   // 上面的哈希不一致原样上抛：它已经是一条可操作的中文判词
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "无法把内嵌的 7-Zip 释放到 " + target + "（" + ex.GetType().Name + "：" + ex.Message +
                    "）。请确认该目录可写，并把 " + directory +
                    " 加入杀毒软件白名单后重试（杀软拦截是这一步最常见的失败原因）", ex);
            }
            finally
            {
                // 失败路径绝不留下半截临时文件（成功路径已经把 temporary 置回 null）。
                if (temporary != null)
                {
                    try { if (File.Exists(temporary)) { File.Delete(temporary); } }
                    catch (Exception) { }
                }
            }

            // 落位之后再核一次**盘上**那份：写入过程被截断/替换会在这里被抓住，
            // 而不是等到 7z 跑出莫名其妙的解压结果。
            if (!HashMatches(target, expectedSha))
            {
                throw new InvalidOperationException(
                    "内嵌 7-Zip 释放后校验失败：" + target + " 的哈希与构建时记录的不一致（期望 " + expectedSha +
                    "）。请把 " + directory + " 加入杀毒软件白名单后重试");
            }
        }

        private static bool HashMatches(string path, string expectedSha)
        {
            try
            {
                if (!File.Exists(path)) { return false; }
                return HashEquals(Sha256Of(path), expectedSha);
            }
            catch (Exception)
            {
                // 读不出来（被独占锁住 / 权限不足 / 路径畸形）就等于「不是我们要的那份」：
                // 走重新释放的路径，而不是把异常抛给「只是想解压一个包」的用户。
                return false;
            }
        }

        private static bool HashEquals(string actual, string expected)
        {
            if (actual == null || expected == null) { return false; }
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        // 打开内嵌资源。执行程序集里没有时回退到**同目录的 Rerar.exe**：
        // 测试目标（dist\tests.exe）刻意不含内嵌资源 —— build.ps1 的 /resource: 只给应用目标，
        // 否则每个测试 exe 都会白胖 ~2.4 MB（那是一条必须钉住的构建约束）。于是内嵌分支的用例读的
        // 是**真正随包发出**的那份资源，而不是为测试另备的副本。
        private static Stream OpenResource(string resourceName)
        {
            Assembly self = Assembly.GetExecutingAssembly();
            Stream stream = self.GetManifestResourceStream(resourceName);
            if (stream != null) { return stream; }

            string appExe = null;
            string appDirectory = AppDirectory();
            if (!string.IsNullOrEmpty(appDirectory)) { appExe = Path.Combine(appDirectory, "Rerar.exe"); }

            if (IsSameFile(self.Location, appExe))
            {
                // 已经在读自己了：没有第二个地方可找，直接给判词（避免把正在运行的程序集再加载一份）。
                throw new InvalidOperationException(
                    "本程序里没有内嵌资源 " + resourceName + "：这个 exe 不是用 build\\build.ps1 构建的，" +
                    "或者构建时没有内嵌 7-Zip（缺 /resource: 开关）");
            }

            Assembly app;
            try
            {
                app = Assembly.LoadFile(appExe);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "本程序里没有内嵌资源 " + resourceName + "，也无法从同目录的 Rerar.exe（" + appExe +
                    "）读取它（" + ex.GetType().Name + "：" + ex.Message + "）", ex);
            }

            stream = app.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new InvalidOperationException(
                    "内嵌资源 " + resourceName + " 既不在本程序里，也不在 " + appExe +
                    " 里：这个 exe 不是用 build\\build.ps1 构建的，或者构建时没有内嵌 7-Zip（缺 /resource: 开关）");
            }
            return stream;
        }

        private static bool IsSameFile(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) { return false; }

            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // 正在运行的可执行文件所在目录（探测第 1 步的基准）。BaseDirectory 为空时退回执行程序集的位置。
        private static string AppDirectory()
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(directory)) { return directory; }

            Assembly self = Assembly.GetExecutingAssembly();
            string location = self.Location;
            return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
        }

        // 「已尝试过的本机位置」的尾注（只在真的走到内嵌兜底且失败时出现）。
        private static string TriedSuffix(List<string> tried)
        {
            if (tried == null || tried.Count == 0) { return ""; }
            return " 已查找的本机位置：" + string.Join("；", tried.ToArray());
        }
    }
}
