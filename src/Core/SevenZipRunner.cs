// Rerar 的 7-Zip 调用入口（规格 §4.1/§4.3、不变式 I5；审计 G1）。
//
// 本类是「所有归档操作的唯一进程入口」：起进程、读管道、退出码、进度、子进程生命周期
// 全在这里，其它任务一律不许自己 Process.Start。
//
// 三条硬要求（任何一条被简化都算缺陷）：
//   1) I5 —— 绝不允许 7-Zip 等待 stdin。调用方必须显式传 -p<候选密码>，参数表里没有
//      -p 前缀项时本类直接抛 ArgumentException（宁可在起进程前失败）；
//      并且把子进程的 stdin 重定向后立刻关闭：万一 7z 仍然打印密码提示
//      （实测 a 命令 + 空 -p 会弹 "Enter password"），它读到 EOF 就立刻失败，不会挂起。
//   2) 无管道死锁 —— stdout/stderr 一律用 OutputDataReceived/ErrorDataReceived 异步事件累积，
//      绝不 WaitForExit() 之后再 ReadToEnd()。7z 用 \r 原地刷新进度，输出可达数 MB。
//   3) 无孤儿进程 —— 每个子进程都放进 Job Object 并设 JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE，
//      本进程（含 GUI 被强杀）结束时子进程一起死；AssignProcessToJobObject 失败绝不静默降级，
//      当场杀掉子进程并抛异常 —— 唯一的例外是「赋值时子进程已经退出」（实测返回 ERROR_ACCESS_DENIED），
//      那种情况没有孤儿可保护，如实放行。
//   4) 编码由本类集中保证（规格 §4.3）—— 命令行里钉死 -sccUTF-8，否则 7z 按 OEM 代码页输出控制台
//      文本，中文成员名在 StdOut 与进度回调里全变成替换字符 U+FFFD（实测）。调用方自己传了 -scc*
//      就以调用方的为准（本类不覆盖）。
//
// 线程约定：Run 每次调用只用局部状态，顺序调用之间不共享任何可变数据；多线程顺序调用安全
// （不支持两个 Run 并发）。onProgress 在读取管道的后台线程被调用，UI 线程的编组由调用方负责；
// 回调抛出的异常**不会**从管道线程逃出去（那会杀掉整个进程），而是被记下来、在 WaitForExit()
// 之后从 Run 原样抛出，见 Run 里对应的注释。
// TerminateAll 可从任意线程调用。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Rerar.Core
{
    // 一次 7-Zip 调用的结果（brief 的 Produces 形状：public 字段，不是属性）。
    public sealed class RunResult
    {
        public int ExitCode;
        public string StdOut;
        public string StdErr;
    }

    public static class SevenZipRunner
    {
        // Job Object 被打断（TerminateAll / 取消）时给子进程的退出码 = Win32 ERROR_CANCELLED。
        // 刻意不用 1：IsSuccess 认为 0 与 1 都是成功，被取消的运行绝不能看起来像成功。
        private const uint CancelledExitCode = 1223;

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        // 命令行里需要加引号的字符（Windows 的命令行分隔/空白）。
        private static readonly char[] WhitespaceChars = new char[] { ' ', '\t', '\n', '\v', '\f', '\r' };

        // 当前活动的 Job Object（TerminateAll 用）。只在 _jobGate 保护下读写。
        private static readonly object _jobGate = new object();
        private static IntPtr _activeJob = IntPtr.Zero;

        // 跑一次 7-Zip。args 是 7z 的参数表（不含 exe 路径），必须显式含 -p 前缀项。
        public static RunResult Run(string sevenZipPath, string[] args, Action<int, string> onProgress, CancellationToken ct)
        {
            if (sevenZipPath == null) { throw new ArgumentNullException("sevenZipPath"); }
            if (args == null) { throw new ArgumentNullException("args"); }
            if (!HasPasswordSwitch(args))
            {
                // 不变式 I5：-y 压不住密码提示（实测：不给 -p 时 7z 会一直等 stdin），
                // 原 .bat 就是这么挂死的。宁可在起进程之前失败，也不允许一个可能永久等待输入的子进程跑起来。
                throw new ArgumentException(
                    "7-Zip 参数里必须显式带 -p（密码开关）：绝不允许子进程等待 stdin 输入密码（规格 I5）", "args");
            }

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = sevenZipPath;
            psi.Arguments = BuildCommandLine(args);
            psi.UseShellExecute = false;              // 不经过 cmd.exe/Shell：无引号层、无环境变量展开
            psi.CreateNoWindow = true;                // 不弹控制台窗口
            psi.RedirectStandardInput = true;         // 见 1)：机械保证 stdin 不会挂住子进程
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            StringBuilder stdOut = new StringBuilder();
            StringBuilder stdErr = new StringBuilder();
            object outputGate = new object();

            // onProgress 在管道读取线程上抛出的第一个异常（见下面 OutputDataReceived 里的说明）。
            // 由 progressGate 保护；Run 在 WaitForExit() 之后把它原样抛给调用方。
            object progressGate = new object();
            Exception[] progressFailure = new Exception[1];

            IntPtr job = CreateJob();
            Process process = new Process();
            process.StartInfo = psi;
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null) { return; }        // null 是「流结束」标记，不是一行内容
                lock (outputGate) { stdOut.Append(e.Data).Append('\n'); }
                try
                {
                    DispatchProgress(e.Data, onProgress);
                }
                catch (Exception ex)
                {
                    // 调用方回调抛出的异常绝不能从管道读取线程逃出去：
                    //  (a) .NET 2.0 起，任何线程上的未处理异常都直接终止整个进程 —— GUI 会在解压
                    //      中途带着 CLR 崩溃信息消失（实测：调用方抛异常后 stdout 立刻停止，进程退出码
                    //      0xE0434352）；Task 14 的 UI 编组遇到 ObjectDisposedException 就会踩到；
                    //  (b) 即使运行时不杀进程，读取循环一死就再也没有「流结束」通知，
                    //      无参 WaitForExit()（下面）会无限期阻塞 —— 本项目唯一进程入口上的无界挂起。
                    // 所以这里只记下第一个异常、让读取循环继续跑完；等 WaitForExit() 返回后由 Run
                    // 抛给调用方，异常位置从「随机的管道线程」变成「调用方自己的 Run 调用点」。
                    lock (progressGate) { if (progressFailure[0] == null) { progressFailure[0] = ex; } }
                }
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null) { return; }
                lock (outputGate) { stdErr.Append(e.Data).Append('\n'); }
            };

            bool started = false;
            bool registered = false;
            CancellationTokenRegistration registration = default(CancellationTokenRegistration);
            int exitCode;
            try
            {
                process.Start();
                started = true;

                // I5 的机械保证：管道立好就立刻关掉 stdin 端（ProcessStartInfo 的清单里没有这一条，
                // 是规格 I5「绝不允许 7-Zip 等待 stdin」推出的必要补充）。
                process.StandardInput.Close();

                // 先挂异步读再等退出；绝不 WaitForExit() 之后再 ReadToEnd()（经典管道死锁）。
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!AssignProcessToJobObject(job, process.Handle))
                {
                    int error = Marshal.GetLastWin32Error();
                    // 子进程已经退出时赋值必然失败（实测 Win32 错误 5 = ERROR_ACCESS_DENIED）：
                    // 几十毫秒就结束的 7z（例如打不开的包）会跑赢赋值。这不是缺陷，也不存在孤儿
                    // 风险（进程都没了），必须如实放行；这里再给正在退出的进程 100ms 走完。
                    if (!process.WaitForExit(100))
                    {
                        // 进程还活着却进不了 Job Object = 真的保护不了它。绝不静默降级：
                        // 抛异常，finally 负责把这个子进程杀掉。
                        throw new InvalidOperationException(
                            "AssignProcessToJobObject 失败（Win32 错误 " + error +
                            "）：无法保证 7-Zip 子进程随本进程结束（已杀掉该子进程）");
                    }
                }

                lock (_jobGate) { _activeJob = job; }

                if (ct.CanBeCanceled)
                {
                    // 取消 = 直接 TerminateJobObject（规格 §6.7 两段式取消里的强制阶段）。
                    registration = ct.Register(delegate { TerminateJob(job); });
                    registered = true;
                }

                // 无参 WaitForExit() 会一并等待异步读事件处理完（.NET Framework 保证），
                // 所以这一行之后 stdout/stderr 必定已经积累完整，回调也已经全部跑完
                //（因此下面读 progressFailure 没有竞态）。
                process.WaitForExit();
                exitCode = process.ExitCode;

                // onProgress 抛出的异常在这里浮出水面（绝不在管道线程上抛，也绝不吞掉）：
                // 抛的是回调抛出的那个异常对象本身，类型与消息原样保留，便于调用方按类型处理
                //（例如 Task 14 的 ObjectDisposedException）。代价是堆栈被重置到这一行。
                Exception callbackFailure;
                lock (progressGate) { callbackFailure = progressFailure[0]; }
                if (callbackFailure != null) { throw callbackFailure; }
            }
            finally
            {
                if (registered) { registration.Dispose(); }

                if (started)
                {
                    // 异常路径（含 Job Object 赋值失败）也不能把子进程留在系统里。
                    try
                    {
                        if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
                    }
                    catch (Exception)
                    {
                        // 进程已经退出（或正在退出）：没有孤儿问题，无需处理
                    }
                }

                lock (_jobGate) { if (_activeJob == job) { _activeJob = IntPtr.Zero; } }

                // 关掉 Job Object 句柄：KILL_ON_JOB_CLOSE 保证此刻若还有进程挂在里面也会被杀。
                CloseHandle(job);
                process.Dispose();
            }

            RunResult result = new RunResult();
            result.ExitCode = exitCode;
            lock (outputGate)
            {
                result.StdOut = stdOut.ToString();
                result.StdErr = stdErr.ToString();
            }
            return result;
        }

        // 7z 的退出码语义：0 = 正常，1 = 完成但有警告（仍算本轮成功；是否真的成功要看调用方的
        // 索引比对，见规格 §5-I1），2 及以上 = 错误。
        public static bool IsSuccess(int exitCode)
        {
            return exitCode == 0 || exitCode == 1;
        }

        // 杀掉当前正在跑的 7-Zip（Job Object 里的全部进程）。GUI 关闭/取消时调用；可从任意线程调用。
        public static void TerminateAll()
        {
            lock (_jobGate)
            {
                TerminateJob(_activeJob);
            }
        }

        // 解析一行 7-Zip 进度。7z 的形状是「<百分比>% [条目计数] <标记> <成员名>」：
        //   实测 7-Zip 26.01：解压 " 34% - huge.bin"、压缩 " 11% + seed.bin"；
        //   部分版本/场景会多一个条目计数，如 " 58% 2       - sub\big2.bin"。
        // 任何不像进度行的输入都返回 percent = -1（不抛异常，见 brief Review Focus #2）：
        // null/空/纯空白、没有 %、% 前不是数字、% 后没有标记、成员名为空。
        public static void ParseProgressLine(string line, out int percent, out string member)
        {
            percent = -1;
            member = null;
            if (line == null) { return; }

            int percentSign = line.IndexOf('%');
            if (percentSign <= 0) { return; }

            // 百分比必须是紧挨着 % 前面的一串数字
            int digitsStart = percentSign;
            while (digitsStart > 0 && line[digitsStart - 1] >= '0' && line[digitsStart - 1] <= '9') { digitsStart--; }
            if (digitsStart == percentSign) { return; }

            int value;
            if (!int.TryParse(line.Substring(digitsStart, percentSign - digitsStart), out value)) { return; }
            if (value < 0 || value > 100) { return; }

            // % 之后：[空白] [条目计数] [空白] 标记 成员名
            int i = percentSign + 1;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) { i++; }
            while (i < line.Length && line[i] >= '0' && line[i] <= '9') { i++; }
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) { i++; }
            if (i >= line.Length) { return; }
            if (line[i] != '-' && line[i] != '+') { return; }
            i++;

            string name = line.Substring(i).Trim();
            if (name.Length == 0) { return; }

            percent = value;
            member = name;
        }

        // ---------------- 内部 ----------------

        // 把 stdout 的每一段（按 \r 与 \n 双重切分）当独立进度行解析；只有解析出成员名的段才回调。
        private static void DispatchProgress(string chunk, Action<int, string> onProgress)
        {
            if (onProgress == null || chunk == null) { return; }

            int start = 0;
            for (int i = 0; i <= chunk.Length; i++)
            {
                if (i < chunk.Length && chunk[i] != '\r' && chunk[i] != '\n') { continue; }
                if (i > start)
                {
                    int percent;
                    string member;
                    ParseProgressLine(chunk.Substring(start, i - start), out percent, out member);
                    if (percent >= 0) { onProgress(percent, member); }
                }
                start = i + 1;
            }
        }

        // 参数表里是否有 -p 前缀项（-p 或 -p<密码>）；7z 的开关大小写不敏感。
        private static bool HasPasswordSwitch(string[] args)
        {
            foreach (string arg in args)
            {
                if (arg != null && arg.StartsWith("-p", StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        // 参数表里是否有 -scc 前缀项（7z 的控制台字符集开关，如 -sccUTF-8 / -sccWIN / -sccDOS）。
        // 有的话说明调用方自己决定了控制台字符集，本类不再注入（调用方的选择优先）。
        private static bool HasConsoleCharsetSwitch(string[] args)
        {
            foreach (string arg in args)
            {
                if (arg != null && arg.StartsWith("-scc", StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        // 7z 装在有空格的路子里是常态（C:\Program Files\7-Zip\...），而 .NET Framework 的
        // ProcessStartInfo 只有字符串 Arguments（ArgumentList 是 .NET Core 才有的），
        // 所以这里必须自己做 Windows 的命令行引号转义，否则路径会被拆成两个参数。
        //
        // 规格 §4.3：控制台字符集必须钉死在 UTF-8（与 StandardOutputEncoding 对齐），
        // 否则 7z 按 OEM 代码页输出成员名，StdOut 与进度回调里的非 ASCII 名字全变成 U+FFFD
        //（实测：中文成员名 l -slt 不带开关 → 全是替换字符，带开关 → 完好）。
        // 这是 Runner 的责任：任何调用方忘了传都会静默损坏名字，所以在唯一的命令行出口处注入。
        private static string BuildCommandLine(string[] args)
        {
            StringBuilder sb = new StringBuilder();
            bool first = true;

            if (!HasConsoleCharsetSwitch(args))
            {
                sb.Append("-sccUTF-8");
                first = false;
            }

            for (int i = 0; i < args.Length; i++)
            {
                if (!first) { sb.Append(' '); }
                AppendQuotedArgument(sb, args[i] == null ? "" : args[i]);
                first = false;
            }
            return sb.ToString();
        }

        // CommandLineToArgvW / MSVCRT 的转义规则：反斜杠只在引号前才特殊，
        // 引号前的 2N 个反斜杠 → N 个字面反斜杠，2N+1 个 → N 个字面反斜杠 + 一个字面引号。
        private static void AppendQuotedArgument(StringBuilder sb, string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(WhitespaceChars) < 0 && argument.IndexOf('"') < 0)
            {
                sb.Append(argument);
                return;
            }

            sb.Append('"');
            int backslashes = 0;
            for (int i = 0; i < argument.Length; i++)
            {
                char c = argument[i];
                if (c == '\\') { backslashes++; continue; }

                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
                sb.Append(c);
            }

            if (backslashes > 0) { sb.Append('\\', backslashes * 2); }
            sb.Append('"');
        }

        // 建一个 Job Object，并让它「句柄一关就杀掉里面所有进程」。
        private static IntPtr CreateJob()
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "CreateJobObject 失败（Win32 错误 " + Marshal.GetLastWin32Error() + "）：无法保证 7-Zip 子进程不留孤儿");
            }

            JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

            int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
                {
                    int error = Marshal.GetLastWin32Error();
                    CloseHandle(job);
                    throw new InvalidOperationException(
                        "SetInformationJobObject(JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE) 失败（Win32 错误 " + error +
                        "）：无法保证 7-Zip 子进程随本进程一起结束");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return job;
        }

        // 尽力而为：进程已经退出时返回 false 也无所谓。
        private static void TerminateJob(IntPtr job)
        {
            if (job == IntPtr.Zero) { return; }
            TerminateJobObject(job, CancelledExitCode);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
