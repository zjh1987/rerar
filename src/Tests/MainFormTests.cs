// Task 14：GUI（界面形态 A）的自动化用例。
//
// 【覆盖策略】界面上**能用程序判定**的东西全部在这里钉住：控件树的名字与结构、危险开关的默认值、
// 二次确认对话框的默认按钮、日志尾部的有界性、密码的日志卫生、两阶段进度文案与 ETA 门槛、
// .lnk 解析、设置原子的读写与目录校验、字体回退、产物体积上限、清单是否真的编进了产物。
//
// 【故意不在这里的东西】拖放、DPI 缩放后的像素观感、消息循环下的帧率、模态确认的肉眼观感 ——
// 这些没有人在现场就用程序判不出真假，硬写只会写出**假测试**。它们由 task-14-report.md 的
// 人工验收章如实交代（用什么命令、看什么、结论）。
//
// 【走反射的理由】见 src\Tests\GuiProbe.cs 的文件头：测试目标按全局约束不引 WinForms，
// 而被测对象必须是 dist\Rerar.exe 里那个真实的 Form。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.IO;
using System.Text;

internal sealed class MainFormTests : TestBase
{
    // 给「编组」用例当靶子：委托被真的执行时把标记翻成 true（用例据此判断它有没有跑）。
    private sealed class Flag
    {
        public bool Ran;
        public void Mark() { Ran = true; }
    }

