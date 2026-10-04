// Task 13：EngineLocator（解压引擎定位）的系统测试。
//
// 【这组用例要钉住的性质】
//   * 本机 7-Zip（≥ 25.00）优先，找不到或低于下限就用**内嵌便携版**兜底 —— 那正是
//     「无 7-Zip 的干净机器上双击即用」这唯一一条承诺的全部依据；
//   * 每一步都做**功能性自检**（真跑 `7z i`），不是「文件存在就算数」：
//     一段文本冒充的 7z.exe 必须被跳过，而不是被拿去解压用户的包；
//   * 版本下限 25.00 是**行为**而不只是常量：一个自报 24.09 的本机副本必须被拒、并回落到别的引擎
//     （这条用例拿一份**改造过版本串的副本**来构造前提，见 OldLocalCandidateIsRejectedAndDegraded）；
//   * 内嵌副本落盘前/每次使用前都按**构建时写死的 SHA-256** 校验：盘上那份被改写（模拟 AV 隔离/
//     替换）时必须重新释放，绝不将就跑一个未经验证的二进制；
//   * 释放根是 %LOCALAPPDATA%\Rerar\bin\<buildid>\，**绝不是 %TEMP%**；测试用进程级环境变量
//     RERAR_ENGINE_ROOT 把它指到临时目录 —— 测试一次都不写真实的用户应用数据；
//   * 释放失败必须是一条**可操作的中文判词**（指出路径 + 下一步），不是崩溃、更不是「照常继续」。
//
// 【两条 harness 约定，踩到就是静默假通过或编译错】
//   * 参数表**必须带 -p**：SevenZipRunner 硬拒不带 -p 前缀项的参数表（不变式 I5）——计划示例里的
//     `new[]{ "i" }` 会当场抛 ArgumentException，所以本文件一律走 Run7z()（它统一补 -p）；
//   * 哈希一律比**十六进制字符串**，绝不把 byte[] 交给 AssertEq（那是引用比较，永远不等）。
//
// 【跳过纪律（最终修复轮 Finding 2 + 复审 Minor）】本文件里的 H.Skip 只允许出现在「本机真的造不出
// 前提」的地方（例如某份待改造的 7z.exe 字节里找不到版本串、或探测位置 dist\7z.exe 已被上一次被杀掉
// 的运行占着 —— ledger M9）。「本机 7-Zip 优先」这条性质**不能**被跳掉，但也**不能**去读主机布局：
// 它把一份已知可用的本机 7z.exe 种到 EngineLocator 真的会探测的第 1 步位置再断言优先选中
//（TestEnv.SevenZip 会探 PATH / %LOCALAPPDATA%，EngineLocator 刻意不探，直接比会把环境差异
// 报成产品回归）。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Rerar.Core;

internal sealed class EngineLocatorTests : TestBase
{
    // 内嵌载荷的精确字节数：7z.exe 576,512 + 7z.dll 1,908,736（本机 7-Zip 26.01 实测）。
    // 应用目标的体积下限与测试目标的体积上限都拿它当基线：一个「胖了」、一个「绝不能胖」。
    private const long EmbeddedPayloadBytes = 576512L + 1908736L;

    private const int Megabyte = 1024 * 1024;

