// Task 14：界面用例的反射探针。
//
// 【为什么必须走反射 —— 这不是取巧，是全局约束逼出来的唯一形状】
// build\build.ps1 只给**应用目标**加 System.Windows.Forms / System.Drawing 与 /win32manifest:
//（Task 13 的 /resource: 也是同一条规矩：只给应用目标），所以 dist\tests.exe 在编译期拿不到
// Control / CheckBox 这些类型。而 brief 要求的用例断言的是**产品里那个真实的 Form**：
// new MainForm()、f.Controls.Find("dropZone", true)、chkDelete.Checked —— 替身或桩件在这里一律
// 等于假测试。于是这里从**同目录的 dist\Rerar.exe** 载入真实类型，用反射读它真实的控件树。
//
// 同目录读取产品目标在本项目有先例：EngineLocator 的用例正是从 dist\Rerar.exe 读内嵌资源
// （因为测试目标里没有那份载荷）。载入的是构建产物本身 ⇒ 用例覆盖的就是真正会被双击的东西。
//
// 【绝不用它掩盖失败】Rerar.exe 缺失、类型缺失、构造超时都是**异常**（于是用例 FAIL），
// 只有「本机 COM 造不出 .lnk」这类夹具问题才由用例自己 H.Skip。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

internal static class GuiProbe
{
    // 控件树 / 属性访问的缓存：一个用例里可能调用几千次（日志尾部用例要写 5000 行）。
    private static readonly Dictionary<string, MemberInfo> _members = new Dictionary<string, MemberInfo>();

    private static Assembly _app;

