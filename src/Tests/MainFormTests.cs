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

    // ==================================================================
    // 界面用例的两个小助手。
    //
    // 【为什么不需要「把消息泵转一会儿」】本轮的判据全部落在**同步**发生的事情上：
    //   * SessionRunCount 在 StartRun 里自增（点下去那一刻就 +1，与工作线程无关）；
    //   * 逐项动作按钮/菜单项的可用性、计数标签的文案、会话日志清单都在调用点同步更新。
    // 那些经 BeginInvoke 回到 UI 线程的收尾动作（FinishRun）在**没有消息循环**的测试线程上会一直
    // 留在队列里 —— 那是无害的：它一次控件都没碰到，而且这几条用例要证明的正是「这一轮**起没起**」，
    // 不是「这一轮跑得怎么样」（后者由 Core 的用例负责）。
    // ==================================================================

    private static bool IsRunning(object form)
    {
        return Convert.ToBoolean(GuiProbe.Prop(form, "_running"));
    }

    // 一条真实的、**不存在的**源路径：界面会把这一项交给 Core，Core 如实报「目标不存在或不可读」
    // 并很快收尾 —— 于是用例不必依赖真的压缩包，也不会跑很久。
    private static string MissingSourcePath(string name)
    {
        return Path.Combine(TestEnv.Tmp, name);
    }

    // 让界面以为当前这一轮已经收尾（走**产品自己的** FinishRun，绝不手改 _running）。
    // 为什么需要它：测试线程上没有消息循环，工作线程经 BeginInvoke 投递的 FinishRun 永远不会被执行；
    // 而「第二轮真的起得来」这件事要求 _running 已经回到 false（否则 EnqueueRetry 只会入队，
    // 那正是被修的缺陷要区分的两种情形）。FinishRun 需要一个 RunSummary —— 按应用产物的类型现造。
    private static void FinishRunningRound(object form)
    {
        if (!IsRunning(form)) { return; }
        object summary = GuiProbe.New("Rerar.Core.RunSummary", null);
        GuiProbe.Call(form, "FinishRun", new object[] { summary });
        AssertFalse(IsRunning(form));       // 收尾之后必须真的空闲了（否则后面的断言没有意义）
    }

    // 把界面摆成「选中了一条可以重试的结果」的样子：_results 一条失败结局 + 逐项列表重建 +
    // 选中第 0 行。这一切都经反射做在**产品里那个真实的 MainForm** 上。
    //
    // 【为什么要显式 CreateHandle】ListView.SelectedIndices 只在**句柄已创建**之后才被维护：
    // 没有句柄时 `ListViewItem.Selected = true` 只把项自己的 StateSelected 置上，ListView 的
    // 选中索引集合一直是空的（实测：SelectedIndices.Count 恒为 0），而产品的 SelectedResult()
    // 正是按 SelectedIndices 取行的 —— 于是必须先把句柄建出来（这是测试夹具的准备工作，
    // 不是产品的替代路径：产品里窗口本来就是显示出来的，句柄必然已创建）。
    private static void PrepareRetryTarget(object form, string sourcePath)
    {
        object results = GuiProbe.NewResultListWith(
            GuiProbe.NewResult(sourcePath, "Failed", 0));

        GuiProbe.SetProp(form, "_results", results);
        GuiProbe.SetProp(form, "_itemsBuilt", 0);
        GuiProbe.Call(form, "RefreshItems", null);

        object items = GuiProbe.Prop(form, "_lstItems");
        GuiProbe.CreateHandle(items);
        // ListView.SelectedItems 是只读集合 —— 所以不碰它，而是把**行元素**的 Selected 置 true
        //（Item[int] 索引器属性 + ListViewItem.Selected 的 setter，两条都是反射可达的公开成员）。
        GuiProbe.SetPropPath(items, "Items[0].Selected", true);
        AssertEq(Convert.ToInt32(GuiProbe.PropPath(items, "SelectedIndices.Count")), 1);
        GuiProbe.Call(form, "UpdateItemActions", null);
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

                // 修复轮 Minor 11：原来这里断言「Length >= 0」—— 那对任何字符串都成立，
                // 等于什么都没断言。改成对**文案本身**的真实断言：拖放区必须给出明确的下一步
                //（J2/J11：拖放失效时的备用入口要在同一块里看得见）。
                string dropZoneText = GuiProbe.AllText(GuiProbe.Find(f, "dropZone"));
                AssertTrue(dropZoneText.IndexOf("拖到这里", StringComparison.Ordinal) >= 0);
                AssertTrue(dropZoneText.IndexOf("选择文件夹", StringComparison.Ordinal) >= 0);
                AssertTrue(dropZoneText.IndexOf("选择压缩包", StringComparison.Ordinal) >= 0);
                AssertTrue(dropZoneText.IndexOf("原件默认保留", StringComparison.Ordinal) >= 0);
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
            AssertTrue(running.IndexOf("已发现 23", StringComparison.Ordinal) >= 0);
            AssertTrue(running.IndexOf("已用 12:31", StringComparison.Ordinal) >= 0);
            AssertFalse(running.IndexOf("剩余", StringComparison.Ordinal) >= 0);      // 还没满 3 个包
            AssertFalse(running.IndexOf("%", StringComparison.Ordinal) >= 0);        // 绝不报百分比
            // 修复轮 Minor 2：单位是「项」（交给 Core 判定的候选文件），不是「个包」。
            AssertTrue(running.IndexOf("项", StringComparison.Ordinal) >= 0);
            AssertFalse(running.IndexOf("个包", StringComparison.Ordinal) >= 0);

            string scanning = (string)GuiProbe.Static("Rerar.MainForm", "FormatScanningText",
                new object[] { 23 });
            AssertTrue(scanning.IndexOf("已发现 23", StringComparison.Ordinal) >= 0);
            AssertFalse(scanning.IndexOf("%", StringComparison.Ordinal) >= 0);       // 扫描期的百分比是谎话
            // 修复轮 Minor 2：单位必须与名词相符 —— 这里数的是**候选文件**（枚举层不按后缀过滤，
            // 「是不是压缩包」由 Core 逐个判），所以文案里不许出现「个压缩包」。
            AssertTrue(scanning.IndexOf("候选文件", StringComparison.Ordinal) >= 0);
            AssertFalse(scanning.IndexOf("个压缩包", StringComparison.Ordinal) >= 0);
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

        // ==================================================================
        // 修复轮（Task 14 第一轮 fix）：三条 Important + 相邻 Minors
        // ==================================================================

        // ---- 修复轮 Finding 1：空闲时点「重试」必须**真的跑起来** ----
        //
        // 【被修的缺陷】以前两个逐项动作处理器只调 EnqueueRetry（只入队），而出队点只有密码面板
        // 的两个按钮；同时 UpdateItemActions 又只在 `!_running`（= 点下去永远跑不起来的那个时刻）
        // 才启用这两个按钮。于是「空闲时点重试」是一次静默失败：状态行还谎称「当前批次结束后重试」，
        // 而当时根本没有批次。
        //
        // 【为什么断言 SessionRunCount】那是「本会话真的起过几轮运行」的只增计数，在 StartRun 里
        // **同步**自增 —— 与工作线程的进度无关，所以这条断言是确定性的（不靠竞态）。
        // 若 TryStartIdleRetry 又被摘掉，这里就是 0，用例当场红。
        H.Run("Gui.IdleRetryClickActuallyStartsRetry", delegate {
            GuiProbe.WithForm(delegate(object f) {
                AssertEq(Convert.ToInt32(GuiProbe.Prop(f, "SessionRunCount")), 0);

                string source = MissingSourcePath("重试目标-不存在.zip");
                PrepareRetryTarget(f, source);

                // 前置条件本身就是契约的一半：空闲 + 选中一条未成功的结果 ⇒ 按钮必须可用。
                AssertTrue(Convert.ToBoolean(GuiProbe.Prop(GuiProbe.Prop(f, "_btnRetry"), "Enabled")));

                GuiProbe.Call(f, "BtnRetry_Click", new object[] { null, EventArgs.Empty });
                AssertEq(Convert.ToInt32(GuiProbe.Prop(f, "SessionRunCount")), 1);   // **这一下就起了**

                // 会话日志清单立即可达（修复轮 Finding 3 的第二半）。
                string[] logs = (string[])GuiProbe.Call(f, "SessionLogPaths", null);
                AssertEq(logs.Length, 1);
                AssertTrue(File.Exists(logs[0]));

                // 两个口径的计数标签在起跑那一刻就已经**并排**存在（谁也冒充不了谁）。
                string counters = GuiProbe.TextOf(GuiProbe.Find(f, "lblCounters"));
                string session = GuiProbe.TextOf(GuiProbe.Find(f, "lblSessionCounters"));
                AssertTrue(counters.IndexOf("【本轮】", StringComparison.Ordinal) >= 0);
                AssertTrue(session.IndexOf("【本次会话】", StringComparison.Ordinal) >= 0);
            }); });

        // 反证守卫：把 TryStartIdleRetry 从 EnqueueRetry 里摘掉，上面那条用例必须变红 ——
        // 这里额外直接钉住「入队的同时就会尝试启动」这件事的两半（队列被消费 + 状态行不再说
        //「当前批次结束后重试」），因为**状态行说谎**本身就是被复核者点名的缺陷之一。
        H.Run("Gui.IdleRetryNeverClaimsABatchThatDoesNotExist", delegate {
            GuiProbe.WithForm(delegate(object f) {
                GuiProbe.Call(f, "EnqueueRetry", new object[] { MissingSourcePath("空闲入队.zip"), "重试" });

                // 空闲时入队 ⇒ 立刻开始（队列被消费掉），状态行**不得**再说「当前批次结束后」。
                AssertEq(Convert.ToInt32(GuiProbe.Prop(f, "SessionRunCount")), 1);
                string status = GuiProbe.TextOf(GuiProbe.Find(f, "lblStatus"));
                AssertFalse(status.IndexOf("当前批次结束后", StringComparison.Ordinal) >= 0);
                AssertTrue(status.IndexOf("开始重试", StringComparison.Ordinal) >= 0);
                AssertTrue(IsRunning(f));                      // 真的在跑（不是只把话说得好听）
            }); });

        // ---- 修复轮 Finding 1（第二半）：按钮与右键菜单的可用性必须一致 ----
        H.Run("Gui.ContextMenuItemsCannotDisagreeWithActionButtons", delegate {
            GuiProbe.WithForm(delegate(object f) {
                object menu = GuiProbe.Prop(f, "_itemMenu");
                AssertEq(Convert.ToInt32(GuiProbe.PropPath(menu, "Items.Count")), 4);

                // 没有任何选中项（构造后的初态）：四个按钮与四个菜单项**全部**不可用。
                string[] names = new string[] { "btnForceArchive", "btnEnterPassword", "btnRetry", "btnOpenOutput" };
                for (int i = 0; i < 4; i++)
                {
                    AssertFalse(Convert.ToBoolean(GuiProbe.Prop(GuiProbe.Find(f, names[i]), "Enabled")));
                    AssertFalse(MenuItemEnabled(menu, i));
                }

                // 选中一条失败结局：三个动作可用，且按钮与菜单项**逐一同值**。
                PrepareRetryTarget(f, MissingSourcePath("菜单一致性.zip"));
                for (int i = 0; i < 4; i++)
                {
                    bool button = Convert.ToBoolean(GuiProbe.Prop(GuiProbe.Find(f, names[i]), "Enabled"));
                    AssertEq(MenuItemEnabled(menu, i), button);
                }
                AssertTrue(Convert.ToBoolean(GuiProbe.Prop(GuiProbe.Find(f, "btnRetry"), "Enabled")));

                // 运行中：四对必须**同时**关掉（以前菜单项从不被禁用，两个入口在这里正好相反）。
                GuiProbe.Call(f, "SetItemActionsEnabled", new object[] { false });
                for (int i = 0; i < 4; i++)
                {
                    AssertFalse(Convert.ToBoolean(GuiProbe.Prop(GuiProbe.Find(f, names[i]), "Enabled")));
                    AssertFalse(MenuItemEnabled(menu, i));
                }
            }); });

        // ---- 修复轮 Finding 2：每次开始都有的预检摘要（含引擎未定位时的诚实措辞） ----
        H.Run("Gui.PreflightSummarySaysEverythingEvenWithDeleteOff", delegate {
            string text = (string)GuiProbe.Static("Rerar.MainForm", "BuildSummaryText",
                new object[] { 7, false, null });
            AssertTrue(text.IndexOf("7 项", StringComparison.Ordinal) >= 0);           // 目标项数
            AssertTrue(text.IndexOf("保留", StringComparison.Ordinal) >= 0);           // 删除关着 ⇒ 原包不动
            AssertTrue(text.IndexOf("原地", StringComparison.Ordinal) >= 0);           // §6.11 的输出去向
            AssertTrue(text.IndexOf("将在开始时定位", StringComparison.Ordinal) >= 0);  // 引擎：未定位就说未定位
            AssertFalse(text.IndexOf("25.00", StringComparison.Ordinal) >= 0);         // 绝不冒充已定位

            // 定位到之后如实换成真值；删除开着时写明后果（含确切数量）。
            string resolved = (string)GuiProbe.Static("Rerar.MainForm", "FormatEngineSummaryLine",
                new object[] { "解压引擎：7-Zip 26.01（本机安装）：C:\\Program Files\\7-Zip\\7z.exe" });
            AssertTrue(resolved.IndexOf("26.01", StringComparison.Ordinal) >= 0);
            AssertFalse(resolved.IndexOf("将在开始时定位", StringComparison.Ordinal) >= 0);

            string deleting = (string)GuiProbe.Static("Rerar.MainForm", "FormatDeleteSummaryLine",
                new object[] { true, 3 });
            AssertTrue(deleting.IndexOf("回收站", StringComparison.Ordinal) >= 0);
            AssertTrue(deleting.IndexOf("校验通过", StringComparison.Ordinal) >= 0);
            AssertTrue(deleting.IndexOf("3 项", StringComparison.Ordinal) >= 0);        // 说出确切数量

            // 窗体上真的有一个摘要标签，且初态是收起的（它不是常驻装饰）。
            GuiProbe.WithForm(delegate(object f) {
                object summary = GuiProbe.Find(f, "lblSummary");
                AssertEq(GuiProbe.TypeName(summary), "Label");
                AssertFalse(Convert.ToBoolean(GuiProbe.Prop(summary, "Visible")));
            });
        });

        // ---- 修复轮 Finding 2 的相邻项（Minor 1）：三个固定行为复选框必须**置灰** ----
        H.Run("Gui.FixedBehaviourOptionsAreDisabledWithReason", delegate {
            GuiProbe.WithForm(delegate(object f) {
                string[] names = new string[] { "chkCamouflage", "chkVolumes", "chkDict" };
                for (int i = 0; i < names.Length; i++)
                {
                    object box = GuiProbe.Find(f, names[i]);
                    AssertEq(GuiProbe.TypeName(box), "CheckBox");
                    // 勾着（那正是 v1.0 的固定行为），但**不能改**：可点却什么都不改变就是在骗人。
                    AssertTrue(GuiProbe.Checked(box));
                    AssertFalse(Convert.ToBoolean(GuiProbe.Prop(box, "Enabled")));
                    AssertTrue(GuiProbe.Prop(box, "AccessibleName").ToString()
                        .IndexOf("固定行为", StringComparison.Ordinal) >= 0);
                }

                // 提示语的措辞由纯函数给，用例连文案本身一起钉住。
                string tip = (string)GuiProbe.Static("Rerar.MainForm", "FixedOptionTooltip",
                    new object[] { "按内容识别改了后缀名的压缩包" });
                AssertTrue(tip.IndexOf("按内容识别", StringComparison.Ordinal) >= 0);   // 说清它做什么
                AssertTrue(tip.IndexOf("v1.0", StringComparison.Ordinal) >= 0);        // 说清为什么点不动

                // 反证：删除开关仍然是**能改**的（置灰不能误伤真正的杠杆）。
                AssertTrue(Convert.ToBoolean(GuiProbe.Prop(GuiProbe.Find(f, "chkDelete"), "Enabled")));
            }); });

        // ---- 修复轮 Finding 3：两个口径 + 会话日志清单 + 日志文件名的唯一性 ----
        H.Run("Gui.CountersCarryTheirOwnScopeAndLogsStayReachable", delegate {
            // (1) 计数行必须带口径标签 —— 数字不能脱离它自己的范围出现。
            object round = GuiProbe.NewResultListWith(
                GuiProbe.NewResult(@"C:\in\a.zip", "Failed", 0),
                GuiProbe.NewResult(@"C:\in\b.zip", "Completed", 4));
            string scoped = (string)GuiProbe.Static("Rerar.MainForm", "FormatScopedCountersLine",
                new object[] { "本轮", round, 1 });
            AssertTrue(scoped.IndexOf("【本轮】", StringComparison.Ordinal) >= 0);
            AssertTrue(scoped.IndexOf("❌ 1 个失败", StringComparison.Ordinal) >= 0);

            // (2) 会话口径 = 已收尾各轮 + 当前轮，按路径取**最后一条**（重试过的包绝不数两遍）。
            //     被复核者点名的那个场景就在这里：200 项 30 失败之后跑一轮 1 项的重试，
            //     会话口径必须**仍然**报出那 30 个失败和 1 个未处理。
            object closed = GuiProbe.NewResultListWith(
                GuiProbe.NewResult(@"C:\in\bad1.zip", "Failed", 0),
                GuiProbe.NewResult(@"C:\in\bad2.zip", "Failed", 0),
                GuiProbe.NewResult(@"C:\in\retried.zip", "Failed", 0));
            object current = GuiProbe.NewResultListWith(
                GuiProbe.NewResult(@"C:\in\retried.zip", "Completed", 9));
            string session = (string)GuiProbe.Static("Rerar.MainForm", "FormatSessionCountersLine",
                new object[] { closed, current, 1, 0 });
            AssertTrue(session.IndexOf("【本次会话】", StringComparison.Ordinal) >= 0);
            AssertTrue(session.IndexOf("❌ 2 个失败", StringComparison.Ordinal) >= 0);   // 30→2 的缩样：同一条规则
            AssertTrue(session.IndexOf("⏳ 1 个未处理", StringComparison.Ordinal) >= 0);
            AssertFalse(session.IndexOf("❌ 3 个失败", StringComparison.Ordinal) >= 0);  // 重试过的那条**不数两遍**

            // 只报本轮的旧行为就是那句谎：这一条从反面钉住「会话口径 ≠ 本轮口径」。
            string onlyRound = (string)GuiProbe.Static("Rerar.MainForm", "FormatScopedCountersLine",
                new object[] { "本轮", current, 0 });
            AssertTrue(onlyRound.IndexOf("❌ 0 个失败", StringComparison.Ordinal) >= 0);

            // (3) 会话日志清单：**每一轮**的路径都留着，不是只留最后一条。
            //     两轮都在**起跑那一刻**取清单 —— 判据全部是同步的（这一轮起没起、日志路径是哪份），
            //     与工作线程的进度无关（那部分由 Core 的用例负责）。
            GuiProbe.WithForm(delegate(object f) {
                string[] before = (string[])GuiProbe.Call(f, "SessionLogPaths", null);
                AssertEq(before.Length, 0);

                GuiProbe.Call(f, "EnqueueRetry", new object[] { MissingSourcePath("日志甲.zip"), "重试" });
                AssertEq(Convert.ToInt32(GuiProbe.Prop(f, "SessionRunCount")), 1);
                string[] first = (string[])GuiProbe.Call(f, "SessionLogPaths", null);
                AssertEq(first.Length, 1);
                AssertTrue(File.Exists(first[0]));

                // 让第一轮收尾（产品的 FinishRun），这样第二轮才有机会真的起起来。
                FinishRunningRound(f);

                // 第二轮紧接着开始（同一秒内）—— 这正是 Minor 8 那个「同名截断上一份」的场景。
                GuiProbe.Call(f, "EnqueueRetry", new object[] { MissingSourcePath("日志乙.zip"), "重试" });
                AssertEq(Convert.ToInt32(GuiProbe.Prop(f, "SessionRunCount")), 2);

                string[] after = (string[])GuiProbe.Call(f, "SessionLogPaths", null);
                AssertEq(after.Length, 2);                    // 上一轮的日志路径没有被丢掉
                AssertEq(after[0], first[0]);
                AssertTrue(after[0] != after[1]);             // 两份必须是不同的文件
                AssertTrue(File.Exists(after[0]));
                AssertTrue(File.Exists(after[1]));

                // 提示标签里能看见逐轮路径（「日志还在，但界面上找不到」等于没有日志）。
                string hint = GuiProbe.TextOf(GuiProbe.Find(f, "lblLogHint"));
                AssertTrue(hint.IndexOf(after[0], StringComparison.Ordinal) >= 0);
                AssertTrue(hint.IndexOf(after[1], StringComparison.Ordinal) >= 0);

                // 第一份仍然有内容（同名截断的旧缺陷会让它变空；现在两轮各写各的文件）。
                AssertTrue(new FileInfo(after[0]).Length > 0);
            });
        });

        // 日志文件名唯一化（修复轮 Minor 8）：秒分辨率 + 秒内序号，且**绝不**覆盖盘上已有的那份。
        H.Run("Gui.LogFileNameIsUniqueWhenTwoRoundsStartInTheSameSecond", delegate {
            DateTime fixedMoment = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Local);
            string baseName = (string)GuiProbe.Static("Rerar.MainForm", "BuildLogFileName",
                new object[] { fixedMoment });
            // 锚的格式没变（人读那份仍然是 run-<yyyyMMdd_HHmmss>.log）。
            AssertEq(baseName, "run-20261002_120000.log");

            string dir = Path.Combine(TestEnv.Tmp, "logs");
            Directory.CreateDirectory(dir);
            string wanted = Path.Combine(dir, baseName);

            // 已经存在同名文件（= 同一秒里的第二轮）⇒ 唯一化必须换名，绝不截断上一份。
            File.WriteAllText(wanted, "第一轮的日志\r\n", new UTF8Encoding(true));
            string second = (string)GuiProbe.Static("Rerar.MainForm", "UniqueLogPath",
                new object[] { wanted, 1 });
            AssertTrue(second != wanted);
            AssertTrue(second.IndexOf("run-20261002_120000-1.log", StringComparison.Ordinal) >= 0);
            AssertEq(Path.GetDirectoryName(second), dir);

            File.WriteAllText(second, "第二轮的日志\r\n", new UTF8Encoding(true));
            // 第一份**原样还在**（这就是以前被截断掉的那一份）。
            AssertEq(File.ReadAllText(wanted, Encoding.UTF8).IndexOf("第一轮", StringComparison.Ordinal) >= 0, true);
            AssertEq(File.ReadAllText(second, Encoding.UTF8).IndexOf("第二轮", StringComparison.Ordinal) >= 0, true);

            // 序号继续往后走（第三轮不会撞上前两份）。
            string third = (string)GuiProbe.Static("Rerar.MainForm", "UniqueLogPath",
                new object[] { wanted, 2 });
            AssertTrue(third != wanted && third != second);
        });

        // ---- 「关于/开源许可」（Task 15 修复轮 / 发现 2：规格 §8 的许可义务在程序内的落地） ----
        //
        // 判据与其它界面用例同一立场：控件树里**真的**有这个入口、点下去**真的**把 exe 旁边随包
        // 分发的 THIRD-PARTY-NOTICES.txt 展示出来、文件缺席/读不动时**真的**给指名期望路径的
        // 说明文本而不是抛异常。（展示窗口的像素观感不在自动化范围 —— task-14 的立场照旧。）

        H.Run("Gui.LicenseEntryExistsBesideLogTools", delegate {
            GuiProbe.WithForm(delegate(object f) {
                AssertEq(GuiProbe.FindCount(f, "btnLicense"), 1);
                object btn = GuiProbe.Find(f, "btnLicense");
                AssertTrue(GuiProbe.TextOf(btn).IndexOf("开源许可", StringComparison.Ordinal) >= 0);
                AssertTrue(GuiProbe.IsDescendantOf(btn, f));
                // 接点：与「打开日志文件」同一个工具条（Task 15 报告 §4 建议的位置）。
                AssertTrue(ReferenceEquals(GuiProbe.Prop(btn, "Parent"),
                    GuiProbe.Prop(GuiProbe.Find(f, "btnOpenLog"), "Parent")));
            }); });

        H.Run("Gui.LicenseClickShowsBundledNoticeWindow", delegate {
            GuiProbe.WithForm(delegate(object f) {
                GuiProbe.CreateHandle(f);        // Show(this) 需要属主句柄（窗口正常显示时本来就有）
                object btn = GuiProbe.Find(f, "btnLicense");

                // 为什么不 PerformClick：实测（本轮探针，.NET Framework 4.8 的 Button.PerformClick）
                // 对「已挂父窗、父窗已建句柄但**未显示**」的按钮会**静默跳过** —— 无父/父窗已显示
                // 两种情形都会触发，唯独这个组合不触发。那是 WinForms 自己的守门，不是产品的路径
                // （真实使用中窗口必然可见）。本套件处理这类点击的既有模式是反射直接调**产品自己的**
                // Click 处理方法（Gui.IdleRetryClickActuallyStartsRetry 的 BtnRetry_Click 同款）。
                GuiProbe.Call(f, "BtnLicense_Click", new object[] { btn, EventArgs.Empty });

                object dialog = GuiProbe.Prop(f, "_licenseDialog");
                AssertTrue(dialog != null);
                AssertTrue(Convert.ToBoolean(GuiProbe.Prop(dialog, "Visible")));
                string notice = GuiProbe.TextOf(GuiProbe.Find(dialog, "licenseText"));

                // 展示的就是 exe 旁边那份许可文件的原文（BOM 由 ReadAllText 归一）。
                string bundled = Path.Combine(Path.GetDirectoryName(TestEnv.ExePath),
                    "THIRD-PARTY-NOTICES.txt");
                AssertTrue(File.Exists(bundled));   // 缺席时这里 FAIL：随包分发是构建的责任
                AssertEq(notice, File.ReadAllText(bundled, Encoding.UTF8));
                AssertTrue(notice.IndexOf("7-Zip", StringComparison.Ordinal) >= 0);

                GuiProbe.Call(dialog, "Dispose", null);   // 收尾，不留窗口
            }); });

        H.Run("Gui.LicenseMissingFileShowsPathInsteadOfThrowing", delegate {
            // BuildLicenseNotice 是真实点击路径（Application.StartupPath）的唯一文本来源；
            // 目录可控 ⇒ 「文件缺席」方向在临时目录上打同一个入口。断言本身就是「不抛」：
            // 下面的调用若抛异常，用例直接 FAIL。
            string notice = Convert.ToString(GuiProbe.Static(
                "Rerar.MainForm", "BuildLicenseNotice", new object[] { TestEnv.Tmp }));
            string expected = Path.Combine(TestEnv.Tmp, "THIRD-PARTY-NOTICES.txt");
            AssertFalse(File.Exists(expected));    // 缺席前提自证（夹具自检）
            AssertTrue(notice.IndexOf("未找到", StringComparison.Ordinal) >= 0);
            AssertTrue(notice.IndexOf(expected, StringComparison.Ordinal) >= 0);   // 指名期望路径

            // 「文件在但读不动」同方向：独占句柄锁住 ⇒ 仍是说明文本，绝不是异常穿透。
            string lockedDir = Path.Combine(TestEnv.Tmp, "locked-license");
            Directory.CreateDirectory(lockedDir);
            string lockedFile = Path.Combine(lockedDir, "THIRD-PARTY-NOTICES.txt");
            File.WriteAllText(lockedFile, "占位内容", new UTF8Encoding(false));
            using (FileStream hold = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                string lockedNotice = Convert.ToString(GuiProbe.Static(
                    "Rerar.MainForm", "BuildLicenseNotice", new object[] { lockedDir }));
                AssertTrue(lockedNotice.IndexOf("无法读取", StringComparison.Ordinal) >= 0);
                AssertTrue(lockedNotice.IndexOf(lockedFile, StringComparison.Ordinal) >= 0);
            }
        });

        // ---- 日志尾部的横向滚动（用户报的「内容多的时候显示不全，还没有滚动条」） ----
        //
        // 【为什么这组用例不做「只看控件在不在」的空断言】只看控件存在，对一条永远为 0 的滚动范围
        // 同样成立 —— 那正是这个缺陷活下来的方式。下面每条都作用在**产品自己的**属性上：
        //   * HScrollBarMaximum / HScrollBarValue / MaxHOffset / HOffset 直接读那个横向滚动条的
        //     真实 Maximum/Value，以及视图自己算出来的范围与偏移；
        //   * 触发路径走**产品自己的**公开入口（Width/Height/HOffset 属性、SetLines），
        //     不走测试侧重算的公式 —— 判据是「不同宽度的文本、不同的可用宽度，范围必须跟着变」，
        //     而不是「某个数字等于多少像素」（像素随字体与 DPI 变，钉不住）。
        // 【故意不在这里的】真的把「Shift + 滚轮」事件投进来、真的按住滚动条滑块拖：
        // 这两种都要一条活的消息循环与真实输入，硬写就是假测试。它们的手工核验见
        // .superpowers\fixes\log-scroll-report.md 的「无法自动化」一节。

        // 日志行经常是完整的绝对中文路径（用户那次报障里就是），右侧一旦超出就再也读不到。
        // 这条钉住：**横向滚动条真的在控件树里**，而且就是 HScrollBar 那个类型。
        H.Run("Gui.LogTailHasAHorizontalScrollBar", delegate {
            GuiProbe.WithStaObject("Rerar.LogTailView", null, delegate(object view) {
                object bar = FindScrollBar(view, "HScrollBar");
                AssertEq(GuiProbe.TypeName(bar), "HScrollBar");
                // 反证：竖向那根**不能**被换成横向的（两根各司其职）。
                AssertEq(GuiProbe.TypeName(FindScrollBar(view, "VScrollBar")), "VScrollBar");

                // 两根必须同时挂在同一个容器上，且父窗就是日志视图自己。
                AssertTrue(ReferenceEquals(GuiProbe.Prop(bar, "Parent"), view));
            }); });

        // 横向范围必须由**当前可见行里最宽的那一行**决定，并且用真实字体量（TextRenderer），
        // 不是「字符数 × 某个假设的字宽」。两条判据合起来把这件事钉死：
        //   (a)(b) 范围 = 最宽可见行的实测宽度 - 视口宽度，多一分少一分都红；
        //   (c)    同一条文本、同一个视口，字号放大 3 倍 ⇒ 量出来的宽度成倍变大
        //          （按固定字宽算的实现会给出同一个数，这里当场红）。
        H.Run("Gui.LogTailHorizontalRangeFollowsWidestVisibleLine", delegate {
            GuiProbe.WithStaObject("Rerar.LogTailView", null, delegate(object view) {
                GuiProbe.SetProp(view, "Width", 400);
                GuiProbe.SetProp(view, "Height", 200);
                // 两条日志行，正是本项目日志里那种完整中文路径（用户报障那次产出的就是这一条）。
                // 刻意让两条的实测宽度拉开一大段距离：「夹具太短以致装得下」这种与本缺陷无关的
                // 失败就不会混进来（前提在下面显式断言出来）。
                string narrow = "E:\\中国移动广东有限公司采购代理机构工作指导手册20260826(1)\\附件3：采购文件示范文本（试行）.doc";
                string wide = narrow + "（第二次修订版）";

                GuiProbe.Call(view, "SetLines", new object[] { new string[] { "短行", narrow } });
                int narrowRange = Convert.ToInt32(GuiProbe.Prop(view, "MaxHOffset"));
                int narrowWidest = Convert.ToInt32(GuiProbe.Prop(view, "WidestVisibleLineWidth"));
                int viewport = Convert.ToInt32(GuiProbe.Prop(view, "HViewportWidth"));
                AssertTrue(viewport > 0);

                // 前提自证：这个视口确实装不下那两行 —— 否则下面「范围 > 0」会因为夹具没超宽而失败，
                // 那种失败与产品无关（前提在这里显式写出来，不让读者去猜）。
                AssertTrue(narrowWidest > viewport);

                GuiProbe.Call(view, "SetLines", new object[] { new string[] { "短行", wide } });
                int wideRange = Convert.ToInt32(GuiProbe.Prop(view, "MaxHOffset"));
                int wideWidest = Convert.ToInt32(GuiProbe.Prop(view, "WidestVisibleLineWidth"));
                AssertTrue(wideWidest > narrowWidest);

                // (a) 超出视口的行 ⇒ 范围非 0；更宽的行 ⇒ 更大的范围（差值就是多出来的那截字宽）。
                // 另外：默认**不动** —— 范围是 0 起算的，日志一出来不会自己横着跳到中间。
                AssertTrue(narrowRange > 0);
                AssertTrue(wideRange > narrowRange);
                AssertEq(Convert.ToInt32(GuiProbe.Prop(view, "HOffset")), 0);

                // (b) 【范围算得对】范围 = 最宽可见行的实测宽度 - 视口宽度，一分不多一分不少。
                // 光断言「范围 > 0」的话，把宽度算成「字符数 × 8」这种错也照样是绿的。
                AssertEq(narrowRange + viewport, narrowWidest);
                AssertEq(wideRange + viewport, wideWidest);
                AssertEq(wideRange - narrowRange, wideWidest - narrowWidest);

                // (c) 【量的是「字体渲染出来的宽度」，不是「字符数 × 某个假设的字宽」】
                // 上面 (b) 三条把「宽度 → 范围」这条算术钉死了，但它管不到「宽度本身是怎么来的」。
                // 这里改用**同一字体下、字符数同比增减**的两条文本：中文与字母混排时宽度大致随字符数
                // 线性增长，于是「宽度之比 ≈ 字符数之比」—— 按固定字宽硬算的实现给不出这个比例关系。
                // 【为什么不用换字体的办法】实测在「反射载入 + 无消息循环的 STA 夹具」里给控件换
                // Font 后再量，GDI+ 的字体映射会给出 0 宽（产品里换字体的路径由 OnFontChanged →
                // SyncBar 覆盖；那一条没能自动化，见 .superpowers\fixes\log-scroll-report.md）。
                double widthRatio = (double)wideWidest / narrowWidest;
                double charRatio = (double)wide.Length / narrow.Length;
                AssertTrue(widthRatio > charRatio);
                AssertTrue(widthRatio < charRatio * 1.5);

                // (d) 滚动条的范围必须真的等于产品算出来的可滚动量：「Maximum - LargeChange + 1」
                // 正是 WinForms 里滑块能到达的最右位置（裸 Maximum 是文档长度，不是可滚动量）。
                object bar = FindScrollBar(view, "HScrollBar");
                AssertEq(Convert.ToInt32(GuiProbe.Prop(view, "HScrollBarMaximum")), wideRange);
                AssertEq(Convert.ToInt32(GuiProbe.Prop(bar, "Maximum")) -
                         Convert.ToInt32(GuiProbe.Prop(bar, "LargeChange")) + 1, wideRange);

                // (e) 视口比那一行还宽 ⇒ 一个字都不用滚：范围归 0，滚动条逻辑上关闭（不做无意义的滚动）。
                GuiProbe.SetProp(view, "Width", 4000);
                AssertEq(Convert.ToInt32(GuiProbe.Prop(view, "MaxHOffset")), 0);
                AssertEq(Convert.ToInt32(GuiProbe.Prop(view, "HScrollBarMaximum")), 0);
                AssertFalse(Convert.ToBoolean(GuiProbe.Prop(FindScrollBar(view, "HScrollBar"), "Enabled")));
                AssertEq(Convert.ToInt32(GuiProbe.Prop(view, "HOffset")), 0);   // 范围没了 ⇒ 偏移必须回 0
            }); });

        // 偏移两端的夹取：右端不许超过「最宽可见行 - 视口」，左端不许为负；越界的赋值要被夹回来
        //（不是静默吞掉），范围一旦不再需要就**回 0**而不是停在半路。
        H.Run("Gui.LogTailHorizontalOffsetClampsAtBothEnds", delegate {
            GuiProbe.WithStaObject("Rerar.LogTailView", null, delegate(object view) {
                GuiProbe.SetProp(view, "Width", 400);
                GuiProbe.SetProp(view, "Height", 120);
                string wide = "E:\\中国移动广东有限公司采购代理机构工作指导手册20260826(1)\\附件3：采购文件示范文本（试行）.doc";
                GuiProbe.Call(view, "SetLines", new object[] { new string[] { wide, "短行" } });

                int max = Convert.ToInt32(GuiProbe.Prop(view, "MaxHOffset"));
                AssertTrue(max > 0);                                  // 前提自证：夹具确实超出了视口

                // 右端：写一个远超范围的偏移 ⇒ 停在范围上，且滚动条的 Value 与它一致。
                GuiProbe.SetProp(view, "HOffset", max + 5000);
                AssertEq(Convert.ToInt32(GuiProbe.Prop(view, "HOffset")), max);
                AssertEq(Convert.ToInt32(GuiProbe.Prop(view, "HScrollBarValue")), max);
                AssertTrue(Convert.ToInt32(GuiProbe.Prop(view, "HScrollBarValue")) <=
                           Convert.ToInt32(GuiProbe.Prop(view, "MaxHOffset")));   // 「绝不超过范围」这条契约

                // 左端：负数夹回 0。
                GuiProbe.SetProp(view, "HOffset", -1);
                AssertEq(Convert.ToInt32(GuiProbe.Prop(view, "HOffset")), 0);

                // 内容换短（最宽行变窄）⇒ 原来的偏移失效，必须归 0，绝不留下越界的陈旧值。
                GuiProbe.SetProp(view, "HOffset", max);
                GuiProbe.Call(view, "SetLines", new object[] { new string[] { "短行" } });
                AssertEq(Convert.ToInt32(GuiProbe.Prop(view, "MaxHOffset")), 0);
                AssertEq(Convert.ToInt32(GuiProbe.Prop(view, "HOffset")), 0);

                // 视口变宽同理（同一个「偏移不再适用 ⇒ 回 0」的规则，走的是另一条路径：Resize）。
                GuiProbe.Call(view, "SetLines", new object[] { new string[] { wide } });
                GuiProbe.SetProp(view, "HOffset", Convert.ToInt32(GuiProbe.Prop(view, "MaxHOffset")));
                GuiProbe.SetProp(view, "Width", 4000);
                AssertEq(Convert.ToInt32(GuiProbe.Prop(view, "HOffset")), 0);
                AssertEq(Convert.ToInt32(GuiProbe.Prop(view, "HScrollBarMaximum")), 0);
            }); });

        // 竖向那根的行为**不许**被这次改动带坏：范围仍然按「尾部 2000 行」的口径走，并且滚动条
        // 自己的 Maximum/Value 仍然自洽（Value 不超过 Maximum - LargeChange + 1，用户才拖得到底）。
        H.Run("Gui.LogTailVerticalScrollBarStillMatchesTailSemantics", delegate {
            GuiProbe.WithStaObject("Rerar.LogTailView", null, delegate(object view) {
                GuiProbe.SetProp(view, "Width", 600);
                GuiProbe.SetProp(view, "Height", 200);      // 十几行可见
                int lines = 2000;                            // 就是要钉的那个尾部容量本身
                string[] all = new string[lines];
                for (int i = 0; i < all.Length; i++) { all[i] = "第" + i + "行"; }
                GuiProbe.Call(view, "SetLines", new object[] { all });

                int visible = Convert.ToInt32(GuiProbe.Prop(view, "VisibleLineCount"));
                AssertTrue(visible >= 2);                    // 200px 的窗口绝不止一行
                int first = Convert.ToInt32(GuiProbe.Prop(view, "FirstVisibleLine"));
                AssertEq(first, lines - visible);            // 默认自动滚动 ⇒ 视口停在尾部

                object bar = FindScrollBar(view, "VScrollBar");
                int maximum = Convert.ToInt32(GuiProbe.Prop(bar, "Maximum"));
                int large = Convert.ToInt32(GuiProbe.Prop(bar, "LargeChange"));
                int value = Convert.ToInt32(GuiProbe.Prop(bar, "Value"));
                AssertEq(maximum, first + visible - 1);      // 旧的 Maximum 语义原样保留
                AssertEq(value, first);
                AssertTrue(value <= maximum - large + 1);    // 滑块停得到底（否则最后一屏永远看不见）
                AssertTrue(Convert.ToBoolean(GuiProbe.Prop(bar, "Enabled")));

                // 行数变化 ⇒ 竖向范围跟着走（不是构造时算一次就不动了）。
                int rangeWith2000 = maximum - Convert.ToInt32(GuiProbe.Prop(view, "VScrollBarMinimum"));
                GuiProbe.Call(view, "SetLines", new object[] { new string[1500] });
                int rangeWith1500 = Convert.ToInt32(GuiProbe.Prop(view, "VScrollBarMaximum")) -
                                    Convert.ToInt32(GuiProbe.Prop(view, "VScrollBarMinimum"));
                AssertTrue(rangeWith1500 < rangeWith2000);

                // 行数少于可视行数 ⇒ 竖向范围 0、滚动条关闭（与横向那条的语义一致）。
                GuiProbe.Call(view, "SetLines", new object[] { new string[] { "只有一行" } });
                AssertEq(Convert.ToInt32(GuiProbe.Prop(view, "VScrollBarMaximum")), 0);
                AssertFalse(Convert.ToBoolean(GuiProbe.Prop(FindScrollBar(view, "VScrollBar"), "Enabled")));
            }); });

        // 长行真的能读到末尾：把偏移推到范围上（最右），偏移仍然合法，而且这时候**可见行没有变**
        // —— 横向滚动不许把纵向视口带跑（用户正在看的那一行必须还在原地）。
        H.Run("Gui.LogTailLongLineStaysReachableAtFullOffset", delegate {
            GuiProbe.WithStaObject("Rerar.LogTailView", null, delegate(object view) {
                GuiProbe.SetProp(view, "Width", 360);
                GuiProbe.SetProp(view, "Height", 160);
                GuiProbe.Call(view, "SetLines", new object[] {
                    new string[] { "第一行",
                        "E:\\中国移动广东有限公司采购代理机构工作指导手册20260826(1)\\附件3：采购文件示范文本（试行）.doc",
                        "第三行" } });

                int firstBefore = Convert.ToInt32(GuiProbe.Prop(view, "FirstVisibleLine"));
                int max = Convert.ToInt32(GuiProbe.Prop(view, "MaxHOffset"));
                AssertTrue(max > 0);                     // 前提自证：这一行真的超出了视口

                GuiProbe.SetProp(view, "HOffset", max);
                AssertEq(Convert.ToInt32(GuiProbe.Prop(view, "HOffset")), max);
                AssertEq(Convert.ToInt32(GuiProbe.Prop(view, "FirstVisibleLine")), firstBefore);

                // 最右端时「偏移 + 视口宽度」必须**恰好盖住**那一整段文本（= 最宽可见行的实测宽度）：
                // 这是「末尾可达」的程序化判据 —— 少一像素就说明尾巴还差一点读不到。
                // 不写像素常量：视口宽度、范围、最宽行宽三者都由产品自己给，判据是它们之间的关系。
                int viewport = Convert.ToInt32(GuiProbe.Prop(view, "HViewportWidth"));
                int widest = Convert.ToInt32(GuiProbe.Prop(view, "WidestVisibleLineWidth"));
                AssertTrue(viewport > 0);
                AssertEq(max + viewport, widest);        // 不多不少：正好滚到最后一列
            }); });
    }

    // 在日志视图的**直接子控件**里按类型名找滚动条（GuiProbe 没有按类型找控件的方法）。
    // 找不到就抛：控件被换类型/删掉必须算 FAIL，绝不能静默跳过（与 GuiProbe.Find 同一立场）。
    private static object FindScrollBar(object view, string typeName)
    {
        int count = GuiProbe.ControlCount(view);
        for (int i = 0; i < count; i++)
        {
            object child = GuiProbe.PropPath(view, "Controls[" + i + "]");
            if (string.Equals(GuiProbe.TypeName(child), typeName, StringComparison.Ordinal)) { return child; }
        }
        throw new InvalidOperationException("日志视图的直接子控件里没有 " + typeName + "（共 " + count + " 个子控件）");
    }

    private static bool MenuItemEnabled(object menu, int index)
    {
        return Convert.ToBoolean(GuiProbe.PropPath(menu, "Items[" + index + "].Enabled"));
    }
}
