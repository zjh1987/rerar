// Task 16：打包收尾的验收 —— 体积预算 / 冷启动预算 / 版本资源与图标的落点。
//
// 【这组用例要钉住的性质】
//   * dist\Rerar.exe 必须 ≤ 5 MB（规格 §9.2 第 6 条的全局约束；内嵌 7z.exe + 7z.dll 之后仍要守住）；
//   * --cli --selftest（冷启动）必须 < 2 s —— tests\acceptance.ps1 的 A06b 是同一预算的进程外口径；
//   * 版本信息**真的有内容**：公司/产品/描述/版权四个用户可见字段都非空（Properties 对话框
//     显示的就是它们）—— 这也是「从下载链接拿到一个未签名 exe 看起来正经而不是可疑」的底线；
//   * 版本号与程序集自报的一致：--selftest 打印 version=<n>（tests\smoke.ps1 的契约），
//     版本块里的 FileVersion / ProductVersion 必须是同一个数；
//   * 中英双语的字符串表都要真的编进产物：FileVersionInfo 只按 UI 语言挑一张表展示，
//     字节扫描才能看到两张都在；
//   * /win32res: 与 /win32icon: **只给应用目标** —— 与 /resource:（Task 13）、/win32manifest:
//    （Task 14）同一条规矩，测试产物绝不能跟着带上（用例与 Engine.PayloadEmbeddedInAppTargetOnly、
//     Gui.ManifestIsEmbeddedInAppTargetOnly 同一手法：直接扫两个产物的字节）。
//     反向探针用的串必须**运行时拼装**：写成常量会被编进 tests.exe 自己的元数据，反向断言
//     于是恒假（详见 Package.VersionInfoInAppTargetOnly 里的说明）。
//
// 【为什么字节扫描用的是「内容」而不是「结构」关键字】csc 对没有任何程序集特性的目标会**自动**
// 合成一个只有骨架的版本块（实测两个产物都有：VS_VERSION_INFO / StringFileInfo / '000004b0'
// 表、FileVersion=0.0.0.0，但 FileDescription/LegalCopyright 只是一个空格、根本没有
// CompanyName/ProductName）。结构关键字两个产物都命中，区分不了「我们的 .res 编进去了没有」；
// 能区分的只有我们亲手写进去的内容本身。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

internal sealed class PackageTests : TestBase
{
    // build\make-res.ps1 写进版本资源的中英文内容（名字与值都要与它保持一致；改那里必须改这里）。
    private const string ProductNameChinese = "Rerar 递归解压工具";
    private const string CompanyNameEnglish = "Rerar Project";
    private const string CopyrightEnglish = "Copyright (C) 2026 Rerar Project";