    // dist\Rerar.exe（构建产物）里的程序集。加载失败一律抛异常并说明原因 —— 那必须是用例的 FAIL。
    public static Assembly App
    {
        get
        {
            if (_app == null)
            {
                string path = TestEnv.ExePath;
                if (!File.Exists(path))
                {
                    throw new InvalidOperationException(
                        "找不到应用产物「" + path + "」：界面用例断言的是产品里那个真实的 MainForm，" +
                        "产物缺席必须算失败，不能算跳过");
                }
                try
                {
                    _app = Assembly.LoadFrom(path);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "无法载入应用产物「" + path + "」（" + ex.GetType().Name + "：" + ex.Message + "）");
                }
            }
            return _app;
        }
    }

    public static Type Type(string fullName)
    {
        Type t = App.GetType(fullName, false);
        if (t == null)
        {
            throw new InvalidOperationException("应用产物里没有类型「" + fullName + "」：" +
                "它应当是 src\\App\\MainForm.cs 里的实现");
        }
        return t;
    }

    // ------------------------------------------------------------------
    // 构造 / 调用 / 读写
    // ------------------------------------------------------------------

    // 按类型名 + 构造参数创建实例（找不到构造器或构造失败一律抛异常）。
    public static object New(string typeName, params object[] args)
    {
        Type t = Type(typeName);
        object[] actual = args == null ? new object[0] : args;

        ConstructorInfo best = null;
        foreach (ConstructorInfo c in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (c.GetParameters().Length == actual.Length) { best = c; break; }
        }
        if (best == null)
        {
            throw new InvalidOperationException("类型「" + typeName + "」没有接受 " + actual.Length + " 个参数的构造器");
        }
        return Unwrap(delegate { return best.Invoke(actual); });
    }

    // 静态方法调用（用例用它测纯函数：文本格式化、字体探测、.lnk 解析、设置读写）。
    public static object Static(string typeName, string method, object[] args)
    {
        Type t = Type(typeName);
        object[] actual = args == null ? new object[0] : args;

        foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (!string.Equals(m.Name, method, StringComparison.Ordinal)) { continue; }
            if (m.GetParameters().Length != actual.Length) { continue; }
            MethodInfo target = m;
            return Unwrap(delegate { return target.Invoke(null, actual); });
        }
        throw new InvalidOperationException("类型「" + typeName + "」没有静态方法「" + method + "」/" + actual.Length + " 个参数");
    }

    public static object Call(object target, string method, object[] args)
    {
        if (target == null) { throw new ArgumentNullException("target"); }
        object[] actual = args == null ? new object[0] : args;

        foreach (MethodInfo m in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (!string.Equals(m.Name, method, StringComparison.Ordinal)) { continue; }
            if (m.GetParameters().Length != actual.Length) { continue; }
            MethodInfo found = m;
            return Unwrap(delegate { return found.Invoke(target, actual); });
        }
        throw new InvalidOperationException("类型「" + target.GetType().FullName + "」没有实例方法「" + method + "」");
    }

    // 反射调用把被测代码抛的异常包在 TargetInvocationException 里。必须**拆开**再抛：
    // 否则用例报告里只有一句「调用的目标发生了异常」，真正的失败原因（中文、带类型）就丢了 ——
    // 那正是「失败信息要可操作」这条要求的反面。
    private static object Unwrap(Func<object> invoke)
    {
        try
        {
            return invoke();
        }
        catch (TargetInvocationException ex)
        {
            if (ex.InnerException != null) { throw ex.InnerException; }
            throw;
        }
    }

    // 读属性**或**字段（同一个名字两处都找）：GUI 控件暴露属性，AppSettings 之类的数据类用字段。
    public static object Prop(object target, string name)
    {
        if (target == null) { throw new ArgumentNullException("target"); }
        Type t = target.GetType();

        MemberInfo member = Member(t, name);
        PropertyInfo p = member as PropertyInfo;
        if (p != null) { return p.GetValue(target, null); }
        return ((FieldInfo)member).GetValue(target);
    }

    public static void SetProp(object target, string name, object value)
    {
        if (target == null) { throw new ArgumentNullException("target"); }
        Type t = target.GetType();

        MemberInfo member = Member(t, name);
        PropertyInfo p = member as PropertyInfo;
        if (p != null) { p.SetValue(target, value, null); return; }
        ((FieldInfo)member).SetValue(target, value);
    }

    private static MemberInfo Member(Type t, string name)
    {
        // 缓存键必须带上**程序集**：Rerar.Core.ArchiveResult 在 tests.exe 与 Rerar.exe 里是两个
        // FullName 相同、类型身份不同的类型（跨程序集没有类型统一）。只按 FullName 做键会把
        // 另一个程序集的成员缓存串过来，症状是莫名其妙的转型失败。
        string key = t.Assembly.FullName + "|" + t.FullName + "::" + name;
        MemberInfo cached;
        if (_members.TryGetValue(key, out cached)) { return cached; }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        MemberInfo found = t.GetProperty(name, flags);
        if (found == null) { found = t.GetField(name, flags); }
        if (found == null)
        {
            throw new InvalidOperationException("类型「" + t.FullName + "」既没有属性也没有字段「" + name + "」");
        }
        _members[key] = found;
        return found;
    }

    // ------------------------------------------------------------------
    // 控件树
    // ------------------------------------------------------------------

    // 在**专用 STA 线程**上构造 MainForm、执行 body、随后 Dispose。
    // 为什么单开线程：WinForms 要求 STA（Application.Run 的线程就是 STA），而测试运行器的主线程是
    // MTA —— 在 MTA 上建窗口/注册拖放走的是另一条路径，测出来的东西与产品不是同一件事。
    // body 里抛出的异常（含断言失败）原样抛回调用线程：断言失败仍是 FAIL，绝不被吞掉。
    public static void WithForm(Action<object> body)
    {
        if (body == null) { throw new ArgumentNullException("body"); }

        Exception failure = null;
        Thread thread = new Thread(delegate()
        {
            object form = null;
            try
            {
                form = New("Rerar.MainForm", null);
                body(form);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (form != null)
                {
                    try { Call(form, "Dispose", null); }
                    catch (Exception) { }
                }
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // 有界等待：构造 Form 是同步的（不起消息循环），若 30 秒还没回来那就是真的卡住了 ——
        // 那必须是 FAIL，绝不能把整轮测试吊死。
        if (!thread.Join(30000))
        {
            throw new InvalidOperationException("构造/检查 MainForm 超过 30 秒未返回：界面构造里有阻塞调用");
        }
        if (failure != null) { throw failure; }
    }

    // 在**专用 STA 线程**上构造任意界面类型、执行 body、随后 Dispose。
    // 与 WithForm 同一套理由与时序（WinForms 要 STA；有界等待；异常原样抛回调用线程），
    // 只是靶子不是主窗体（例如自绘日志视图）。
    public static void WithStaObject(string typeName, object[] args, Action<object> body)
    {
        if (body == null) { throw new ArgumentNullException("body"); }

        Exception failure = null;
        Thread thread = new Thread(delegate()
        {
            object instance = null;
            try
            {
                instance = New(typeName, args);
                body(instance);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (instance != null)
                {
                    try { Call(instance, "Dispose", null); }
                    catch (Exception) { }
                }
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(30000))
        {
            throw new InvalidOperationException("构造/检查 " + typeName + " 超过 30 秒未返回");
        }
        if (failure != null) { throw failure; }
    }

    public static int ControlCount(object control)
    {
        object controls = Prop(control, "Controls");
        return Convert.ToInt32(Prop(controls, "Count"));
    }

    // 按 Name 递归找控件（等价于 brief 里的 f.Controls.Find(name, true)）。找不到抛异常。
    public static object Find(object control, string name)
    {
        Array found = FindAll(control, name);
        if (found.Length == 0)
        {
            throw new InvalidOperationException("控件树里找不到名为「" + name + "」的控件");
        }
        return found.GetValue(0);
    }

    public static int FindCount(object control, string name)
    {
        return FindAll(control, name).Length;
    }

    private static Array FindAll(object control, string name)
    {
        object controls = Prop(control, "Controls");
        MethodInfo find = null;
        foreach (MethodInfo m in controls.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!string.Equals(m.Name, "Find", StringComparison.Ordinal)) { continue; }
            ParameterInfo[] ps = m.GetParameters();
            if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(bool))
            {
                find = m;
                break;
            }
        }
        if (find == null) { throw new InvalidOperationException("控件集合上找不到 Find(string, bool)"); }
        return (Array)find.Invoke(controls, new object[] { name, true });
    }

    // child 是不是 ancestor 的后代（沿 Parent 链往上走）：用来钉住「删除项确实在危险分区里」。
    public static bool IsDescendantOf(object child, object ancestor)
    {
        object current = child;
        for (int i = 0; i < 64 && current != null; i++)
        {
            if (ReferenceEquals(current, ancestor)) { return true; }
            object parent = Prop(current, "Parent");
            current = parent;
        }
        return false;
    }

    public static bool Checked(object control)
    {
        return Convert.ToBoolean(Prop(control, "Checked"));
    }

    public static string TextOf(object control)
    {
        object text = Prop(control, "Text");
        return text == null ? "" : text.ToString();
    }

    public static string TypeName(object instance)
    {
        return instance == null ? "" : instance.GetType().Name;
    }

    // ------------------------------------------------------------------
    // .lnk 夹具（测试侧自己造，走 WScript.Shell 晚绑定，不引 COM 接口定义）
    // ------------------------------------------------------------------

    // 造一个 System.Windows.Forms.MethodInvoker（MainForm.UiPost 的参数类型）。
    // 测试目标不引 WinForms，编译期拿不到这个类型，所以从 UiPost 自己的**参数类型**现取 ——
    // 比按名字猜「System.Windows.Forms.MethodInvoker」可靠：签名一变这里当场失败，而不是悄悄找不到。
    public static Delegate MakeInvoker(object target, string targetMethod)
    {
        Type holder = Type("Rerar.MainForm");
        Type invokerType = null;
        foreach (MethodInfo m in holder.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (!string.Equals(m.Name, "UiPost", StringComparison.Ordinal)) { continue; }
            ParameterInfo[] parameters = m.GetParameters();
            if (parameters.Length != 1) { continue; }
            invokerType = parameters[0].ParameterType;
            break;
        }

        if (invokerType == null)
        {
            throw new InvalidOperationException("Rerar.MainForm 上没有 UiPost(委托) 这个编组入口：界面编组契约变了");
        }
        return Delegate.CreateDelegate(invokerType, target, targetMethod);
    }

    // ------------------------------------------------------------------
    // 跨程序集的结果对象：界面里的方法签名要的是 **Rerar.exe 那份** Rerar.Core.ArchiveResult。
    //
    // 【为什么不能直接用 tests.exe 自己的 ArchiveResult】两个产物各自编了同一份 src\Core\*.cs，
    // 于是 FullName 相同而类型身份不同 —— 把 tests.exe 的 List<ArchiveResult> 传给 Rerar.exe 的方法
    // 会得到「无法转换」的 ArgumentException（实测）。所以在应用产物里现造类型与列表。
    // ------------------------------------------------------------------

    // 应用产物里的 List<Rerar.Core.ArchiveResult>（以 IList 暴露，Add 用非泛型接口）。
    public static object NewResultList()
    {
        Type resultType = Type("Rerar.Core.ArchiveResult");
        Type listType = typeof(List<>).MakeGenericType(new Type[] { resultType });
        return Activator.CreateInstance(listType);
    }

    // 应用产物里的 ArchiveResult（只填界面上用到的字段）。
    public static object NewResult(string path, string statusName, int files)
    {
        Type resultType = Type("Rerar.Core.ArchiveResult");
        object result = Activator.CreateInstance(resultType);

        SetProp(result, "Path", path);

        // 结局枚举也必须是**应用产物里**那一个（跨程序集没有类型统一）。
        // 注意 ArchiveResult.Status 是**字段**不是属性 —— 用 GetProperty 拿会得到 null。
        SetProp(result, "Status", Enum.Parse(Type("Rerar.Core.ArchiveStatus"), statusName));
        SetProp(result, "Files", files);
        return result;
    }

    // 造一个真实的 .lnk 快捷方式。造不出来时返回 false 并给出原因（用例据此 H.Skip —— 那是**夹具**
    // 问题，不是产品缺陷）。产品侧的解析由 MainForm.TryResolveShortcut 负责，本方法只用于造夹具。
    public static bool TryMakeShortcut(string lnkPath, string targetPath, out string problem)
    {
        problem = null;
        try
        {
            Type shellType = System.Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) { problem = "本机没有注册 WScript.Shell（ProgID 不存在）"; return false; }

            object shell = Activator.CreateInstance(shellType);
            object shortcut = shellType.InvokeMember(
                "CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (shortcut == null) { problem = "CreateShortcut 返回 null"; return false; }

            shortcut.GetType().InvokeMember(
                "TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
            shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        catch (Exception ex)
        {
            problem = ex.GetType().Name + "：" + ex.Message;
            return false;
        }

        if (!File.Exists(lnkPath)) { problem = "Save 之后 .lnk 仍不存在"; return false; }
        return true;
    }

    // 在字节流里找一段 ASCII 子串（用于「manifest 被真的编进了应用产物」这类断言）。
    public static bool ContainsAscii(byte[] data, string needle)
    {
        if (data == null || needle == null || needle.Length == 0) { return false; }
        if (data.Length < needle.Length) { return false; }

        for (int i = 0; i <= data.Length - needle.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (data[i + j] != (byte)needle[j]) { hit = false; break; }
            }
            if (hit) { return true; }
        }
        return false;
    }
}
