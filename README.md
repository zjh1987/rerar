# Rerar

**Rerar** 是一个 Windows 单文件递归解压工具：把多层嵌套的压缩包（网盘 / 资源站资源包常见形状）一次性解开，同时守住「不弄丢你的文件、不往目标之外写一个字节」两条底线。

- 平台：Windows 10 1903+ / Windows 11（x64），无需安装 .NET（系统预装即可）
- 引擎：[7-Zip](https://www.7-zip.org)（优先本机已装的，找不到就用内嵌便携版兜底 —— 无 7-Zip 的干净机器上双击即用）
- 体积：单个 exe，约 3 MB（内嵌 7z.exe + 7z.dll，未修改的官方二进制）
- 界面语言：中文

## 它解决什么问题

手动处理嵌套压缩包通常意味着：一层层右键解压、遇到改过名的后缀逐个试、分卷缺一片不知道、带密码的包翻帖子找密码、解压失败还担心原包被删。Rerar 把这条流水线自动化，并把每一步的安全边界写成了硬性不变量（见下文）。

## 功能

- **递归解压**：自动发现并解开嵌套压缩包，深度可调（默认有上限，触顶会显式列出，绝不静默丢弃）
- **伪装后缀识别**：按魔数而非扩展名判断真实格式（`.dat` / `.exe` 里藏 zip 也能认出来）
- **容器文档门控**：OOXML（docx/xlsx）、APK、OLE2 复合文档等「长得像压缩包的文档」只登记、不递归、绝不删除
- **分卷集识别**：`.z01/.002`、`.001/.002`、`.part1.rar` 等分卷族自动分组、定位权威成员、缺卷明确报告
- **密码支持**：手动指定密码、密码字典（`--dict`），以及从文件名 / 说明文本中自动提取密码候选（如 `解压密码：abc123`）并按阶梯逐个验证
- **安全删除**：默认不删原包；开启删除后仅在「解压成功且完整性校验通过」时删，走回收站，删前查卷配额、删后核实，超配额绝不硬删
- **失败可诊断**：崩溃恢复日志、逐归档 JSON 结果、界面失败清单可展开
- **GUI + CLI 双入口**：双击即用的图形界面（拖放 / 预检摘要 / 两阶段进度 / 可折叠日志），以及功能等价的命令行（可无人值守）

## 安全设计（五条不变量）

1. **成功判定基于索引比对**，而非 7-Zip 退出码 —— 解压前记录索引（条目数 / 总字节），解压后核对磁盘实际结果；`Everything is Ok` 不等于每条都写出来了。
2. **零数据丢失** —— 任何失败场景下原包仍然存在：先解到同卷暂存目录，完整性校验通过后原子改名提交，失败则保留原包并把暂存目录标记为「未完成」。
3. **零越界写入** —— 解压结果的每个字节都落在目标根之内：拒绝 / 展平 reparse point（符号链接目录穿越）、归档类型白名单门控、路径清洗。
4. **引擎来源可控** —— 本机 7-Zip 必须不低于 25.00（更低版本存在已在野利用的符号链接穿越漏洞）；内嵌副本每次使用前重新校验 SHA-256，被篡改 / 截断的副本一律拒绝。
5. **进程卫生** —— 子进程 7z 挂在 Win32 Job Object 上，主进程崩溃也不留孤儿；不写注册表、不装服务、不提权。

## 使用

### 图形界面

双击 `Rerar.exe`（无参数即起界面），把文件或文件夹拖进窗口，确认预检摘要后开始。日志默认折叠，失败清单可展开。

### 命令行

```
Rerar.exe --cli --target <路径> [--target <路径>...] [--delete] [--password <密码>]
          [--dict <字典文件>] [--depth <层数>] [--json-out <报告文件>]
```

- `--target` 可重复，指定多个解压目标
- `--delete` 解压成功且校验通过后把原包送入回收站（默认保留）
- `--json-out` 输出机器可读结果（UTF-8 带 BOM）：每个归档恰好一个对象，键为 `path, status, layers, files, failed, outputDir, message, originalDisposition`
- 退出码：`0` 全部成功；`1` 有失败或跳过（含用户取消）；`2` 致命错误
- `--selftest`：打印 `version=<n>` 并退出 0（健康自检）

示例：

```powershell
.\Rerar.exe --cli --target D:\downloads\bundle.zip --dict passwords.txt --json-out result.json
```

### 环境变量（可选，不设置则行为完全不变）

| 变量 | 作用 |
|---|---|
| `RERAR_ENGINE_ROOT` | 改写内嵌 7-Zip 的释放根（默认 `%LOCALAPPDATA%\Rerar\bin`） |
| `RERAR_ENGINE_LOCAL` | 设为 `off` / `0` / `false` 时跳过全部本机 7-Zip 探测，强制使用内嵌便携版 |
| `RERAR_JOURNAL_ROOT` | 改写崩溃恢复日志根（默认 `%LOCALAPPDATA%\Rerar\journal`） |

## 从源码构建

### 前提

- Windows 10 1903+ / Windows 11（x64）
- PowerShell 5.1（系统自带）
- .NET Framework 4.x 的 `csc.exe`（`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，系统自带）
- 一份 **7-Zip ≥ 25.00**（`7z.exe` 与 `7z.dll` 同目录）作为内嵌载荷来源：装在默认位置即可被找到；没有安装时用环境变量 `RERAR_7Z_DIR` 指向一个解压出来的 7-Zip 目录

不依赖 MSBuild、Visual Studio、NuGet 或任何第三方库 —— 构建只调系统自带的 csc。

### 构建

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build\build.ps1
```

产物：`dist\Rerar.exe`（含内嵌引擎、版本资源、图标）与 `dist\tests.exe`（单元测试运行器）。构建会自动完成内嵌载荷的版本下限检查与哈希固化，并校验 `THIRD-PARTY-NOTICES.txt` 随包就位。

### 测试

```powershell
# 1) 构造 21 个验收夹具（真实 7-Zip / .NET zip 库生成，自带形状自检）
powershell -NoProfile -File tests\fixtures.ps1

# 2) 冒烟 + 验收：驱动真正随包发出的 dist\Rerar.exe 跑全部验收标准
powershell -NoProfile -File tests\smoke.ps1
powershell -NoProfile -File tests\acceptance.ps1

# 3) 单元测试（无框架运行器，反射发现全部用例）
.\dist\tests.exe
```

验收脚本自带越界写入守卫（对五个根做前后快照比对），并支持负向对照：设 `RERAR_ACCEPTANCE_NEGATIVE_CONTROL=1` 再跑一次，守卫应当报错 —— 用来证明守卫本身不是空转。

## 项目结构

```
├─ assets/            应用图标
├─ build/             构建脚本（csc 直编 + Win32 资源合成）
├─ src/
│  ├─ App/            程序入口与 GUI（MainForm）
│  ├─ Core/           解压流水线（嗅探、门控、索引、密码、分卷、暂存、校验、提交、安全删除…）
│  └─ Tests/          无框架单元测试
├─ tests/             夹具构造 / 冒烟 / 验收脚本
├─ THIRD-PARTY-NOTICES.txt   7-Zip（LGPL + BSD + unRAR 限制）许可声明，必须随 exe 分发
└─ build.ps1 入口说明见上文
```

## 许可证

- Rerar 自身代码以 [MIT](LICENSE) 许可发布。
- 本程序内嵌**未经修改**的官方 7-Zip（`7z.exe` / `7z.dll`），构成 7-Zip 的二进制再分发，完整许可信息见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)（LGPL + BSD 3-clause + BSD 2-clause + unRAR 限制）。
- Rerar 只使用 7-Zip 的解压能力，不含任何压缩功能，不派生、修改、再分发 unRAR 源码；unRAR 许可限制（不得用于重新实现 RAR 压缩算法）对本程序的使用方式是满足的。

## 致谢

- [7-Zip](https://www.7-zip.org)（Igor Pavlov）—— 解压引擎