    public static void Run()
    {
        // ------------------------------------------------------------------
        // brief Step 1 的四条（名字逐字来自 task-13-brief.md）
        // ------------------------------------------------------------------

        // 计划给的形状。注意参数表带 -p（见文件头：不带 -p 会抛 ArgumentException）。
        H.Run("Engine.LocalEngineIsFoundAndFunctional", delegate {
            EngineInfo e = EngineLocator.Resolve();

            AssertTrue(File.Exists(e.Path));
            AssertTrue(e.Version != null);

            // 功能性自检，而不是只比版本号：真跑一次 `7z i` 并确认退出码 0。
            RunResult r = Run7z(e.Path, "i");
            AssertEq(r.ExitCode, 0);
            AssertTrue(r.StdOut.IndexOf("7-Zip ", StringComparison.Ordinal) >= 0);
        });

        H.Run("Engine.PrefersLocalWhenAtLeast2500", delegate {
            // 【前提必须是确定性的】（最终修复轮 Minor）TestEnv.SevenZip 会去
            // %LOCALAPPDATA%\Programs\7-Zip 与 PATH 找引擎，而 EngineLocator **刻意**只探规格里那
            // 5 步（程序目录 / 两个 Program Files / 注册表两视图）。在一台「只有 PATH 或
            // %LOCALAPPDATA% 上有 7-Zip」的机器上，harness 找得到、EngineLocator 却合理地回落到
            // 内嵌 —— 于是这条用例 FAIL，而 FAIL 会落在产品断言上、指着错的原因。
            // 修法：把一份**已知可用**的本机 7z.exe 种到 EngineLocator 真的会探测的位置（第 1 步：
            // exe 同目录 = dist\，与 OldLocalCandidateIsRejectedAndDegraded 同一手法），再断言它被
            // 优先选中。这仍是真断言：本机探测路径一坏（第 1 步不再被探、功能性自检失效、内嵌优先），
            // 它就 FAIL；只是不再因为与本用例无关的主机布局而假失败。
            string source = TestEnv.SevenZip;         // harness 保证存在（Harness.Main 的预检）
            AssertTrue(File.Exists(source));

            string victim = Path.Combine(AppDirectory(), "7z.exe");
            if (File.Exists(victim))
            {
                H.Skip("Engine.PrefersLocalWhenAtLeast2500",
                    "探测位置 " + victim + " 上已经有文件：本用例不覆盖别人的 7z.exe");
                return;
            }

            try
            {
                File.Copy(source, victim, false);

                // 先把「种下去的这份确实可跑、且自报 ≥ 25.00」这条**环境**前提钉死：不成立时报出的
                // 是这一条，而不是让下面那几条产品断言替环境背锅。
                RunResult localProbe = Run7z(victim, "i");
                AssertEq(localProbe.ExitCode, 0);
                AssertTrue(EngineLocator.MeetsVersionFloor(EngineLocator.ParseVersion(localProbe.StdOut)));

                EngineInfo e = EngineLocator.Resolve();

                // 产品性质：第 1 步（exe 同目录）那份本机候选必须被优先选中，绝不回落到内嵌。
                AssertFalse(e.IsEmbedded);
                AssertTrue(string.Equals(e.Path, victim, StringComparison.OrdinalIgnoreCase));
                AssertTrue(e.Version >= EngineLocator.MinimumVersion);

                // 自报的版本必须与真跑一次得到的一致（EngineInfo.Version 不是编出来的）。
                AssertEq(e.Version, EngineLocator.ParseVersion(Run7z(e.Path, "i").StdOut));

                // 用的是本机那份，绝不是我们从资源里释放出来的内嵌副本。
                AssertFalse(IsUnder(e.Path, EngineLocator.EmbeddedRoot));
                AssertFalse(IsUnder(e.Path, Path.GetTempPath()));
            }
            finally
            {
                // dist 是构建产物目录：本用例放进去的东西必须原样收走（后面的两条用例也依赖这一点）。
                try { if (File.Exists(victim)) { File.Delete(victim); } }
                catch (Exception) { }
            }
        });

        H.Run("Engine.FallsBackWhenForcedEmbedded", delegate {
            EngineInfo e = EngineLocator.ResolveLocalDisabled();

            AssertTrue(e.IsEmbedded);
            AssertTrue(File.Exists(e.Path));
            AssertTrue(e.Version >= EngineLocator.MinimumVersion);

            // 释放根是**覆盖后的根**（测试环境 = 临时目录）：这条断言同时钉住了
            //「内嵌副本不会写进真实的 %LOCALAPPDATA%\Rerar\bin」与「绝不用 %TEMP%」（默认根见
            // EmbeddedRootDefaultsToLocalAppData）。
            AssertTrue(IsUnder(e.Path, EngineLocator.EmbeddedRoot));

            // 7z.dll 必须在同一个目录里：老版本 7z.exe 需要它才能工作，这正是我们**两个都内嵌**的原因。
            AssertTrue(File.Exists(Path.Combine(Path.GetDirectoryName(e.Path), "7z.dll")));
        });

        H.Run("Engine.EmbeddedHashMatches", delegate {
            EngineInfo e = EngineLocator.ResolveLocalDisabled();

            // 比十六进制字符串，绝不比 byte[]（byte[] 的 AssertEq 是引用比较）。
            AssertEq(EngineLocator.Sha256Of(e.Path), EngineLocator.EmbeddedSha256);
            AssertEq(
                EngineLocator.Sha256Of(Path.Combine(Path.GetDirectoryName(e.Path), "7z.dll")),
                EngineLocator.EmbeddedDllSha256);
        });

        H.Run("Engine.EmbeddedBinaryIsFunctional", delegate {
            EngineInfo e = EngineLocator.ResolveLocalDisabled();

            AssertEq(Run7z(e.Path, "i").ExitCode, 0);

            // 规格 §6.2 要的是「版本 + 一次真实列目录」：内嵌副本必须真的能读一个真归档的清单
            //（只报版本号不足以证明它能把用户的包打开）。
            RunResult listed = Run7z(e.Path, "l", "-slt", TestEnv.NestedZip, "-y");
            AssertEq(listed.ExitCode, 0);
            AssertTrue(listed.StdOut.IndexOf("readme.txt", StringComparison.Ordinal) >= 0);
        });

        // ------------------------------------------------------------------
        // 哈希校验与「每次执行前重新校验」的实际效果
        // ------------------------------------------------------------------

        H.Run("Engine.TamperedEmbeddedPayloadIsReExtracted", delegate {
            EngineInfo first = EngineLocator.ResolveLocalDisabled();
            long originalSize = new FileInfo(first.Path).Length;

            // 模拟 AV 隔离/改写：把盘上那份 7z.exe 换成一段长度也不对的垃圾。
            File.WriteAllBytes(first.Path, new byte[16]);
            AssertFalse(string.Equals(EngineLocator.Sha256Of(first.Path), EngineLocator.EmbeddedSha256,
                StringComparison.OrdinalIgnoreCase));

            // 下一次解析必须**重新释放**，且释放出来的哈希与构建时常量一致 ——
            // 绝不把「哈希对不上」降级成「凑合用」。
            EngineInfo second = EngineLocator.ResolveLocalDisabled();
            AssertTrue(second.IsEmbedded);
            AssertEq(second.Path, first.Path);                       // 落点是确定的（同一个 buildid 目录）
            AssertEq(EngineLocator.Sha256Of(second.Path), EngineLocator.EmbeddedSha256);
            AssertEq(new FileInfo(second.Path).Length, originalSize);
            AssertEq(Run7z(second.Path, "i").ExitCode, 0);
        });

        // ------------------------------------------------------------------
        // 版本下限：常量边界 + 真实横幅解析 + 行为
        // ------------------------------------------------------------------

        H.Run("Engine.VersionFloorRejectsOlderBuilds", delegate {
            // 真实横幅（本机 7-Zip 26.01 实测输出，逐字固定；与机器上装没装 7-Zip 无关）。
            Version real = EngineLocator.ParseVersion(
                "\n7-Zip 26.01 (x64) : Copyright (c) 1999-2026 Igor Pavlov : 2026-04-27\n" +
                "\nLibs:\n 0 : 26.01 : C:\\Program Files\\7-Zip\\7z.dll\n");
            AssertEq(real, new Version(26, 1));
            AssertTrue(EngineLocator.MeetsVersionFloor(real));

            // 下限 25.00 的边界：24.09 / 24.99 必须被拒（那之前的版本带已被在野利用的
            // 符号链接目录穿越问题），25.00 起放行。
            AssertEq(EngineLocator.MinimumVersion, new Version(25, 0));
            AssertFalse(EngineLocator.MeetsVersionFloor(new Version(24, 9)));
            AssertFalse(EngineLocator.MeetsVersionFloor(new Version(24, 99)));
            AssertTrue(EngineLocator.MeetsVersionFloor(new Version(25, 0)));
            AssertTrue(EngineLocator.MeetsVersionFloor(new Version(25, 1)));
            AssertFalse(EngineLocator.MeetsVersionFloor(null));

            // 报给用户的版本写法必须与 7-Zip 自己的命名一致（26.01 而不是 26.1：
            // 用户按 "26.1" 在 7-zip.org 上是找不到对应版本的）。
            AssertEq(EngineLocator.FormatVersion(new Version(26, 1)), "26.01");
            AssertEq(EngineLocator.FormatVersion(new Version(25, 0)), "25.00");
            AssertEq(EngineLocator.FormatVersion(new Version(24, 9)), "24.09");

            // 「报不出可解析的版本」= 不可信 = 拒绝：绝不把「不是 7-Zip 的东西」认成 7-Zip。
            AssertTrue(EngineLocator.ParseVersion(null) == null);
            AssertTrue(EngineLocator.ParseVersion("这不是 7-Zip，只是一段文本。\n") == null);
            AssertTrue(EngineLocator.ParseVersion("Formats:\n 0 C...F.......... 7z 7z\n") == null);
            // 只在**行首**的 7-Zip 横幅上取版本，绝不在整段输出里乱找数字。
            AssertTrue(EngineLocator.ParseVersion("Copyright (c) Igor Pavlov 7-Zip 26.01\n") == null);
            // 横幅在、版本串不合法 ⇒ 依然判不可信。
            AssertTrue(EngineLocator.ParseVersion("7-Zip not-a-version (x64) : Copyright\n") == null);
        });

        H.Run("Engine.OldLocalCandidateIsRejectedAndDegraded", delegate {
            // 前提：把一份**自报 24.09** 的 7z.exe 放到探测第 1 步的位置（exe 同目录）。
            // 造法：复制内嵌的 7z.exe，把横幅里的 ASCII 版本串（实测 26.01）改成同长度的 24.09 ——
            // 于是它仍然是一个**真能跑、也真自报 24.09** 的 7-Zip，而不是一段坏掉的字节。
            string victim = Path.Combine(AppDirectory(), "7z.exe");
            if (File.Exists(victim))
            {
                H.Skip("Engine.OldLocalCandidateIsRejectedAndDegraded",
                    "探测位置 " + victim + " 上已经有文件：本用例不覆盖别人的 7z.exe");
                return;
            }

            string caseName = "Engine.OldLocalCandidateIsRejectedAndDegraded";
            try
            {
                EngineInfo embedded = EngineLocator.ResolveLocalDisabled();
                string current = FormatVersion(embedded.Version);      // 例如 "26.01"
                File.Copy(embedded.Path, victim, false);

                if (!PatchAscii(victim, current, "24.09"))
                {
                    H.Skip(caseName, "无法在这份 7z.exe 的字节里找到版本串 " + current +
                        "：构造不出「本机引擎低于下限」这个前提");
                    return;
                }

                // 前提自检：改造出来的副本必须真的跑得起来、并**如实自报 24.09**。
                // 否则本用例什么也证明不了（那只是一份坏二进制，会被「跑不起来」拒掉）。
                RunResult probe = Run7z(victim, "i");
                Version reported = EngineLocator.ParseVersion(probe.StdOut);
                if (probe.ExitCode != 0 || reported == null || reported.Major != 24 || reported.Minor != 9)
                {
                    H.Skip(caseName, "改造出来的副本没有如实自报 24.09（退出码 " + probe.ExitCode +
                        "，版本 " + (reported == null ? "解析不出" : reported.ToString()) +
                        "）：构造不出「本机引擎低于下限」这个前提");
                    return;
                }

                // 真正的断言：低于下限的本机副本必须被拒，引擎必须落到**别处且可用**。
                EngineInfo e = EngineLocator.Resolve();
                AssertFalse(string.Equals(e.Path, victim, StringComparison.OrdinalIgnoreCase));
                AssertTrue(e.Version >= EngineLocator.MinimumVersion);
                AssertEq(Run7z(e.Path, "i").ExitCode, 0);
            }
            finally
            {
                // dist 是构建产物目录：本用例放进去的东西必须原样收走。
                try { if (File.Exists(victim)) { File.Delete(victim); } }
                catch (Exception) { }
            }
        });

        H.Run("Engine.UnrunnableLocalCandidateIsSkipped", delegate {
            // 探测第 1 步位置上一段**根本不是可执行文件**的文本：必须被跳过（功能性自检的作用），
            // 绝不能被拿去解压用户的包。
            string victim = Path.Combine(AppDirectory(), "7z.exe");
            if (File.Exists(victim))
            {
                H.Skip("Engine.UnrunnableLocalCandidateIsSkipped",
                    "探测位置 " + victim + " 上已经有文件：本用例不覆盖别人的 7z.exe");
                return;
            }

            try
            {
                File.WriteAllText(victim, "这不是 7-Zip，只是一段文本。\r\n", new UTF8Encoding(false));

                EngineInfo e = EngineLocator.Resolve();
                AssertFalse(string.Equals(e.Path, victim, StringComparison.OrdinalIgnoreCase));
                AssertTrue(File.Exists(e.Path));
                AssertEq(Run7z(e.Path, "i").ExitCode, 0);
            }
            finally
            {
                try { if (File.Exists(victim)) { File.Delete(victim); } }
                catch (Exception) { }
            }
        });

        // ------------------------------------------------------------------
        // 释放失败 = 可操作的中文判词（绝不是崩溃，也绝不是「照常继续」）
        // ------------------------------------------------------------------

        H.Run("Engine.ExtractionFailureGivesActionableChineseError", delegate {
            // 把释放根指到一个**不可能创建**的路径：父路径是一个普通文件。
            string blocker = TestEnv.MakeFile("engine-blocker.txt", "not a directory");
            string badRoot = Path.Combine(blocker, "bin");
            string previous = Environment.GetEnvironmentVariable(EngineLocator.EngineRootVariable);
            try
            {
                Environment.SetEnvironmentVariable(EngineLocator.EngineRootVariable, badRoot);

                string message = null;
                try { EngineLocator.ResolveLocalDisabled(); }
                catch (InvalidOperationException ex) { message = ex.Message; }

                AssertTrue(message != null);                                   // 必须抛，绝不返回路径
                AssertTrue(message.IndexOf("内嵌", StringComparison.Ordinal) >= 0);
                AssertTrue(message.IndexOf(badRoot, StringComparison.OrdinalIgnoreCase) >= 0);  // 指明路径
                AssertTrue(message.IndexOf("白名单", StringComparison.Ordinal) >= 0);            // 下一步
            }
            finally
            {
                Environment.SetEnvironmentVariable(EngineLocator.EngineRootVariable, previous);
            }
        });

        // ------------------------------------------------------------------
        // 释放根的默认值 / 构建产物的体积契约
        // ------------------------------------------------------------------

        H.Run("Engine.EmbeddedRootDefaultsToLocalAppData", delegate {
            string previous = Environment.GetEnvironmentVariable(EngineLocator.EngineRootVariable);
            try
            {
                Environment.SetEnvironmentVariable(EngineLocator.EngineRootVariable, null);   // 真的走默认分支
                string root = EngineLocator.EmbeddedRoot;

                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                AssertEq(root, Path.Combine(localAppData, "Rerar", "bin"));

                // 规格 §6.2：内嵌副本**绝不**释放到 %TEMP%（杀软最敏感的落地路径，且会被清理器删掉）。
                AssertFalse(IsUnder(root, Path.GetTempPath()));
                AssertTrue(root.IndexOf("Temp", StringComparison.OrdinalIgnoreCase) < 0);
            }
            finally
            {
                Environment.SetEnvironmentVariable(EngineLocator.EngineRootVariable, previous);
            }
        });

        H.Run("Engine.PayloadEmbeddedInAppTargetOnly", delegate {
            // 应用目标：内嵌载荷（≈ 2.37 MB）必须在里面，且整体不超过 5 MB（规格 §9.2 第 6 条）。
            // 「载荷真的在里面」由上面几条内嵌用例证明（它们是从 dist\Rerar.exe 里读出资源来的）；
            // 这条只管体积契约。
            FileInfo app = new FileInfo(TestEnv.ExePath);
            AssertTrue(app.Exists);
            AssertTrue(app.Length >= EmbeddedPayloadBytes);
            AssertTrue(app.Length <= 5L * Megabyte);

            // 测试目标**绝不能跟着胖**：build\build.ps1 的 /resource: 只给应用目标
            //（放进两个目标共享的参数数组里就会让 dist\tests.exe 白胖 2.4 MB）。
            FileInfo tests = new FileInfo(TestEnv.TestsExePath);
            AssertTrue(tests.Exists);
            AssertTrue(tests.Length < EmbeddedPayloadBytes);
        });

        H.Run("Engine.CliReportsVersionAndSourcePath", delegate {
            // 规格 §6.2：实际使用的版本与来源路径要能显示给用户 —— CLI 无头面同样如实报出。
            CliResult r = TestEnv.RunCli("--target", TestEnv.NestedZip);
            AssertEq(r.ExitCode, 0);

            AssertTrue(r.StdOut.IndexOf("解压引擎：", StringComparison.Ordinal) >= 0);
            AssertTrue(r.StdOut.IndexOf("7-Zip ", StringComparison.Ordinal) >= 0);
            AssertTrue(r.StdOut.IndexOf("7z.exe", StringComparison.OrdinalIgnoreCase) >= 0);
            AssertTrue(r.StdOut.IndexOf("本机安装", StringComparison.Ordinal) >= 0 ||
                       r.StdOut.IndexOf("内置便携版", StringComparison.Ordinal) >= 0);
        });

        // ------------------------------------------------------------------
        // 端到端：真进程 + 只用内置引擎（「无 7-Zip 的干净机器」那条承诺）
        // ------------------------------------------------------------------

        H.Run("Engine.CliEndToEndWithEmbeddedEngineOnly", delegate {
            // RERAR_ENGINE_LOCAL=off 关掉全部本机探测 ⇒ 真正随包发出的 dist\Rerar.exe 只能走内嵌兜底。
            // 这是那条承诺唯一够得着其主体的证明方式：进程内的 ResolveLocalDisabled() 够不着子进程，
            // 而「双击即用」说的正是那个子进程。
            CliResult r = TestEnv.RunCliWithLocalEngineDisabled("--target", TestEnv.NestedZip);

            AssertEq(r.ExitCode, 0);                                                   // 整批成功
            AssertTrue(r.StdOut.IndexOf("内置便携版", StringComparison.Ordinal) >= 0);  // 用的确实是内嵌副本
            AssertTrue(r.StdOut.IndexOf("解压引擎：7-Zip ", StringComparison.Ordinal) >= 0);

            // 释放根也被指到了临时目录（用例传给子进程的环境变量生效了）：真实的 %LOCALAPPDATA% 一个字节没写。
            AssertTrue(r.StdOut.IndexOf(TestEnv.EngineRoot, StringComparison.OrdinalIgnoreCase) >= 0);

            // 解压**真的**做完了（不是「引擎起来了但什么也没干」）。
            AssertTrue(File.Exists(TestEnv.InPlaceOutOf(TestEnv.NestedZip, "readme.txt")));
            AssertTrue(File.Exists(TestEnv.InPlaceOutOf(TestEnv.NestedZip, "inner", "hello.txt")));
        });

        H.Run("Engine.CliFailsWithActionableChineseErrorWhenNoEngine", delegate {
            // 本机探测关掉 + 释放根不可写 ⇒ 一个引擎都用不上。此时必须是「退出码 2 + 可操作的中文
            // 判词 + 一个字节都不写」，绝不是崩溃，更不是硬着头皮拿一个没验证过的引擎去解压。
            CliResult r = TestEnv.RunCliWithNoUsableEngine("--target", TestEnv.NestedZip);

            AssertEq(r.ExitCode, 2);                                  // 致命：什么都没跑成
            AssertTrue(r.Output.IndexOf("内嵌", StringComparison.Ordinal) >= 0);
            AssertTrue(r.Output.IndexOf("白名单", StringComparison.Ordinal) >= 0);       // 下一步是什么
            AssertFalse(File.Exists(TestEnv.InPlaceOutOf(TestEnv.NestedZip, "readme.txt")));
        });
    }

