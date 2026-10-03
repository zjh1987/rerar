// Rerar 解压编排（Task 10；规格 §4.2 数据流、§6.5、§6.6、§6.7、§6.11 与五条不变式）。
//
// 本类是整条流水线的汇聚点，也是安全不变式唯一真正落地的地方。管线顺序（Task 5 的裁定：
// 规格 §4.2 把 Gater 排在 Indexer 之前是**不可能的**，门控需要清单）：
//
//   Sniffer → 分卷族 → 索引(Read) → Gater → 预检(空间/长路径/重名) → 密码 → 暂存 → 解压 →
//   校验 → 提交 → （仅当开启删除且「完成且校验通过」）回收站/隔离
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

        // 因致命原因中止而**没有处理**的候选（原样路径）。与规格 §10.1 对深度上限的要求同理：
        // 绝不静默停止 —— 未处理的项必须能被列出来。它们不是 Failed（没试过，谈不上失败）。
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

        // 低水位标志由**轮询线程**写、由解压线程读，故用 volatile（见 RunExtraction 的说明）。
        // 三个标志一起决定判词说的是哪一段（开始前 / 解压中 / 解压后复核），绝不把「其实解压完了」
        // 说成「已在解压中途中止」。
        private volatile bool _lowWaterHit;
        private volatile bool _extractionRunning;
        private volatile bool _lowWaterDuringExtraction;
        private bool _lowWaterAfterRun;
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
                        summary.NotAttempted.Add(task.Path);
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
                    foreach (ArchiveTask task in next) { summary.NotAttempted.Add(task.Path); }
                    break;
                }
                if (next.Count == 0) { break; }

                depth++;
                round = next;
            }

            return summary;
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
            SniffKind kind = Sniff(sourcePath);
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
            if (kind == SniffKind.Unknown)
            {
                return Reject(result, ArchiveStatus.SkippedUnreadable, "无法识别的格式：已跳过（界面上可对该项选择「强制按压缩包尝试」），原文件保留");
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
            ArchiveIndex index = SevenZipIndex.Read(_options.SevenZipPath, archiveArg, null);
            string password = null;
            if (Preflight.NeedsPassword(index))
            {
                // 头部加密：连清单都读不出来。候选用 `l` 验证（对头部加密的 7z，只有正确密码的
                // `l -p…` 才成功；比 `t` 便宜得多），验证到了再用它把清单读出来。
                password = FindPassword(archiveArg, true, null);
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

            // 长路径按**实际写入的那个目录名**判定：暂存目录名是随机的 .rerar-stage-xxx，
            // 而最终目标名可能很长（被消毒后的归档名最长 120 字符）—— 两者都可能成为最长的那一个。
            string writeRoot = stagingBase.Length >= target.Length ? stagingBase : target;
            string overlong = Preflight.FindOverlongEntry(writeRoot, index);
            if (overlong != null)
            {
                return Reject(result, ArchiveStatus.SkippedUnreadable,
                    "路径过长（预检拒绝，规格 §10.2）：条目「" + overlong + "」加上目标目录会超过 " +
                    Preflight.MaxPathLength + " 字符；原包保留");
            }
            string conflict = Preflight.FindConflictingEntries(index);
            if (conflict != null)
            {
                return Reject(result, ArchiveStatus.SkippedUnreadable,
                    "条目名冲突（大小写不敏感的重名，Windows 上是同一个路径，例如 Readme.txt 与 README.TXT）：" +
                    "拒绝写入以免静默覆盖（冲突项「" + conflict + "」）；原包保留");
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
            if (password == null)
            {
                string listing = RunListingProbe(archiveArg);
                if (Preflight.MentionsEncryption(listing))
                {
                    List<string> clues = HarvestClues(sourcePath, listing);
                    password = FindPassword(archiveArg, false, clues);
                    if (password == null) { return PasswordUnavailable(result); }
                    // 只记「哪个候选 + 来源类别」，绝不把密码值写进结果/报告（凭据卫生，§6.5）。
                    result.Message = AppendMessage(result.Message,
                        "已用密码候选 " + _lastMatchedCandidate + " 通过 `7z t` 验证");
                }
            }

            // --- 7) 低水位轮询 + 暂存 → 解压 → 校验 → 提交 → 删除 ---
            // 轮询从「预检通过」就启动，一直活到本归档处理结束：它覆盖密码阶梯、暂存与解压全过程，
            // 于是「预检时够、真正写的时候不够」这个窗口也被盯住了。轮询线程与解压线程并发，
            // 低水位时的动作是 `linked.Cancel()` —— Runner 走 Job Object 把子进程打断（退出码 1223）。
            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_options.Cancellation);
            _lowWaterHit = false;
            _lowWaterAfterRun = false;
            _lowWaterDuringExtraction = false;
            Timer poller = StartDiskPoller(destRoot, linked);
            RunResult extraction = null;
            try
            {
                PollDisk(destRoot, linked);     // 同步复检一次：不让「刚够」变成「边写边满」
                if (_lowWaterHit)
                {
                    return LowWaterAbort(result, stagingBase, target, sourcePath, "已中止本归档（尚未开始解压）");
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

                extraction = RunExtraction(archiveArg, sourcePath, stagingBase, password, destRoot, linked);

                // --- 8) 先分类「被打断」：1223 是被 Job Object 打断，不是解压失败，更不是成功 ---
                // 兜底复核：即便轮询线程错过了窗口（解压太快 / 定时器还没到点），也绝不把产物提交到
                // 已经低于低水位的卷上。它与轮询走**同一条**「不提交」路径，只是触发时机不同。
                if (!_lowWaterHit && _disk.FreeBytes(destRoot) < _options.MinFreeBytes)
                {
                    _lowWaterHit = true;
                    _lowWaterAfterRun = true;
                }

                if (_lowWaterHit || linked.IsCancellationRequested || extraction.ExitCode == CancelledExitCode)
                {
                    if (_lowWaterHit)
                    {
                        // Review Focus #1：低水位 —— 干净中止，绝不提交半成品、绝不删原包。
                        // 判词区分「解压中途中止」与「解压完成后复核发现」：两者是同一条安全路径，
                        // 但读者（用户与复核者）需要知道是哪一种。
                        return LowWaterAbort(result, stagingBase, target, sourcePath, _lowWaterAfterRun
                            ? "解压完成后的复核发现空间不足，未提交"
                            : (_lowWaterDuringExtraction ? "已在解压中途中止" : "已在解压开始前中止（未提交）"));
                    }

                    _summary.Cancelled = true;
                    MarkIncomplete(stagingBase, target, sourcePath, "用户取消");
                    result.Status = ArchiveStatus.Failed;
                    result.Message = "已取消：本归档未完成（暂存目录已标记为未完成）；原包保留";
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
                MarkIncomplete(stagingBase, target, sourcePath, "校验期间有 " + scan.Unreadable.Count + " 个条目读不动");
                result.Status = ArchiveStatus.Failed;
                result.Message = "无法完成完整性校验：暂存树里有 " + scan.Unreadable.Count +
                    " 个条目读不动（例如路径过长或被占用）：未提交，原包保留";
                return result;
            }

            if (extraction.ExitCode >= 2)
            {
                string diagnostics = Head(Best(extraction));
                string fatal = DiskFullReason(extraction);
                if (fatal != null) { FailRun(fatal); }
                MarkIncomplete(stagingBase, target, sourcePath, "7-Zip 退出码 " + extraction.ExitCode);
                result.Status = ArchiveStatus.Failed;
                result.Message = "解压失败（7-Zip 退出码 " + extraction.ExitCode + "）：" + diagnostics + "；原包保留";
                return result;
            }

            int expectedFiles;
            long expectedBytes;
            bool countsFiles;
            if (!Preflight.TryGetBaseline(index, extraction.StdOut, out expectedFiles, out expectedBytes, out countsFiles))
            {
                // 绝不用退出码顶上来当成功证据（I1）。没有基线 ⇒ 不提交、原包保留。
                MarkIncomplete(stagingBase, target, sourcePath, "没有可用的完整性基线");
                result.Status = ArchiveStatus.Failed;
                result.Message = "无法校验完整性：没有可用的索引基线（绝不以退出码判定成功）；未提交，原包保留";
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
                MarkIncomplete(stagingBase, target, sourcePath, "提交失败：" + ex.Message);
                result.Status = ArchiveStatus.Failed;
                result.Message = AppendMessage(result.Message, "提交失败（同卷改名 " + ex.GetType().Name + "：" + ex.Message + "）：原包保留");
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
            if (_options.DeleteOriginals)
            {
                if (result.Status == ArchiveStatus.Completed)
                {
                    DeleteEligibleOriginal(task, result);
                }
                else
                {
                    result.Message = AppendMessage(result.Message, "未删除原包（只有「完成且校验通过」的归档才允许删除）");
                }
            }

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
        private string RunListingProbe(string archiveArg)
        {
            string[] args = new string[] { "l", "-slt", archiveArg, "-p", "-y" };
            RunResult listing = SevenZipRunner.Run(_options.SevenZipPath, args, null, CancellationToken.None);
            return (listing.StdOut == null ? "" : listing.StdOut) + "\n" + (listing.StdErr == null ? "" : listing.StdErr);
        }

        // 密码阶梯（§6.5）：手动 → 本次已验证成功 → 导入字典 → 内置字典 → 同目录线索。
        // 逐个用 `t`（或头部加密时的 `l`）验证 —— **绝不用 x**：错密码的 x 会写出大量垃圾。
        private string FindPassword(string archiveArg, bool useListing, IEnumerable<string> clues)
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

                if (!VerifyPassword(archiveArg, candidate, useListing)) { continue; }

                // 只在**验证通过之后**才缓存（I5：ZipCrypto 有 1/256 的头校验假阳性，
                // 凭「有文件出现」缓存会把一个错密码推成下一个归档的首选）。
                string source = DescribeCandidateSource(candidate, dict);
                if (!_verifiedPasswords.Contains(candidate)) { _verifiedPasswords.Add(candidate); }
                _lastMatchedCandidate = "#" + ordinal + "（" + source + "）";
                return candidate;
            }
            return null;
        }

        // 只用 `t`（测试）或 `l`（清单）验证候选，绝不用 `x`。两者都必须带 -p（I5）。
        private bool VerifyPassword(string archiveArg, string candidate, bool useListing)
        {
            string[] args = useListing
                ? new string[] { "l", "-slt", archiveArg, "-p" + candidate, "-y" }
                : new string[] { "t", archiveArg, "-p" + candidate, "-y" };

            return SevenZipRunner.IsSuccess(SevenZipRunner.Run(_options.SevenZipPath, args, null, CancellationToken.None).ExitCode);
        }

        // 命中候选来自阶梯的哪一层（只报**类别**，不报值 —— 密码绝不进结果/报告）。
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
        // ------------------------------------------------------------------

        private void DeleteEligibleOriginal(ArchiveTask task, ArchiveResult result)
        {
            string origin = task.Path;

            // 规格 §9.2 第 5 条 ④：分卷集要么所有成员一并处置，要么**明确全部不处置**。这里选后者：
            // 只删用户点名的那个成员会留下 .z02/.part2.rar 孤儿（原脚本的老毛病），而推断出来的
            // 兄弟卷并不在本次运行的候选清单里 —— 删它们就越过了「绝不删除本次运行之外的任何文件」。
            if (task.VolumeMembers != null && task.VolumeMembers.Count > 1)
            {
                result.Message = AppendMessage(result.Message, "分卷集（" + task.VolumeMembers.Count +
                    " 个成员）：按「全部不处置」处理，原包全部保留（避免只删一个、留下孤儿分卷）");
                return;
            }

            string why;
            DeletePlan plan = RecycleBinGuard.Plan(origin, out why);

            if (plan == DeletePlan.Refuse)
            {
                result.Message = AppendMessage(result.Message, "未删除原包：" + why);
                return;
            }

            if (plan == DeletePlan.Quarantine)
            {
                QuarantineOriginal(origin, result, why);
                return;
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
                return;
            }

            bool verified = called && RecycleBinGuard.VerifyInBin(fileName, before);
            result.Message = AppendMessage(result.Message, verified
                ? "原包已移入回收站（已核实：回收站里新增了该条目）"
                : "原包已被删除，但回收站未能核实（很可能已被永久删除）");
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

        // 规格 §6.7 的失败路径：暂存目录改名 "<目标名> (未完成)" + 写哨兵。
        // 幂等、尽力而为、**绝不删除**：改名或写哨兵失败（磁盘满时很常见）也绝不抛给调用方。
        private static void MarkIncomplete(string staging, string target, string sourcePath, string reason)
        {
            try
            {
                if (!Directory.Exists(staging)) { return; }

                string destination = target + IncompleteSuffix;
                for (int n = 2; n < MaxNameAttempts && (File.Exists(destination) || Directory.Exists(destination)); n++)
                {
                    destination = target + IncompleteSuffix + " (" + n + ")";
                }

                try { Directory.Move(staging, destination); }
                catch (Exception) { destination = staging; }     // 改名失败就留在原暂存名下，绝不删

                string text = "Rerar 未完成的解压\r\n" +
                    "源归档：" + sourcePath + "\r\n" +
                    "原因：" + reason + "\r\n" +
                    "本目录是解压中途留下的产物，**不是**完整结果；源归档未被删除。\r\n";
                File.WriteAllText(Path.Combine(destination, SentinelName), text, new UTF8Encoding(true));
            }
            catch (Exception)
            {
            }
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

        private static ArchiveResult Reject(ArchiveResult result, ArchiveStatus status, string message)
        {
            result.Status = status;
            result.Message = message;
            return result;
        }

        // 密码阶梯走完仍没有可用密码（或中途被取消/盘已满）。取消绝不是「需要密码」，盘满也不是。
        private ArchiveResult PasswordUnavailable(ArchiveResult result)
        {
            if (_lowWaterHit)
            {
                return LowWaterAbort(result, null, null, null, "已中止本归档（尚未开始解压）");
            }
            if (_summary.Cancelled || _options.Cancellation.IsCancellationRequested)
            {
                result.Status = ArchiveStatus.Failed;
                result.Message = "已取消：密码尚未验证完成；原包保留";
                return result;
            }

            result.Status = ArchiveStatus.SkippedNeedsPassword;
            result.Message = "需要密码：" + _lastLadderTries +
                " 个候选都未通过验证（候选一律用 `7z t` 或 `l` 验证，绝不用 x 试密码）；原包保留";
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
        private ArchiveResult LowWaterAbort(ArchiveResult result, string stagingBase, string target, string sourcePath, string phase)
        {
            string reason = LowWaterReason(phase);
            FailRun(reason);
            if (!string.IsNullOrEmpty(stagingBase)) { MarkIncomplete(stagingBase, target, sourcePath, reason); }
            result.Status = ArchiveStatus.Failed;
            result.Message = reason;
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
