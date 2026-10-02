# 递归解压工具（Rerar）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 `E:\DSH\rerar` 下两个递归解压 .bat 重写为一个 Windows 双击即用、带 GUI、且在任何失败场景下都不会弄丢用户文件的单文件 exe。

**Architecture:** 纯逻辑核心（`src/Core`，无 UI 依赖）承担全部安全不变式与判定逻辑；薄 WinForms 外壳（`src/App`）只做展示与事件编组；所有解压经统一的 `SevenZipRunner` 调外部 7-Zip（Job Object 托管、异步读管道、始终显式传 `-p`）。exe 提供 **GUI 与 CLI 双入口**，CLI 是验收脚本的驱动面。

**Tech Stack:** C# 5（系统自带 `csc.exe`，.NET Framework 4.8）、WinForms、Win32 P/Invoke（Job Object / GetDriveType / Shell）、PowerShell 5.1 构建与验收脚本。**无 NuGet、无 MSBuild、无第三方库。**

**Spec:** `docs/superpowers/specs/2026-10-02-recursive-extractor-gui-design.md`

## Global Constraints

- **7-Zip 版本下限 25.00**（此前版本存在已在野利用的符号链接目录穿越漏洞）；低于下限必须回落到内置副本
- 目标环境 **Windows 10 1903+ / Windows 11 x64**；依赖系统预装的 .NET Framework 4.8
- **C# 5 语法上限**：禁止 `$"..."` 插值、`?.`、表达式体成员、`nameof`
- 构建与验收脚本必须是 **PowerShell 5.1** 兼容（本机 `pwsh` 实为 5.1，`Start-Process -Environment` 等 PS7 特性不可用）
- **产物 exe ≤ 5MB**，冷启动 ≤ 2s
- **绝不以管理员身份运行**（提权会导致 UIPI 静默拦截拖放）
- **绝不向非空目录解压**；**绝不删除任何非本次运行产生的文件**
- 所有面向用户的文本为中文；日志/报告一律 **UTF-8 with BOM**
- 内置 7-Zip 释放到 `%LOCALAPPDATA%\Rerar\bin\<buildid>\`，**不得使用 `%TEMP%`**；每次执行前校验 SHA-256
- 内嵌 7-Zip 时须随包附带 `THIRD-PARTY-NOTICES.txt`（7-Zip LGPL 全文 + BSD 声明 + unRAR 限制段落）

## Review Focus

以下五类输入/失败模式是规格隐含但没有任何任务的测试覆盖、且最可能伤到真实用户的，各自的责任任务已按下方标注加入对应测试：

1. **解压中途目标盘写满** —— 用户期望的是"干净中止并明确告知"，而不是几百条级联 I/O 报错后留下貌似完整的产物。规格只写了启动前预检。→ 责任任务 **Task 10**（低水位中止测试，通过可注入的磁盘空间提供者模拟）。
2. **7z 进度输出出现畸形/截断行**（大文件、多字节字符被切断、只有 CR 没有 LF）—— 用户期望界面不崩、进度不乱跳。规格只说"按 `\r` 切分"。→ 责任任务 **Task 8**（畸形进度行喂给解析器，断言不抛异常且状态不变）。
3. **归档内条目名为空串或超长（>4096 字符）** —— 恶意/损坏归档常见，用户期望跳过该条目并继续，而不是整批失败。规格未提。→ 责任任务 **Task 1**（PathSanitizer 对空名/超长名的断言）。
4. **同目录同时存在 `.zip` 与 `.z01`，但两者各自是完整独立归档**（并非分卷集）—— 用户期望两个都被正常解压，而不是被误判成一个分卷集而报"缺卷"。规格 §6.6 只给了判别原则。→ 责任任务 **Task 6**（"每片都带完整签名 ⇒ 判为独立"的测试）。
5. **拖入的目标路径不存在、是离线占位文件、或含首尾空格** —— 用户期望每个条目给出"无法读取（可能是网络位置或离线文件）"的逐项提示，而不是崩溃或静默跳过。规格未提。→ 责任任务 **Task 12**（不可读目标的逐项报告测试）。

---

## File Structure

| 路径 | 职责 |
|---|---|
| `src/Core/Models.cs` | `ArchiveTask`/`ArchiveResult`/`ArchiveStatus`/`RunOptions` 等纯数据 |
| `src/Core/PathSanitizer.cs` | 输出名消毒、保留设备名规避、路径越界判定、reparse point 断言 |
| `src/Core/Sniffer.cs` | 魔数/HTML/下载中后缀/0 字节/EOCD 头部损坏判定 |
| `src/Core/SevenZipIndex.cs` | `l -slt` 清单解析；`x` 尾部汇总解析 |
| `src/Core/SevenZipRunner.cs` | 进程调用、Job Object、异步读管道、退出码映射、进度行解析 |
| `src/Core/ArchiveGater.cs` | 容器文档（OOXML/APK/JAR/EPUB/ODF）识别与拒绝 |
| `src/Core/VolumeFamily.cs` | 分卷族表、权威成员、缺卷报告、独立性判别 |
| `src/Core/PasswordCandidates.cs` | 候选阶梯、变体生成、同目录线索采集 |
| `src/Core/RecycleBinGuard.cs` | 卷能力/配额前置检查、回收站核实 |
| `src/Core/Journal.cs` | 追加式运行日志与启动恢复 |
| `src/Core/Preflight.cs` | 体积/空间/长路径/密码需求预检 |
| `src/Core/Extractor.cs` | 编排：冻结候选 → 预检 → 逐归档（门控→索引→密码→暂存→校验→提交）→ 删除 |
| `src/Core/Reporter.cs` | 汇总表、CSV/TXT 报告导出 |
| `src/App/Program.cs` | 入口：GUI / CLI 双模式分发 |
| `src/App/MainForm.cs` | WinForms 界面（薄壳：只做展示与编组） |
| `src/App/app.manifest` | PerMonitorV2 + longPathAware + asInvoker |
| `src/Tests/Harness.cs` | 无框架断言测试运行器 |
| `src/Tests/*Tests.cs` | 各 Core 单元的测试 |
| `build/build.ps1` | 调 `csc.exe` 编译（含内嵌 7-Zip、manifest） |
| `build/make-res.ps1` | 生成 Win32 版本资源 `.res` |
| `tests/fixtures.ps1` | 程序化构造 §9.1 fixture 矩阵 |
| `tests/acceptance.ps1` | 驱动 CLI 跑 fixture，输出 PASS/FAIL 表 |

**构建命令形状**（无 MSBuild，靠 csc 一次编译多文件）：

```
csc /nologo /target:winexe /out:dist\Rerar.exe /win32manifest:src\App\app.manifest `
    /resource:<7z.exe>,sz.7z.exe /resource:<7z.dll>,sz.7z.dll `
    src\Core\*.cs src\App\*.cs
csc /nologo /target:exe /out:dist\tests.exe src\Core\*.cs src\Tests\*.cs
```

---

### Task 0: 构建骨架与 CLI 入口

**Files:**
- Create: `build/build.ps1`, `.gitignore`, `src/Core/Models.cs`, `src/App/Program.cs`
- Test: `tests/smoke.ps1`

**Interfaces:**
- Consumes: 无
- Produces: `Rerar.Program.Main(string[] args) -> int`；CLI 契约 `Rerar.exe --cli --selftest` 打印 `version=<x>` 并以 0 退出

- [ ] **Step 1: 初始化仓库并写 .gitignore**

```powershell
git init
# .gitignore: dist/  *.user  .superpowers/
```

- [ ] **Step 2: 写最小 CLI 入口 `src/App/Program.cs`**

签名：`static int Main(string[] args)`。当 `args` 含 `--cli --selftest` 时打印 `version=` + `Assembly.GetName().Version` 并返回 0；`/target:winexe` 下无参数时暂返回 0。

- [ ] **Step 3: 写 `build/build.ps1` 并编译**

调用 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，参数见上方"构建命令形状"（本任务先不加 `/resource:` 与 `/win32manifest:`）。脚本在有编译错误时以非 0 退出。

- [ ] **Step 4: 写冒烟测试 `tests/smoke.ps1`**

断言：`build.ps1` 退出 0；`dist\Rerar.exe` 存在；`& dist\Rerar.exe --cli --selftest` 的 stdout 匹配 `^version=\d+`。

- [ ] **Step 5: 运行冒烟测试**

Run: `powershell -File tests\smoke.ps1`
Expected: `PASS`，退出码 0

- [ ] **Step 6: Commit**

```bash
git add .gitignore build/build.ps1 src/Core/Models.cs src/App/Program.cs tests/smoke.ps1
git commit -m "chore: build skeleton with csc and CLI self-test entry"
```

---

### Task 1: 测试运行器 + TestEnv + PathSanitizer

**Files:**
- Create: `src/Tests/Harness.cs`, `src/Tests/TestEnv.cs`, `src/Tests/PathSanitizerTests.cs`, `src/Core/PathSanitizer.cs`
- Modify: `build/build.ps1`（增加 `dist\tests.exe` 编译目标）

**Interfaces:**
- Consumes: 无
- Produces:
  - `static class H`：`H.Run(string name, Action body)`、`int H.Report()`（打印 PASS/FAIL 表，任一失败返回 1）、断言助手 `AssertEq<T>(T a, T b)` / `AssertTrue(bool)` / `AssertFalse(bool)`。测试文件中以 `H` 直接调用。
  - `static class TestEnv`：**后续每个任务的测试都依赖它**。本任务先提供：
    - `string Tmp`（每用例独立临时目录）、`string OutRoot`
    - `string SevenZip`（本机 7-Zip 绝对路径；缺失则测试整体失败并给出明确提示）
    - `byte[] B(string s)`（转义串 → 字节，供 Sniffer 测试）
    - `string[] F(params string[] names)`
    - `string MakeFile(string name, string content)`、`string TmpFile(string name)`
    - `void Cleanup()`
    - *后续任务按需向 `TestEnv` 追加自己的 fixture 构造器（`NestedZip`、`AesZip`、`IndexDocx`、`SymlinkTar`、`OversizedFile`、`FakeDisk` 等），追加时须在**该任务的 Files 块**里写明。*
  - `string PathSanitizer.Sanitize(string archiveFileName)` → 输出目录名（不含路径）
  - `bool PathSanitizer.IsStrictChild(string rootFullPath, string candidateFullPath)`
  - `string PathSanitizer.Uniquify(string desiredDir)` → 已存在且非空时返回 `名字 (2)` 形式

- [ ] **Step 1: 写 Harness 与失败测试**

```csharp
// PathSanitizerTests.cs
H.Run("Sanitize.ReplacesIllegalChars", delegate {
  AssertEq(PathSanitizer.Sanitize("a:b*c?.zip"), "a_b_c_"); });
H.Run("Sanitize.AvoidsReservedDeviceNames", delegate {
  AssertEq(PathSanitizer.Sanitize("CON.rar"), "_CON"); });
H.Run("Sanitize.StripsTrailingDotAndSpace", delegate {
  AssertEq(PathSanitizer.Sanitize("name .zip"), "name"); });
H.Run("Sanitize.CapsLengthAt120", delegate {
  AssertTrue(PathSanitizer.Sanitize(new string('x',300) + ".zip").Length <= 120); });
H.Run("Sanitize.EmptyNameGetsFallback", delegate {
  AssertTrue(PathSanitizer.Sanitize(".zip").Length > 0); });          // Review Focus #3
H.Run("Sanitize.OverlongEntryNameDoesNotThrow", delegate {
  AssertTrue(PathSanitizer.Sanitize(new string('y',5000) + ".zip").Length > 0); }); // Review Focus #3
H.Run("IsStrictChild.RejectsSibling", delegate {
  AssertFalse(PathSanitizer.IsStrictChild(@"C:\out", @"C:\out2\f")); });
H.Run("IsStrictChild.RejectsTraversal", delegate {
  AssertFalse(PathSanitizer.IsStrictChild(@"C:\out", @"C:\out\..\evil")); });
H.Run("IsStrictChild.AcceptsNested", delegate {
  AssertTrue(PathSanitizer.IsStrictChild(@"C:\out", @"C:\out\a\b.txt")); });
```

- [ ] **Step 2: 运行以确认失败**

Run: `build\build.ps1; dist\tests.exe`
Expected: 编译失败（`PathSanitizer` 不存在）

- [ ] **Step 3: 实现 `PathSanitizer`**

消毒顺序：去压缩包扩展名 → 替换 `: ? * < > | "` 与 `\ /` 为 `_` → 去控制字符 → 若基名（去扩展名前）等于保留设备名（大小写不敏感，`CON/PRN/AUX/NUL/COM1-9/LPT1-9`）则前置 `_` → 去尾点与尾空格 → 截断至 120 → 空则用 `archive`。`IsStrictChild` 用 `Path.GetFullPath` 归一后比较，且要求候选严格长于根且带分隔符（用序数忽略大小写比较）。

- [ ] **Step 4: 运行以确认通过**

Run: `dist\tests.exe`
Expected: 全 `PASS`，退出码 0

- [ ] **Step 5: Commit**

```bash
git add src/Core/PathSanitizer.cs src/Tests/ build/build.ps1
git commit -m "feat: path sanitizer with traversal guard and test harness"
```

---

### Task 2: Sniffer 格式识别

**Files:**
- Create: `src/Core/Sniffer.cs`, `src/Tests/SnifferTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `enum SniffKind { Zip, Rar, Rar5, SevenZip, Gzip, Bzip2, Xz, Tar, Html, InProgressDownload, Empty, DamagedHeader, Unknown }`
  - `SniffKind Sniffer.Classify(byte[] head, long fileLength, string fileName, byte[] tail = null)`
  - `bool Sniffer.HasEocdInTail(byte[] tail)`

- [ ] **Step 1: 写失败测试**

```csharp
H.Run("Sniffer.Zip", delegate { AssertEq(Sniffer.Classify(B("PK\3\4" ), 10, "a.zip"), SniffKind.Zip); });
H.Run("Sniffer.Rar5", delegate { AssertEq(Sniffer.Classify(B("Rar!\x1a\7\1\0"), 10, "a.rar"), SniffKind.Rar5); });
H.Run("Sniffer.SevenZip", delegate { AssertEq(Sniffer.Classify(B("7z\xbc\xaf\x27\x1c"), 10, "a.7z"), SniffKind.SevenZip); });
H.Run("Sniffer.CloakedJpgIsZip", delegate { AssertEq(Sniffer.Classify(B("PK\3\4"), 10, "a.jpg"), SniffKind.Zip); });
H.Run("Sniffer.HtmlIsNotArchive", delegate {
  AssertEq(Sniffer.Classify(B("<!DOCTYPE html>"), 20, "a.zip"), SniffKind.Html); });
H.Run("Sniffer.InProgressSuffix", delegate {
  AssertEq(Sniffer.Classify(B("PK\3\4"), 10, "a.zip.crdownload"), SniffKind.InProgressDownload); });
H.Run("Sniffer.ZeroByte", delegate { AssertEq(Sniffer.Classify(new byte[0], 0, "a.zip"), SniffKind.Empty); });
H.Run("Sniffer.DamagedHeaderFromEocd", delegate {
  AssertEq(Sniffer.Classify(B("\0\0\0\0"), 100, "a.zip", B("garbagegarbagePK\5\6")), SniffKind.DamagedHeader); });
```

- [ ] **Step 2: 运行以确认失败** — Run: `dist\tests.exe`；Expected: 编译失败

- [ ] **Step 3: 实现 `Sniffer`**

按签名表匹配头部；`Html` 检测 `<!DOCTYPE html`/`<html`/`<?xml`（大小写不敏感，跳过前导空白）；`InProgressDownload` 匹配后缀集（`.crdownload .part .partial .!ut .td .xltd .baiduyun.p.downloading`）；长度 0 → `Empty`；其余无签名但 `HasEocdInTail` → `DamagedHeader`；否则 `Unknown`。

- [ ] **Step 4: 运行以确认通过** — Run: `dist\tests.exe`；Expected: 全 PASS

- [ ] **Step 5: Commit**

```bash
git add src/Core/Sniffer.cs src/Tests/SnifferTests.cs
git commit -m "feat: format sniffer with cloaked-extension and damaged-header detection"
```

---

### Task 3: SevenZipRunner（进程、退出码、Job Object、进度解析）

**Files:**
- Create: `src/Core/SevenZipRunner.cs`, `src/Tests/SevenZipRunnerTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `class RunResult { public int ExitCode; public string StdOut; public string StdErr; }`
  - `RunResult SevenZipRunner.Run(string sevenZipPath, string[] args, Action<int,string> onProgress, CancellationToken ct)`
  - `static bool SevenZipRunner.IsSuccess(int exitCode)` → `exitCode == 0 || exitCode == 1`
  - `static void SevenZipRunner.ParseProgressLine(string line, out int percent, out string member)`
  - `SevenZipRunner.TerminateAll()`（杀 Job Object）

- [ ] **Step 1: 写失败测试**

```csharp
H.Run("Runner.ParseProgress.ExtractsPercentAndMember", delegate {
  int p; string m; SevenZipRunner.ParseProgressLine(" 58% 2       - sub\\big2.bin", out p, out m);
  AssertEq(p, 58); AssertEq(m, "sub\\big2.bin"); });
H.Run("Runner.ParseProgress.MalformedLineIsIgnored", delegate {          // Review Focus #2
  int p; string m; SevenZipRunner.ParseProgressLine("   \r  %  - ", out p, out m);
  AssertEq(p, -1); });                                                   // -1 => 无有效进度
H.Run("Runner.ParseProgress.TruncatedMultibyteIsIgnored", delegate {     // Review Focus #2
  int p; string m; SevenZipRunner.ParseProgressLine(" 12% 1 - \u4e2d\u6587", out p, out m);
  AssertEq(p, 12); });
H.Run("Runner.IsSuccess.ZeroAndOneAreSuccess", delegate {
  AssertTrue(SevenZipRunner.IsSuccess(0)); AssertTrue(SevenZipRunner.IsSuccess(1));
  AssertFalse(SevenZipRunner.IsSuccess(2)); AssertFalse(SevenZipRunner.IsSuccess(255)); });
H.Run("Runner.CorruptArchiveExitsTwo", delegate {
  var r = SevenZipRunner.Run(TestEnv.SevenZip, new[]{ "x", TestEnv.CorruptZip, "-o"+TestEnv.Tmp, "-y", "-p" }, null, CancellationToken.None);
  AssertEq(r.ExitCode, 2); });
H.Run("Runner.EncryptedWithoutPasswordDoesNotHang", delegate {           // 回归：原脚本挂起
  var sw = Stopwatch.StartNew();
  var r = SevenZipRunner.Run(TestEnv.SevenZip, new[]{ "t", TestEnv.AesZip, "-p", "-y" }, null, CancellationToken.None);
  AssertTrue(sw.Elapsed.TotalSeconds < 20); AssertTrue(r.ExitCode != 0); });
```

- [ ] **Step 2: 运行以确认失败** — Run: `dist\tests.exe`；Expected: 编译失败

- [ ] **Step 3: 实现 `SevenZipRunner`**

要点（全部是 I 级要求，不可简化）：
- `ProcessStartInfo`：`UseShellExecute=false`、`CreateNoWindow=true`、`RedirectStandardOutput/Error=true`、`StandardOutputEncoding=StandardErrorEncoding=Encoding.UTF8`
- **调用方必须传 `-p<候选>`**；Runner 在 args 不含任何 `-p` 前缀项时抛 `ArgumentException`（防止无人值守挂起 = I5）
- 用 `OutputDataReceived`/`ErrorDataReceived` 异步事件累积；**绝不** `WaitForExit()` 后再 `ReadToEnd()`
- 进度：把 stdout 按 `\r` 与 `\n` 双重切分后再逐段解析
- 创建 Job Object 并 `AssignProcessToJobObject`，设 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`
- `TerminateAll()` 调 `TerminateJobObject`

- [ ] **Step 4: 运行以确认通过** — Run: `dist\tests.exe`；Expected: 全 PASS

- [ ] **Step 5: Commit**

```bash
git add src/Core/SevenZipRunner.cs src/Tests/SevenZipRunnerTests.cs
git commit -m "feat: 7-Zip runner with job object, async pipes and mandatory -p"
```

---

### Task 4: SevenZipIndex（清单解析与完整性基线）

**Files:**
- Create: `src/Core/SevenZipIndex.cs`, `src/Tests/SevenZipIndexTests.cs`

**Interfaces:**
- Consumes: `SevenZipRunner.Run`
- Produces:
  - `class ArchiveIndex { public List<IndexEntry> Entries; public long TotalBytes; public int FileCount; public bool HasEncryptedHeaders; }`
  - `class IndexEntry { public string Path; public long Size; public bool IsDirectory; public bool IsReparsePoint; }`
  - `ArchiveIndex SevenZipIndex.Read(string sevenZipPath, string archivePath, string password)`
  - `static bool SevenZipIndex.TryParseSummary(string stdOut, out int folders, out int files, out long size)`

- [ ] **Step 1: 写失败测试**

```csharp
H.Run("Index.ReadsEntryCountAndBytes", delegate {
  var ix = SevenZipIndex.Read(TestEnv.SevenZip, TestEnv.TwoFileZip, null);
  AssertEq(ix.FileCount, 2); AssertTrue(ix.TotalBytes > 0); });
H.Run("Index.DetectsReparsePointEntry", delegate {                        // 实测 7z 会创建软链
  var ix = SevenZipIndex.Read(TestEnv.SevenZip, TestEnv.SymlinkTar, null);
  AssertTrue(ix.Entries.Exists(delegate(IndexEntry e){ return e.IsReparsePoint; })); });
H.Run("Index.EncryptedHeadersReported", delegate {
  var ix = SevenZipIndex.Read(TestEnv.SevenZip, TestEnv.HeaderEncryptedRar, "wrong");
  AssertTrue(ix.HasEncryptedHeaders); });
H.Run("Index.ParsesExtractSummary", delegate {
  int f; long s; string o = "Folders: 1\nFiles: 3\nSize:       12582922\nCompressed: 12583932";
  AssertTrue(SevenZipIndex.TryParseSummary(o, out f, out s)); AssertEq(f, 3); AssertEq(s, 12582922L); });
```

- [ ] **Step 2: 运行以确认失败** — Run: `dist\tests.exe`；Expected: 编译失败

- [ ] **Step 3: 实现 `SevenZipIndex`**

`Read` 调 `7z l -slt -sccUTF-8 <archive> [-p<pw>]`，按空行分块解析 `Path=`/`Size=`/`Attributes=`/`Folder=`/`Symbolic Link=`；`IsReparsePoint` 由 `Symbolic Link=` 非空或 `Attributes` 含 `L`/reparse 标记判定。任何 `l` 失败且 stderr 含加密特征 → `HasEncryptedHeaders=true`。

- [ ] **Step 4: 运行以确认通过** — Run: `dist\tests.exe`；Expected: 全 PASS

- [ ] **Step 5: Commit**

```bash
git add src/Core/SevenZipIndex.cs src/Tests/SevenZipIndexTests.cs
git commit -m "feat: -slt index parsing as the completeness baseline"
```

---

### Task 5: ArchiveGater 递归门控（I4）

**Files:**
- Create: `src/Core/ArchiveGater.cs`, `src/Tests/ArchiveGaterTests.cs`

**Interfaces:**
- Consumes: `ArchiveIndex`
- Produces:
  - `enum GateVerdict { Allow, ContainerDocument }`
  - `GateVerdict ArchiveGater.Judge(ArchiveIndex index, out string reason)`

- [ ] **Step 1: 写失败测试**

```csharp
H.Run("Gater.RefusesDocx", delegate {
  string why; AssertEq(ArchiveGater.Judge(TestEnv.IndexDocx, out why), GateVerdict.ContainerDocument); });
H.Run("Gater.RefusesApk", delegate {
  string why; AssertEq(ArchiveGater.Judge(TestEnv.IndexApk, out why), GateVerdict.ContainerDocument); });
H.Run("Gater.RefusesJar", delegate {
  string why; AssertEq(ArchiveGater.Judge(TestEnv.IndexJar, out why), GateVerdict.ContainerDocument); });
H.Run("Gater.AllowsPlainZip", delegate {
  string why; AssertEq(ArchiveGater.Judge(TestEnv.IndexPlain, out why), GateVerdict.Allow); });
```

- [ ] **Step 2: 运行以确认失败** — Run: `dist\tests.exe`；Expected: 编译失败

- [ ] **Step 3: 实现 `ArchiveGater`**

按规格 §6.4 的特征表判定：`[Content_Types].xml` 或 `_rels/` 前缀 → OOXML；`AndroidManifest.xml` + `classes.dex` → APK；`META-INF/MANIFEST.MF` + 任一 `.class` → JAR；`mimetype` 内容等于 `application/epub+zip` → EPUB；`application/vnd.oasis.opendocument.` 前缀 → ODF。

- [ ] **Step 4: 运行以确认通过** — Run: `dist\tests.exe`；Expected: 全 PASS

- [ ] **Step 5: Commit**

```bash
git add src/Core/ArchiveGater.cs src/Tests/ArchiveGaterTests.cs
git commit -m "feat: content-identity recursion gate to protect Office/APK/JAR containers"
```

---

### Task 6: VolumeFamily 分卷族

**Files:**
- Create: `src/Core/VolumeFamily.cs`, `src/Tests/VolumeFamilyTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `class VolumeSet { public string AuthoritativeMember; public List<string> Members; public List<string> Missing; }`
  - `bool VolumeFamily.TryResolve(IEnumerable<string> filesInDir, string candidate, out VolumeSet set, bool allMembersHaveFullSignature = false)`

- [ ] **Step 1: 写失败测试**

```csharp
H.Run("Volume.Part1RarIsAuthoritative", delegate {
  VolumeSet s; AssertTrue(VolumeFamily.TryResolve(F("m.part1.rar","m.part2.rar","m.part3.rar"), "m.part1.rar", out s));
  AssertEq(Path.GetFileName(s.AuthoritativeMember), "m.part1.rar"); });
H.Run("Volume.ZipIsAuthoritativeNotZ01", delegate {                       // 规格 §6.6
  VolumeSet s; AssertTrue(VolumeFamily.TryResolve(F("a.zip","a.z01","a.z02"), "a.z01", out s));
  AssertEq(Path.GetFileName(s.AuthoritativeMember), "a.zip"); });
H.Run("Volume.SevenZip001IsAuthoritative", delegate {
  VolumeSet s; AssertTrue(VolumeFamily.TryResolve(F("b.7z.001","b.7z.002"), "b.7z.002", out s));
  AssertEq(Path.GetFileName(s.AuthoritativeMember), "b.7z.001"); });
H.Run("Volume.ReportsExactMissingMember", delegate {                      // 规格 §6.6
  VolumeSet s; VolumeFamily.TryResolve(F("c.7z.001","c.7z.003"), "c.7z.001", out s);
  AssertTrue(s.Missing.Contains("c.7z.002")); });
H.Run("Volume.IndependentArchivesAreNotASet", delegate {                  // Review Focus #4
  VolumeSet s; AssertFalse(VolumeFamily.TryResolve(F("x.zip","x.z01"), "x.zip", out s, allMembersHaveFullSignature: true)); });
```

- [ ] **Step 2: 运行以确认失败** — Run: `dist\tests.exe`；Expected: 编译失败

- [ ] **Step 3: 实现 `VolumeFamily`**

按规格 §6.6 的族表实现；分组条件：终止式 token 正则匹配 + 基名一致（序数忽略大小写）+ 位宽一致 + 编号连续。`allMembersHaveFullSignature` 为真时判为独立文件、返回 false。

- [ ] **Step 4: 运行以确认通过** — Run: `dist\tests.exe`；Expected: 全 PASS

- [ ] **Step 5: Commit**

```bash
git add src/Core/VolumeFamily.cs src/Tests/VolumeFamilyTests.cs
git commit -m "feat: split-volume family table with authoritative member and missing-part report"
```

---

### Task 7: PasswordCandidates

**Files:**
- Create: `src/Core/PasswordCandidates.cs`, `src/Tests/PasswordCandidatesTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `IEnumerable<string> PasswordCandidates.Build(string manual, IEnumerable<string> succeeded, IEnumerable<string> dictLines, IEnumerable<string> clues)`
  - `IEnumerable<string> PasswordCandidates.Variants(string raw)`
  - `IEnumerable<string> PasswordCandidates.CluesFromFileNames(IEnumerable<string> names)`
  - `IEnumerable<string> PasswordCandidates.CluesFromTextFile(string content)`

- [ ] **Step 1: 写失败测试**

```csharp
H.Run("Pwd.VariantsIncludeTrimmedAndFullWidthNormalised", delegate {
  var v = new List<string>(PasswordCandidates.Variants("１２３４５６ "));
  AssertTrue(v.Contains("１２３４５６")); AssertTrue(v.Contains("123456")); });
H.Run("Pwd.VariantsStripZeroWidthAndNbsp", delegate {
  var v = new List<string>(PasswordCandidates.Variants("ab\u200bcd\u00a0"));
  AssertTrue(v.Contains("abcd")); });
H.Run("Pwd.ClueFromFileName", delegate {
  var c = new List<string>(PasswordCandidates.CluesFromFileNames(F("【解压密码：hello123】movie.rar")));
  AssertTrue(c.Contains("hello123")); });
H.Run("Pwd.ClueFromTextFile", delegate {
  var c = new List<string>(PasswordCandidates.CluesFromTextFile("下载说明\n解压密码： pw=abc123 \n"));
  AssertTrue(c.Contains("abc123")); });
H.Run("Pwd.ClueFromUrlHost", delegate {
  var c = new List<string>(PasswordCandidates.CluesFromTextFile("http://www.example.com/x"));
  AssertTrue(c.Contains("www.example.com") || c.Contains("example.com")); });
H.Run("Pwd.OrderManualFirstThenSucceeded", delegate {
  var l = new List<string>(PasswordCandidates.Build("M", F("S1","S2"), F("D1"), F("C1")));
  AssertEq(l[0], "M"); AssertEq(l[1], "S1"); AssertEq(l[2], "S2"); });
```

- [ ] **Step 2: 运行以确认失败** — Run: `dist\tests.exe`；Expected: 编译失败

- [ ] **Step 3: 实现 `PasswordCandidates`**

阶梯顺序：manual → succeeded（按归档记录，全局仅作首选）→ dictLines → clues；每项都展开 `Variants`（原文、去首尾空白、全角转半角、去零宽 `\u200b\u200c\u200d\ufeff` 与 `\u00a0`、剥 `密码：/password=/pwd:` 前缀）。去重且保序。

- [ ] **Step 4: 运行以确认通过** — Run: `dist\tests.exe`；Expected: 全 PASS

- [ ] **Step 5: Commit**

```bash
git add src/Core/PasswordCandidates.cs src/Tests/PasswordCandidatesTests.cs
git commit -m "feat: password candidate ladder with variant expansion and local clue harvesting"
```

---

### Task 8: 进度解析加固 + Reporter

**Files:**
- Modify: `src/Core/SevenZipRunner.cs`（进度行健壮性）
- Create: `src/Core/Reporter.cs`, `src/Tests/ReporterTests.cs`

**Interfaces:**
- Consumes: `ArchiveResult`
- Produces:
  - `void Reporter.WriteCsv(IEnumerable<ArchiveResult> results, string path)`（UTF-8 with BOM）
  - `void Reporter.WriteSummary(IEnumerable<ArchiveResult> results, string path)`
  - `string Reporter.RenderTable(IEnumerable<ArchiveResult> results)`

- [ ] **Step 1: 写失败测试**

```csharp
H.Run("Reporter.CsvIsUtf8WithBom", delegate {
  Reporter.WriteCsv(new[]{ TestEnv.SampleResult("中文名.zip") }, TestEnv.TmpFile("r.csv"));
  var b = File.ReadAllBytes(TestEnv.TmpFile("r.csv"));
  AssertEq(b[0], 0xEF); AssertEq(b[1], 0xBB); AssertEq(b[2], 0xBF); });
H.Run("Reporter.CsvContainsChineseUncorrupted", delegate {                 // 回归：原脚本乱码
  var s = File.ReadAllText(TestEnv.TmpFile("r.csv"), Encoding.UTF8);
  AssertTrue(s.Contains("中文名.zip")); AssertFalse(s.Contains("\uFFFD")); });
H.Run("Reporter.TableHasOneRowPerArchive", delegate {
  AssertEq(Reporter.RenderTable(new[]{ TestEnv.SampleResult("a.zip"), TestEnv.SampleResult("b.zip") }).Split('\n').Length >= 2, true); });
```

- [ ] **Step 2: 运行以确认失败** — Run: `dist\tests.exe`；Expected: 编译失败

- [ ] **Step 3: 实现 `Reporter` 并加固进度解析**

`Reporter` 全部文件写入用 `new UTF8Encoding(true)`。进度解析加固：对空行、纯空白、无 `%`、`%` 后非数字、成员名为空的输入一律返回 `percent = -1` 且不抛异常（Review Focus #2）。

- [ ] **Step 4: 运行以确认通过** — Run: `dist\tests.exe`；Expected: 全 PASS

- [ ] **Step 5: Commit**

```bash
git add src/Core/Reporter.cs src/Core/SevenZipRunner.cs src/Tests/ReporterTests.cs
git commit -m "feat: UTF-8 BOM reporter and hardened progress parsing"
```

---

### Task 9: RecycleBinGuard

**Files:**
- Create: `src/Core/RecycleBinGuard.cs`, `src/Tests/RecycleBinGuardTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `enum DeletePlan { Recycle, Quarantine, Refuse }`
  - `DeletePlan RecycleBinGuard.Plan(string filePath, out string reason)`
  - `bool RecycleBinGuard.Recycle(string filePath)`
  - `bool RecycleBinGuard.VerifyInBin(string fileName)`

- [ ] **Step 1: 写失败测试**

```csharp
H.Run("Guard.RecyclesAndVerifies", delegate {
  var f = TestEnv.MakeFile("guard_me.txt", "x");
  AssertTrue(RecycleBinGuard.Recycle(f));
  AssertFalse(File.Exists(f));
  AssertTrue(RecycleBinGuard.VerifyInBin("guard_me.txt")); });            // 实测可核实
H.Run("Guard.PlansQuarantineWhenTooLargeForQuota", delegate {
  string why; AssertEq(RecycleBinGuard.Plan(TestEnv.OversizedFile, out why), DeletePlan.Quarantine); });
H.Run("Guard.RefusesOnRemovableOrRemote", delegate {
  string why; AssertEq(RecycleBinGuard.Plan(@"Z:\remote\x.zip", out why), DeletePlan.Refuse); });
```

- [ ] **Step 2: 运行以确认失败** — Run: `dist\tests.exe`；Expected: 编译失败

- [ ] **Step 3: 实现 `RecycleBinGuard`**

`Plan`：`GetDriveType` 为 `DRIVE_REMOTE`/`DRIVE_REMOVABLE` → `Refuse`；卷无 `$Recycle.Bin` → `Refuse`；文件体积 > 该卷 `MaxCapacity`（读 `HKCU\...\BitBucket\Volume\{GUID}`，取不到则用保守默认）→ `Quarantine`；否则 `Recycle`。`Recycle` 用 `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., SendToRecycleBin)`。`VerifyInBin` 用 `Shell.Application` 枚举 `ssfBITBUCKET` 按名匹配。

- [ ] **Step 4: 补测规格 §11.1 的回收站未决行为**

1. **UNC / 映射网络盘语义**：若有可用网络位置（已有映射盘或 `net share`，后者需管理员），实测 `SendToRecycleBin` 是抛异常、返回失败，还是**静默永久删除**。无网络位置可用时，**明确记为"未验证"**，并保持 `Plan()` 对 `DRIVE_REMOTE` 一律 `Refuse` 的保守策略。
2. **超配额语义**（可选）：把**临时目录所在卷**的 `MaxCapacity` 临时改为极小值，测试回收一个大于它的文件，**测试后必须恢复原值**。若不做此项，则依赖 `VerifyInBin` 兜底——该兜底本身已能保证不谎称"已移入回收站"。

无论结论正负，都写回 `docs/research/2026-10-02-edge-case-audit.md` 第三节。

- [ ] **Step 5: 运行以确认通过** — Run: `dist\tests.exe`；Expected: 全 PASS

- [ ] **Step 6: Commit**

```bash
git add src/Core/RecycleBinGuard.cs src/Tests/RecycleBinGuardTests.cs docs/research/
git commit -m "feat: recycle-bin guard with quota precheck and post-delete verification"
```

---

### Task 10: Preflight + Extractor 核心编排（I1、I2）

**Files:**
- Create: `src/Core/Preflight.cs`, `src/Core/Extractor.cs`, `src/Tests/ExtractorTests.cs`
- Create: `src/Core/DiskSpaceProvider.cs`（可注入，便于 Review Focus #1 的模拟）

**Interfaces:**
- Consumes: `Sniffer`, `SevenZipIndex`, `ArchiveGater`, `VolumeFamily`, `PasswordCandidates`, `SevenZipRunner`, `PathSanitizer`, `Reporter`
- Produces:
  - `interface IDiskSpaceProvider { long FreeBytes(string path); }`（默认实现走 `DriveInfo`）
  - `class Extractor { public Extractor(RunOptions o, IDiskSpaceProvider disk, IProgressSink progress); public RunSummary Run(IEnumerable<string> targets); }`
  - `ArchiveResult.Status ∈ { Completed, CompletedWithFailures, SkippedNeedsPassword, SkippedContainer, SkippedUnreadable, Failed, NotAttemptedDepthLimit }`

- [ ] **Step 1: 写失败测试**

```csharp
H.Run("Extract.NestedZipFullyExtracted", delegate {
  var s = TestEnv.RunExtract(TestEnv.NestedZip);
  AssertEq(s.Results[0].Status, ArchiveStatus.Completed);
  AssertTrue(File.Exists(TestEnv.OutOf(TestEnv.NestedZip, "inner", "hello.txt"))); });
H.Run("Extract.CorruptKeepsOriginal", delegate {                          // 回归 ①
  var s = TestEnv.RunExtract(TestEnv.CorruptZip);
  AssertEq(s.Results[0].Status, ArchiveStatus.Failed);
  AssertTrue(File.Exists(TestEnv.CorruptZip)); });
H.Run("Extract.AesKeepsOriginalAndReportsNeedsPassword", delegate {        // 回归 ①②
  var s = TestEnv.RunExtract(TestEnv.AesZip);
  AssertEq(s.Results[0].Status, ArchiveStatus.SkippedNeedsPassword);
  AssertTrue(File.Exists(TestEnv.AesZip)); });
H.Run("Extract.DocxIsNotRecursed", delegate {                              // I4
  var s = TestEnv.RunExtract(TestEnv.Docx); AssertEq(s.Results[0].Status, ArchiveStatus.SkippedContainer); });
H.Run("Extract.NonEmptyTargetGetsNumberedName", delegate {                 // I2
  var s = TestEnv.RunExtractWithExistingTarget(TestEnv.PlainZip);
  AssertTrue(s.Results[0].OutputDir.EndsWith("(2)")); });
H.Run("Extract.LeavesNoReparsePoint", delegate {                           // 实测 7z 会建软链
  TestEnv.RunExtract(TestEnv.SymlinkTar);
  AssertFalse(TestEnv.AnyReparsePointUnder(TestEnv.OutRoot)); });
H.Run("Extract.CorrectPasswordSucceeds", delegate {                        // §6.5 候选阶梯
  var s = TestEnv.RunExtractWithPassword(TestEnv.AesZip, "SECRET");
  AssertEq(s.Results[0].Status, ArchiveStatus.Completed);
  AssertTrue(File.Exists(TestEnv.AesZip)); });
H.Run("Extract.WrongPasswordLeavesNoGarbage", delegate {                   // 候选须经 t 验证，不得凭"有文件出现"缓存
  var s = TestEnv.RunExtractWithPassword(TestEnv.AesZip, "WRONG");
  AssertEq(s.Results[0].Status, ArchiveStatus.SkippedNeedsPassword);
  AssertFalse(Directory.Exists(Path.Combine(TestEnv.OutRoot, "aes"))); });
H.Run("Extract.AbortsWhenFreeSpaceBelowThreshold", delegate {              // Review Focus #1
  var s = TestEnv.RunExtractWithFakeDisk(freeBytes: 1024, TestEnv.BigSevenZip);
  AssertEq(s.Results[0].Status, ArchiveStatus.Failed);
  AssertTrue(s.FatalReason.Contains("空间")); });
H.Run("Extract.DepthLimitListsUnprocessed", delegate {
  var s = TestEnv.RunExtractWithDepth(1, TestEnv.DeepNestedZip);
  AssertTrue(s.Results.Exists(delegate(ArchiveResult r){ return r.Status == ArchiveStatus.NotAttemptedDepthLimit; })); });
```

- [ ] **Step 2: 运行以确认失败** — Run: `dist\tests.exe`；Expected: 编译失败

- [ ] **Step 3: 实现 `Preflight` 与 `Extractor`**

严格按规格 §4.2 数据流与 §6.7、§6.11：冻结候选 → 预检 → 逐归档（Sniffer → Gater → Index → 密码 → 暂存 → 解压 → 校验 → 提交）→ 删除（默认关）。校验必须包含：条目数/字节比对、**零 reparse point 断言**、`IsStrictChild` 路径断言。失败时暂存改名 `<名字> (未完成)` 并写哨兵。运行中每 2 秒轮询 `IDiskSpaceProvider`，低于阈值即中止（Review Focus #1）。

- [ ] **Step 4: 补测规格 §11.1 的未决行为，并把结论写回风险登记册**

两条一次性探针，结论无论正负都必须记入 `docs/research/2026-10-02-edge-case-audit.md` 第三节：

1. **长路径不对称性**：构造深层嵌套使某条目路径 >260 字符，观察 7-Zip 是否成功写出（其内部可能用 `\\?\`）而我方 `Directory.GetFiles`/`Path.GetFullPath` 是否随后失败。→ 决定 `Verifier` 是否需要 `\\?\` 前缀枚举。
2. **hardlink 条目**：用 GNU tar 构造 hardlink 条目（`tar -cf x.tar --hard-dereference` 的反面：直接 `ln` 一个文件后归档），确认 7-Zip 落盘为硬链接、普通文件，还是报错。→ 校准 `IndexEntry.IsReparsePoint` 的判定范围。

- [ ] **Step 5: 运行以确认通过** — Run: `dist\tests.exe`；Expected: 全 PASS

- [ ] **Step 6: Commit**

```bash
git add src/Core/Preflight.cs src/Core/Extractor.cs src/Core/DiskSpaceProvider.cs src/Tests/ExtractorTests.cs docs/research/
git commit -m "feat: staged extract-verify-promote pipeline with all five safety invariants"
```

---

### Task 11: Journal 崩溃恢复

**Files:**
- Create: `src/Core/Journal.cs`, `src/Tests/JournalTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `Journal.Open(string runId)` → `%LOCALAPPDATA%\Rerar\journal\<runId>.log`
  - `void Journal.Note(string step, string detail)`（追加 + `Flush(true)`）
  - `IEnumerable<string> Journal.FindIncompleteDestinations()`
  - `IEnumerable<string> Journal.RunsWithoutCleanShutdown()`

- [ ] **Step 1: 写失败测试**

```csharp
H.Run("Journal.FlushesBeforeIrreversibleStep", delegate {
  var j = Journal.Open("t1"); j.Note("about-to-extract", @"C:\x\a.zip");
  AssertTrue(File.ReadAllText(j.Path).Contains("about-to-extract")); });
H.Run("Journal.DetectsUncleanShutdown", delegate {
  Journal.Open("t2").Note("about-to-extract", @"C:\x\a.zip");
  AssertTrue(new List<string>(Journal.RunsWithoutCleanShutdown()).Contains("t2")); });
H.Run("Journal.CleanRunNotReported", delegate {
  var j = Journal.Open("t3"); j.Note("about-to-extract", "x"); j.Note("done", "x");
  AssertFalse(new List<string>(Journal.RunsWithoutCleanShutdown()).Contains("t3")); });
```

- [ ] **Step 2: 运行以确认失败** — Run: `dist\tests.exe`；Expected: 编译失败

- [ ] **Step 3: 实现 `Journal`**

追加式写入，`FileStream` + `Flush(true)`；`done` 记录标记干净结束。**journal 路径必须在本地卷（`%LOCALAPPDATA%`），绝不放目标卷。**

- [ ] **Step 4: 运行以确认通过** — Run: `dist\tests.exe`；Expected: 全 PASS

- [ ] **Step 5: Commit**

```bash
git add src/Core/Journal.cs src/Tests/JournalTests.cs
git commit -m "feat: append-only journal for crash recovery"
```

---

### Task 12: CLI 完整化与逐项不可读报告

**Files:**
- Modify: `src/App/Program.cs`
- Create: `src/Tests/CliTests.cs`

**Interfaces:**
- Consumes: `Extractor`
- Produces: CLI 契约（验收脚本依赖，不得随意更名）
  - `Rerar.exe --cli --target <path> [--target <path>...] [--delete] [--password <pw>] [--dict <file>] [--depth <n>] [--json-out <file>]`
  - 退出码：`0` 全部成功；`1` 有失败/跳过；`2` 致命错误
  - `--json-out` 写出机器可读结果数组（字段：`path,status,layers,files,failed,outputDir,message`）

- [ ] **Step 1: 写失败测试**

```csharp
H.Run("Cli.SelfTestExitsZero", delegate { AssertEq(TestEnv.RunCli("--selftest").ExitCode, 0); });
H.Run("Cli.ExtractsAndWritesJson", delegate {
  var r = TestEnv.RunCli("--target", TestEnv.NestedZip, "--json-out", TestEnv.JsonOut);
  AssertEq(r.ExitCode, 0); AssertTrue(File.ReadAllText(TestEnv.JsonOut).Contains("\"status\":\"Completed\"")); });
H.Run("Cli.NonExistentTargetReportedPerItem", delegate {                   // Review Focus #5
  var r = TestEnv.RunCli("--target", @"C:\nope\missing.zip", "--json-out", TestEnv.JsonOut);
  AssertEq(r.ExitCode, 1);
  AssertTrue(File.ReadAllText(TestEnv.JsonOut).Contains("SkippedUnreadable")); });
H.Run("Cli.PathWithTrailingSpaceHandled", delegate {                       // Review Focus #5
  var r = TestEnv.RunCli("--target", TestEnv.PathWithTrailingSpace, "--json-out", TestEnv.JsonOut);
  AssertTrue(r.ExitCode == 0 || r.ExitCode == 1); });
H.Run("Cli.NeverHangsOnEncrypted", delegate {                              // 回归 ②
  var sw = Stopwatch.StartNew();
  TestEnv.RunCli("--target", TestEnv.AesZip, "--json-out", TestEnv.JsonOut);
  AssertTrue(sw.Elapsed.TotalSeconds < 60); });
```

- [ ] **Step 2: 运行以确认失败** — Run: `dist\tests.exe`；Expected: 编译失败

- [ ] **Step 3: 实现 CLI 分发**

`Program.Main` 见 `--cli` 走 CLI，否则 `Application.Run(new MainForm())`。CLI 必须在**不显示任何窗口**的情况下运行完整 Extractor 流程。不可读目标（不存在/离线/权限拒绝）逐项记为 `SkippedUnreadable` 并给出原因，**不得崩溃、不得静默跳过**（Review Focus #5）。

- [ ] **Step 4: 运行以确认通过** — Run: `dist\tests.exe`；Expected: 全 PASS

- [ ] **Step 5: Commit**

```bash
git add src/App/Program.cs src/Tests/CliTests.cs
git commit -m "feat: headless CLI contract for automated acceptance"
```

---

### Task 13: EngineLocator 与内嵌 7-Zip 兜底

**Files:**
- Create: `src/Core/EngineLocator.cs`, `src/Tests/EngineLocatorTests.cs`
- Modify: `build/build.ps1`（加 `/resource:`）

**Interfaces:**
- Consumes: 无
- Produces:
  - `class EngineInfo { public string Path; public Version Version; public bool IsEmbedded; }`
  - `EngineInfo EngineLocator.Resolve()`（按规格 §6.2 的 5 步顺序 + 功能性自检）
  - `const string EmbeddedSha256`

- [ ] **Step 1: 写失败测试**

```csharp
H.Run("Engine.PrefersLocalWhenAtLeast2500", delegate {
  var e = EngineLocator.Resolve(); AssertFalse(e.IsEmbedded); AssertTrue(e.Version >= new Version(25,0)); });
H.Run("Engine.FallsBackWhenForcedEmbedded", delegate {
  var e = EngineLocator.ResolveLocalDisabled(); AssertTrue(e.IsEmbedded); AssertTrue(File.Exists(e.Path)); });
H.Run("Engine.EmbeddedHashMatches", delegate {
  AssertEq(EngineLocator.Sha256Of(EngineLocator.ResolveLocalDisabled().Path), EngineLocator.EmbeddedSha256); });
H.Run("Engine.EmbeddedBinaryIsFunctional", delegate {                       // 功能性自检，非只比版本号
  var e = EngineLocator.ResolveLocalDisabled();
  AssertEq(SevenZipRunner.Run(e.Path, new[]{ "i" }, null, CancellationToken.None).ExitCode, 0); });
```

- [ ] **Step 2: 运行以确认失败** — Run: `dist\tests.exe`；Expected: 编译失败

- [ ] **Step 3: 实现 `EngineLocator` 并在 build.ps1 内嵌资源**

释放到 `%LOCALAPPDATA%\Rerar\bin\<buildid>\`（**非 `%TEMP%`**），先写临时名再 `File.Move`，每次执行前校验 SHA-256。注册表读取须**显式处理 64 位与 WOW6432Node 两个视图**。

- [ ] **Step 4: 运行以确认通过** — Run: `dist\tests.exe`；Expected: 全 PASS

- [ ] **Step 5: Commit**

```bash
git add src/Core/EngineLocator.cs src/Tests/EngineLocatorTests.cs build/build.ps1
git commit -m "feat: engine locator with version floor, hash check and embedded fallback"
```

---

### Task 14: GUI MainForm（界面形态 A）

**Files:**
- Create: `src/App/MainForm.cs`, `src/App/app.manifest`
- Modify: `build/build.ps1`（加 `/win32manifest:`）

**Interfaces:**
- Consumes: `Extractor`, `Reporter`, `Preflight`, `EngineLocator`
- Produces: 无（叶子组件）。全部业务逻辑仍在 Core，Form 只做展示与 `Invoke` 编组。

- [ ] **Step 1: 写可自动化的界面测试**

```csharp
H.Run("Gui.FormConstructsWithoutEngine", delegate {
  using (var f = new MainForm()) { AssertTrue(f.Controls.Count > 0); } });
H.Run("Gui.DropZoneIsPresentAndNamed", delegate {
  using (var f = new MainForm()) { AssertTrue(f.Controls.Find("dropZone", true).Length == 1); } });
H.Run("Gui.DangerousDeleteIsUncheckedByDefault", delegate {                 // I3
  using (var f = new MainForm()) {
    AssertFalse(((CheckBox)f.Controls.Find("chkDelete", true)[0]).Checked); } });
H.Run("Gui.ManifestDeclaresPerMonitorV2AndAsInvoker", delegate {
  var m = File.ReadAllText(TestEnv.ManifestPath);
  AssertTrue(m.Contains("PerMonitorV2")); AssertTrue(m.Contains("asInvoker")); AssertTrue(m.Contains("longPathAware")); });
```

- [ ] **Step 2: 运行以确认失败** — Run: `dist\tests.exe`；Expected: 编译失败

- [ ] **Step 3: 实现 `MainForm` 与 manifest**

按规格 §6.1 的布局与交互决策实现：拖放区 + 两个选择按钮、四个选项复选框（删除项在独立红色"危险操作"区且默认不勾）、可折叠密码设置、开始按钮（先弹预检摘要）、两阶段进度、一行状态、`[查看详情 ▸]` 可折叠日志与逐项动作。中文文案；字体显式 `Microsoft YaHei UI`；`AutoScaleMode.Dpi`。**不得请求提权。**

- [ ] **Step 4: 运行以确认通过** — Run: `dist\tests.exe`；Expected: 全 PASS

- [ ] **Step 5: 人工目视验收（一次性）**

Run: `dist\Rerar.exe`
检查：空状态写出「原件默认保留」；删除项默认未勾；勾选删除时弹二次确认且不默认聚焦确定；窗口在 150% DPI 下不模糊错位。**结论记入验收报告。**

- [ ] **Step 6: Commit**

```bash
git add src/App/MainForm.cs src/App/app.manifest build/build.ps1
git commit -m "feat: WinForms GUI per layout A with safe-by-default options"
```

---

### Task 15: 验收脚本与 fixture 矩阵

**Files:**
- Create: `tests/fixtures.ps1`, `tests/acceptance.ps1`, `THIRD-PARTY-NOTICES.txt`
- Modify: `build/build.ps1`（产物加通知文件与许可对话框入口）

**Interfaces:**
- Consumes: CLI 契约（Task 12）
- Produces: `tests\acceptance.ps1` 输出 PASS/FAIL 表并以 0/1 退出

- [ ] **Step 1: 写 `tests/fixtures.ps1`**

按规格 §9.1 构造 18 个 fixture：正常嵌套、损坏包、AES zip、ZipCrypto zip、伪装后缀、分卷正常集、分卷缺卷、OOXML、APK、zip 炸弹、Quine、深嵌套长路径、tar 软链穿越（用本会话已验证的 GNU tar + 二进制补丁法）、HTML 假包、0 字节卷、路径冲突、非空目标、大输出量。每个 fixture 输出到 `tests\_fixtures\`。

- [ ] **Step 2: 写 `tests/acceptance.ps1`**

对每个 fixture 调用 CLI，断言规格 §9.2 的 7 条验收标准，特别是：**零数据丢失不变量**（失败场景下原包仍存在）、**零越界写入不变量**（父目录快照比对）。输出 PASS/FAIL 表。

- [ ] **Step 3: 写 `THIRD-PARTY-NOTICES.txt`**

含 7-Zip LGPL 全文、BSD-2/BSD-3 声明、**unRAR 限制段落**、7-zip.org 源码链接。

- [ ] **Step 4: 运行全量验收**

Run: `powershell -File tests\acceptance.ps1`
Expected: 18/18 PASS，退出码 0

- [ ] **Step 5: Commit**

```bash
git add tests/ THIRD-PARTY-NOTICES.txt build/build.ps1
git commit -m "test: full fixture matrix and acceptance run"
```

---

### Task 16: 打包收尾（版本资源、图标、体积与启动检查）

**Files:**
- Create: `build/make-res.ps1`, `assets/rerar.ico`
- Modify: `build/build.ps1`（加 `/win32res:` `/win32icon:`）

**Interfaces:**
- Consumes: 无
- Produces: `dist\Rerar.exe` 带中文版本信息（公司/产品/描述/版权/版本号）

- [ ] **Step 1: 写 `build/make-res.ps1`**

直接生成 Win32 `.res` 二进制（`VERSIONINFO` 资源），无需 `rc.exe`。**若两次尝试后仍无法产出 7-Zip 能识别的合法 `.res`，则放弃版本资源并记录为已知缺口**（AV 信誉属打磨项，不阻塞交付）。

- [ ] **Step 2: 加图标与版本资源到 `build.ps1`**

- [ ] **Step 3: 体积与启动预算测试**

```csharp
H.Run("Package.ExeUnder5MB", delegate {
  AssertTrue(new FileInfo(TestEnv.ExePath).Length <= 5 * 1024 * 1024); });
H.Run("Package.ColdStartUnder2s", delegate {
  var sw = Stopwatch.StartNew(); TestEnv.RunCli("--selftest"); AssertTrue(sw.Elapsed.TotalSeconds < 2); });
H.Run("Package.VersionInfoPresent", delegate {
  AssertTrue(FileVersionInfo.GetVersionInfo(TestEnv.ExePath).ProductName != null); });
```

- [ ] **Step 4: 运行以确认通过** — Run: `dist\tests.exe && powershell -File tests\acceptance.ps1`
Expected: 单元测试与验收全 PASS

- [ ] **Step 5: 干净环境验收（人工，一次性）**

在**临时移除/重命名本机 7-Zip 目录**（或设置环境变量强制走内嵌分支）的条件下运行 `--cli --target <嵌套包>`，确认自动释放内置 7-Zip 并成功解压。**结论记入验收报告。**

- [ ] **Step 6: Commit**

```bash
git add build/ assets/ src/Tests/
git commit -m "chore: version resource, icon and packaging acceptance"
```

---

## 附：依赖顺序

```
Task 0 ─┬─ Task 1 ─┬─ Task 2 ─┐
        │          ├─ Task 3 ─┼─ Task 4 ─┬─ Task 5 ─┐
        │          │          │          ├─ Task 6 ─┤
        │          │          │          └─ Task 7 ─┤
        │          └─ Task 8 ─┘                     ├─ Task 10 ─┬─ Task 11
        │                                            │           ├─ Task 12 ─ Task 15
        └─ Task 9 ───────────────────────────────────┘           └─ Task 13 ─ Task 14 ─ Task 16
```

Task 1、2、6、7、9 相互独立，可并行；Task 10 是汇聚点，必须等 2–9 完成。