    public static void Run()
    {
        // ---- 工作线程回调：必须编组，且绝不能在窗体销毁后把异常抛回流水线 ----
        //
        // 【为什么这条最要紧】Task 3 的裁定：进度回调里抛出的异常会在 Extractor.Run 的调用点浮出来，
        // 也就是**掀翻整批解压**。界面这一侧有两个经典翻车方式：在工作线程上直接碰控件；
        // 以及窗体被关掉之后还去 Invoke（ObjectDisposedException 抛回工作线程）。这条用例同时钉住两者。
        H.Run("Gui.WorkerCallbacksNeverTouchTheFormAfterClose", delegate {
            GuiProbe.WithForm(delegate(object f) {
                // (1) 在 UI 线程上投递 ⇒ 就地执行（界面自己那条路径就靠它）。
                Flag onUiThread = new Flag();
                GuiProbe.Call(f, "UiPost", new object[] { GuiProbe.MakeInvoker(onUiThread, "Mark") });
                AssertTrue(onUiThread.Ran);

                // (2) 窗体销毁之后，从**工作线程**投递 ⇒ 静默丢弃：既不执行，也绝不抛。
                GuiProbe.Call(f, "Dispose", null);
                Flag afterClose = new Flag();
                Delegate mark = GuiProbe.MakeInvoker(afterClose, "Mark");
                Exception escaped = null;
                System.Threading.Thread worker = new System.Threading.Thread(delegate()
                {
                    try { GuiProbe.Call(f, "UiPost", new object[] { mark }); }
                    catch (Exception ex) { escaped = ex; }
                });
                worker.IsBackground = true;
                worker.Start();
                AssertTrue(worker.Join(15000));            // 不许挂住
                AssertTrue(escaped == null);               // 绝不能把异常抛回工作线程
                AssertFalse(afterClose.Ran);               // 已销毁的窗体绝不再被碰
            }); });

        // ---- brief Step 1 逐字要求的四条 ----

        H.Run("Gui.FormConstructsWithoutEngine", delegate {
            GuiProbe.WithForm(delegate(object f) {
                AssertTrue(GuiProbe.ControlCount(f) > 0);
            }); });

        H.Run("Gui.DropZoneIsPresentAndNamed", delegate {
            GuiProbe.WithForm(delegate(object f) {
                AssertEq(GuiProbe.FindCount(f, "dropZone"), 1);
                AssertTrue(GuiProbe.TextOf(GuiProbe.Find(f, "dropZone")).Length >= 0);   // 取得到，不是 null
            }); });

        H.Run("Gui.DangerousDeleteIsUncheckedByDefault", delegate {                 // 不变式 I3 的界面层
            GuiProbe.WithForm(delegate(object f) {
                object box = GuiProbe.Find(f, "chkDelete");
                AssertEq(GuiProbe.TypeName(box), "CheckBox");        // 真的是复选框，不是同名的 Label
                AssertFalse(GuiProbe.Checked(box));
            }); });

        H.Run("Gui.ManifestDeclaresPerMonitorV2AndAsInvoker", delegate {
            string m = File.ReadAllText(TestEnv.ManifestPath);
            AssertTrue(m.IndexOf("PerMonitorV2", StringComparison.Ordinal) >= 0);
            AssertTrue(m.IndexOf("longPathAware", StringComparison.Ordinal) >= 0);
            // 断言的是 requestedExecutionLevel 的**取值**（不是「文件里出现过某个词」：说明性注释里
            // 会提到别的级别名，光搜字符串会误报）。绝不请求提权（J2：提权后 UIPI 静默拦截拖放）。
            AssertTrue(m.IndexOf("level=\"asInvoker\"", StringComparison.Ordinal) >= 0);
            AssertFalse(m.IndexOf("level=\"requireAdministrator\"", StringComparison.Ordinal) >= 0);
            AssertFalse(m.IndexOf("level=\"highestAvailable\"", StringComparison.Ordinal) >= 0);
        });

        // ---- 空状态与危险分区（J11 / A5）----

        H.Run("Gui.EmptyStateSaysOriginalsKeptByDefault", delegate {
            GuiProbe.WithForm(delegate(object f) {
                string text = GuiProbe.TextOf(GuiProbe.Find(f, "lblEmptyState"));
                AssertTrue(text.IndexOf("原件默认保留", StringComparison.Ordinal) >= 0);
                // 一句话说清这个工具做什么（J11：非技术用户要知道下一步）。
                AssertTrue(text.IndexOf("嵌套", StringComparison.Ordinal) >= 0);
            }); });

        H.Run("Gui.DeleteCheckboxSitsInItsOwnDangerSection", delegate {
            GuiProbe.WithForm(delegate(object f) {
                object section = GuiProbe.Find(f, "grpDanger");
                object delete = GuiProbe.Find(f, "chkDelete");
                AssertEq(GuiProbe.TypeName(section), "GroupBox");
                AssertTrue(GuiProbe.TextOf(section).IndexOf("危险", StringComparison.Ordinal) >= 0);
                AssertTrue(GuiProbe.IsDescendantOf(delete, section));            // 删除项在危险分区内
                // 反向：三个安全选项**不在**危险分区里（安全项与危险项视觉上必须分得开）。
                AssertFalse(GuiProbe.IsDescendantOf(GuiProbe.Find(f, "chkCamouflage"), section));
                AssertFalse(GuiProbe.IsDescendantOf(GuiProbe.Find(f, "chkVolumes"), section));
                AssertFalse(GuiProbe.IsDescendantOf(GuiProbe.Find(f, "chkDict"), section));
            }); });

        H.Run("Gui.DeleteConfirmationNeverDefaultsToOk", delegate {
            object dialog = GuiProbe.New("Rerar.ConfirmDeleteForm", new object[] { 3 });
            try
            {
                // 回车**不能**确认删除：AcceptButton 必须为空（否则用户一路回车就把原包删了）。
                AssertTrue(GuiProbe.Prop(dialog, "AcceptButton") == null);
                AssertFalse(GuiProbe.Prop(dialog, "CancelButton") == null);      // Esc 只能「返回」

                object ok = GuiProbe.Find(dialog, "btnConfirm");
                object back = GuiProbe.Find(dialog, "btnBack");
                // 键盘默认落在「返回」上：TabIndex 更小且 AcceptButton 不是它。
                AssertTrue(Convert.ToInt32(GuiProbe.Prop(back, "TabIndex")) <
                           Convert.ToInt32(GuiProbe.Prop(ok, "TabIndex")));
            }
            finally
            {
                try { GuiProbe.Call(dialog, "Dispose", null); }
                catch (Exception) { }
            }
        });

        H.Run("Gui.DeleteConfirmationStatesExactCount", delegate {
            string text = (string)GuiProbe.Static("Rerar.MainForm", "DeleteConfirmText", new object[] { 7 });
            AssertTrue(text.IndexOf("7", StringComparison.Ordinal) >= 0);          // 说出**确切**数量
            AssertTrue(text.IndexOf("回收站", StringComparison.Ordinal) >= 0);      // 说清去向
            AssertTrue(text.IndexOf("失败", StringComparison.Ordinal) >= 0);        // 也说清什么**不会**发生
            AssertFalse(text.IndexOf("可能", StringComparison.Ordinal) >= 0);       // 不含糊
        });

        // ---- 日志：有界尾部 + 暂停自动滚动 + 密码卫生（J4 / I5 的界面侧）----

        // 「自动滚动」不是装饰：用户往上翻着读历史时，每 200ms 的刷新把视口拽回底部，
        // 那份日志就没法读了（审计 J4 专门点出这件事）。这条用例钉住暂停之后视口**不动**。
        H.Run("Gui.LogTailStopsFollowingWhenAutoScrollIsPaused", delegate {
            GuiProbe.WithStaObject("Rerar.LogTailView", null, delegate(object view) {
                GuiProbe.SetProp(view, "Width", 480);
                GuiProbe.SetProp(view, "Height", 200);

                string[] first = new string[300];
                for (int i = 0; i < first.Length; i++) { first[i] = "第" + i + "行"; }
                GuiProbe.Call(view, "SetLines", new object[] { first });

                int followed = Convert.ToInt32(GuiProbe.Prop(view, "FirstVisibleLine"));
                AssertTrue(followed > 0);                    // 默认自动滚动：视口在尾部（不是停在第一行）

                GuiProbe.SetProp(view, "AutoScroll", false);  // 用户取消勾选「自动滚动」= 往上翻

                string[] more = new string[600];
                for (int i = 0; i < more.Length; i++) { more[i] = "新第" + i + "行"; }
                GuiProbe.Call(view, "SetLines", new object[] { more });

                AssertEq(Convert.ToInt32(GuiProbe.Prop(view, "FirstVisibleLine")), followed);
                AssertEq(Convert.ToInt32(GuiProbe.Prop(view, "LineCount")), 600);
            }); });

        H.Run("Gui.LogTailKeepsOnlyNewestTwoThousandLines", delegate {
            object tail = GuiProbe.New("Rerar.LogTailBuffer", new object[] { 2000 });
            for (int i = 0; i < 5000; i++)
            {
                GuiProbe.Call(tail, "Add", new object[] { "第" + i + "行" });
            }

            AssertEq(Convert.ToInt32(GuiProbe.Prop(tail, "Count")), 2000);
            string[] lines = (string[])GuiProbe.Call(tail, "Snapshot", null);
            AssertEq(lines.Length, 2000);
            AssertEq(lines[0], "第3000行");            // 最老的 3000 行被丢掉（不是保留最老的）
            AssertEq(lines[1999], "第4999行");         // 最新的一行还在
            AssertEq(Convert.ToInt64(GuiProbe.Prop(tail, "TotalWritten")), 5000L);
        });

        H.Run("Gui.LogNeverContainsRegisteredPassword", delegate {
            string logPath = Path.Combine(TestEnv.Tmp, "gui-log.txt");
            object sink = GuiProbe.New("Rerar.LogSink", new object[] { logPath });
            try
            {
                GuiProbe.Call(sink, "RegisterSecret", new object[] { "SECRET-PW" });
                GuiProbe.Call(sink, "Write", new object[] { "候选 #3（手动输入）验证通过" });
                GuiProbe.Call(sink, "Write", new object[] { "万一有人把密码 SECRET-PW 拼进了判词" });

                string all = string.Join("\n", (string[])GuiProbe.Call(sink, "Snapshot", null));
                AssertFalse(all.IndexOf("SECRET-PW", StringComparison.Ordinal) >= 0);
                AssertTrue(all.IndexOf("候选 #3", StringComparison.Ordinal) >= 0);   // 该留的诊断还在
                AssertTrue(all.IndexOf("***", StringComparison.Ordinal) >= 0);       // 被替换而不是被删行
                AssertEq(GuiProbe.Prop(sink, "FilePath").ToString(), logPath);
            }
            finally
            {
                // 先 Dispose（会 Flush）再读盘：读的是**真正落下去的那份文件**，
                // 而不是与写方共享句柄时看到的东西。
                try { GuiProbe.Call(sink, "Dispose", null); }
                catch (Exception) { }
            }

            // 完整日志落的文件里同样不许出现密码（「日志三份存储」里的第一份就是它）。
            string fileText = File.ReadAllText(logPath, Encoding.UTF8);
            AssertFalse(fileText.IndexOf("SECRET-PW", StringComparison.Ordinal) >= 0);
            AssertTrue(fileText.IndexOf("候选 #3", StringComparison.Ordinal) >= 0);
        });

        // ---- 两阶段进度：只显示已发现的工作量与已用时间（J1）----

        H.Run("Gui.ProgressShowsDiscoveredCountAndElapsedOnly", delegate {
            string running = (string)GuiProbe.Static("Rerar.MainForm", "FormatProgressText",
                new object[] { 7, 23, TimeSpan.FromSeconds(751) });
            AssertTrue(running.IndexOf("第 7", StringComparison.Ordinal) >= 0);
            AssertTrue(running.IndexOf("已发现 23 个包", StringComparison.Ordinal) >= 0);
            AssertTrue(running.IndexOf("已用 12:31", StringComparison.Ordinal) >= 0);
            AssertFalse(running.IndexOf("剩余", StringComparison.Ordinal) >= 0);      // 还没满 3 个包
            AssertFalse(running.IndexOf("%", StringComparison.Ordinal) >= 0);        // 绝不报百分比

            string scanning = (string)GuiProbe.Static("Rerar.MainForm", "FormatScanningText",
                new object[] { 23 });
            AssertTrue(scanning.IndexOf("已发现 23", StringComparison.Ordinal) >= 0);
            AssertFalse(scanning.IndexOf("%", StringComparison.Ordinal) >= 0);       // 扫描期的百分比是谎话
        });

        H.Run("Gui.EtaAppearsOnlyAfterThreeArchivesAndIsLabelledRough", delegate {
            AssertEq((string)GuiProbe.Static("Rerar.MainForm", "FormatEta",
                new object[] { TimeSpan.FromSeconds(600), 2, 20 }), "");
            string eta = (string)GuiProbe.Static("Rerar.MainForm", "FormatEta",
                new object[] { TimeSpan.FromSeconds(600), 10, 20 });
            AssertTrue(eta.IndexOf("剩余", StringComparison.Ordinal) >= 0);
            AssertTrue(eta.IndexOf("粗略", StringComparison.Ordinal) >= 0);
        });

        // ---- 计数行：跳过（需密码）绝不与失败混为一谈（J6 / §7）----

        H.Run("Gui.CounterLineSeparatesFailuresSkipsAndUnprocessed", delegate {
            // 【关键】列表必须是**应用产物里**那份 ArchiveResult：两个产物各编了一份 src\Core\*.cs，
            // 于是 Rerar.Core.ArchiveResult 在两个程序集里 FullName 相同、类型身份不同。用 tests.exe
            // 自己的 List 传过去会得到「无法转换」——这里按应用产物的类型现造（见 GuiProbe 的说明）。
            System.Collections.IList results = (System.Collections.IList)GuiProbe.NewResultList();
            results.Add(GuiProbe.NewResult(@"C:\in\ok.zip", "Completed", 12));
            results.Add(GuiProbe.NewResult(@"C:\in\bad.rar", "Failed", 0));
            results.Add(GuiProbe.NewResult(@"C:\in\locked.7z", "SkippedNeedsPassword", 0));

            string counters = (string)GuiProbe.Static("Rerar.MainForm", "FormatCountersLine",
                new object[] { results, 1 });
            AssertTrue(counters.IndexOf("❌ 1 个失败", StringComparison.Ordinal) >= 0);
            AssertTrue(counters.IndexOf("🔒 1 个需密码", StringComparison.Ordinal) >= 0);
            AssertTrue(counters.IndexOf("⏳ 1 个未处理", StringComparison.Ordinal) >= 0);

            string extracted = (string)GuiProbe.Static("Rerar.MainForm", "FormatExtractedLine",
                new object[] { results });
            AssertTrue(extracted.IndexOf("已解出 12 个文件", StringComparison.Ordinal) >= 0);
        });

        // ---- .lnk（J8）----

        H.Run("Gui.ShortcutTargetIsResolved", delegate {
            string target = TestEnv.MakeFile("lnk-target.zip", "不是真的压缩包，只用来当快捷方式的目标");
            string lnk = Path.Combine(TestEnv.Tmp, "桌面快捷方式.lnk");

            string makeProblem;
            if (!GuiProbe.TryMakeShortcut(lnk, target, out makeProblem))
            {
                H.Skip("Gui.ShortcutTargetIsResolved",
                    "本机造不出 .lnk 夹具（" + makeProblem + "）：这是夹具问题，不是产品缺陷");
                return;
            }

            object[] args = new object[] { lnk, null, null };
            bool ok = (bool)GuiProbe.Static("Rerar.MainForm", "TryResolveShortcut", args);
            AssertTrue(ok);
            AssertTrue(string.Equals(target, (string)args[1], StringComparison.OrdinalIgnoreCase));
        });

        H.Run("Gui.ShortcutResolutionFailureIsAWarningNotACrash", delegate {
            string bogus = Path.Combine(TestEnv.Tmp, "不是快捷方式.lnk");
            File.WriteAllText(bogus, "这不是一个 .lnk 文件", new UTF8Encoding(false));

            object[] args = new object[] { bogus, null, null };
            bool ok = (bool)GuiProbe.Static("Rerar.MainForm", "TryResolveShortcut", args);
            AssertFalse(ok);
            AssertTrue(((string)args[2]).Length > 0);      // 逐项告警：给出原因，绝不抛异常

            // 不存在的路径同样只是告警（拖进来的东西可能已经被删了）。
            object[] missing = new object[] { Path.Combine(TestEnv.Tmp, "根本没有这个.lnk"), null, null };
            AssertFalse((bool)GuiProbe.Static("Rerar.MainForm", "TryResolveShortcut", missing));
            AssertTrue(((string)missing[2]).Length > 0);
        });

        // ---- 设置：原子写 + 加载时校验目录仍在（窗口几何 / 上次目录）----

        H.Run("Gui.SettingsRoundTripValidatesFolders", delegate {
            string path = Path.Combine(TestEnv.Tmp, "settings.ini");
            string folder = Path.Combine(TestEnv.Tmp, "上次用的目录");
            Directory.CreateDirectory(folder);

            object settings = GuiProbe.New("Rerar.AppSettings", new object[] { path });
            GuiProbe.SetProp(settings, "HasGeometry", true);
            GuiProbe.SetProp(settings, "WindowX", 120);
            GuiProbe.SetProp(settings, "WindowY", 64);
            GuiProbe.SetProp(settings, "WindowWidth", 900);
            GuiProbe.SetProp(settings, "WindowHeight", 640);
            GuiProbe.SetProp(settings, "Maximized", true);
            GuiProbe.SetProp(settings, "LastInputFolder", folder);
            GuiProbe.Call(settings, "Save", null);
            AssertTrue(File.Exists(path));

            object again = GuiProbe.New("Rerar.AppSettings", new object[] { path });
            AssertTrue((bool)GuiProbe.Call(again, "Load", null));
            AssertEq(Convert.ToInt32(GuiProbe.Prop(again, "WindowX")), 120);
            AssertEq(Convert.ToInt32(GuiProbe.Prop(again, "WindowWidth")), 900);
            AssertEq(Convert.ToInt32(GuiProbe.Prop(again, "WindowHeight")), 640);
            AssertEq(Convert.ToBoolean(GuiProbe.Prop(again, "Maximized")), true);
            AssertTrue((bool)GuiProbe.Prop(again, "HasGeometry"));
            AssertEq(GuiProbe.Prop(again, "LastInputFolder").ToString(), folder);

            // 存下来的目录已经不在了 ⇒ 加载时丢弃：绝不把用户带到一个不存在的初始目录。
            object stale = GuiProbe.New("Rerar.AppSettings", new object[] { path });
            GuiProbe.SetProp(stale, "LastInputFolder", Path.Combine(TestEnv.Tmp, "已经没有了"));
            GuiProbe.Call(stale, "Save", null);

            object reloaded = GuiProbe.New("Rerar.AppSettings", new object[] { path });
            GuiProbe.Call(reloaded, "Load", null);
            AssertEq(GuiProbe.Prop(reloaded, "LastInputFolder").ToString(), "");

            // 坏文件绝不能让界面起不来：读一份垃圾进去，Load 如实返回 false，实例保持默认值。
            File.WriteAllText(path, "这不是配置文件\r\n乱码 \u0000\u0001", new UTF8Encoding(false));
            object broken = GuiProbe.New("Rerar.AppSettings", new object[] { path });
            GuiProbe.Call(broken, "Load", null);
            AssertEq(Convert.ToInt32(GuiProbe.Prop(broken, "WindowWidth")), 0);
        });

        // ---- 字体（J15：显式中文字体 + 回退链）----

        H.Run("Gui.FontProbeRejectsMissingFamiliesAndPrefersYaHeiUi", delegate {
            // 不存在的字体一律不选（绝不静默用错字体）。
            object missing = GuiProbe.Static("Rerar.MainForm", "PickFirstInstalledFamily",
                new object[] { new string[] { "Rerar不存在的字体", "Rerar也不存在" } });
            AssertEq(missing == null ? "" : missing.ToString(), "");

            // 真实候选链：结果必须是链上**已经被装上**的那一个（或空串 = 回退系统字体）。
            string family = (string)GuiProbe.Static("Rerar.MainForm", "PickFirstInstalledFamily",
                new object[] { new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimSun" } });
            AssertTrue(family == "Microsoft YaHei UI" || family == "Microsoft YaHei" ||
                       family == "SimSun" || family == "");

            // 窗体真的用了探测出来的那个字体（不是构造完就忘了设）。
            GuiProbe.WithForm(delegate(object f) {
                object font = GuiProbe.Prop(f, "Font");
                string used = GuiProbe.Prop(font, "Name").ToString();
                string expected = (string)GuiProbe.Static("Rerar.MainForm", "PickFontFamily", null);
                AssertEq(used, expected);
            });
        });

        // ---- 产物预算与「清单只给应用目标」----

        H.Run("Gui.AppExeStaysWithinSizeBudget", delegate {
            long size = new FileInfo(TestEnv.ExePath).Length;
            AssertTrue(size > 0);
            AssertTrue(size <= 5L * 1024 * 1024);      // 全局约束：exe 必须 ≤ 5 MB
        });

        H.Run("Gui.ManifestIsEmbeddedInAppTargetOnly", delegate {
            byte[] app = File.ReadAllBytes(TestEnv.ExePath);
            AssertTrue(GuiProbe.ContainsAscii(app, "PerMonitorV2"));
            AssertTrue(GuiProbe.ContainsAscii(app, "asInvoker"));

            // /win32manifest: 只给应用目标 —— 测试产物里绝不能带着它（与 /resource: 同一条规矩）。
            byte[] tests = File.ReadAllBytes(TestEnv.TestsExePath);
            AssertFalse(GuiProbe.ContainsAscii(tests, "PerMonitorV2"));
        });

        // 【为什么这也值得一条用例】build.ps1 里两个目标只差「额外引用 + 额外开关」两处参数，
        // 把 WinForms 的引用或 /win32manifest: 挪进共享数组**不会编译失败**，只会让测试产物悄悄
        // 变胖/带上界面清单 —— 而那种回归没有任何人会注意到。这里直接扫两个产物的元数据字符串
        //（程序集引用名以 UTF-8 存在 PE 里），与 Engine.PayloadEmbeddedInAppTargetOnly 同一手法。
        H.Run("Gui.TestTargetStaysWinFormsFree", delegate {
            byte[] app = File.ReadAllBytes(TestEnv.ExePath);
            byte[] tests = File.ReadAllBytes(TestEnv.TestsExePath);

            AssertTrue(GuiProbe.ContainsAscii(app, "System.Windows.Forms"));    // 应用目标当然要引
            AssertFalse(GuiProbe.ContainsAscii(tests, "System.Windows.Forms")); // 测试目标**不引**
            AssertFalse(GuiProbe.ContainsAscii(tests, "System.Drawing"));
        });
    }
}
