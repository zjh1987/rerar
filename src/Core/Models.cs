// Rerar 核心数据模型。
//
// 归属（progress.md 的 PRE-TASK-8 裁定）：Task 8 定义 ArchiveResult / ArchiveStatus —— Reporter
// 消费它们，而 Task 8 先于 Task 10 落地（把它们留给 Task 10 会是一个「先消费后定义」的死依赖）。
// Task 10 在本文件追加 ArchiveTask / RunOptions，本任务不定义那两个。
//
// 字段名与拼写是契约：Task 12 的 CLI JSON 键逐字就是 path,status,layers,files,failed,outputDir,message，
// 本类字段与 Reporter 的 CSV 列名都按这个顺序对齐。改名即破坏报告与 CLI 的一致性。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Rerar.Core
{
    // 单个源归档的最终结局。
    //   Skipped* 三种是「按设计不处理 / 处理不了但不计为失败」（需要密码、容器文档、无法读取）；
    //   NotAttemptedDepthLimit 是「递归深度触顶」——规格 §6.1 要求触顶必须**显式列出未处理项**，
    //   绝不静默停止，所以它是一个独立状态，而不是 Failure。
    public enum ArchiveStatus
    {
        Completed,
        CompletedWithFailures,
        SkippedNeedsPassword,
        SkippedContainer,
        SkippedUnreadable,
        Failed,
        NotAttemptedDepthLimit
    }

    // 一个源归档的处理结果：汇总表 / 导出报告 / CLI JSON 的唯一数据源。
    public class ArchiveResult
    {
        public string Path;          // 源归档路径
        public ArchiveStatus Status; // 结局
        public int Layers;           // 该归档达到的嵌套层数
        public int Files;            // 产出的文件数
        public int Failed;           // 归档本身已完成，但内部有 N 个文件失败
        public string OutputDir;     // 解压去向（没有则为 ""）
        public string Message;       // 原因 / 诊断文本（没有则为 ""）
    }

    // 冻结后的一个候选归档（Task 10 的「本轮候选清单」元素；PlanPlanner/JobPlanner 的产物形状）。
    //
    // 为什么要有 Parent 这条反向引用：`ArchiveResult.Layers` 是「该归档**达到**的嵌套层数」，
    // 而层数只有把下一轮的结果汇总回来才知道（规格 §9.1 用例 1 的「报 2 层」）。反向引用让
    // 汇总沿链一次做完，不必维护一张额外的层级表。
    //
    // VolumeMembers 只用于**删除策略**：规格 §9.2 第 5 条 ④ 要求分卷集「所有成员一并处置，
    // 或明确全部不处置」—— Task 10 选后者（理由见 Extractor.DeleteEligibleOriginal）。它也是
    // 「绝不删除本次运行之外的任何文件」这条约束的边界：只有清单里的路径才可能被处置。
    public class ArchiveTask
    {
        public string Path;                 // 候选归档的绝对路径（分卷集时 = 权威成员）
        public int Depth;                   // 递归层数：顶层 = 1
        public ArchiveTask Parent;          // 上一层归档（Layers 汇总用）；顶层为 null
        public List<string> VolumeMembers;  // 分卷集成员（空 = 非分卷集）
    }

    // 一次运行的选项。Task 12 的 CLI 与 Task 14 的界面都通过它配置，默认值一律取「安全」那一侧。
    public class RunOptions
    {
        // 7-Zip 可执行文件绝对路径（必填；Task 13 的 EngineLocator 提供）。
        public string SevenZipPath;

        // 输出根。""（默认）⇒ 原地输出到**归档所在目录**（规格 §6.11 的唯一权威定义）；
        // 非空时为该根目录（测试与 CLI 需要把产物收在一处时用）。嵌套层的输出根不取这个值，
        // 而是固定放在上一层输出目录之内（§6.11 抑制 MAX_PATH 增长）。
        public string OutputRoot;

        // I3：删除默认**关**。打开后也只删「完成且校验通过」的归档，且走回收站/隔离文件夹。
        public bool DeleteOriginals;

        // 规格 §10.1：默认 10 层，可调。触顶必须显式列出未处理项（NotAttemptedDepthLimit）。
        public int MaxDepth = 10;

        // 密码阶梯第 1 层：用户手动输入的密码（可空）。
        public string Password;

        // 密码阶梯第 3 层：导入的字典（逐行；内置常用字典由 Extractor 追加在其后）。
        public List<string> DictLines;

        // 低水位：预检要求「可用空间 ≥ 归档总字节 + 本值」，运行中轮询要求「可用空间 ≥ 本值」。
        // 默认 64 MB —— 小到不影响正常解压，大到能在写满之前就中止（Review Focus #1）。
        public long MinFreeBytes = 64L * 1024 * 1024;

        // 运行中磁盘轮询间隔（秒）。默认 2 秒（brief Step 3）；传 0 表示「尽量快」（1 ms），
        // 供测试把「低水位」变成确定性事件。轮询走独立 Timer，**不依赖 7-Zip 的进度回调**
        //（实测该回调在真实 `x` 上一次都不会触发，见 docs/research 的 V27）。
        public int DiskPollSeconds = 2;

        // 用户取消（两段式取消的入口）。默认不可取消；取消一律走「保留原包 + 暂存标未完成」。
        public CancellationToken Cancellation;

        // 「强制按压缩包尝试」的**逐项**覆盖（规格 §6.1 的逐项动作，对应 §6.3 的「永不静默丢弃」）。
        // 这里列出的路径跳过**格式门控**（Sniffer 判为无法识别 / 网页 / 0 字节 / 下载未完成的那几档），
        // 直接交给 7-Zip 试一次 —— 用户已经明确要求「不管你怎么判，试一次」。
        //
        // 覆盖范围**只有**格式门控。以下全部照旧适用，一条都不放过：
        //   * I1：仍然必须由「索引比对」判成功，绝不因为「用户要求了」就报成功；
        //   * I2：仍然先落同卷空暂存目录、校验通过后改名提交；
        //   * I3：失败/跳过一律不删原包；删除仍然默认关；
        //   * I4：容器文档门控（docx/apk/jar…）不受影响 —— 那是「不递归」的判定，不是格式识别；
        //   * Preflight 的安全上限（压缩炸弹 / 海量条目）不受影响，见 Preflight.CheckExpansion。
        //
        // 形状：路径清单（用 List 而不是 HashSet：Task 12 的 CLI / Task 14 的界面按 JSON 数组传，
        // 顺序与重复都无害）。空（默认）= 不覆盖任何项；匹配一律先归一成绝对路径、大小写不敏感。
        public List<string> ForceTreatAsArchive = new List<string>();

        // 该路径是否被用户要求「强制按压缩包尝试」。归一失败就退回原样字符串比较 ——
        // 绝不能因为路径写法不同（相对/绝对、大小写）而静默丢掉用户的请求。
        public bool IsForcedTreatAsArchive(string path)
        {
            if (string.IsNullOrEmpty(path) || ForceTreatAsArchive == null) { return false; }

            for (int i = 0; i < ForceTreatAsArchive.Count; i++)
            {
                string raw = ForceTreatAsArchive[i];
                if (string.IsNullOrEmpty(raw)) { continue; }
                if (string.Equals(raw, path, StringComparison.OrdinalIgnoreCase)) { return true; }

                string a;
                string b;
                try { a = Path.GetFullPath(raw); }
                catch (Exception) { a = raw; }
                try { b = Path.GetFullPath(path); }
                catch (Exception) { b = path; }

                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }
    }
}