    public static void Run()
    {
        // ------------------------------------------------------------------
        // brief Step 3 的三条（名字逐字来自 task-16-brief.md）
        // ------------------------------------------------------------------

        H.Run("Package.ExeUnder5MB", delegate {
            AssertTrue(new FileInfo(TestEnv.ExePath).Length <= 5 * 1024 * 1024);
        });

        H.Run("Package.ColdStartUnder2s", delegate {
            Stopwatch sw = Stopwatch.StartNew();
            TestEnv.RunCli("--selftest");
            AssertTrue(sw.Elapsed.TotalSeconds < 2);
        });

        H.Run("Package.VersionInfoPresent", delegate {
            FileVersionInfo v = FileVersionInfo.GetVersionInfo(TestEnv.ExePath);

            // brief 的字面断言保留；但只靠它在本机是不够的：csc 会替没有任何程序集特性的目标
            // 自动合成一个骨架版本块（见文件头），ProductName 键干脆缺席 ⇒ FileVersionInfo 返回
            // **空串**而不是 null，上面的字面断言在没有版本资源时也会 PASS（空转）。
            // 真正的「有版本信息」是四个用户可见字段都非空 —— Properties 对话框显示的就是它们。
            AssertTrue(v.ProductName != null);
            AssertTrue(v.ProductName.Length > 0);
            AssertTrue(v.CompanyName != null && v.CompanyName.Length > 0);
            AssertTrue(v.FileDescription != null && v.FileDescription.Trim().Length > 0);
            AssertTrue(v.LegalCopyright != null && v.LegalCopyright.Trim().Length > 0);
        });

        // ------------------------------------------------------------------
        // 版本号与程序集一致；双语内容都在；资源只给应用目标
        // ------------------------------------------------------------------

        H.Run("Package.VersionInfoMatchesAssembly", delegate {
            FileVersionInfo v = FileVersionInfo.GetVersionInfo(TestEnv.ExePath);
            string assemblyVersion = AssemblyName.GetAssemblyName(TestEnv.ExePath).Version.ToString();

            // --selftest 打印的就是这个程序集版本（tests\smoke.ps1 靠它）。
            // 版本块若与程序集各报各的，用户的资源管理器与 --selftest 就会给出两个答案。
            AssertEq(v.FileVersion, assemblyVersion);
            AssertEq(v.ProductVersion, assemblyVersion);
        });

        H.Run("Package.VersionInfoCarriesChineseAndEnglish", delegate {
            // 双语对照是 brief 的硬要求（公司/产品/描述/版权中英文都要有）：两张语言表都必须
            // 真的在产物里。这里各挑一条**只在我们的 .res 里存在**的中英文串做字节级钉子。
            byte[] app = File.ReadAllBytes(TestEnv.ExePath);
            AssertTrue(ContainsUtf16(app, ProductNameChinese));
            AssertTrue(ContainsUtf16(app, CopyrightEnglish));
        });

        H.Run("Package.VersionInfoInAppTargetOnly", delegate {
            byte[] app = File.ReadAllBytes(TestEnv.ExePath);
            AssertTrue(ContainsUtf16(app, ProductNameChinese));
            AssertTrue(ContainsUtf16(app, CompanyNameEnglish));

            // /win32res:（版本块 + 清单 + 图标）与 /resource: 都只给应用目标
            // （build\build.ps1 的 $appSwitches）。测试产物里绝不能出现我们写入的版本内容。
            //
            // 【反向探针为什么在运行时现拼】上一版直接把上面的常量拿去扫 tests.exe，用例必挂：
            // 常量会被编进 tests.exe **自己的元数据**（UTF-16），扫到的正是探针本身。
            // 下面这两个串只存在于 build\make-res.ps1 生成的 .res 里；用两个局部量相加拼出来，
            // 编译器不会折叠成一整个字面量，tests.exe 里就不会有同样的连续字节。
            byte[] tests = File.ReadAllBytes(TestEnv.TestsExePath);
            AssertFalse(ContainsUtf16(tests, ChineseCopyrightNeedle()));
            AssertFalse(ContainsUtf16(tests, EnglishProductNameNeedle()));
        });
    }

    // 运行时拼装的反向探针（见上面 Package.VersionInfoInAppTargetOnly 的说明）。
    private static string ChineseCopyrightNeedle()
    {
        string head = "版权所有";
        string tail = " (C) 2026 Rerar 项目";
        return head + tail;
    }

    private static string EnglishProductNameNeedle()
    {
        string head = "Rerar Recursive ";
        string tail = "Extractor";
        return head + tail;
    }

    // UTF-16LE 子串扫描：版本资源里的字符串就是 UTF-16LE 存的。
    //（GuiProbe.ContainsAscii 是按 ASCII 扫的，对版本资源里的中文/宽字符串不适用，
    //  这里放一个本文件自己的窄实现，不做通用化 —— YAGNI。）
    private static bool ContainsUtf16(byte[] data, string needle)
    {
        if (data == null || string.IsNullOrEmpty(needle)) { return false; }

        byte[] pattern = new UnicodeEncoding(false, false).GetBytes(needle);
        for (int i = 0; i + pattern.Length <= data.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j]) { ok = false; break; }
            }
            if (ok) { return true; }
        }
        return false;
    }
}