    // ------------------------------------------------------------------
    // 本文件自己的小工具
    // ------------------------------------------------------------------

    // 跑一次 7-Zip：参数表末尾统一补 -p（不变式 I5 逼出来的；`7z i` / `7z l` 会忽略它）。
    private static RunResult Run7z(string sevenZip, params string[] args)
    {
        List<string> argv = new List<string>();
        if (args != null) { argv.AddRange(args); }
        argv.Add("-p");
        return SevenZipRunner.Run(sevenZip, argv.ToArray(), null, CancellationToken.None);
    }

    // 「版本号 → 7-Zip 横幅里的写法」：产品自己提供（EngineLocator.FormatVersion，CLI 输出用的是
    // 同一个函数）。构造「自报老版本」的副本时要按 7-Zip 的写法（26.01）去字节里找版本串。
    private static string FormatVersion(Version version)
    {
        return EngineLocator.FormatVersion(version);
    }

    // 把文件里的 ASCII 串 from 原地替换成同长度的 to（构造「自报老版本」的副本用）。
    // 找不到任何一处（或长度不等）返回 false —— 调用方据此记 SKIPPED，绝不假装构造成功。
    private static bool PatchAscii(string path, string from, string to)
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || from.Length != to.Length) { return false; }

        byte[] bytes = File.ReadAllBytes(path);
        byte[] needle = new ASCIIEncoding().GetBytes(from);
        byte[] replacement = new ASCIIEncoding().GetBytes(to);
        if (needle.Length != replacement.Length) { return false; }

        bool replaced = false;
        for (int i = 0; i + needle.Length <= bytes.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (bytes[i + j] != needle[j]) { match = false; break; }
            }
            if (!match) { continue; }

            for (int j = 0; j < replacement.Length; j++) { bytes[i + j] = replacement[j]; }
            replaced = true;
        }

        if (!replaced) { return false; }
        File.WriteAllBytes(path, bytes);
        return true;
    }

    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) { return false; }

        string full = Path.GetFullPath(path).TrimEnd('\\');
        string fullRoot = Path.GetFullPath(root).TrimEnd('\\');
        return full.StartsWith(fullRoot + "\\", StringComparison.OrdinalIgnoreCase);
    }

    // 正在运行的可执行文件所在目录（测试里就是 dist\）。
    private static string AppDirectory()
    {
        return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    }
}
