// Rerar 解压编排（Task 10；规格 §4.2 数据流、§6.5、§6.6、§6.7、§6.11 与五条不变式）。
//
// 本类是整条流水线的汇聚点，也是安全不变式唯一真正落地的地方。管线顺序（Task 5 的裁定：
// 规格 §4.2 把 Gater 排在 Indexer 之前是**不可能的**，门控需要清单）：
//
//   Sniffer → 分卷族 → 索引(Read) → Gater → 预检(安全上限/空间/长路径/重名) → 密码 → 暂存 → 解压 →
//   校验 → 提交 → （仅当开启删除且「完成且校验通过」）回收站/隔离
//
// 两个逐项开关（都只放开「要不要试」这一层，绝不放开任何安全判定）：
//   * RunOptions.ForceTreatAsArchive：「强制按压缩包尝试」（规格 §6.1 的逐项动作）跳过**格式门控**；
//   * 预检安全上限（Preflight.CheckExpansion，压缩炸弹 / 海量条目）**没有**开关，一律生效 ——
//     包括被强制的项（用例 Extract.ForcedAttemptStillAppliesCaps 钉住这一点）。
//
// 每个归档**只 Read 一次索引**，它同时服务门控（内容身份）与完整性基线（条目数/总字节）。
// 唯一的例外是头部加密：那次 Read 什么清单都拿不到，找到密码后必须再读一次才有基线。
//
// 五条不变式在本文件里的落点：
//   I1 成功 = 「磁盘实际结果 vs 索引」的比对，**绝不是退出码**。基线规则见 Preflight.TryGetBaseline
//      （ListingFailed ⇒ 没有基线；FileCount == 0 ⇒ 退回 x 尾部汇总，只有字节可用）。
//   I2 任何写入先落**同卷空暂存目录**，校验通过后 Directory.Move 改名提交；目标被同名**文件**
//      占用时中止该归档（规格 §6.11，不回落父目录）；校验含三条断言：条目数/字节比对、
//      零 reparse point（实测 7z 会按 tar 条目建软链）、每条产出路径都是暂存根的严格子项。
//   I3 删除默认关；只有「完成且校验通过」可删；CompletedWithFailures / 跳过 / 失败一律不删；
//      回收站删完必须 VerifyInBin 核实，核实不到就如实报「已永久删除」。
//   I4 门控拒绝（容器文档）⇒ SkippedContainer：不递归、不删除、不留输出目录。
//   I5 每一次 7-Zip 调用都带 -p；候选密码只用 `t`（头部加密时用 `l`）验证，绝不用 `x`；
//      只在验证成功之后缓存密码；密码值绝不写进结果/日志/报告（只记「候选序号 + 来源类别」）。
//
// 原文 .bat 的两个致命缺陷就在这里被结构性挡住：
//   ① 解压失败仍然删除原包 —— 删除只挂在 Completed 上，且删前删后都有检查；
//   ② 加密包永久挂起 —— 每一次 7-Zip 调用都带 -p（Runner 硬拒不带 -p 的参数表），stdin 也已关闭。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Rerar.Core
{
    // 运行进度回调。全部方法都可以为 null（传 null 的 Extractor 就是不汇报），
    // 而且**抛出任何异常都被 Extractor 吞掉**：绝不能因为界面被关掉而让解压流水线中断。
    public interface IProgressSink
    {
        void ArchiveStarted(string archivePath, int depth);
        void ArchiveFinished(ArchiveResult result);
        void Progress(string archivePath, int percent, string member);
        void Message(string text);
    }

    // 一次运行的总结果（Reporter 与 CLI/Task 12、界面/Task 14 的数据源）。
    public sealed class RunSummary
    {
        // 每个归档一条（含顶层与各嵌套层、以及因深度上限未处理的项）。
        public List<ArchiveResult> Results = new List<ArchiveResult>();

        // 非空 = 整批因**致命**原因中止（规格 §7；CLI 退出码 2）。当前只有两类：
        // 磁盘空间不足、用户取消导致的致命收尾。
        public string FatalReason;

        // 用户取消（或低水位中止的子进程退出码 1223）。取消**绝不是成功**，也绝不是「解压失败」：
        // 它只是「没做完」，而原包一律保留。注意 ArchiveStatus 里没有 Cancelled 成员（那是
        // Task 8 定下的契约），所以单个归档的状态是 Failed + 判词「已取消」，由本标志把
        // 「取消」与「真的失败」在运行级别上区分开。
        public bool Cancelled;

        // **未处理项的权威清单**（原样路径；规格 §10.1「触顶必须显式列出未处理项」）。
        //
        // 契约（Task 10 修复轮 #3 的 Finding 1 裁定）：
        //   * 本清单是**唯一权威**的「这一项没有被处理」的集合，**与原因无关** —— 递归深度触顶、
        //     致命中止（盘满 / 取消）之后还没来得及处理的候选，全部都在这里；
        //   * 因**深度触顶**而未处理的项在 Results 里**同时**有一条 Status = NotAttemptedDepthLimit
        //     的结果（每个归档的结局枚举必须如实反映，报告/界面靠它分类），于是同一个归档会**同时**
        //     出现在 Results 与 NotAttempted 两个集合里；
        //   * 消费方**不得把两个集合相加**去数「未处理项」：未处理项的总数就是 NotAttempted.Count，
        //     Results 里那条 NotAttemptedDepthLimit 是同一件事的另一种表述（按状态分类用）；
        //   * 除深度触顶外的其它未处理原因**没有**对应的 Results 条目（它们没被处理过，谈不上结局）。
        //
        // 它们都不是 Failed（没试过，谈不上失败），也绝不静默丢掉。
        public List<string> NotAttempted = new List<string>();
    }

    public sealed class Extractor
    {
        // 内置常用密码字典（阶梯第 4 层）：一个精简的公开常用列表，随包可查、不做在线更新（§6.5）。
        // 顺序就是尝试顺序，先放最常被当作压缩包密码的那一批。
        //
        // 【硬约束】这张表里**不得**出现任何测试夹具的真实密码（夹具用 SECRET，本表刻意不含
        // secret/SECRET）：否则「错密码 ⇒ SkippedNeedsPassword」的回归用例会被字典猜中而变成假通过。
        internal static readonly string[] BuiltInDictionary = new string[]
        {
            "123456", "password", "12345678", "qwerty", "123456789", "12345", "1234", "111111",
            "1234567", "123123", "abc123", "letmein", "666666", "1234567890", "123321", "1qaz2wsx",
            "7777777", "000000", "121212", "qazwsx", "123qwe", "zxcvbnm", "asdfgh", "iloveyou",
            "sunshine", "112233", "555555", "888888", "5201314", "woaini", "a123456", "aa123456",
            "admin", "root", "88888888", "11111111", "123456a", "q1w2e3", "1q2w3e", "asd123",
            "147258369", "741852963", "password1", "passw0rd", "p@ssw0rd", "abcd1234",
            "1qaz2wsx3edc", "123abc", "admin123", "123456789a"
        };

        // 7-Zip 被 Job Object 打断时的退出码（Win32 ERROR_CANCELLED）。**不是**解压失败，更不是成功。
        private const int CancelledExitCode = 1223;

        private const string StagingPrefix = ".rerar-stage-";
        private const string IncompleteSuffix = " (未完成)";
        private const string SentinelName = "_RERAR_INCOMPLETE.txt";
        private const string OriginalsPrefix = "_originals_";

        // 目录/文件名的尝试上限：畸形输入（例如目标名全部被占用）不得变成死循环。
        private const int MaxNameAttempts = 500;

        // 规格 §6.5：只有 ≤256KB 的小文本文件才作为密码线索来源。
        private const int MaxTextClueBytes = 256 * 1024;

        private static readonly string[] TextClueExtensions = new string[]
        {
            ".txt", ".nfo", ".md", ".url", ".html", ".htm", ".ini"
        };

        private readonly RunOptions _options;
        private readonly IDiskSpaceProvider _disk;
        private readonly IProgressSink _progress;

        private RunSummary _summary;
        private List<string> _verifiedPasswords;
        private HashSet<string> _processed;
        private Dictionary<ArchiveTask, ArchiveResult> _resultsByTask;

        // 本次运行的崩溃恢复日志（Task 11）。null = 日志不可用（例如 %LOCALAPPDATA% 建不出来）：
        // 日志是**辅助**记录，它缺席绝不改变解压行为（见 OpenJournal / NoteJournal）。
        private Journal _journal;

        // 低水位标志由**轮询线程**写、由解压线程读，故用 volatile（见 RunExtraction 的说明）。
        // 三个标志一起决定判词说的是哪一段（开始前 / 解压中 / 解压后复核），绝不把「其实解压完了」
        // 说成「已在解压中途中止」。
        private volatile bool _lowWaterHit;
        private volatile bool _extractionRunning;
        private volatile bool _lowWaterDuringExtraction;
        private bool _lowWaterAfterRun;

        // 低水位判定来自「查询可用空间失败」时（注入的 provider 抛异常）的**如实判词**：
        // 那时说「可用空间低于低水位 N 字节」是不实陈述，所以单独记下真正的原因。
        // 与上面几个标志一样是「运行级 + 每归档重置」的。
        private string _lowWaterProbeReason;
        private int _lastLadderTries;
        private string _lastMatchedCandidate = "";

        public Extractor(RunOptions options, IDiskSpaceProvider disk, IProgressSink progress)
        {
            if (options == null) { throw new ArgumentNullException("options"); }
            if (string.IsNullOrEmpty(options.SevenZipPath))
            {
                throw new ArgumentException("RunOptions.SevenZipPath 不能为空：没有 7-Zip 就无法解压", "options");
            }
            if (disk == null) { throw new ArgumentNullException("disk"); }

            _options = options;
            _disk = disk;
            _progress = progress;
        }

        // 跑一批目标（文件或目录里的归档由调用方展平后传入）。一个 Extractor 只跑一次 Run
        // （与 SevenZipRunner 的顺序调用约定一致）：每次 Run 都会重置运行级状态。
        public RunSummary Run(IEnumerable<string> targets)
        {
            RunSummary summary = new RunSummary();
            _summary = summary;
            _verifiedPasswords = new List<string>();
            _processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _resultsByTask = new Dictionary<ArchiveTask, ArchiveResult>();
            _journal = OpenJournal();

            List<ArchiveTask> round = FreezeTargets(targets, summary);
            int depth = 1;

            while (round.Count > 0)
            {
                // 规格 §10.1：触顶必须**显式列出未处理项**，绝不静默停止。
                if (depth > _options.MaxDepth)
                {
                    foreach (ArchiveTask task in round)
                    {
                        ArchiveResult notAttempted = DepthLimitResult(task);
                        summary.Results.Add(notAttempted);
                        // 同一项也进 NotAttempted（**权威的未处理清单**，与原因无关）：
                        // 只把它放进 Results 会让「打印 NotAttempted」的消费方**少报**未处理项。
                        summary.NotAttempted.Add(task.Path);
                        NotifyFinished(notAttempted);      // 界面/CLI 也要看到这些「未处理项」
                    }
                    break;
                }

                // 本轮只扫「上一轮解出来的输出目录」并冻结成本轮的候选清单：绝不重新全树扫描，
                // 否则父归档会被反复重新发现（无限循环）。
                List<ArchiveTask> next = new List<ArchiveTask>();

                foreach (ArchiveTask task in round)
                {
                    if (Stopped())
                    {
                        // 用户取消要在**运行级**如实标注：取消既不是成功，也不是「这个归档失败了」。
                        if (_options.Cancellation.IsCancellationRequested) { summary.Cancelled = true; }
                        summary.NotAttempted.Add(task.Path);       // 权威的未处理清单（与原因无关）
                        continue;
                    }

                    NotifyStarted(task.Path, task.Depth);

                    ArchiveResult result;
                    try
                    {
                        result = Process(task, next);
                    }
                    catch (Exception ex)
                    {
                        // 单个归档的内部异常绝不能掀翻整批，也不能被读成成功。
                        result = NewResult(task);
                        result.Status = ArchiveStatus.Failed;
                        result.Message = "处理该归档时发生内部错误（" + ex.GetType().Name + "：" + ex.Message + "）；原包保留";
                    }

                    Record(task, result, summary);
                }

                if (Stopped())
                {
                    if (_options.Cancellation.IsCancellationRequested) { summary.Cancelled = true; }
                    // 本轮已解出、但**还没轮到处理**的候选：进权威的未处理清单（没有 Results 条目 ——
                    // 它们没被处理过，谈不上结局）。
                    foreach (ArchiveTask task in next) { summary.NotAttempted.Add(task.Path); }
                    break;
                }
                if (next.Count == 0) { break; }

                depth++;
                round = next;
            }

            // Task 11：干净收尾标记。**没有**这条记录 = 进程死在途中（RunsWithoutCleanShutdown 的
            // 唯一判据）。它说的是「控制流走完了本方法」，与每个归档各自的结局无关 ——
            // 「哪些目的地没有做完」由 about-to-extract / extract-done 逐条回答。
            NoteJournal(Journal.DoneStep,
                "results=" + summary.Results.Count +
                "\tcancelled=" + (summary.Cancelled ? "true" : "false") +
                "\tfatal=" + (summary.FatalReason == null ? "false" : "true"));

            return summary;
        }

        // ------------------------------------------------------------------
        // 崩溃恢复日志（Task 11）：只**增加**记录，不改动任何安全路径
        // ------------------------------------------------------------------

        // 每次 Run 一条独立的日志（runId = 本地时间戳 + 随机后缀，同秒多次运行也不会撞名）。
        // 打不开就返回 null：日志缺席绝不能让一次本来正常的解压变成失败。
        private Journal OpenJournal()
        {
            try
            {
                string runId = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "-" +
                    Guid.NewGuid().ToString("N").Substring(0, 8);
                return Journal.Open(runId);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // 落一条记录。**吞掉一切异常**：日志是辅助记录，它的失败绝不能把结果降级成「内部错误」
        // （那会改动 Task 10 的行为）；盘上的哨兵 + 「(未完成)」改名才是主要的失败标记。
        private void NoteJournal(string step, string detail)
        {
            Journal journal = _journal;
            if (journal == null) { return; }
            try { journal.Note(step, detail); }
            catch (Exception) { }
        }

        // 日志明细的扩展字段（格式见 Journal.cs 文件头：首字段是目的地，其余 key=value）。
        private static string JournalField(string key, string value)
        {
            return "\t" + key + "=" + (value == null ? "" : value);
        }

        // 冻结顶层候选清单。不存在的目标逐项记为 SkippedUnreadable（Review Focus #5：不得崩溃、
        // 不得静默跳过）；同一份文件被传两次只处理一次（运行级去重）。
        private List<ArchiveTask> FreezeTargets(IEnumerable<string> targets, RunSummary summary)
        {
            List<ArchiveTask> frozen = new List<ArchiveTask>();
            if (targets == null) { return frozen; }

            foreach (string raw in targets)
            {
                if (string.IsNullOrEmpty(raw)) { continue; }

                string full;
                try { full = Path.GetFullPath(raw); }
                catch (Exception ex)
                {
                    ArchiveResult invalid = Unreadable(raw, "路径无效（" + ex.GetType().Name + "）：" + raw);
                    summary.Results.Add(invalid);
                    NotifyFinished(invalid);
                    continue;
                }

                if (!File.Exists(full))
                {
                    ArchiveResult missing = Unreadable(full, "目标不存在或不可读（离线 / 权限拒绝 / 路径含尾空格等）：" + full);
                    summary.Results.Add(missing);
                    NotifyFinished(missing);
                    continue;
                }
                if (!TryMarkProcessed(full)) { continue; }

                ArchiveTask task = new ArchiveTask();
                task.Path = full;
                task.Depth = 1;
                frozen.Add(task);
            }
            return frozen;
        }

        private static ArchiveResult Unreadable(string path, string message)
        {
            ArchiveResult result = new ArchiveResult();
            result.Path = path;
            result.Status = ArchiveStatus.SkippedUnreadable;
            result.Layers = 0;
            result.Files = 0;
            result.Failed = 0;
            result.OutputDir = "";
            result.Message = message;
            return result;
        }

        // ------------------------------------------------------------------
        // 逐归档处理
        // ------------------------------------------------------------------

        private ArchiveResult Process(ArchiveTask task, List<ArchiveTask> next)
        {
            ArchiveResult result = NewResult(task);
            string sourcePath = task.Path;

            // 低水位标志是「运行级」的：每个归档开始时清一次，避免上一个归档的致命状态
            // 在本归档的密码阶段被误读成「盘满」。
            _lowWaterHit = false;
            _lowWaterAfterRun = false;
            _lowWaterDuringExtraction = false;

            // --- 1) Sniffer（规格 §6.3：三档判定，永不静默丢弃）---
            // 「强制按压缩包尝试」（规格 §6.1 的逐项动作）只覆盖**格式门控**：用户已经明确要求
            //「不管你怎么判，试一次」，那就试一次 —— 但 I1（索引比对）、I2（暂存→校验→提交）、
            // I3（失败绝不删）、I4（容器文档门控）与预检安全上限全部照旧适用（见 RunOptions 的注释）。
            bool forced = _options.IsForcedTreatAsArchive(sourcePath);
            SniffKind kind = Sniff(sourcePath);
            if (!IsArchiveKind(kind))
            {
                if (!forced)
                {
                    if (kind == SniffKind.InProgressDownload)
                    {
                        return Reject(result, ArchiveStatus.SkippedUnreadable, "下载未完成（后缀表明文件还在下载中）：已跳过，原文件保留");
                    }
                    if (kind == SniffKind.Html)
                    {
                        return Reject(result, ArchiveStatus.SkippedUnreadable, "这是网页而不是压缩包（下载很可能已失败）：已跳过，原文件保留");
                    }
                    if (kind == SniffKind.Empty)
                    {
                        return Reject(result, ArchiveStatus.SkippedUnreadable, "0 字节文件：视为缺卷，已跳过，原文件保留");
                    }
                    return Reject(result, ArchiveStatus.SkippedUnreadable, "无法识别的格式：已跳过（界面上可对该项选择「强制按压缩包尝试」），原文件保留");
                }

                // 强制路径必须自己说明白：否则用户看到的是一条「明明被识别成网页却照样解压」的记录，
                // 无从知道这是自己点过的动作。判词到此为止 —— **不写「原文件保留」**：这一项万一真的
                // 解压成功，删除开关打开时它是允许被处置的（删不删由状态与 I3 决定，另有判词）。
                result.Message = AppendMessage(result.Message,
                    "已按用户要求「强制按压缩包尝试」（跳过格式识别门控：" + DescribeKind(kind) + "）");
            }
            // DamagedHeader（头部被改写、尾部仍有 zip 标记）继续往下走：7-Zip 有时仍读得出内容；
            // 读不出来会在下面得到「清单读取失败」的明确结局，绝不在这里猜。

            // --- 2) 分卷族（规格 §6.6：交给 7-Zip 的必须是权威成员；缺卷必须报具体名字）---
            VolumeSet volumes;
            string archiveArg = ResolveVolumeMember(sourcePath, task, out volumes);
            if (volumes != null && volumes.Missing != null && volumes.Missing.Count > 0)
            {
                return Reject(result, ArchiveStatus.SkippedUnreadable,
                    "分卷集不完整：缺 " + Join(volumes.Missing) + "（现有 " + Join(volumes.Members) + "）：已跳过，原文件全部保留");
            }

            // --- 3) 索引：**一次** Read 同时服务门控与完整性基线 ---
            // 【为什么这一次 Read 仍然用 CancellationToken.None（修复轮 #3 的 Finding 2，明确保留）】
            //   1. 它是**取消之前**的那次读：此刻我们还不知道这个包需不需要密码，而「需不需要密码」
            //      只能由它的结论（ListingFailed + HasEncryptedHeaders）决定 —— 半途打断会让后续
            //      每一步都建立在「读不出来」之上，把「被取消」误判成「需要密码」；
            //   2. 它的实现是 Task 4 已定稿的 SevenZipIndex.Read(path, archive, password)，签名里
            //      没有 token。为了这一处给它加参数会改动本修复轮之外的模块（及它的其它调用方），
            //      而 Task 12/14 已在消费这个签名 —— 不在本轮范围内动它。
            // 密码阶梯与清单探针的每一次调用都拿到真实 token（见下），那才是会跑很久的那一批。
            ArchiveIndex index = SevenZipIndex.Read(_options.SevenZipPath, archiveArg, null);
            string password = null;
            if (Preflight.NeedsPassword(index))
            {
                // 头部加密：连清单都读不出来。候选用 `l` 验证（对头部加密的 7z，只有正确密码的
                // `l -p…` 才成功；比 `t` 便宜得多），验证到了再用它把清单读出来。
                password = FindPassword(archiveArg, true, null, _options.Cancellation);
                if (password == null) { return PasswordUnavailable(result); }
                index = SevenZipIndex.Read(_options.SevenZipPath, archiveArg, password);
            }
            if (index.ListingFailed)
            {
                return Reject(result, ArchiveStatus.Failed,
                    "无法读取归档清单（不是压缩包或已损坏，7-Zip 退出码 " + index.ExitCode + "）：原包保留");
            }

            // --- 4) 门控（I4；必须在 Read 之后；拒绝 ⇒ 不递归、不删除）---
            string gateReason;
            if (ArchiveGater.Judge(index, out gateReason) == GateVerdict.ContainerDocument)
            {
                return Reject(result, ArchiveStatus.SkippedContainer, gateReason);
            }

            // --- 5) 输出目标与预检（§6.11 / §4.2）---
            string destRoot = ResolveOutputRoot(task);
            string stagingBase;
            string target;
            string namingProblem = ResolveTarget(destRoot, sourcePath, out stagingBase, out target);
            if (namingProblem != null)
            {
                return Reject(result, ArchiveStatus.SkippedUnreadable, namingProblem);
            }

            // 长路径按**流程实际能产生的最长目录名**判定（修复轮 #3 的 Finding 3）：解压写盘发生在
            // 暂存目录下（随机 .rerar-stage-xxx），但任何失败路径都会把暂存目录改名成
            // "<目标名> (未完成)"（同名已存在时再加 " (2)"）—— 那个名字比目标名本身长 10 个字符。
            // 只按「暂存名 / 裸目标名」判定会漏掉这一档：条目路径离上限只差几个字符时，改名本身
            // 会失败（改名失败会留下一个**已带哨兵**的目录，见 MarkIncomplete，但用户看到的名字
            // 不是他要的 "<名字> (未完成)"）。所以这里取三者中最长的那个做度量。
            string writeRoot = LongestWriteRoot(stagingBase, target);
            string overlong = Preflight.FindOverlongEntry(writeRoot, index);
            if (overlong != null)
            {
                return Reject(result, ArchiveStatus.SkippedUnreadable,
                    "路径过长（预检拒绝，规格 §10.2）：条目「" + overlong + "」加上输出目录名会超过 " +
                    Preflight.MaxPathLength + " 字符（度量的是流程实际会用到的最长目录名，含失败路径要改成的" +
                    "「 (未完成)」名）；原包保留");
            }
            string conflict = Preflight.FindConflictingEntries(index);
            if (conflict != null)
            {
                return Reject(result, ArchiveStatus.SkippedUnreadable,
                    "条目名冲突（大小写不敏感的重名，Windows 上是同一个路径，例如 Readme.txt 与 README.TXT）：" +
                    "拒绝写入以免静默覆盖（冲突项「" + conflict + "」）；原包保留");
            }
            // 安全上限（压缩炸弹 / 海量条目）：必须在**任何写入之前**判定，也必须排在空间预检
            // **之前** —— 空间不足是规格 §7 的**致命**档（中止整批），而炸弹是**单个归档**的问题，
            // 正确处置是跳过它、继续处理下一个（多目标用例钉住了这一点）。
            //
            // 分母是「包自身的物理字节」：分卷集按**所有成员求和**（那是这个包真实占的盘）；
            // 任何查询失败一律按 0 处理 —— 分母为 0 时 CheckExpansion 会退回 64 KiB 的下限，
            // 方向是**更保守**（比值更大、更容易拒绝），绝不会因为读不到大小就放行。
            PreflightReport caps = Preflight.CheckExpansion(index, ArchivePhysicalBytes(task, archiveArg));
            if (!caps.Ok)
            {
                return Reject(result, ArchiveStatus.SkippedUnreadable, caps.Reason);
            }

            PreflightReport space = Preflight.CheckFreeSpace(_disk, destRoot, index.TotalBytes, _options.MinFreeBytes);
            if (!space.Ok)
            {
                // 规格 §7：磁盘空间不足属**致命**档 ⇒ 中止整批，单条明确提示。原包一律保留。
                result.Status = ArchiveStatus.Failed;
                result.Message = space.Reason;
                FailRun(space.Reason);
                return result;
            }

            // --- 6) 密码（§6.5）：线索必须在任何改名/移动/删除**之前**采集 ---
            // 这一段里的每一次 7-Zip 调用都带真实 token（修复轮 #3 的 Finding 2）：此刻 linked 还没
            // 建起来（低水位轮询是从「预检通过」之后的第 7 步才启动的，见下），所以能发生的取消只有
            // 用户那一个 —— 正是 `_options.Cancellation`。取消落在这一段时，FindPassword 返回 null，
            // PasswordUnavailable 会把「已取消」如实报出来（绝不误报成「需要密码」）。
            if (password == null)
            {
                string listing = RunListingProbe(archiveArg, _options.Cancellation);
                if (Preflight.MentionsEncryption(listing))
                {
                    List<string> clues = HarvestClues(sourcePath, listing);
                    password = FindPassword(archiveArg, false, clues, _options.Cancellation);
                    if (password == null) { return PasswordUnavailable(result); }
                    // 只记「哪个候选 + 来源类别」，绝不把密码值写进结果/报告（凭据卫生，§6.5）。
                    result.Message = AppendMessage(result.Message,
                        "已用密码候选 " + _lastMatchedCandidate + " 通过 `7z t` 验证");
                }
            }

            // --- 7) 低水位轮询 + 暂存 → 解压 → 校验 → 提交 → 删除 ---
            // 轮询从这里（预检通过、密码已验证）启动，一直活到本归档处理结束：它覆盖暂存与解压
            // 全过程，于是「预检时够、真正写的时候不够」这个窗口也被盯住了。轮询线程与解压线程并发，
            // 低水位时的动作是 `linked.Cancel()` —— Runner 走 Job Object 把子进程打断（退出码 1223）。
            // 【范围如实说明】密码阶梯（上一段）发生时轮询还没启动，那一段里能发生的取消只有用户
            // 取消（已按 Finding 2 穿进每一次 `t`/`l`）；低水位轮询不覆盖密码阶梯 —— 这是本轮
            // 刻意不动的一处（只修被点名的发现，不重排安全路径）。
            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_options.Cancellation);
            _lowWaterHit = false;
            _lowWaterAfterRun = false;
            _lowWaterDuringExtraction = false;
            _lowWaterProbeReason = null;
            Timer poller = StartDiskPoller(destRoot, linked);
            RunResult extraction = null;
            try
            {
                PollDisk(destRoot, linked);     // 同步复检一次：不让「刚够」变成「边写边满」
                if (_lowWaterHit)
                {
                    return LowWaterAbort(result, stagingBase, target, sourcePath,
                        LowWaterReason("已中止本归档（尚未开始解压）"));
                }
                if (_options.Cancellation.IsCancellationRequested)
                {
                    _summary.Cancelled = true;
                    result.Status = ArchiveStatus.Failed;
                    result.Message = "已取消：尚未开始解压；原包保留";
                    return result;
                }

                try
                {
                    Directory.CreateDirectory(destRoot);
                    Directory.CreateDirectory(stagingBase);
                }
                catch (Exception ex)
                {
                    return Reject(result, ArchiveStatus.Failed,
                        "无法创建暂存目录（" + ex.GetType().Name + "：" + ex.Message + "）：原包保留");
                }
                try
                {
                    if (Directory.GetFileSystemEntries(stagingBase).Length != 0)
                    {
                        // I2：绝不向非空目录解压（这条同时消灭覆盖、撞名、半成品一整类问题）。
                        return Reject(result, ArchiveStatus.Failed, "暂存目录不是空的（I2 要求只向空目录解压）：原包保留");
                    }
                }
                catch (Exception)
                {
                }

                // Task 11（不可逆点 ①）：解压**开始之前**落一条记录，说明「即将写什么、写到哪里」。
                // 崩溃在这一步之后、提交之前，恢复路径就能从这一条（而没有随后的 extract-done）
                // 认出「这个目的地没有完成记录」。暂存目录也记下来，好让残留的暂存树可被定位。
                NoteJournal(Journal.AboutToExtractStep, target +
                    JournalField("source", sourcePath) + JournalField("staging", stagingBase));

                extraction = RunExtraction(archiveArg, sourcePath, stagingBase, password, destRoot, linked);

                // --- 8) 先分类「被打断」：1223 是被 Job Object 打断，不是解压失败，更不是成功 ---
                // 兜底复核：即便轮询线程错过了窗口（解压太快 / 定时器还没到点），也绝不把产物提交到
                // 已经低于低水位的卷上。它与轮询走**同一条**「不提交」路径，只是触发时机不同。
                //
                // 【为什么这里也要 try/catch（修复轮 #3 的 Finding 2）】注入进来的 provider 可能抛
                //（默认实现内部已吞掉异常并返回 0）。**绝不能让它逃出 Process** —— 那会把结果降级成
                //「内部错误」，用户看到的是「程序出错了」而不是「空间不明、未提交、原包保留」。
                // 处置方向与预检一致：查不出来 ⇒ 按**不足**处理（绝不因为查不出来就当够用），
                // 走与低水位**同一条**不提交路径，判词如实说是查询失败（不谎称可用空间低于低水位）。
                if (!_lowWaterHit)
                {
                    try
                    {
                        if (_disk.FreeBytes(destRoot) < _options.MinFreeBytes)
                        {
                            _lowWaterHit = true;
                            _lowWaterAfterRun = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _lowWaterHit = true;
                        _lowWaterAfterRun = true;
                        _lowWaterProbeReason = "无法查询目标卷可用空间（" + ex.GetType().Name + "：" + ex.Message +
                            "）：按空间不足处理，解压结果未提交；原包保留";
                    }
                }

                if (_lowWaterHit || linked.IsCancellationRequested || extraction.ExitCode == CancelledExitCode)
                {
                    if (_lowWaterHit)
                    {
                        // Review Focus #1：低水位 —— 干净中止，绝不提交半成品、绝不删原包。
                        // 判词区分「解压中途中止」与「解压完成后复核发现」：两者是同一条安全路径，
                        // 但读者（用户与复核者）需要知道是哪一种。
                        return LowWaterAbort(result, stagingBase, target, sourcePath, _lowWaterProbeReason != null
                            ? _lowWaterProbeReason
                            : LowWaterReason(_lowWaterAfterRun
                                ? "解压完成后的复核发现空间不足，未提交"
                                : (_lowWaterDuringExtraction ? "已在解压中途中止" : "已在解压开始前中止（未提交）")));
                    }

                    _summary.Cancelled = true;
                    string cancelMark = MarkIncomplete(stagingBase, target, sourcePath, "用户取消");
                    result.Status = ArchiveStatus.Failed;
                    result.Message = AppendMessage("已取消：本归档未完成；原包保留", cancelMark);
                    return result;
                }

                return VerifyPromoteAndDelete(task, result, index, extraction, stagingBase, target, sourcePath, next);
            }
            finally
            {
                poller.Dispose();
                linked.Dispose();
            }
        }

        // 第 9~12 步：校验（I1 + I2 的三条断言）→ 提交 → 下一轮候选 → 删除。
        // 单独成方法只是为了让上面那段 try/finally 不被十几个 return 撑得读不动；它不改变任何语义。
        private ArchiveResult VerifyPromoteAndDelete(ArchiveTask task, ArchiveResult result, ArchiveIndex index,
            RunResult extraction, string stagingBase, string target, string sourcePath, List<ArchiveTask> next)
        {
            // --- 9) 校验（I1 + I2 的三条断言）---
            TreeScan scan = ScanTree(stagingBase);

            if (scan.ReparsePoints.Count > 0 || scan.Escapes.Count > 0)
            {
                // 安全断言失败优先于一切：实测 7-Zip 会按 tar/zip 条目创建软链（探针 V17/V28）。
                // 整棵暂存树是**本次运行创建的**，所以这里可以整体收掉：把「指向别处的入口」留给用户，
                // 比「少一个未完成目录」危险得多。删除本身不跟随 reparse point（见 RemoveUnsafeTree）。
                RemoveUnsafeTree(stagingBase);
                result.Status = ArchiveStatus.Failed;
                result.Message = "暂存树安全断言失败（reparse point " + scan.ReparsePoints.Count +
                    " 个 / 越界路径 " + scan.Escapes.Count +
                    " 个）：未提交，暂存内容已收掉；原包保留";
                return result;
            }

            if (scan.Unreadable.Count > 0)
            {
                // 枚举/属性读不动 ⇒ 校验做不完（长路径、被占用、权限）：绝不据此判成功，
                // 但也**不删**暂存内容 —— 它是用户可能想要的半成品，按规格 §6.7 改名"未完成"。
                string unreadableMark = MarkIncomplete(stagingBase, target, sourcePath, "校验期间有 " + scan.Unreadable.Count + " 个条目读不动");
                result.Status = ArchiveStatus.Failed;
                result.Message = AppendMessage("无法完成完整性校验：暂存树里有 " + scan.Unreadable.Count +
                    " 个条目读不动（例如路径过长或被占用）：未提交，原包保留", unreadableMark);
                return result;
            }

            if (extraction.ExitCode >= 2)
            {
                string diagnostics = Head(Best(extraction));
                string fatal = DiskFullReason(extraction);
                if (fatal != null) { FailRun(fatal); }
                string failedMark = MarkIncomplete(stagingBase, target, sourcePath, "7-Zip 退出码 " + extraction.ExitCode);
                result.Status = ArchiveStatus.Failed;
                result.Message = AppendMessage("解压失败（7-Zip 退出码 " + extraction.ExitCode + "）：" + diagnostics + "；原包保留", failedMark);
                return result;
            }

            int expectedFiles;
            long expectedBytes;
            bool countsFiles;
            if (!Preflight.TryGetBaseline(index, extraction.StdOut, out expectedFiles, out expectedBytes, out countsFiles))
            {
                // 绝不用退出码顶上来当成功证据（I1）。没有基线 ⇒ 不提交、原包保留。
                string baselineMark = MarkIncomplete(stagingBase, target, sourcePath, "没有可用的完整性基线");
                result.Status = ArchiveStatus.Failed;
                result.Message = AppendMessage("无法校验完整性：没有可用的索引基线（绝不以退出码判定成功）；未提交，原包保留", baselineMark);
                return result;
            }

            result.Files = scan.Files;
            bool complete = countsFiles
                ? (scan.Files == expectedFiles && scan.Bytes == expectedBytes)
                : (scan.Bytes == expectedBytes);

            if (complete)
            {
                result.Status = ArchiveStatus.Completed;
            }
            else
            {
                // 「完成但有 N 个文件失败」：归档整体走完了，但磁盘上少东西。提交（用户拿得到大部分），
                // 状态如实标注，**且绝不删原包**（I3）。
                result.Status = ArchiveStatus.CompletedWithFailures;
                result.Failed = (countsFiles && expectedFiles > scan.Files) ? (expectedFiles - scan.Files) : 1;
                result.Message = AppendMessage(result.Message, "校验未通过：磁盘 " + scan.Files + " 个文件 / " + scan.Bytes +
                    " 字节，索引 " + expectedFiles + " 个文件 / " + expectedBytes + " 字节");
            }

            // --- 10) 提交（I2：同卷改名，原子）---
            try
            {
                Directory.Move(stagingBase, target);
            }
            catch (Exception ex)
            {
                string commitMark = MarkIncomplete(stagingBase, target, sourcePath, "提交失败：" + ex.Message);
                result.Status = ArchiveStatus.Failed;
                result.Message = AppendMessage(
                    AppendMessage(result.Message, "提交失败（同卷改名 " + ex.GetType().Name + "：" + ex.Message + "）：原包保留"),
                    commitMark);
                return result;
            }
            if (!Directory.Exists(target))
            {
                result.Status = ArchiveStatus.Failed;
                result.Message = AppendMessage(result.Message, "提交后复核失败：最终输出目录不存在；原包保留");
                return result;
            }
            result.OutputDir = target;

            // --- 11) 下一轮的候选只从**本轮新建的输出目录**里找 ---
            if (result.Status == ArchiveStatus.Completed || result.Status == ArchiveStatus.CompletedWithFailures)
            {
                CollectNested(target, task, next);
            }

            // --- 12) 删除（默认关；只有「完成且校验通过」才有资格）---
            // 返回值只喂给下面那条崩溃恢复记录（原包的最终去向），不参与任何安全判定。
            string originalDisposition = "kept";
            if (_options.DeleteOriginals)
            {
                if (result.Status == ArchiveStatus.Completed)
                {
                    originalDisposition = DeleteEligibleOriginal(task, result);
                }
                else
                {
                    result.Message = AppendMessage(result.Message, "未删除原包（只有「完成且校验通过」的归档才允许删除）");
                }
            }

            // --- 13) 提交已经成功 ⇒ 落「这个目的地处理完了」的持久记录（Task 11 不可逆点 ②）---
            // 有这一条（且 status=Completed），恢复查询就不会把一个**校验通过**的目录报成半成品；
            // 没有这一条（崩在提交与它之间）会保守地报成未完成 —— 那是刻意的方向（宁多报，绝不少报）。
            // status 如实带出校验结论：CompletedWithFailures 虽然也已提交，但它在内容上就是不完整的
            //（少文件），日志把这件事记下来，恢复查询据此继续把它报成未完成（详见 Journal 的说明）。
            // 它是否删原包由 I3 决定，明细里的 original 如实记录。
            NoteJournal(Journal.ExtractDoneStep, target +
                JournalField("status", result.Status.ToString()) +
                JournalField("original", originalDisposition));

            return result;
        }

        // ------------------------------------------------------------------
        // 7-Zip 调用
        // ------------------------------------------------------------------

        // 解压到暂存目录。参数表：x <归档> -o<暂存> -p<密码> -y -bsp1 -bb1。
        //   * `-o` 与路径必须是同一个参数（7z 的开关语法）；
        //   * 永远带 `-p`（I5；Runner 也会硬拒不带 -p 的参数表）；
        //   * `-bsp1 -bb1` 打开进度；7z 用 \r 原地刷新，切分由 Runner 负责（按 \r 与 \n 双重切）；
        //   * `-sccUTF-8` 由 Runner 统一注入，这里不重复传。
        //
        // 【为什么低水位轮询用独立线程，而不是挂在进度回调上】本轮实测（7-Zip 26.01）：
        // `x -bsp1 -bb1` 把百分比与成员名放在**不同的 \r 段**里 ——
        //     "  0%\r    \r- big.bin\r\n"
        // 而 Runner 的进度解析要求「百分比 + 标记 + 成员名」在同一段里（它的契约如此，本任务不改它），
        // 于是这类解压**一次回调都不会有**（探针实测 onProgress callbacks=0）。把「盘满就必须停」
        // 这件安全关键的事挂在一条可能一次都不触发的回调上，等于没有实现 Review Focus #1。
        // 所以轮询走 System.Threading.Timer：与进度回调是否存在、7z 的输出形状如何全都无关。
        private RunResult RunExtraction(string archiveArg, string sourcePath, string staging, string password,
            string destRoot, CancellationTokenSource linked)
        {
            List<string> args = new List<string>();
            args.Add("x");
            args.Add(archiveArg);
            args.Add("-o" + staging);
            args.Add("-p" + (password == null ? "" : password));
            args.Add("-y");
            args.Add("-bsp1");
            args.Add("-bb1");

            // 回调一律吞异常：Runner 会把回调抛出的异常重新抛到 Run 调用点（Task 3 的修复），
            // 而这里的一切失败都已在内部处理，不该再有一条从管道线程冒出来的路径。
            Action<int, string> onProgress = delegate(int percent, string member)
            {
                try { NotifyProgress(sourcePath, percent, member); }
                catch (Exception) { }
            };

            // 「这一刻子进程在跑」是判词诚实性的依据（轮询线程据此区分「解压中途中止」与
            // 「开始前中止」）。写这个标志不需要同步：最坏情况是判词用词略偏保守。
            _extractionRunning = true;
            try
            {
                return SevenZipRunner.Run(_options.SevenZipPath, args.ToArray(), onProgress, linked.Token);
            }
            finally
            {
                _extractionRunning = false;
            }
        }

        // 起一个后台轮询：DiskPollSeconds <= 0 ⇒ 尽量快（1 ms），测试用它把低水位变成确定性事件。
        private Timer StartDiskPoller(string destRoot, CancellationTokenSource linked)
        {
            long periodMs = _options.DiskPollSeconds > 0 ? _options.DiskPollSeconds * 1000L : 1L;
            return new Timer(delegate(object state) { PollDisk(destRoot, linked); }, null, periodMs, periodMs);
        }

        // 查一次可用空间；低于低水位就置标志并打断正在跑的子进程。
        // 这段代码在轮询线程与主线程上都会跑，所以它**绝不抛异常**（Timer 回调里漏出去的异常
        // 会终止整个进程），且标志是 volatile。
        private void PollDisk(string destRoot, CancellationTokenSource linked)
        {
            try
            {
                if (_disk.FreeBytes(destRoot) < _options.MinFreeBytes)
                {
                    _lowWaterHit = true;
                    if (_extractionRunning) { _lowWaterDuringExtraction = true; }
                    try { linked.Cancel(); }
                    catch (Exception) { }
                }
            }
            catch (Exception)
            {
            }
        }

        private string LowWaterReason(string phase)
        {
            return "磁盘空间不足：可用空间低于低水位 " + _options.MinFreeBytes + " 字节，" + phase + "；原包保留";
        }

        // `l -slt` 探针：一个**便宜**的清单调用（不解压），同时用于两件事：
        //   1. 判「成员是否加密」（`Encrypted = +`）——ArchiveIndex 不带这个信息；
        //   2. 采集归档注释作为密码线索（§6.5）。
        // 它不承担基线职责：基线只由那次 SevenZipIndex.Read 提供（I1 的「一次 Read」）。
        //
        // ct 必须传进来（修复轮 #3 的 Finding 2）：这一步发生在密码阶梯之前，对「成员加密、
        // 头部明文」的包，它是整条流水线上第一个可能跑很久的 7-Zip 调用；用户按下取消时，
        // 它同样必须能被 Job Object 立刻打断，而不是等它自己跑完。
        private string RunListingProbe(string archiveArg, CancellationToken ct)
        {
            string[] args = new string[] { "l", "-slt", archiveArg, "-p", "-y" };
            RunResult listing = SevenZipRunner.Run(_options.SevenZipPath, args, null, ct);
            return (listing.StdOut == null ? "" : listing.StdOut) + "\n" + (listing.StdErr == null ? "" : listing.StdErr);
        }

        // 密码阶梯（§6.5）：手动 → 本次已验证成功 → 导入字典 → 内置字典 → 同目录线索。
        // 逐个用 `t`（或头部加密时的 `l`）验证 —— **绝不用 x**：错密码的 x 会写出大量垃圾。
        //
        // ct（修复轮 #3 的 Finding 2）：每一次候选验证都是一次**完整校验**，在几十 GB 的包上
        // 可能一跑就是几分钟 —— 那正是最需要能被打断的地方。取消必须能立刻打断**正在跑的那一次**
        // 调用，而不是等它自己结束之后才由循环顶部的 Stopped() 发现。
        private string FindPassword(string archiveArg, bool useListing, IEnumerable<string> clues, CancellationToken ct)
        {
            List<string> dict = new List<string>();
            if (_options.DictLines != null) { dict.AddRange(_options.DictLines); }
            dict.AddRange(BuiltInDictionary);

            IEnumerable<string> ladder = PasswordCandidates.Build(_options.Password, _verifiedPasswords, dict, clues);

            _lastLadderTries = 0;
            _lastMatchedCandidate = "";

            int ordinal = 0;
            foreach (string candidate in ladder)
            {
                if (Stopped()) { return null; }

                ordinal++;
                _lastLadderTries = ordinal;

                if (!VerifyPassword(archiveArg, candidate, useListing, ct)) { continue; }

                // 先**分类**，再把它加进 _verifiedPasswords。顺序反了的话，字典层命中的候选会在分类
                // 之前就被塞进「本次运行已验证过的密码」，于是所有字典命中一律被报成那一类 ——
                // 内置字典 / 导入字典这两个标签永远不可达（修复轮 #3 的 Finding 4）。
                // 分类保持阶梯的层次顺序（手动 → 本次已验证 → 字典 → 线索）：标签要说的是
                // 「这一个候选是**从阶梯哪一层**来的」，而不是「它属于哪些集合」。
                string source = DescribeCandidateSource(candidate, dict);

                // 只在**验证通过之后**才缓存（I5：ZipCrypto 有 1/256 的头校验假阳性，
                // 凭「有文件出现」缓存会把一个错密码推成下一个归档的首选）。
                if (!_verifiedPasswords.Contains(candidate)) { _verifiedPasswords.Add(candidate); }
                _lastMatchedCandidate = "#" + ordinal + "（" + source + "）";
                return candidate;
            }
            return null;
        }

        // 只用 `t`（测试）或 `l`（清单）验证候选，绝不用 `x`。两者都必须带 -p（I5）。
        // ct 直通 SevenZipRunner：token 一响，Runner 走 Job Object 把这次 `t`/`l` 子进程打断
        //（退出码 1223，IsSuccess == false），于是「取消」不必等这次完整校验自己跑完。
        private bool VerifyPassword(string archiveArg, string candidate, bool useListing, CancellationToken ct)
        {
            string[] args = useListing
                ? new string[] { "l", "-slt", archiveArg, "-p" + candidate, "-y" }
                : new string[] { "t", archiveArg, "-p" + candidate, "-y" };

            return SevenZipRunner.IsSuccess(SevenZipRunner.Run(_options.SevenZipPath, args, null, ct).ExitCode);
        }

        // 命中候选来自阶梯的哪一层（只报**类别**，不报值 —— 密码绝不进结果/报告）。
        //
        // 调用点必须排在 `_verifiedPasswords.Add` **之前**（见 FindPassword）：否则刚加进去的
        // 字典候选会被这里第二档吃掉，内置字典 / 导入字典两个标签永远不可达（Finding 4）。
        // 三档的顺序刻意与阶梯一致（手动 → 本次已验证 → 字典）：标签说的是「来自阶梯哪一层」。
        private string DescribeCandidateSource(string candidate, List<string> dict)
        {
            if (!string.IsNullOrEmpty(_options.Password))
            {
                foreach (string variant in PasswordCandidates.Variants(_options.Password))
                {
                    if (variant == candidate) { return "手动输入"; }
                }
            }
            foreach (string verified in _verifiedPasswords)
            {
                if (verified == candidate) { return "本次运行已验证过的密码"; }
            }
            if (dict.Contains(candidate))
            {
                return (_options.DictLines != null && _options.DictLines.Contains(candidate)) ? "导入字典" : "内置字典";
            }
            return "同目录线索";
        }

        // ------------------------------------------------------------------
        // 密码线索采集（§6.5）—— 必须在任何改名/移动/删除之前调用
        // ------------------------------------------------------------------

        private List<string> HarvestClues(string sourcePath, string listingText)
        {
            List<string> clues = new List<string>();
            string directory = null;
            try { directory = Path.GetDirectoryName(sourcePath); }
            catch (Exception) { directory = null; }

            // 1) 文件名与文件夹名（含归档自己的名字与它所在的目录名）。
            try
            {
                List<string> names = new List<string>();
                names.Add(sourcePath);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    names.Add(directory);
                    names.AddRange(Directory.GetFiles(directory));
                    names.AddRange(Directory.GetDirectories(directory));
                }
                clues.AddRange(PasswordCandidates.CluesFromFileNames(names));
            }
            catch (Exception)
            {
                // 目录读不动就少一类线索，绝不因此中断处理。
            }

            // 2) 同目录小文本文件的**内容**（不是路径）。
            try
            {
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    foreach (string file in Directory.GetFiles(directory))
                    {
                        if (!IsTextClueFile(file)) { continue; }
                        string content = ReadTextForClues(file);
                        if (content != null) { clues.AddRange(PasswordCandidates.CluesFromTextFile(content)); }
                    }
                }
            }
            catch (Exception)
            {
            }

            // 3) 归档注释（来自 `l -slt` 的 `Comment = …` 行）。
            try
            {
                foreach (string comment in CommentsFrom(listingText))
                {
                    clues.AddRange(PasswordCandidates.CluesFromTextFile(comment));
                }
            }
            catch (Exception)
            {
            }

            return clues;
        }

        private static bool IsTextClueFile(string path)
        {
            string extension = null;
            try { extension = Path.GetExtension(path); }
            catch (Exception) { return false; }
            if (string.IsNullOrEmpty(extension)) { return false; }

            bool known = false;
            foreach (string candidate in TextClueExtensions)
            {
                if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
            }
            if (!known) { return false; }

            try { return new FileInfo(path).Length <= MaxTextClueBytes; }
            catch (Exception) { return false; }
        }

        // 先按严格 UTF-8 读（非法字节序列直接失败），失败再退回系统 ANSI 代码页（中文 Windows 上
        // 就是 GBK）—— 网盘文本文件两种编码都很常见，只认一种会白丢一半线索。
        private static string ReadTextForClues(string path)
        {
            try { return File.ReadAllText(path, new UTF8Encoding(false, true)); }
            catch (Exception) { }

            try { return File.ReadAllText(path, Encoding.Default); }
            catch (Exception) { return null; }
        }

        // `l -slt` 里非空的 `Comment = …` 值（属性段与条目段都可能是线索）。
        private static List<string> CommentsFrom(string listingText)
        {
            List<string> comments = new List<string>();
            if (string.IsNullOrEmpty(listingText)) { return comments; }

            int start = 0;
            for (int i = 0; i <= listingText.Length; i++)
            {
                if (i < listingText.Length && listingText[i] != '\n') { continue; }

                string line = listingText.Substring(start, i - start).Trim();
                start = i + 1;

                if (!line.StartsWith("Comment", StringComparison.OrdinalIgnoreCase)) { continue; }
                int equals = line.IndexOf('=');
                if (equals < 0) { continue; }

                string value = line.Substring(equals + 1).Trim();
                if (value.Length > 0) { comments.Add(value); }
            }
            return comments;
        }

        // ------------------------------------------------------------------
        // 删除（I3）—— 默认关；只有「完成且校验通过」才走到这里
        //
        // 返回值 = 原包的最终去向（"kept" / "deleted" / "quarantined"），只用于崩溃恢复日志的
        // extract-done 明细；它**不参与任何安全判定**，判定仍然只由 Plan / 删前删后的检查决定。
        // ------------------------------------------------------------------

        private string DeleteEligibleOriginal(ArchiveTask task, ArchiveResult result)
        {
            string origin = task.Path;

            // 规格 §9.2 第 5 条 ④：分卷集要么所有成员一并处置，要么**明确全部不处置**。这里选后者：
            // 只删用户点名的那个成员会留下 .z02/.part2.rar 孤儿（原脚本的老毛病），而推断出来的
            // 兄弟卷并不在本次运行的候选清单里 —— 删它们就越过了「绝不删除本次运行之外的任何文件」。
            if (task.VolumeMembers != null && task.VolumeMembers.Count > 1)
            {
                result.Message = AppendMessage(result.Message, "分卷集（" + task.VolumeMembers.Count +
                    " 个成员）：按「全部不处置」处理，原包全部保留（避免只删一个、留下孤儿分卷）");
                return "kept";
            }

            string why;
            DeletePlan plan = RecycleBinGuard.Plan(origin, out why);

            if (plan == DeletePlan.Refuse)
            {
                result.Message = AppendMessage(result.Message, "未删除原包：" + why);
                return "kept";
            }

            // Task 11（不可逆点 ③）：处置是**不可逆**的（回收站未必可核实、隔离是一次移动），
            // 所以在这一刻之前先落一条记录。崩在「记录已写、处置未做」之间时，日志留下的是
            //「已计划删除、结局未知」—— 回读它的人绝不能据此认为原包已经安全消失，必须去盘上核实。
            // 结局（deleted / quarantined / kept）写在随后的 extract-done 明细里。
            NoteJournal(Journal.AboutToDeleteStep, origin + JournalField("destination", result.OutputDir));

            if (plan == DeletePlan.Quarantine)
            {
                QuarantineOriginal(origin, result, why);
                return File.Exists(origin) ? "kept" : "quarantined";
            }

            // Recycle：删前取基线，删后核实**本次删除新增了条目**。
            // Recycle() 返回 true 只是「API 没报错」—— 实测远程卷/超配额卷上它会不报错地永久删除，
            // 所以返回值一律不作数（I3 的诚实层）。
            string fileName = Path.GetFileName(origin);
            RecycleBinSnapshot before = RecycleBinGuard.CaptureBin(fileName);
            bool called = RecycleBinGuard.Recycle(origin);

            if (File.Exists(origin))
            {
                result.Message = AppendMessage(result.Message,
                    called ? "原包删除未生效，文件仍在" : "回收站删除失败，原包仍在（" + why + "）");
                return "kept";
            }

            bool verified = called && RecycleBinGuard.VerifyInBin(fileName, before);
            result.Message = AppendMessage(result.Message, verified
                ? "原包已移入回收站（已核实：回收站里新增了该条目）"
                : "原包已被删除，但回收站未能核实（很可能已被永久删除）");
            return "deleted";
        }

        // 隔离：同卷移动到 `_originals_<时间戳>\`。不删除，只换位置；任何失败都如实报「原包仍在」。
        private void QuarantineOriginal(string origin, ArchiveResult result, string why)
        {
            try
            {
                string directory = Path.GetDirectoryName(origin);
                if (string.IsNullOrEmpty(directory)) { directory = Path.GetPathRoot(origin); }

                string folder = Path.Combine(directory, OriginalsPrefix + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Directory.CreateDirectory(folder);

                string destination = Path.Combine(folder, Path.GetFileName(origin));
                for (int n = 2; n < MaxNameAttempts && File.Exists(destination); n++)
                {
                    destination = Path.Combine(folder,
                        Path.GetFileNameWithoutExtension(origin) + " (" + n + ")" + Path.GetExtension(origin));
                }

                File.Move(origin, destination);
                if (File.Exists(origin) || !File.Exists(destination))
                {
                    result.Message = AppendMessage(result.Message, "隔离移动未生效，原包仍在：" + origin);
                    return;
                }
                result.Message = AppendMessage(result.Message, "原包已移动到同卷隔离文件夹 " + folder + "（未删除；原因：" + why + "）");
            }
            catch (Exception ex)
            {
                result.Message = AppendMessage(result.Message,
                    "隔离移动失败（" + ex.GetType().Name + "：" + ex.Message + "），原包仍在");
            }
        }

        // ------------------------------------------------------------------
        // 暂存树扫描 / 清理
        // ------------------------------------------------------------------

        private sealed class TreeScan
        {
            public int Files;
            public long Bytes;
            public List<string> FilePaths = new List<string>();
            public List<string> ReparsePoints = new List<string>();
            public List<string> Escapes = new List<string>();
            public List<string> Unreadable = new List<string>();
        }

        // 遍历暂存树：数文件与字节，同时收下三类违规。
        // **绝不进入 reparse point**：一个指回父目录的软链就能让遍历死循环、或枚举到输出根之外。
        private static TreeScan ScanTree(string root)
        {
            TreeScan scan = new TreeScan();

            string rootFull;
            try { rootFull = Path.GetFullPath(root); }
            catch (Exception ex)
            {
                scan.Unreadable.Add(root + "（" + ex.GetType().Name + "）");
                return scan;
            }

            Stack<string> pending = new Stack<string>();
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Push(rootFull);
            visited.Add(rootFull);

            while (pending.Count > 0)
            {
                string directory = pending.Pop();

                string[] entries;
                try { entries = Directory.GetFileSystemEntries(directory); }
                catch (Exception ex)
                {
                    scan.Unreadable.Add(directory + "（" + ex.GetType().Name + "）");
                    continue;
                }

                foreach (string entry in entries)
                {
                    // 规格 §6.7：每条产出路径都必须是暂存根的**严格子项**。
                    if (!PathSanitizer.IsStrictChild(rootFull, entry))
                    {
                        scan.Escapes.Add(entry);
                        continue;
                    }

                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(entry); }
                    catch (Exception ex)
                    {
                        scan.Unreadable.Add(entry + "（" + ex.GetType().Name + "）");
                        continue;
                    }

                    // 实测 7-Zip 会按 tar/zip 条目创建 reparse point（软链/联接）：单独成类，不往下走。
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        scan.ReparsePoints.Add(entry);
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (visited.Add(entry)) { pending.Push(entry); }
                        continue;
                    }

                    scan.Files++;
                    scan.FilePaths.Add(entry);
                    try { scan.Bytes += new FileInfo(entry).Length; }
                    catch (Exception ex)
                    {
                        scan.Unreadable.Add(entry + "（" + ex.GetType().Name + "）");
                    }
                }
            }

            return scan;
        }

        // 收掉一棵**本次运行创建的**暂存树。手工后序遍历，且绝不对 reparse point 递归
        //（软链/联接本身只是一个目录项，删掉它不会碰到目标）。
        private static void RemoveUnsafeTree(string root)
        {
            List<string> directories = new List<string>();
            Stack<string> pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                directories.Add(directory);

                string[] entries;
                try { entries = Directory.GetFileSystemEntries(directory); }
                catch (Exception) { continue; }

                foreach (string entry in entries)
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(entry); }
                    catch (Exception) { continue; }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        try
                        {
                            if ((attributes & FileAttributes.Directory) != 0) { Directory.Delete(entry, false); }
                            else { File.Delete(entry); }
                        }
                        catch (Exception)
                        {
                        }
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    try { File.Delete(entry); }
                    catch (Exception) { }
                }
            }

            for (int i = directories.Count - 1; i >= 0; i--)
            {
                try { Directory.Delete(directories[i], false); }
                catch (Exception) { }
            }
        }

        // 规格 §6.7 的失败路径：写哨兵 + 暂存目录改名 "<目标名> (未完成)"。
        // 幂等、尽力而为、**绝不删除**、绝不抛给调用方。
        //
        // 【顺序是安全属性，不是风格（修复轮 #3 的 Finding 3）】哨兵**先写、且写在暂存目录名之下**，
        // 然后才改名。原因：改名后的目录名比目标名还长 6 个字符（再消歧一次就是 10 个），而哨兵
        // 文件名本身还有 21 个字符；在离 MAX_PATH(259) 上限很近的输入上，「改名成功、往新名字里写
        // 哨兵失败」会让用户拿到一个**没有哨兵**的目录（旧实现正是这样，而且异常被吞掉，用户完全
        // 不知情）—— 而在半成品/盘满那一档，哨兵是唯一能告诉他「这不是完整结果」的东西。
        // 先写在短名字下 ⇒ 改名只是 Directory.Move，哨兵随目录一起搬过去 ⇒ 改名失败也绝不可能
        // 变成「没有哨兵」。
        //
        // 返回值：null = 一切照旧；否则 = 一句**必须如实写进该归档判词**的话
        //（改名失败 / 哨兵写不进去）。静默留下一个没有标记的目录是本轮修掉的缺陷之一，
        // 所以这里绝不再吞掉「标记没写成」这件事。
        private static string MarkIncomplete(string staging, string target, string sourcePath, string reason)
        {
            string warning = null;
            try
            {
                if (!Directory.Exists(staging)) { return null; }

                // 1) 哨兵先落在**暂存名下**（同一次运行创建的目录，名字最短）。
                string text = "Rerar 未完成的解压\r\n" +
                    "源归档：" + sourcePath + "\r\n" +
                    "原因：" + reason + "\r\n" +
                    "本目录是解压中途留下的产物，**不是**完整结果；源归档未被删除。\r\n";
                try
                {
                    File.WriteAllText(Path.Combine(staging, SentinelName), text, new UTF8Encoding(true));
                }
                catch (Exception ex)
                {
                    warning = "未完成标记写入失败（" + ex.GetType().Name + "：" + ex.Message + "）：目录 " +
                        staging + " 里没有 " + SentinelName + "，请勿把它当作完整结果";
                }

                // 2) 再改名；失败就留在暂存名下（哨兵已经在里面了，绝不删）。
                string destination = target + IncompleteSuffix;
                for (int n = 2; n < MaxNameAttempts && (File.Exists(destination) || Directory.Exists(destination)); n++)
                {
                    destination = target + IncompleteSuffix + " (" + n + ")";
                }

                try
                {
                    Directory.Move(staging, destination);
                }
                catch (Exception ex)
                {
                    warning = AppendMessage(warning, "未完成目录改名失败（" + ex.GetType().Name + "：" + ex.Message +
                        "）：仍是 " + staging + "（" + SentinelName + " 已在该目录里）");
                }
            }
            catch (Exception ex)
            {
                // 连 Directory.Exists / 拼路径都炸了：绝不假装标记写好了。
                warning = AppendMessage(warning, "未完成目录处理失败（" + ex.GetType().Name + "：" + ex.Message +
                    "）：暂存目录 " + staging);
            }
            return warning;
        }

        // 失败路径实际会用到的最长目录名：暂存名 vs 目标名 vs 目标名 + " (未完成)" + " (2)"。
        // " (2)" 是 MarkIncomplete 在同名目录已存在时的消歧后缀（与规格 §6.11 的 " (2)" 同形）。
        // 预检拿它做度量根，于是「改名后仍然放得下」是被**事前**保证的，而不是事后吞掉异常。
        private static string LongestWriteRoot(string stagingBase, string target)
        {
            string longest = stagingBase.Length >= target.Length ? stagingBase : target;
            string incomplete = target + IncompleteSuffix + " (2)";
            return incomplete.Length > longest.Length ? incomplete : longest;
        }

        // ------------------------------------------------------------------
        // 输出目标（规格 §6.11）
        // ------------------------------------------------------------------

        private string ResolveOutputRoot(ArchiveTask task)
        {
            // 顶层可以指定输出根；嵌套层固定在归档自己所在目录（也就是上一层输出目录**之内**），
            // 不再新套一层包装目录 —— 规格 §6.11 用它抑制 MAX_PATH 增长。
            if (task.Parent == null && !string.IsNullOrEmpty(_options.OutputRoot))
            {
                try { return Path.GetFullPath(_options.OutputRoot); }
                catch (Exception) { }
            }

            string directory = Path.GetDirectoryName(task.Path);
            return string.IsNullOrEmpty(directory) ? Path.GetPathRoot(task.Path) : directory;
        }

        // 目标目录名 + 暂存目录名。返回非 null = 中止该归档的中文原因（不回落父目录、不删原包）。
        private string ResolveTarget(string destRoot, string sourcePath, out string stagingBase, out string target)
        {
            stagingBase = Path.Combine(destRoot, StagingPrefix + Guid.NewGuid().ToString("N").Substring(0, 8));
            target = null;

            string desired = Path.Combine(destRoot, PathSanitizer.Sanitize(Path.GetFileName(sourcePath)));

            // 规格 §6.11：目标被**同名文件**占用 ⇒ 中止该归档、不回落父目录、不删原包。
            // PathSanitizer.Uniquify 对「被文件占用」刻意原样返回，这就是那句「必须自己发现」的落实点。
            if (File.Exists(desired))
            {
                return "目标路径「" + desired + "」被同名文件占用：按规格 §6.11 中止该归档（不回落父目录、不删原包）";
            }

            target = PathSanitizer.Uniquify(desired);
            if (File.Exists(target) || Directory.Exists(target))
            {
                // Uniquify 把「空目录」当可复用，但本流程的提交是 Directory.Move（目标必须不存在）；
                // 而删掉一个非本次创建的空目录同样越界。于是自己往下找一个「文件与目录都不存在」的名字。
                target = NextFreeName(desired);
            }
            if (target == null)
            {
                return "找不到可用的输出目录名（已尝试 " + MaxNameAttempts + " 次）：原包保留";
            }

            // 规格 §6.11 的禁止项：输出路径绝不能等于任何输入归档自身的路径。这里至少钉住「自己」。
            try
            {
                if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
                {
                    return "输出路径与源归档路径相同（规格 §6.11 禁止边读边写）：已中止该归档，原包保留";
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        private static string NextFreeName(string desired)
        {
            for (int n = 2; n < MaxNameAttempts; n++)
            {
                string candidate = desired + " (" + n + ")";
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) { return candidate; }
            }
            return null;
        }

        // ------------------------------------------------------------------
        // 下一轮候选 / 运行级去重
        // ------------------------------------------------------------------

        // 只扫**本轮新建并提交成功**的输出目录（绝不重新全树扫描），把里面的归档记成下一轮候选。
        private void CollectNested(string root, ArchiveTask parent, List<ArchiveTask> next)
        {
            TreeScan scan = ScanTree(root);
            foreach (string file in scan.FilePaths)
            {
                if (!IsArchiveKind(Sniff(file))) { continue; }
                if (!TryMarkProcessed(file)) { continue; }      // 运行级去重（规范路径 + 大小 + mtime）

                ArchiveTask task = new ArchiveTask();
                task.Path = file;
                task.Depth = parent.Depth + 1;
                task.Parent = parent;
                next.Add(task);
            }
        }

        private bool TryMarkProcessed(string path)
        {
            string key;
            try
            {
                FileInfo info = new FileInfo(path);
                key = info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            }
            catch (Exception)
            {
                key = path;
            }
            return _processed.Add(key);
        }

        private static bool IsArchiveKind(SniffKind kind)
        {
            switch (kind)
            {
                case SniffKind.Zip:
                case SniffKind.Rar:
                case SniffKind.Rar5:
                case SniffKind.SevenZip:
                case SniffKind.Gzip:
                case SniffKind.Bzip2:
                case SniffKind.Xz:
                case SniffKind.Tar:
                case SniffKind.DamagedHeader:
                    return true;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------
        // 嗅探 / 分卷
        // ------------------------------------------------------------------

        private static SniffKind Sniff(string path)
        {
            long length = -1;
            try { length = new FileInfo(path).Length; }
            catch (Exception) { }

            // tar 的魔数在偏移 257，所以头部要 ≥262 字节；64KB 尾部窗口是**调用方**的责任（规格 §6.3）。
            return Sniffer.Classify(ReadPrefix(path, 262), length, Path.GetFileName(path), ReadSuffix(path, 64 * 1024));
        }

        private static byte[] ReadPrefix(string path, int count)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int take = (int)Math.Min(stream.Length, count);
                    if (take <= 0) { return new byte[0]; }

                    byte[] buffer = new byte[take];
                    int read = stream.Read(buffer, 0, take);
                    if (read == take) { return buffer; }

                    byte[] exact = new byte[read];
                    Array.Copy(buffer, exact, read);
                    return exact;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static byte[] ReadSuffix(string path, int count)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long length = stream.Length;
                    int take = (int)Math.Min(length, count);
                    if (take <= 0) { return new byte[0]; }

                    byte[] buffer = new byte[take];
                    stream.Seek(length - take, SeekOrigin.Begin);
                    int read = stream.Read(buffer, 0, take);
                    if (read == take) { return buffer; }

                    byte[] exact = new byte[read];
                    Array.Copy(buffer, exact, read);
                    return exact;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // 分卷族解析：返回交给 7-Zip 的权威成员，并把成员清单记在 task 上（删除策略要用）。
        // 任何查询失败都按「独立文件」处理：不做拼接、不推断兄弟卷（安全方向）。
        private static string ResolveVolumeMember(string sourcePath, ArchiveTask task, out VolumeSet volumes)
        {
            volumes = null;
            try
            {
                string directory = Path.GetDirectoryName(sourcePath);
                if (string.IsNullOrEmpty(directory)) { return sourcePath; }

                VolumeSet set;
                if (!VolumeFamily.TryResolve(Directory.GetFiles(directory), sourcePath, out set)) { return sourcePath; }
                if (set == null || set.Members == null || set.Members.Count < 2) { return sourcePath; }

                volumes = set;
                task.VolumeMembers = set.Members;
                string authoritative = set.AuthoritativeMember;
                return string.IsNullOrEmpty(authoritative) ? sourcePath : authoritative;
            }
            catch (Exception)
            {
                return sourcePath;
            }
        }

        // 归档自身的物理字节数（预检解压比的分母，见 Preflight.CheckExpansion）。
        // 分卷集 = **所有成员之和**（那才是这个包真实占的盘，用第一个成员的大小会把比值人为放大）；
        // 否则 = 交给 7-Zip 的那个文件。任何一项查不到大小就按 0 计入：分母偏小只会让比值偏大、
        // 更容易被拒 —— 安全方向。绝不因为「读不到大小」而跳过上限。
        private static long ArchivePhysicalBytes(ArchiveTask task, string archiveArg)
        {
            if (task.VolumeMembers != null && task.VolumeMembers.Count > 0)
            {
                long total = 0;
                foreach (string member in task.VolumeMembers)
                {
                    long one = LengthOrZero(member);
                    if (total > long.MaxValue - one) { return long.MaxValue; }   // 饱和，绝不回绕成小数
                    total += one;
                }
                return total;
            }
            return LengthOrZero(archiveArg);
        }

        private static long LengthOrZero(string path)
        {
            if (string.IsNullOrEmpty(path)) { return 0; }
            try { return new FileInfo(path).Length; }
            catch (Exception) { return 0; }
        }

        // Sniffer 结论的中文说法：只用在「强制尝试」的判词里，用户需要知道自己 override 了哪一档。
        private static string DescribeKind(SniffKind kind)
        {
            switch (kind)
            {
                case SniffKind.Unknown: return "无法识别";
                case SniffKind.Html: return "这是网页";
                case SniffKind.Empty: return "0 字节";
                case SniffKind.InProgressDownload: return "下载未完成";
                default: return kind.ToString();
            }
        }

        // ------------------------------------------------------------------
        // 结果与运行级状态
        // ------------------------------------------------------------------

        private ArchiveResult NewResult(ArchiveTask task)
        {
            ArchiveResult result = new ArchiveResult();
            result.Path = task.Path;
            result.Status = ArchiveStatus.Failed;    // 保守默认：任何提前返回都不会「默认成功」
            result.Layers = task.Depth;
            result.Files = 0;
            result.Failed = 0;
            result.OutputDir = "";
            result.Message = "";
            return result;
        }

        // 拒绝这一项：置状态 + 判词。用 AppendMessage 而不是覆盖 —— 「强制按压缩包尝试」在格式门控
        // 处已经写下了一条判词，之后任何一档拒绝（清单读不出、门控、预检、暂存）都必须把它保留下来，
        // 否则用户看到的记录会与自己点过的动作对不上。对非强制的项，进入这里时 Message 一定是空的
        //（每条提前返回路径都紧跟着 return），所以 AppendMessage 与直接赋值完全等价。
        private static ArchiveResult Reject(ArchiveResult result, ArchiveStatus status, string message)
        {
            result.Status = status;
            result.Message = AppendMessage(result.Message, message);
            return result;
        }

        // 密码阶梯走完仍没有可用密码（或中途被取消/盘已满）。取消绝不是「需要密码」，盘满也不是。
        private ArchiveResult PasswordUnavailable(ArchiveResult result)
        {
            if (_lowWaterHit)
            {
                return LowWaterAbort(result, null, null, null, LowWaterReason("已中止本归档（尚未开始解压）"));
            }
            if (_summary.Cancelled || _options.Cancellation.IsCancellationRequested)
            {
                // 运行级也要如实标注「取消」（修复轮 #3 的 Finding 2）：只把判词写成「已取消」而
                // RunSummary.Cancelled 仍是 false，消费方（CLI 退出码 / 界面）会把一次被取消的运行
                // 当成正常跑完 —— 这正是「取消必须被**观察到**」的另一半。
                _summary.Cancelled = true;
                result.Status = ArchiveStatus.Failed;
                result.Message = "已取消：密码尚未验证完成；原包保留";
                return result;
            }

            result.Status = ArchiveStatus.SkippedNeedsPassword;
            // 用 AppendMessage 而不是直接赋值：强制按压缩包尝试的项在这里也必须保留那条
            //「这是你要求强制的」判词，否则用户会看到一条与自己的动作对不上的记录。
            result.Message = AppendMessage(result.Message, "需要密码：" + _lastLadderTries +
                " 个候选都未通过验证（候选一律用 `7z t` 或 `l` 验证，绝不用 x 试密码）；原包保留");
            return result;
        }

        private ArchiveResult DepthLimitResult(ArchiveTask task)
        {
            ArchiveResult result = NewResult(task);
            result.Status = ArchiveStatus.NotAttemptedDepthLimit;
            result.Message = "达到递归深度上限（" + _options.MaxDepth + " 层）：本归档未处理，原包保留";
            return result;
        }

        private void Record(ArchiveTask task, ArchiveResult result, RunSummary summary)
        {
            _resultsByTask[task] = result;
            PropagateLayers(task, result.Layers);
            summary.Results.Add(result);
            NotifyFinished(result);
        }

        // Layers = 该归档**达到**的嵌套层数：自己那一层（task.Depth）沿 Parent 链向上取最大。
        private void PropagateLayers(ArchiveTask task, int layers)
        {
            ArchiveTask parent = task.Parent;
            while (parent != null)
            {
                ArchiveResult parentResult;
                if (_resultsByTask.TryGetValue(parent, out parentResult) && parentResult.Layers < layers)
                {
                    parentResult.Layers = layers;
                }
                parent = parent.Parent;
            }
        }

        private bool Stopped()
        {
            // 低水位也算「停」：盘已经不够了，再往下试密码（每个候选都是一次完整解压校验）
            // 只会白烧时间，还可能把盘写得更满。
            return _summary.FatalReason != null || _summary.Cancelled || _lowWaterHit ||
                _options.Cancellation.IsCancellationRequested;
        }

        // 低水位中止：三个触发点（开始前同步复检 / 密码阶段 / 解压中途或解压后复核）走的是
        // **同一条**「不提交、不删除、如实报原因」的路径，只有判词按阶段不同。
        // reason 是**完整的**判词（不再在这里拼「低于低水位」那句前缀）：可用空间**查不出来**
        // 时也走这条路，那时的判词是「无法查询…按空间不足处理」—— 绝不说一句自己证明不了的话。
        private ArchiveResult LowWaterAbort(ArchiveResult result, string stagingBase, string target, string sourcePath, string reason)
        {
            FailRun(reason);
            string markWarning = null;
            if (!string.IsNullOrEmpty(stagingBase)) { markWarning = MarkIncomplete(stagingBase, target, sourcePath, reason); }
            result.Status = ArchiveStatus.Failed;
            result.Message = AppendMessage(reason, markWarning);
            return result;
        }

        private void FailRun(string reason)
        {
            if (string.IsNullOrEmpty(_summary.FatalReason)) { _summary.FatalReason = reason; }
        }

        // ------------------------------------------------------------------
        // 进度汇报（回调一律吞异常：界面出问题绝不该中断解压）
        // ------------------------------------------------------------------

        private void NotifyStarted(string archivePath, int depth)
        {
            if (_progress == null) { return; }
            try { _progress.ArchiveStarted(archivePath, depth); }
            catch (Exception) { }
        }

        private void NotifyFinished(ArchiveResult result)
        {
            if (_progress == null) { return; }
            try { _progress.ArchiveFinished(result); }
            catch (Exception) { }
        }

        private void NotifyProgress(string archivePath, int percent, string member)
        {
            if (_progress == null) { return; }
            try { _progress.Progress(archivePath, percent, member); }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 小工具
        // ------------------------------------------------------------------

        private static string Join(List<string> values)
        {
            if (values == null || values.Count == 0) { return "（无）"; }
            return string.Join("、", values.ToArray());
        }

        private static string AppendMessage(string message, string addition)
        {
            if (string.IsNullOrEmpty(message)) { return addition; }
            return message + "；" + addition;
        }

        // 7-Zip 自己报「写不下了」时，把整批也判成致命（规格 §7 的磁盘空间不足）。
        private static string DiskFullReason(RunResult run)
        {
            string text = ((run.StdErr == null ? "" : run.StdErr) + " " + (run.StdOut == null ? "" : run.StdOut)).ToLowerInvariant();
            if (text.IndexOf("not enough space", StringComparison.Ordinal) >= 0 ||
                text.IndexOf("disk full", StringComparison.Ordinal) >= 0 ||
                text.IndexOf("空间不足", StringComparison.Ordinal) >= 0)
            {
                return "磁盘空间不足（7-Zip 报告写入失败）：" + Head(Best(run));
            }
            return null;
        }

        private static string Best(RunResult run)
        {
            if (run == null) { return ""; }
            if (!string.IsNullOrEmpty(run.StdErr)) { return run.StdErr; }
            return run.StdOut == null ? "" : run.StdOut;
        }

        private static string Head(string text)
        {
            if (string.IsNullOrEmpty(text)) { return ""; }
            string flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return flat.Length <= 300 ? flat : flat.Substring(0, 300) + "…";
        }
    }
}
