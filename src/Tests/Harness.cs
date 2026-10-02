// Rerar 单元测试运行器（无框架、无第三方依赖）：与 src\Core\*.cs 一起编进 dist\tests.exe。
//
// 约定（后续任务的测试文件照此编写）：
//   1. 测试文件放在 src\Tests\，类名以 Tests 结尾，并声明 public static void Run()；
//   2. 运行器用反射自动发现全部 *Tests 类（按类名序数排序）并依次执行，无需注册；
//   3. 测试类继承 TestBase，即可直接写 AssertEq/AssertTrue/AssertFalse（不必加 H. 前缀），
//      与 brief 里所有示例的写法一致；
//   4. 断言一律写成 AssertEq(实际值, 期望值) —— brief 全部示例都是这个参数顺序；
//   5. 每个用例开始前 H.Run 会调 TestEnv.Cleanup()，故 Tmp/OutRoot 每用例都是全新的空目录；
//   6. 任一用例失败不中断整轮，最后 H.Report() 汇总并以退出码 1 结束。
//
// C# 5 语法；源码一律 UTF-8 带 BOM（csc 另加 /codepage:65001 双保险）。

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

internal static class H
{
    // 单个用例的结果：Error 为 null 表示通过。
    private sealed class Result
    {
        public string Name;
        public string Error;
    }

    // 断言失败专用异常：与「测试体自己抛的异常」区分开，便于报告里给出不同措辞。
    private sealed class AssertionException : Exception
    {
        public AssertionException(string message) : base(message) { }
    }

    private static readonly List<Result> _results = new List<Result>();

    // 执行一个用例：先重置每用例独立的临时目录，再跑测试体。
    // 断言失败或任何异常都只记为一条 FAIL，不中断整轮测试。
    public static void Run(string name, Action body)
    {
        try
        {
            TestEnv.Cleanup();
            body();
            Record(name, null);
        }
        catch (Exception ex)
        {
            Record(name, Describe(ex));
        }
    }

    // 断言相等。参数顺序与 brief 全部示例一致：a = 实际值，b = 期望值。
    public static void AssertEq<T>(T a, T b)
    {
        if (!EqualityComparer<T>.Default.Equals(a, b))
        {
            throw new AssertionException("期望 [" + Dump(b) + "]，实际 [" + Dump(a) + "]");
        }
    }

    public static void AssertTrue(bool condition)
    {
        if (!condition)
        {
            throw new AssertionException("期望 true，实际 false");
        }
    }

    public static void AssertFalse(bool condition)
    {
        if (condition)
        {
            throw new AssertionException("期望 false，实际 true");
        }
    }

    // 打印 PASS/FAIL 汇总表：任一用例失败返回 1，全通过返回 0。
    public static int Report()
    {
        int pass = 0;
        int fail = 0;
        foreach (Result r in _results)
        {
            if (r.Error == null) { pass++; } else { fail++; }
        }

        Console.WriteLine("----");
        Console.WriteLine("用例 " + _results.Count + " 个：PASS " + pass + "，FAIL " + fail);

        if (fail > 0)
        {
            Console.WriteLine("失败用例：");
            foreach (Result r in _results)
            {
                if (r.Error != null) { Console.WriteLine("  " + r.Name); }
            }
            Console.WriteLine("FAIL: " + fail + " 个用例未通过");
            return 1;
        }

        Console.WriteLine("PASS: 全部用例通过");
        return 0;
    }

    // 进程入口（dist\tests.exe 的唯一 Main：测试目标不编译 src\App\*.cs）。
    public static int Main()
    {
        try
        {
            // 预检：本机 7-Zip 缺失时 TestEnv.SevenZip 抛异常，整轮测试直接失败并说明原因。
            string sevenZip = TestEnv.SevenZip;
            if (string.IsNullOrEmpty(sevenZip))
            {
                Console.WriteLine("FAIL: TestEnv.SevenZip 为空，无法定位本机 7-Zip");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: " + ex.Message);
            return 1;
        }

        foreach (Type t in DiscoverTestClasses())
        {
            MethodInfo entry = TestRunMethod(t);
            try
            {
                entry.Invoke(null, null);
            }
            catch (TargetInvocationException ex)
            {
                // 测试类自身在 H.Run 之外抛异常（例如 fixture 构造写在 Run() 里）
                Record(t.Name + ".Run()", Describe(ex.InnerException == null ? ex : ex.InnerException));
            }
            catch (Exception ex)
            {
                Record(t.Name + ".Run()", Describe(ex));
            }
        }

        if (_results.Count == 0)
        {
            Console.WriteLine("FAIL: 未执行任何用例（需要 src\\Tests\\*Tests.cs 里 public static void Run()）");
            return 1;
        }

        int code = Report();
        TestEnv.Cleanup();   // 收尾：清掉最后一个用例留下的临时文件
        return code;
    }

    private static void Record(string name, string error)
    {
        _results.Add(new Result { Name = name, Error = error });
        if (error == null)
        {
            Console.WriteLine("PASS " + name);
        }
        else
        {
            Console.WriteLine("FAIL " + name);
            Console.WriteLine("     " + error);
        }
    }

    // 发现约定：类名以 Tests 结尾，且自身声明了 public static void Run()。
    private static List<Type> DiscoverTestClasses()
    {
        List<Type> found = new List<Type>();
        foreach (Type t in Assembly.GetExecutingAssembly().GetTypes())
        {
            if (!t.Name.EndsWith("Tests", StringComparison.Ordinal)) { continue; }
            if (TestRunMethod(t) == null) { continue; }
            found.Add(t);
        }
        found.Sort(delegate(Type x, Type y) { return string.CompareOrdinal(x.FullName, y.FullName); });
        return found;
    }

    private static MethodInfo TestRunMethod(Type t)
    {
        MethodInfo m = t.GetMethod(
            "Run",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
            null,
            Type.EmptyTypes,
            null);
        if (m == null || m.ReturnType != typeof(void)) { return null; }
        return m;
    }

    private static string Describe(Exception ex)
    {
        if (ex is AssertionException) { return ex.Message; }
        return ex.GetType().Name + "：" + ex.Message;
    }

    // 打印值：null 显式标注；字符串中的控制字符转成 \xNN，避免二进制 fixture 搅乱输出。
    private static string Dump(object value)
    {
        if (value == null) { return "null"; }

        string s = value as string;
        if (s == null) { return value.ToString(); }

        StringBuilder sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            if (c < ' ' || c == '\u007f') { sb.Append("\\x").Append(((int)c).ToString("x2")); }
            else { sb.Append(c); }
        }
        return sb.ToString();
    }
}

// 测试类基类：把 H 的断言助手以「非限定名」暴露给各 *Tests 类。
internal abstract class TestBase
{
    protected static void AssertEq<T>(T a, T b) { H.AssertEq(a, b); }
    protected static void AssertTrue(bool condition) { H.AssertTrue(condition); }
    protected static void AssertFalse(bool condition) { H.AssertFalse(condition); }
}
