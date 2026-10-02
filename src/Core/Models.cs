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
}
