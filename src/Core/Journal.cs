// Rerar 崩溃恢复日志（Task 11；规格 §5「中断后可恢复」/ §7 致命档的善后）。
//
// 【为什么需要它】原文 .bat 最严重的缺陷是「解压失败仍然删除原包」。Task 10 用「暂存 → 校验 →
// 提交」把删除结构性挂在「完成且校验通过」上；但进程若在解压 / 提交 / 处置原包之间的任意一刻
// 被杀（断电、任务管理器结束任务、崩溃），盘上就会留下「处理到一半」的状态。下一次启动必须能
// 只看盘上的记录**独立地**回答两个问题：
//     1) 哪些目标目录没有完成记录（= 可能是半成品）？        → FindIncompleteDestinations()
//     2) 哪些运行没有正常结束（= 进程在途中死了）？          → RunsWithoutCleanShutdown()
// 这就是本类：一个**追加式、每条都立刻落盘**的运行记录，写在每一个不可逆动作**之前**。
//
// 【为什么在 %LOCALAPPDATA%，绝不在目标卷】
// 目标卷正是「可能被写满 / 会掉线」的那一个（规格 §7 的致命档）。恢复记录必须比它活得久，
// 所以它只写在系统卷的用户配置目录下：%LOCALAPPDATA%\Rerar\journal\<runId>.log。
//
// 【记录格式】每条记录一行，UTF-8（无 BOM），字段用**制表符**分隔：
//     <UTC 时间戳>\t<步骤>\t<明细>
// 明细对「目的地」类记录**首字段就是目的地绝对路径**（其余为 key=value 扩展字段），
// 于是恢复查询不必理解后半段就能工作：
//     run-start        run=<runId>
//     about-to-extract <目标目录>\tsource=<源归档>\tstaging=<暂存目录>
//     extract-done     <目标目录>\tstatus=<Completed|CompletedWithFailures>\toriginal=<kept|deleted|quarantined>
//                      （status 缺失时按 Completed 处理：步骤名本身就是「完成」的意思。
//                        CompletedWithFailures **不**关闭目的地 —— 见下面的恢复查询说明。）
//     about-to-delete  <原包路径>\tdestination=<目标目录>
//     done             results=<n>\tcancelled=<true|false>\tfatal=<true|false>
//
// 【哪些文件/记录承载恢复查询】
//   * RunsWithoutCleanShutdown()：逐个日志文件扫描，看**最后一条 run-start 之后**有没有 done。
//     没有 ⇒ 该 runId 没有正常结束。Open() 每次都会写一条 run-start，所以同一 runId 的第二次
//     运行自成一个「段」：新一段的结局绝不会被上一段的 done 掩盖，而上一段的记录一条都不会被
//     改写或丢弃（严格追加式）。
//   * FindIncompleteDestinations()：按文件顺序扫描，about-to-extract 记下目的地，之后一条
//     **status=Completed** 的 extract-done 才把它消掉；剩下的就是「不是完整结果」的目的地
//    （失败、半途、以及「已提交但内容不全」的 CompletedWithFailures 都留在里面 —— 恢复查询要
//     回答的是「哪些目的地不是完整结果」，不是「这个进程有没有继续碰它」）。
//     **每个文件各自结算**（一次运行一条日志）：跨文件不做抵消 —— 在某次运行的日志里没做完的
//     目的地，绝不因为另一次运行的日志里出现过同名目的地就被当成做完了。
//     方向刻意保守：崩在「提交成功后、完成记录落盘前」也会被报成未完成 —— 宁可多报，绝不少报
//     （少报就是把一个可能半成品的目的地当成完整结果）。
//
// 【落盘语义】每条 Note：打开 → 追加 → Flush(true) → 关闭。Flush(true) 把数据刷到磁盘
//（不只是进程缓冲区）—— 一条留在缓冲里的记录遇崩溃等于没写，那正是本类存在的全部意义。
// 之所以是 open/close 而不是长开句柄：崩溃后不留句柄，且测试能随时清掉临时根（长开句柄会让
// 目录删除失败，用例之间就不再自洽）。
//
// 【并发】Note 走一把**静态**锁：同一进程内多个线程、甚至多个实例追加同一个文件，也绝不会
// 交错出半条记录（一次 Write + 一次 Flush，锁内完成）。
//
// 【密码绝不写入】本类不处理、不接收任何密码：Task 10 只记「候选序号 + 来源类别」，日志这一侧
// 保持同一性质（凭据卫生 §6.5）。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Rerar.Core
{
    public sealed class Journal
    {
        // 记录词汇表（格式见文件头）。公开是为了让调用方与恢复查询引用同一份事实，
        // 而不是各自硬编码字符串。
        public const string RunStartStep = "run-start";
        public const string AboutToExtractStep = "about-to-extract";
        public const string ExtractDoneStep = "extract-done";
        public const string AboutToDeleteStep = "about-to-delete";
        public const string DoneStep = "done";

        public const string FileExtension = ".log";

        // 一个进程内所有实例共用：并发追加同一文件时保证「一条记录 = 一次 Write」。
        private static readonly object AppendLock = new object();

        // 日志是数据文件，不是源码：UTF-8 **无 BOM**（追加写绝不能每开一次就写一次 BOM）。
        private static readonly Encoding LogEncoding = new UTF8Encoding(false);

        // 测试接缝：非 null 时日志根改用它（见 Root）。生产路径永远走 DefaultRoot。
        private static string _root;

        private readonly string _path;

        private Journal(string path)
        {
            _path = path;
        }

        // 本实例写入的日志文件绝对路径。
        public string Path
        {
            get { return _path; }
        }

        // 生产默认根：%LOCALAPPDATA%\Rerar\journal（绝不在目标卷，理由见文件头）。
        // 每次访问现算，绝不静态缓存路径。
        public static string DefaultRoot
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local))
                {
                    throw new InvalidOperationException(
                        "无法定位 %LOCALAPPDATA%：崩溃恢复日志必须写在本地用户配置目录，绝不放目标卷");
                }
                return System.IO.Path.Combine(local, "Rerar", "journal");
            }
        }

        // 当前日志根。测试经 TestEnv 把它重定向到临时目录（生产从不设置它）。
        public static string Root
        {
            get
            {
                string overridden = _root;
                return overridden != null ? overridden : DefaultRoot;
            }
            set { _root = value; }
        }

        // 打开（必要时新建）一次运行的日志：<Root>\<runId>.log（**追加**，绝不截断）。
        // 返回的实例只记录路径，不持有句柄；每条 Note 自己开、写、Flush(true)、关。
        //
        // runId 会被当作文件名，所以这里拒绝空值与任何非法文件名字符（含路径分隔符）：
        // 调用方传来的 runId 绝不能变成一条越界写盘的路径。
        public static Journal Open(string runId)
        {
            if (string.IsNullOrEmpty(runId))
            {
                throw new ArgumentException("runId 不能为空：它是日志文件名的一部分", "runId");
            }
            if (runId.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException(
                    "runId 不能包含路径分隔符或文件名非法字符：" + runId, "runId");
            }

            string root = Root;
            Directory.CreateDirectory(root);

            Journal journal = new Journal(System.IO.Path.Combine(root, runId + FileExtension));

            // 分段标记：它让「同一 runId 的第二次运行」与第一次彻底分开 ——
            // 没有它，第二段崩掉时文件里那条旧的 done 会让 RunsWithoutCleanShutdown 误报「干净结束」。
            journal.Note(RunStartStep, "run=" + runId);
            return journal;
        }

        // 追加一条记录并立刻落盘（Flush(true)）。明细里的制表符是字段分隔符，必须保留；
        // 换行会被替换成空格，保证「一次 Note = 恰好一行」。
        public void Note(string step, string detail)
        {
            if (string.IsNullOrEmpty(step))
            {
                throw new ArgumentException("step 不能为空", "step");
            }

            StringBuilder line = new StringBuilder();
            line.Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            line.Append('\t').Append(OneLine(step));
            line.Append('\t').Append(OneLine(detail));
            line.Append("\r\n");

            Append(_path, line.ToString());
        }

        // ------------------------------------------------------------------
        // 恢复查询（只读日志文件，不依赖任何进程内状态）
        // ------------------------------------------------------------------

        // 没有正常结束的运行（runId，按名称序数排序）。启动恢复路径用它找出「进程死在途中」的那几次运行。
        public static IEnumerable<string> RunsWithoutCleanShutdown()
        {
            List<string> runs = new List<string>();
            foreach (string file in JournalFiles())
            {
                if (!EndedCleanly(ReadLines(file)))
                {
                    runs.Add(System.IO.Path.GetFileNameWithoutExtension(file));
                }
            }
            runs.Sort(StringComparer.Ordinal);
            return runs;
        }

        // 没有完成记录的目的地（绝对路径，按序数排序、大小写不敏感去重）。
        public static IEnumerable<string> FindIncompleteDestinations()
        {
            List<string> incomplete = new List<string>();

            foreach (string file in JournalFiles())
            {
                HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<string> order = new List<string>();

                foreach (string line in ReadLines(file))
                {
                    string step;
                    string detail;
                    if (!Split(line, out step, out detail)) { continue; }

                    if (string.Equals(step, AboutToExtractStep, StringComparison.Ordinal))
                    {
                        string destination = FirstField(detail);
                        if (destination.Length == 0) { continue; }
                        if (pending.Add(destination)) { order.Add(destination); }
                    }
                    else if (string.Equals(step, ExtractDoneStep, StringComparison.Ordinal))
                    {
                        // 只有「校验通过 + 提交成功」的记录才算这个目的地做完了：
                        // CompletedWithFailures 的产物少文件（内容上就是不完整的），必须继续报出来。
                        if (IsVerifiedComplete(detail)) { pending.Remove(FirstField(detail)); }
                    }
                }

                foreach (string destination in order)
                {
                    if (pending.Contains(destination) && !Contains(incomplete, destination))
                    {
                        incomplete.Add(destination);
                    }
                }
            }

            incomplete.Sort(StringComparer.Ordinal);
            return incomplete;
        }

        // ------------------------------------------------------------------
        // 内部：文件枚举、读、解析、追加
        // ------------------------------------------------------------------

        private static List<string> JournalFiles()
        {
            List<string> files = new List<string>();
            string[] found;
            try
            {
                found = Directory.GetFiles(Root, "*" + FileExtension);
            }
            catch (Exception)
            {
                // 根还不存在 / 读不动：没有任何记录可回答，返回空集（绝不凭空报一个运行或目的地）。
                return files;
            }

            files.AddRange(found);
            files.Sort(StringComparer.Ordinal);
            return files;
        }

        // 记录里最后一段（最后一条 run-start 之后）有没有 done。没有 run-start 的文件按整文件算。
        // 读不动的文件按「没有正常结束」处理：绝不因为读不出来就把它当成干净。
        private static bool EndedCleanly(List<string> lines)
        {
            int lastStart = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string step;
                string detail;
                if (Split(lines[i], out step, out detail) &&
                    string.Equals(step, RunStartStep, StringComparison.Ordinal))
                {
                    lastStart = i;
                }
            }

            for (int i = lastStart + 1; i < lines.Count; i++)
            {
                string step;
                string detail;
                if (Split(lines[i], out step, out detail) &&
                    string.Equals(step, DoneStep, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static List<string> ReadLines(string file)
        {
            List<string> lines = new List<string>();
            try
            {
                using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(stream, LogEncoding))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) { lines.Add(line); }
                }
            }
            catch (Exception)
            {
                lines.Clear();
                lines.Add("");      // 解析不出任何记录 ⇒ EndedCleanly 判「没有正常结束」（保守方向）
            }
            return lines;
        }

        // <时间戳>\t<步骤>\t<明细> → step / detail。格式不对的行（例如被外部工具截断）一律忽略。
        private static bool Split(string line, out string step, out string detail)
        {
            step = null;
            detail = "";

            if (string.IsNullOrEmpty(line)) { return false; }

            int first = line.IndexOf('\t');
            if (first < 0) { return false; }
            int second = line.IndexOf('\t', first + 1);
            if (second < 0) { return false; }

            step = line.Substring(first + 1, second - first - 1);
            detail = line.Substring(second + 1);
            return step.Length > 0;
        }

        // 明细的首字段（目的地类记录里就是目的地绝对路径）。明细里的制表符是分隔符。
        private static string FirstField(string detail)
        {
            if (string.IsNullOrEmpty(detail)) { return ""; }

            int tab = detail.IndexOf('\t');
            string field = tab < 0 ? detail : detail.Substring(0, tab);
            return field.Trim();
        }

        // extract-done 记录是不是「校验通过的完整结果」。status= 缺失按 Completed 处理
        //（步骤名本身就是「完成」的意思，手写记录也照此解释）。
        private static bool IsVerifiedComplete(string detail)
        {
            string status = Field(detail, "status");
            return status.Length == 0 || string.Equals(status, "Completed", StringComparison.Ordinal);
        }

        // 明细里的 key=value 扩展字段（首字段是目的地，不含 '='）。找不到返回 ""。
        private static string Field(string detail, string key)
        {
            if (string.IsNullOrEmpty(detail) || string.IsNullOrEmpty(key)) { return ""; }

            string needle = "\t" + key + "=";
            int at = detail.IndexOf(needle, StringComparison.Ordinal);

            int start;
            if (at >= 0)
            {
                start = at + needle.Length;
            }
            else
            {
                // 首字段就是 key=value 的记法（没有目的地前缀）也认。
                string head = key + "=";
                if (!detail.StartsWith(head, StringComparison.Ordinal)) { return ""; }
                start = head.Length;
            }

            int tab = detail.IndexOf('\t', start);
            string value = tab < 0 ? detail.Substring(start) : detail.Substring(start, tab - start);
            return value.Trim();
        }

        private static bool Contains(List<string> values, string value)
        {
            foreach (string one in values)
            {
                if (string.Equals(one, value, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        private static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text)) { return ""; }
            return text.Replace('\r', ' ').Replace('\n', ' ');
        }

        // 追加一行并立刻刷盘。锁是**静态**的：同一文件的并发追加（多线程 / 多实例）不会交错。
        private static void Append(string path, string line)
        {
            byte[] bytes = LogEncoding.GetBytes(line);
            lock (AppendLock)
            {
                using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);     // 只到进程缓冲不算写；必须落到盘上才算记录
                }
            }
        }
    }
}
