# Rerar 验收脚本（Task 15）之二：驱动**真正随包发出的** dist\Rerar.exe 跑规格 §9.2 的 7 条验收标准。
#
# 用法：powershell -NoProfile -File tests\acceptance.ps1
#   退出 0 = 全部行 PASS；1 = 有任何一行 FAIL（或某个 fixture 构造不出来，那也记 FAIL 并写明原因）。
#
# 前提：先跑 tests\fixtures.ps1（本脚本不替你造 fixture；缺了就把对应的行记成 FAIL，绝不静默跳过）。
#
# 【为什么只认 --json-out 与退出码】人读的 TXT 汇总那一行在 Task 12 变过形状，它不是稳定契约；
# 机器可读的 JSON 数组（键：path/status/layers/files/failed/outputDir/message，UTF-8 带 BOM）与
# 退出码 0/1/2 才跨任务稳定。本脚本一行都不去整行匹配人读文本 —— 只在必须证明「用户看得见中文」
# 的地方检查子串。
#
# 【安全边界（本脚本自己必须守住的）】
#   * `--delete` 只作用在**本脚本自己从 fixture 复制出来的副本**上（tests\_fixtures\_run\ 下）；
#     绝不指向任何预先存在的用户文件；
#   * 崩溃恢复日志根（RERAR_JOURNAL_ROOT）与内嵌引擎释放根（RERAR_ENGINE_ROOT）一律指到
#     %TEMP%\rerar-acceptance-<随机>\ 下 —— 这次运行绝不碰 %LOCALAPPDATA%\Rerar 里的真实应用数据
#     （而且这一点是被断言的：真实根的存在性与内容前后必须一致，见 A03）；
#   * 每一次 CLI 调用都有**有界超时**：超时即 taskkill 整棵树并记 FAIL，绝不让脚本自己挂住；
#   * 不提权、不改注册表、不改回收站设置。唯一会碰回收站的动作是「删除开关」那两行 ——
#     删的是刚复制出来的 fixture 副本（回收站核实失败时它会被永久删除，这是产品行为，不是本脚本的）。
#
# 【零越界写入不变量怎么做的】每行调用 CLI 前后，对**四个根**做递归快照比对（见 Get-GuardSnapshot）：
#   1) 该行的目录树，但**排除** work\（work 就是这次运行的输出根，工具本来就该往里写）；
#   2) 仓库根的直接子项（列表 + 类型）—— 抓「在仓库根凭空造了一个目录/文件」；
#   3) tests\ 整棵树，但排除 _fixtures\ —— 抓「写进了测试脚本目录」；
#   4) tests\_fixtures\ 整棵树，但排除本次运行的 _run\ —— 抓「动了其它 fixture / 动了夹具矩阵本身」；
#   5) dist\ 整棵树 —— 抓「往产物目录里写东西」。
# 另外单独断言 %LOCALAPPDATA%\Rerar 的存在性全程不变。
# 每一行比对出来的差异都进 A03 那一行的明细；有任何差异就是 FAIL。
#
# PS 5.1（本机就是 5.1，没有任何 PS7 专有参数）；只用 .NET 自带的 BCL，无 NuGet、无第三方库。

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:FixDir = Join-Path $PSScriptRoot '_fixtures'
$script:RunDir = Join-Path $script:FixDir '_run'
$script:Exe = Join-Path $script:RepoRoot 'dist\Rerar.exe'
$script:TestsExe = Join-Path $script:RepoRoot 'dist\tests.exe'
$script:Scratch = Join-Path $env:TEMP ('rerar-acceptance-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$script:ReportDir = Join-Path $script:Scratch 'reports'
$script:JournalRoot = Join-Path $script:Scratch 'journal'
$script:EngineRoot = Join-Path $script:Scratch 'engine'
$script:Results = New-Object System.Collections.Generic.List[object]
$script:EscapeDiffs = New-Object System.Collections.Generic.List[string]
$script:DataLossChecked = 0
$script:DataLossViolations = New-Object System.Collections.Generic.List[string]
$script:GuardComparisons = 0
$script:LocalAppDataRerar = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Rerar'
$script:LocalAppDataBefore = Test-Path -LiteralPath $script:LocalAppDataRerar

# ==================================================================
# 通用助手
# ==================================================================

function Note-Host([string]$text) { Write-Host $text }

function Remove-Dir([string]$path) {
    if (Test-Path -LiteralPath $path) {
        # PS 5.1 的 Remove-Item 遇到目录软链会抛 NullReferenceException，所以先手工摘掉软链。
        Get-ChildItem -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Attributes -band [System.IO.FileAttributes]::ReparsePoint } |
            Sort-Object { $_.FullName.Length } -Descending |
            ForEach-Object {
                try {
                    if ($_.PSIsContainer) { [System.IO.Directory]::Delete($_.FullName, $false) }
                    else { [System.IO.File]::Delete($_.FullName) }
                } catch { }
            }
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Ensure-Dir([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { New-Item -ItemType Directory -Force -Path $path | Out-Null }
}

function Write-TextFile([string]$path, [string]$text) {
    Ensure-Dir (Split-Path -Parent $path)
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

# 原生命令：**只看退出码**，两流合并成文本。$ErrorActionPreference 临时降级 —— PS 5.1 下
# `native 2>&1` 会把 stderr 的每一行做成 ErrorRecord，而 'Stop' 会把它变成终止错误。
function Invoke-Native([string]$exe, [string[]]$arguments, [string]$workingDirectory) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    $out = ''
    $code = -1
    try {
        if ($workingDirectory) { Push-Location $workingDirectory }
        try {
            $out = & $exe @arguments 2>&1 | Out-String
            $code = $LASTEXITCODE
        } finally {
            if ($workingDirectory) { Pop-Location }
        }
    } finally {
        $ErrorActionPreference = $previous
    }
    $result = New-Object psobject
    $result | Add-Member -MemberType NoteProperty -Name Code -Value $code
    $result | Add-Member -MemberType NoteProperty -Name Out -Value $out
    return $result
}

# Windows 命令行引用规则（含「参数末尾的反斜杠要翻倍」）。Start-Process -ArgumentList 在 PS 5.1 里
# **不会**替你加引号，而 fixture 路径里有空格（Program Files 之类）与中文，所以必须自己来。
function Quote-Arg([string]$argument) {
    if ($null -eq $argument) { return '""' }
    if ($argument.Length -gt 0 -and $argument -notmatch '[\s"]') { return $argument }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($ch in $argument.ToCharArray()) {
        if ($ch -eq '\') { $backslashes++; continue }
        if ($ch -eq '"') {
            [void]$builder.Append(('\' * ($backslashes * 2 + 1)))
            [void]$builder.Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) { [void]$builder.Append(('\' * $backslashes)); $backslashes = 0 }
        [void]$builder.Append($ch)
    }
    if ($backslashes -gt 0) { [void]$builder.Append(('\' * ($backslashes * 2))) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

# 有界超时地跑一次 CLI。环境变量：默认把日志根 / 释放根指到本次运行的临时目录，
# 并且**清掉** RERAR_ENGINE_LOCAL（除非 $Overrides 里显式给了值）。
#
# 【为什么不用 Start-Process】PS 5.1 的 `Start-Process -PassThru` 返回的那个 Process 对象，
# 它的 ExitCode 读出来是**空的**（本机实测：`cmd /c exit 7` 也一样空；同一个进程用
# [System.Diagnostics.Process]::Start 拿到的 ExitCode 就是 7）。退出码是本脚本的主要判据，
# 绝不能建立在一个读不出来的属性上 —— 所以自己建 ProcessStartInfo。
#
# 【为什么标准流按 Latin-1 解码】CLI 写到 stdout/stderr 的是**UTF-8 字节**（Task 12 的 UseUtf8Stdio），
# 而 .NET 默认按控制台 OEM 代码页（中文机器 = 936）解码 —— 那样中文立刻变乱码。用 ISO-8859-1 是
# 有意的：它是**字节↔字符的一一映射**，于是我们既能拿到真正的原始字节（缺陷 ③ 的字节级证据），
# 又能自己按 UTF-8 解出正确的中文。两个都要，缺一不可。
function Invoke-RerarCli {
    param(
        [string[]]$Arguments,
        [int]$TimeoutSec = 120,
        [hashtable]$Overrides = @{},
        [string]$Tag = 'run'
    )

    $keys = @('RERAR_JOURNAL_ROOT', 'RERAR_ENGINE_ROOT', 'RERAR_ENGINE_LOCAL')
    $saved = @{}
    foreach ($key in $keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }

    [Environment]::SetEnvironmentVariable('RERAR_JOURNAL_ROOT', $script:JournalRoot, 'Process')
    [Environment]::SetEnvironmentVariable('RERAR_ENGINE_ROOT', $script:EngineRoot, 'Process')
    [Environment]::SetEnvironmentVariable('RERAR_ENGINE_LOCAL', $null, 'Process')
    foreach ($key in $Overrides.Keys) {
        [Environment]::SetEnvironmentVariable($key, [string]$Overrides[$key], 'Process')
    }

    $quoted = ($Arguments | ForEach-Object { Quote-Arg $_ }) -join ' '
    $latin1 = [System.Text.Encoding]::GetEncoding(28591)
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $timedOut = $false
    $exitCode = -1
    $rawOut = ''
    $rawErr = ''
    $startProblem = $null
    try {
        $info = New-Object System.Diagnostics.ProcessStartInfo
        $info.FileName = $script:Exe
        $info.Arguments = $quoted
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.StandardOutputEncoding = $latin1
        $info.StandardErrorEncoding = $latin1

        $process = $null
        try { $process = [System.Diagnostics.Process]::Start($info) }
        catch { $startProblem = $_.Exception.Message }

        if ($null -ne $process) {
            # 先起**异步**读：大输出量（fixture 18）会把管道缓冲区灌满，同步 ReadToEnd 就是死锁。
            $outTask = $process.StandardOutput.ReadToEndAsync()
            $errTask = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit($TimeoutSec * 1000)) {
                $timedOut = $true
                # 整棵树一起杀：产品自己用 Job Object 兜底，但超时是我们强杀的，子进程未必跟着走。
                Invoke-Native 'taskkill.exe' @('/F', '/T', '/PID', $process.Id.ToString()) $null | Out-Null
                [void]$process.WaitForExit(10000)
            }
            try { $exitCode = [int]$process.ExitCode } catch { $exitCode = -1 }
            # 有界等待两个读取任务：万一有孙进程攥着管道不放，也绝不把验收脚本挂在这里。
            if ($outTask.Wait(30000)) { try { $rawOut = $outTask.Result } catch { $rawOut = '' } }
            if ($errTask.Wait(30000)) { try { $rawErr = $errTask.Result } catch { $rawErr = '' } }
            $process.Dispose()
        }
    } finally {
        $stopwatch.Stop()
        foreach ($key in $keys) {
            [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process')
        }
    }

    $stdoutBytes = $latin1.GetBytes($rawOut)
    $stderrBytes = $latin1.GetBytes($rawErr)
    $utf8 = New-Object System.Text.UTF8Encoding($false)

    $result = New-Object psobject
    $result | Add-Member -MemberType NoteProperty -Name ExitCode -Value $exitCode
    $result | Add-Member -MemberType NoteProperty -Name StdOut -Value ($utf8.GetString($stdoutBytes))
    $result | Add-Member -MemberType NoteProperty -Name StdErr -Value ($utf8.GetString($stderrBytes))
    $result | Add-Member -MemberType NoteProperty -Name StdOutBytes -Value $stdoutBytes
    $result | Add-Member -MemberType NoteProperty -Name StdErrBytes -Value $stderrBytes
    $result | Add-Member -MemberType NoteProperty -Name TimedOut -Value $timedOut
    $result | Add-Member -MemberType NoteProperty -Name ElapsedMs -Value $stopwatch.ElapsedMilliseconds
    $result | Add-Member -MemberType NoteProperty -Name StartProblem -Value $startProblem
    $result | Add-Member -MemberType NoteProperty -Name CommandLine -Value ($script:Exe + ' ' + $quoted)
    return $result
}

# 读机器可读报告。返回 @{ Raw; Objects(array); Bytes(byte[]) }。
function Read-JsonReport([string]$path) {
    $result = New-Object psobject
    $result | Add-Member -MemberType NoteProperty -Name Raw -Value ''
    $result | Add-Member -MemberType NoteProperty -Name Objects -Value (New-Object System.Collections.Generic.List[object])
    $result | Add-Member -MemberType NoteProperty -Name Bytes -Value (New-Object byte[] 0)
    $result | Add-Member -MemberType NoteProperty -Name Boms -Value $false
    if (-not (Test-Path -LiteralPath $path)) { return $result }

    $bytes = [System.IO.File]::ReadAllBytes($path)
    $result.Bytes = $bytes
    $result.Boms = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $raw = [System.Text.Encoding]::UTF8.GetString($bytes)
    if ($result.Boms) { $raw = $raw.Substring(1) }
    $result.Raw = $raw

    $parsed = $null
    try { $parsed = ConvertFrom-Json $raw } catch { return $result }
    if ($null -ne $parsed) {
        foreach ($one in @($parsed)) { if ($null -ne $one) { $result.Objects.Add($one) } }
    }
    return $result
}

function Get-JsonResult([object]$Json, [string]$Path) {
    foreach ($one in $Json.Objects) {
        if ($one.path -eq $Path) { return $one }
    }
    return $null
}

# ------------------------------------------------------------------
# 快照（零越界写入不变量的实现）
# ------------------------------------------------------------------

# 递归枚举一棵树，键 = 绝对路径，值 = 指纹。**绝不进入 reparse point**：
# 一个指回父目录的软链就能让遍历死循环、或枚举到快照根之外去。reparse point 本身单独记一类，
# 因为「输出根里出现 reparse point」本身就是一条要被断言的性质（规格 §9.1 行 13）。
function Get-TreeMap([string]$root, [string[]]$exclude) {
    $map = @{}
    if (-not (Test-Path -LiteralPath $root)) { return $map }

    $rootFull = (Get-Item -LiteralPath $root).FullName
    $excluded = New-Object System.Collections.Generic.List[string]
    foreach ($one in $exclude) { if ($one) { $excluded.Add($one) } }

    $stack = New-Object System.Collections.Stack
    $stack.Push($rootFull)
    while ($stack.Count -gt 0) {
        $dir = [string]$stack.Pop()
        $children = @()
        try { $children = [System.IO.Directory]::GetFileSystemEntries($dir) } catch { continue }
        foreach ($child in $children) {
            $skip = $false
            foreach ($one in $excluded) {
                if ($child.Equals($one, [StringComparison]::OrdinalIgnoreCase) -or
                    $child.StartsWith($one + '\', [StringComparison]::OrdinalIgnoreCase)) { $skip = $true; break }
            }
            if ($skip) { continue }

            try { $attributes = [System.IO.File]::GetAttributes($child) }
            catch { $map[$child] = 'unreadable'; continue }   # 读不动就记下来（绝不吞掉：它也是一处差异）
            if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                $map[$child] = 'reparse'
                continue
            }
            if (($attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                $map[$child] = 'dir'
                $stack.Push($child)
            } else {
                $info = New-Object System.IO.FileInfo($child)
                $map[$child] = ('file|' + $info.Length + '|' + $info.LastWriteTimeUtc.Ticks)
            }
        }
    }
    return $map
}

# 只取直接子项（用于仓库根：抓「凭空多了一个东西」，不去递归 src/ 那几千个文件）。
function Get-ImmediateMap([string]$root) {
    $map = @{}
    if (-not (Test-Path -LiteralPath $root)) { return $map }
    foreach ($child in [System.IO.Directory]::GetFileSystemEntries($root)) {
        $attributes = [System.IO.File]::GetAttributes($child)
        if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { $map[$child] = 'reparse' }
        elseif (($attributes -band [System.IO.FileAttributes]::Directory) -ne 0) { $map[$child] = 'dir' }
        else { $map[$child] = 'file' }
    }
    return $map
}

function Merge-Map($target, $source) {
    foreach ($key in $source.Keys) { $target[$key] = $source[$key] }
    return $target
}

# 一行运行的「越界守门快照」：五个根，见文件头。
function Get-GuardSnapshot([string]$rowDir) {
    $map = @{}
    Merge-Map $map (Get-TreeMap $rowDir @((Join-Path $rowDir 'work'))) | Out-Null
    Merge-Map $map (Get-ImmediateMap $script:RepoRoot) | Out-Null
    Merge-Map $map (Get-TreeMap (Join-Path $script:RepoRoot 'tests') @($script:FixDir)) | Out-Null
    Merge-Map $map (Get-TreeMap $script:FixDir @($script:RunDir)) | Out-Null
    Merge-Map $map (Get-TreeMap (Join-Path $script:RepoRoot 'dist') @()) | Out-Null
    return $map
}

function Compare-Maps($before, $after) {
    $diffs = New-Object System.Collections.Generic.List[string]
    foreach ($key in $before.Keys) {
        if (-not $after.ContainsKey($key)) { $diffs.Add('缺失:' + $key) }
        elseif ($after[$key] -ne $before[$key]) { $diffs.Add('改动:' + $key + ' (' + $before[$key] + ' -> ' + $after[$key] + ')') }
    }
    foreach ($key in $after.Keys) {
        if (-not $before.ContainsKey($key)) { $diffs.Add('新增:' + $key + ' (' + $after[$key] + ')') }
    }
    return ,$diffs
}

# ------------------------------------------------------------------
# 行（row）机械：建目录 → 复制 fixture 副本 → 记快照 → 跑 CLI → 记快照
# ------------------------------------------------------------------

function New-Row {
    param([string]$Id, [string[]]$CopyPaths)
    $rowDir = Join-Path $script:RunDir $Id
    Remove-Dir $rowDir
    $work = Join-Path $rowDir 'work'
    Ensure-Dir $work
    foreach ($source in $CopyPaths) {
        if (-not (Test-Path -LiteralPath $source)) {
            return @{ Id = $Id; Dir = $rowDir; Work = $work; Missing = $source }
        }
        if (Test-Path -LiteralPath $source -PathType Container) {
            Copy-Item -LiteralPath $source -Destination $work -Recurse -Force
        } else {
            Copy-Item -LiteralPath $source -Destination $work -Force
        }
    }
    $canary = Join-Path $work ('canary-' + $Id + '.txt')
    Write-TextFile $canary ('canary for ' + $Id + ' —— 这个文件在本次运行前后必须逐字节一致')
    return @{ Id = $Id; Dir = $rowDir; Work = $work; Canary = $canary; Missing = $null }
}

function Invoke-Row {
    param(
        $Row,
        [string[]]$Targets,
        [string[]]$ExtraArguments = @(),
        [int]$TimeoutSec = 120,
        [hashtable]$Overrides = @{},
        [scriptblock]$Sabotage = $null
    )
    $Row.Before = Get-TreeMap $Row.Work @()
    $Row.GuardBefore = Get-GuardSnapshot $Row.Dir
    $arguments = @('--cli')
    foreach ($target in $Targets) { $arguments += @('--target', $target) }
    $arguments += $ExtraArguments
    $jsonPath = Join-Path $script:ReportDir ($Row.Id + '.json')
    $arguments += @('--json-out', $jsonPath)
    $Row.JsonPath = $jsonPath
    $Row.Cli = Invoke-RerarCli -Arguments $arguments -TimeoutSec $TimeoutSec -Overrides $Overrides -Tag $Row.Id
    # 负向自检的注入点：**必须**落在 GuardBefore 之后、GuardAfter 之前，否则快照比对看不到它。
    if ($Sabotage) { & $Sabotage $Row }
    $Row.GuardAfter = Get-GuardSnapshot $Row.Dir
    $Row.After = Get-TreeMap $Row.Work @()
    $Row.Json = Read-JsonReport $jsonPath
    return $Row
}

# 逗号：不加它，空 List 会被 PowerShell 拆成「什么都没输出」，调用方拿到 $null。
function New-Checks { return ,(New-Object System.Collections.Generic.List[string]) }

# 断言累加器。**绝不返回任何值**：`Check ...` 是当语句用的，返回 $true/$false 会把一堆 True/False
# 打到控制台上（更糟的是混进调用方的输出流）。
function Check([object]$checks, [bool]$condition, [string]$message) {
    if (-not $condition) { [void]$checks.Add($message) }
}

# 每一行都适用的不变式（criterion 2 + 3 + 「拒绝档不留产物」）。
function Check-CommonInvariants {
    param(
        $Row,
        [object]$Checks,
        [bool]$InputsMustBeIntact = $true,
        [bool]$WorkMustBeUnchanged = $false,
        [bool]$ReportExpected = $true
    )

    # --- 零越界写入：五个根的快照必须逐条一致 ---
    $guardDiffs = Compare-Maps $Row.GuardBefore $Row.GuardAfter
    $script:GuardComparisons++
    if ($guardDiffs.Count -gt 0) {
        $script:EscapeDiffs.Add($Row.Id + '：' + ($guardDiffs -join '；'))
        Check $Checks $false ('零越界写入不变量被破坏（目标根之外有变化）：' + ($guardDiffs -join '；'))
    }

    # --- canary：输出根里预先存在的文件必须逐字节原样（零数据丢失的「就地」半边）---
    if ($Row.Canary) {
        Check $Checks ($Row.After.ContainsKey($Row.Canary)) ('输出根里预先存在的 canary 文件不见了：' + $Row.Canary)
        if ($Row.After.ContainsKey($Row.Canary) -and $Row.Before.ContainsKey($Row.Canary)) {
            Check $Checks ($Row.After[$Row.Canary] -eq $Row.Before[$Row.Canary]) 'canary 文件被改动过（长度或时间戳变了）'
        }
    }

    # --- 零数据丢失：运行前就存在的每个输入文件，运行后必须还在且一模一样 ---
    if ($InputsMustBeIntact) {
        $missing = New-Object System.Collections.Generic.List[string]
        foreach ($key in $Row.Before.Keys) {
            if ($Row.Before[$key] -eq 'dir') { continue }
            if (-not $Row.After.ContainsKey($key)) { $missing.Add('缺失:' + $key) }
            elseif ($Row.After[$key] -ne $Row.Before[$key]) { $missing.Add('改动:' + $key) }
        }
        if ($missing.Count -gt 0) {
            $script:DataLossViolations.Add($Row.Id + '：' + ($missing -join '；'))
            Check $Checks $false ('原包/输入被改动或删除（零数据丢失不变量）：' + ($missing -join '；'))
        }
    }

    # --- 拒绝档：输出根里不得多出任何东西（无空目录残留、绝不写盘）---
    if ($WorkMustBeUnchanged) {
        $workDiffs = Compare-Maps $Row.Before $Row.After
        Check $Checks ($workDiffs.Count -eq 0) ('该归档本应被拒绝/中止，输出根里却出现了变化（残留产物？）：' + ($workDiffs -join '；'))
    }

    # --- 输出根里绝不出现 reparse point（规格 §9.1 行 13）---
    $reparse = New-Object System.Collections.Generic.List[string]
    foreach ($key in $Row.After.Keys) { if ($Row.After[$key] -eq 'reparse') { $reparse.Add($key) } }
    Check $Checks ($reparse.Count -eq 0) ('输出根里出现了 reparse point（软链/联接）：' + ($reparse -join '；'))

    # --- 不许超时，也不许根本起不来 ---
    Check $Checks ($null -eq $Row.Cli.StartProblem) ('CLI 进程起不来：' + $Row.Cli.StartProblem)
    Check $Checks (-not $Row.Cli.TimedOut) ('CLI 调用超时（' + $Row.Cli.ElapsedMs + ' ms）：挂起是原脚本的已知缺陷 ②')

    # --- 报告必须是 UTF-8 带 BOM，且不许出现替换符 U+FFFD ---
    # （致命档在解压之前就收场时**没有**报告可查，那种行由调用方传 ReportExpected=$false。）
    $allText = $Row.Cli.StdOut + $Row.Cli.StdErr
    if ($ReportExpected) {
        Check $Checks ($Row.Json.Boms) '机器可读报告不是 UTF-8 带 BOM（规格 §2 第 6 条）'
        $allText = $allText + $Row.Json.Raw
    }
    Check $Checks ($allText.IndexOf([char]0xFFFD) -lt 0) '输出或报告里出现了替换符 U+FFFD（编码乱码）'
}

function Add-Result([string]$id, [string]$name, [bool]$ok, [string]$detail) {
    $row = New-Object psobject
    $row | Add-Member -MemberType NoteProperty -Name Id -Value $id
    $row | Add-Member -MemberType NoteProperty -Name Name -Value $name
    $row | Add-Member -MemberType NoteProperty -Name Ok -Value $ok
    $row | Add-Member -MemberType NoteProperty -Name Detail -Value $detail
    $script:Results.Add($row)
    return $row
}

function Complete-Row($row, [string]$name, [object]$checks, [string]$successDetail) {
    if ($row -and $row.Missing) {
        Add-Result $row.Id $name $false ('无法构造：fixtures.ps1 没有产出 ' + $row.Missing + '（先跑 tests\fixtures.ps1；绝不静默跳过这一行）') | Out-Null
        return
    }
    if ($checks.Count -eq 0) {
        Add-Result $row.Id $name $true $successDetail | Out-Null
    } else {
        Add-Result $row.Id $name $false ($checks -join ' ｜ ') | Out-Null
    }
}

function Get-StatusOf($json, [string]$path) {
    $one = Get-JsonResult $json $path
    if ($null -eq $one) { return '<无对象>' }
    return [string]$one.status
}

function Count-Files([string]$root) {
    if (-not (Test-Path -LiteralPath $root)) { return 0 }
    return @([System.IO.Directory]::GetFiles($root, '*', [System.IO.SearchOption]::AllDirectories)).Count
}

# ==================================================================
# 准备
# ==================================================================

Note-Host ('验收对象：' + $script:Exe)
if (-not (Test-Path -LiteralPath $script:Exe)) {
    Add-Result 'A00' '前置条件' $false ('缺少产物 ' + $script:Exe + '：先跑 build\build.ps1') | Out-Null
    Note-Host ('FAIL: 缺少产物 ' + $script:Exe + '（先跑 powershell -File build\build.ps1）')
    exit 1
}
foreach ($dir in @($script:Scratch, $script:ReportDir, $script:JournalRoot, $script:EngineRoot, $script:RunDir)) { Ensure-Dir $dir }
Note-Host ('本次运行的临时根（日志/释放/报告都在这里，绝不碰真实应用数据）：' + $script:Scratch)
Note-Host ''

function Fixture([string]$relative) { return (Join-Path $script:FixDir $relative) }

# 一个 fixture 目录里的全部文件（目录不存在或为空时返回空数组，绝不抛）。
function Get-FixtureFiles([string]$name) {
    $dir = Fixture $name
    if (-not (Test-Path -LiteralPath $dir)) { return @() }
    return @(Get-ChildItem -LiteralPath $dir -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
}

# 前置条件：夹具矩阵必须齐。缺了的**逐项报出来**，并且对应的行照样会被记成 FAIL（原因写「无法构造」）——
# 绝不静默跳过任何一行（本项目已经因为「静默 skip」判过一次阻断）。
$requiredFixtures = @(
    'f01-nested\outer.zip', 'f02-damaged\broken.zip', 'f03-aes\aes256.zip', 'f04-zipcrypto\zipcrypto.zip',
    'f05-camouflage\photo.jpg', 'f05-camouflage\data.zip删',
    'f06-volumes-ok\vol.7z.001', 'f07-volumes-missing\vol.7z.001',
    'f08-ooxml\trap.docx', 'f09-apk\trap.apk', 'f10-bomb\bomb.zip', 'f11-quine\quine.zip',
    'f12-longpath\long.zip', 'f13-symlink-tar\trav.tar', 'f14-html\fake.zip',
    'f15-zero-volume\zero.7z.002', 'f16-conflict\conflict.zip',
    'f17-existing-target\busy.zip', 'f17-existing-target\busy', 'f18-bulk\bulk.zip'
)
$missingFixtures = @($requiredFixtures | Where-Object { -not (Test-Path -LiteralPath (Fixture $_)) })
if ($missingFixtures.Count -gt 0) {
    Note-Host ''
    Note-Host ('警告：夹具矩阵不完整，缺 ' + $missingFixtures.Count + ' 项。对应的行会被记成 FAIL 并写明「无法构造」，绝不静默跳过：')
    foreach ($one in $missingFixtures) { Note-Host ('  缺：' + (Fixture $one)) }
    Note-Host '请先跑：powershell -NoProfile -ExecutionPolicy Bypass -File tests\fixtures.ps1'
    Note-Host ''
}

# ==================================================================
# F01 正常嵌套
# ==================================================================
$row = New-Row 'F01' @((Fixture 'f01-nested\outer.zip'))
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'outer.zip'
    Invoke-Row $row @($target) | Out-Null
    $outerResult = Get-JsonResult $row.Json $target
    Check $checks ($row.Cli.ExitCode -eq 0) ('退出码 ' + $row.Cli.ExitCode + '，期望 0（全部成功）')
    Check $checks ($row.Json.Objects.Count -eq 2) ('JSON 对象数 ' + $row.Json.Objects.Count + '，期望 2（外层 + 嵌套层各一条）')
    Check $checks ($null -ne $outerResult -and $outerResult.status -eq 'Completed') ('外层结局 ' + (Get-StatusOf $row.Json $target) + '，期望 Completed')
    Check $checks ($null -ne $outerResult -and $outerResult.layers -eq 2) ('外层层数 ' + $outerResult.layers + '，期望 2（规格 §9.1 行 1：报 2 层）')
    Check $checks (Test-Path -LiteralPath (Join-Path $row.Work 'outer\inner\hello.txt')) '解出来的 hello.txt 不在 outer\inner\ 下（两层嵌套的产物没落地）'
    Check-CommonInvariants $row $checks $true $false
}
Complete-Row $row 'F01 正常嵌套' $checks ('退出码 0；2 个 JSON 对象；外层 Completed 层数 2；outer\inner\hello.txt 已落地') | Out-Null

# ==================================================================
# F02 损坏包
# ==================================================================
$row = New-Row 'F02' @((Fixture 'f02-damaged\broken.zip'))
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'broken.zip'
    # 【负向自检（默认关）】RERAR_ACCEPTANCE_NEGATIVE_CONTROL=1 时，在这一行的「跑完 CLI、还没取
    # 越界快照」的窗口里故意制造四处越界 + 一处数据丢失（四个不同的守门根各来一处）。**必须**看到
    # F02 与 A03 双双 FAIL —— 那才证明「零越界写入」的快照比对不是空转（这正是 Task 15 自审清单里
    # 点名要回答的问题）。它同时也验证了「canary / 原有输入必须逐字节原样」那条通道真的会响。
    $sabotage = $null
    if ($env:RERAR_ACCEPTANCE_NEGATIVE_CONTROL -eq '1') {
        $sabotage = {
            param($Row)
            [System.IO.File]::WriteAllText((Join-Path $Row.Dir 'NEGATIVE-CONTROL-escape-in-row.txt'), 'escape')
            [System.IO.File]::WriteAllText((Join-Path $script:RepoRoot 'tests\NEGATIVE-CONTROL-escape-in-tests.txt'), 'escape')
            [System.IO.File]::WriteAllText((Join-Path $script:RepoRoot 'NEGATIVE-CONTROL-escape-at-repo-root.txt'), 'escape')
            [System.IO.File]::WriteAllText((Join-Path $script:RepoRoot 'dist\NEGATIVE-CONTROL-escape-in-dist.txt'), 'escape')
            [System.IO.File]::Delete($Row.Canary)
        }
    }
    Invoke-Row $row @($target) -Sabotage $sabotage | Out-Null
    $one = Get-JsonResult $row.Json $target
    Check $checks ($row.Cli.ExitCode -eq 1) ('退出码 ' + $row.Cli.ExitCode + '，期望 1（有跳过/失败，不是 0 也不是 2）')
    Check $checks ($null -ne $one -and $one.status -ne 'Completed') '损坏包竟然被报成 Completed'
    Check $checks ($null -ne $one -and ([string]$one.message).Length -gt 0) '损坏包没有任何中文判词（诊断信息为空）'
    Check-CommonInvariants $row $checks $true $true
    # 负向自检写完就把那三个「包外」文件收掉（行目录里的那个随下次 New-Row 一起被重建）。
    foreach ($leftover in @('tests\NEGATIVE-CONTROL-escape-in-tests.txt',
                            'NEGATIVE-CONTROL-escape-at-repo-root.txt',
                            'dist\NEGATIVE-CONTROL-escape-in-dist.txt')) {
        Remove-Item -LiteralPath (Join-Path $script:RepoRoot $leftover) -Force -ErrorAction SilentlyContinue
    }
}
Complete-Row $row 'F02 损坏包' $checks '退出码 1；明确失败 + 中文判词；原包保留；输出根无任何残留' | Out-Null

# ==================================================================
# F03 AES-256 加密 zip（同时是缺陷 ② 的回归：必须在限时内返回）
# ==================================================================
$row = New-Row 'F03' @((Fixture 'f03-aes\aes256.zip'))
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'aes256.zip'
    Invoke-Row $row @($target) -TimeoutSec 90 | Out-Null
    $one = Get-JsonResult $row.Json $target
    Check $checks ($row.Cli.ExitCode -eq 1) ('退出码 ' + $row.Cli.ExitCode + '，期望 1')
    Check $checks ($null -ne $one -and $one.status -eq 'SkippedNeedsPassword') ('AES 包结局 ' + (Get-StatusOf $row.Json $target) + '，期望 SkippedNeedsPassword')
    Check $checks ($row.Cli.ElapsedMs -le 60000) ('AES 包用了 ' + $row.Cli.ElapsedMs + ' ms 才返回，超过 60 s 的限定时间（缺陷 ②：密码提示挂起）')
    Check-CommonInvariants $row $checks $true $true
}
Complete-Row $row 'F03 AES-256 加密 zip' $checks ('退出码 1；SkippedNeedsPassword；' + $row.Cli.ElapsedMs + ' ms 内返回（未挂起）；原包保留；不写盘') | Out-Null

# ==================================================================
# F04 ZipCrypto 加密 zip
# ==================================================================
$row = New-Row 'F04' @((Fixture 'f04-zipcrypto\zipcrypto.zip'))
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'zipcrypto.zip'
    Invoke-Row $row @($target) -TimeoutSec 90 | Out-Null
    $one = Get-JsonResult $row.Json $target
    Check $checks ($row.Cli.ExitCode -eq 1) ('退出码 ' + $row.Cli.ExitCode + '，期望 1')
    Check $checks ($null -ne $one -and $one.status -eq 'SkippedNeedsPassword') ('ZipCrypto 包结局 ' + (Get-StatusOf $row.Json $target) + '，期望 SkippedNeedsPassword')
    Check $checks ($row.Cli.ElapsedMs -le 60000) ('ZipCrypto 包用了 ' + $row.Cli.ElapsedMs + ' ms 才返回，超过 60 s')
    Check-CommonInvariants $row $checks $true $true
}
Complete-Row $row 'F04 ZipCrypto 加密 zip' $checks ('退出码 1；SkippedNeedsPassword；' + $row.Cli.ElapsedMs + ' ms 内返回；原包保留；不写盘') | Out-Null

# ==================================================================
# F05 伪装后缀（规格 §9.1 行 5：识别并解压，宿主保留）
# ==================================================================
$row = New-Row 'F05' @((Fixture 'f05-camouflage\photo.jpg'), (Fixture 'f05-camouflage\data.zip删'))
$checks = New-Checks
if (-not $row.Missing) {
    $photo = Join-Path $row.Work 'photo.jpg'
    $del = Join-Path $row.Work 'data.zip删'
    Invoke-Row $row @($photo, $del) | Out-Null
    $photoResult = Get-JsonResult $row.Json $photo
    $delResult = Get-JsonResult $row.Json $del
    Check $checks ($row.Json.Objects.Count -eq 2) ('JSON 对象数 ' + $row.Json.Objects.Count + '，期望 2')
    Check $checks ($null -ne $photoResult -and $photoResult.status -eq 'Completed') ('photo.jpg（真 zip）结局 ' + (Get-StatusOf $row.Json $photo) + '，期望 Completed（规格 §9.1 行 5：识别并解压，宿主保留）')
    Check $checks ($null -ne $delResult -and $delResult.status -eq 'Completed') ('data.zip删（真 zip）结局 ' + (Get-StatusOf $row.Json $del) + '，期望 Completed（规格 §9.1 行 5）')
    Check $checks (Test-Path -LiteralPath $photo) 'photo.jpg 本体不见了（宿主必须保留）'
    Check $checks (Test-Path -LiteralPath $del) 'data.zip删 本体不见了（宿主必须保留）'
    Check-CommonInvariants $row $checks $true $false
}
Complete-Row $row 'F05 伪装后缀' $checks '两个伪装后缀的真 zip 都被识别并解压成各自的输出目录，宿主文件都还在' | Out-Null

# ==================================================================
# F06 分卷正常集
# ==================================================================
$volumeSources = Get-FixtureFiles 'f06-volumes-ok'
if ($volumeSources.Count -eq 0) { $volumeSources = @((Fixture 'f06-volumes-ok\vol.7z.001')) }
$row = New-Row 'F06' $volumeSources
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'vol.7z.001'
    Invoke-Row $row @($target) | Out-Null
    $one = Get-JsonResult $row.Json $target
    Check $checks ($row.Cli.ExitCode -eq 0) ('退出码 ' + $row.Cli.ExitCode + '，期望 0')
    Check $checks ($null -ne $one -and $one.status -eq 'Completed') ('分卷集结局 ' + (Get-StatusOf $row.Json $target) + '，期望 Completed')
    Check $checks (Test-Path -LiteralPath (Join-Path $row.Work 'vol\payload.bin')) '分卷集的 payload.bin 没有解出来'
    Check-CommonInvariants $row $checks $true $false
}
Complete-Row $row 'F06 分卷正常集' $checks '权威成员 .001 正确，整集完整解出 payload.bin，全部卷成员原样保留' | Out-Null

# ==================================================================
# F07 分卷缺中间卷
# ==================================================================
$missingVolumeSources = Get-FixtureFiles 'f07-volumes-missing'
if ($missingVolumeSources.Count -eq 0) { $missingVolumeSources = @((Fixture 'f07-volumes-missing\vol.7z.001')) }
$row = New-Row 'F07' $missingVolumeSources
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'vol.7z.001'
    Invoke-Row $row @($target) | Out-Null
    $one = Get-JsonResult $row.Json $target
    Check $checks ($row.Cli.ExitCode -eq 1) ('退出码 ' + $row.Cli.ExitCode + '，期望 1')
    Check $checks ($null -ne $one -and $one.status -eq 'SkippedUnreadable') ('缺卷结局 ' + (Get-StatusOf $row.Json $target) + '，期望 SkippedUnreadable')
    Check $checks ($null -ne $one -and ([string]$one.message).Contains('缺') -and ([string]$one.message).Contains('.002')) ('缺卷判词没有报出**具体缺哪一个**（要含 .002）：' + $one.message)
    Check $checks ($null -ne $one -and -not ([string]$one.message).Contains('损坏')) ('缺卷被判成「损坏」（规格 §9.1 行 7 明确禁止）：' + $one.message)
    Check-CommonInvariants $row $checks $true $true
}
Complete-Row $row 'F07 分卷缺中间卷' $checks '退出码 1；报出缺 vol.7z.002；未误报损坏；现有卷全部保留；不写盘' | Out-Null

# ==================================================================
# F08 OOXML 陷阱
# ==================================================================
$row = New-Row 'F08' @((Fixture 'f08-ooxml\trap.docx'))
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'trap.docx'
    Invoke-Row $row @($target) | Out-Null
    $one = Get-JsonResult $row.Json $target
    Check $checks ($row.Cli.ExitCode -eq 1) ('退出码 ' + $row.Cli.ExitCode + '，期望 1')
    Check $checks ($null -ne $one -and $one.status -eq 'SkippedContainer') ('OOXML 结局 ' + (Get-StatusOf $row.Json $target) + '，期望 SkippedContainer（I4：拒绝递归）')
    Check $checks ($null -ne $one -and ([string]$one.message).Contains('Content_Types')) ('判词没有说明身份特征：' + $one.message)
    Check-CommonInvariants $row $checks $true $true
}
Complete-Row $row 'F08 OOXML 陷阱' $checks '退出码 1；SkippedContainer（拒绝递归）；绝不删除；不写盘' | Out-Null

# ==================================================================
# F09 APK 陷阱
# ==================================================================
$row = New-Row 'F09' @((Fixture 'f09-apk\trap.apk'))
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'trap.apk'
    Invoke-Row $row @($target) | Out-Null
    $one = Get-JsonResult $row.Json $target
    Check $checks ($row.Cli.ExitCode -eq 1) ('退出码 ' + $row.Cli.ExitCode + '，期望 1')
    Check $checks ($null -ne $one -and $one.status -eq 'SkippedContainer') ('APK 结局 ' + (Get-StatusOf $row.Json $target) + '，期望 SkippedContainer（I4：拒绝递归）')
    Check $checks ($null -ne $one -and ([string]$one.message).Contains('APK')) ('判词没有说明身份特征：' + $one.message)
    Check-CommonInvariants $row $checks $true $true
}
Complete-Row $row 'F09 APK 陷阱' $checks '退出码 1；SkippedContainer（拒绝递归）；绝不删除；不写盘' | Out-Null

# ==================================================================
# F10 zip 炸弹
# ==================================================================
$row = New-Row 'F10' @((Fixture 'f10-bomb\bomb.zip'))
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'bomb.zip'
    Invoke-Row $row @($target) | Out-Null
    $one = Get-JsonResult $row.Json $target
    Check $checks ($row.Cli.ExitCode -eq 1) ('退出码 ' + $row.Cli.ExitCode + '，期望 1')
    Check $checks ($null -ne $one -and $one.status -eq 'SkippedUnreadable') ('炸弹结局 ' + (Get-StatusOf $row.Json $target) + '，期望 SkippedUnreadable')
    Check $checks ($null -ne $one -and ([string]$one.message).Contains('解压比')) ('判词没有说明解压比（安全上限）：' + $one.message)
    Check-CommonInvariants $row $checks $true $true
}
Complete-Row $row 'F10 zip 炸弹' $checks '退出码 1；预检按解压比拒绝；一个字节都没写盘；原包保留' | Out-Null

# ==================================================================
# F11 Quine（自指命名链）
# ==================================================================
$row = New-Row 'F11' @((Fixture 'f11-quine\quine.zip'))
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'quine.zip'
    # 同一个目标给两次：运行级去重必须只处理一次（规格 §9.1 行 11「visited 去重」可观察的那一半）。
    Invoke-Row $row @($target, $target) -TimeoutSec 240 | Out-Null
    $statuses = @($row.Json.Objects | ForEach-Object { [string]$_.status })
    $paths = @($row.Json.Objects | ForEach-Object { [string]$_.path })
    $completed = @($statuses | Where-Object { $_ -eq 'Completed' }).Count
    $depthLimit = @($statuses | Where-Object { $_ -eq 'NotAttemptedDepthLimit' }).Count
    Check $checks (-not $row.Cli.TimedOut) '死循环/挂起：CLI 在限量时间内没有返回'
    Check $checks ($row.Cli.ExitCode -eq 1) ('退出码 ' + $row.Cli.ExitCode + '，期望 1（有深度触顶的未处理项）')
    $unique = @($paths | Sort-Object -Unique)
    Check $checks ($unique.Count -eq $paths.Count) ('同一个路径被处理了多次（visited 去重失效）：' + $paths.Count + ' 条对象 / ' + $unique.Count + ' 个不同路径')
    Check $checks ($completed -eq 10) ('已完成的层数是 ' + $completed + '，期望 10（默认深度上限 10）')
    Check $checks ($depthLimit -eq 1) ('NotAttemptedDepthLimit 条数是 ' + $depthLimit + '，期望 1（触顶必须显式列出）')
    Check $checks (($paths | Where-Object { $_ -eq $target }).Count -eq 1) '重复的 --target 被处理了两次（运行级去重失效）'
    Check-CommonInvariants $row $checks $true $false
}
Complete-Row $row 'F11 Quine（自指命名链）' $checks ('不死循环：' + $row.Cli.ElapsedMs + ' ms 内跑完；11 个归档各一条对象、无重复路径；10 层完成 + 1 层显式触顶') | Out-Null

# ==================================================================
# F12 深嵌套长路径
# ==================================================================
$row = New-Row 'F12' @((Fixture 'f12-longpath\long.zip'))
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'long.zip'
    Invoke-Row $row @($target) | Out-Null
    $one = Get-JsonResult $row.Json $target
    Check $checks ($row.Cli.ExitCode -eq 1) ('退出码 ' + $row.Cli.ExitCode + '，期望 1')
    Check $checks ($null -ne $one -and $one.status -eq 'SkippedUnreadable') ('长路径结局 ' + (Get-StatusOf $row.Json $target) + '，期望 SkippedUnreadable（预检拒绝）')
    Check $checks ($null -ne $one -and ([string]$one.message).Contains('路径过长')) ('判词没有明确报超限：' + $one.message)
    Check-CommonInvariants $row $checks $true $true
}
Complete-Row $row 'F12 深嵌套长路径' $checks '退出码 1；预检明确报「路径过长」并拒绝写盘；原包保留' | Out-Null

# ==================================================================
# F13 tar 软链穿越
# ==================================================================
$row = New-Row 'F13' @((Fixture 'f13-symlink-tar\trav.tar'))
$checks = New-Checks
if (-not $row.Missing) {
    # 穿越目标必须落在**输出根之外**、行目录之内：这样「真的穿出去了」会被越界快照抓住，
    # 而不是落到用户的真实目录里去。tar 里的 linkname 是 ..\..\escape-target，而暂存目录是
    # <row>\work\.rerar-stage-xxxx，于是两级向上正好是 <row>\escape-target。
    $escapeDir = Join-Path $row.Dir 'escape-target'
    Ensure-Dir $escapeDir
    $target = Join-Path $row.Work 'trav.tar'
    Invoke-Row $row @($target) | Out-Null
    $one = Get-JsonResult $row.Json $target
    Check $checks ($null -ne $one -and $one.status -eq 'Failed') ('软链 tar 结局 ' + (Get-StatusOf $row.Json $target) + '，期望 Failed（拒绝/展平）')
    Check $checks ($null -ne $one -and (([string]$one.message).Contains('reparse') -or ([string]$one.message).Contains('安全断言'))) ('判词没有说明是暂存树安全断言失败：' + $one.message)
    Check $checks (@([System.IO.Directory]::GetFileSystemEntries($escapeDir)).Count -eq 0) '软链穿越成功：escape-target 里出现了东西（零越界写入被破坏）'
    Check $checks (-not (Test-Path -LiteralPath (Join-Path $row.Work 'trav'))) '软链 tar 竟然提交了输出目录'
    Check-CommonInvariants $row $checks $true $true
}
Complete-Row $row 'F13 tar 软链穿越' $checks '退出码 1；暂存树安全断言失败（reparse point 被发现）；未提交、暂存已收掉；escape-target 仍为空' | Out-Null

# ==================================================================
# F14 HTML 假包
# ==================================================================
$row = New-Row 'F14' @((Fixture 'f14-html\fake.zip'))
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'fake.zip'
    Invoke-Row $row @($target) | Out-Null
    $one = Get-JsonResult $row.Json $target
    Check $checks ($row.Cli.ExitCode -eq 1) ('退出码 ' + $row.Cli.ExitCode + '，期望 1')
    Check $checks ($null -ne $one -and $one.status -eq 'SkippedUnreadable') ('HTML 假包结局 ' + (Get-StatusOf $row.Json $target) + '，期望 SkippedUnreadable')
    Check $checks ($null -ne $one -and ([string]$one.message).Contains('网页')) ('判词没有说「这是网页，下载失败」：' + $one.message)
    Check-CommonInvariants $row $checks $true $true
}
Complete-Row $row 'F14 HTML 假包' $checks '退出码 1；明确报「这是网页而不是压缩包」；原文件保留；不写盘' | Out-Null

# ==================================================================
# F15 0 字节卷
# ==================================================================
$row = New-Row 'F15' @((Fixture 'f15-zero-volume\zero.7z.001'), (Fixture 'f15-zero-volume\zero.7z.002'))
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'zero.7z.002'
    Invoke-Row $row @($target) | Out-Null
    $one = Get-JsonResult $row.Json $target
    Check $checks ($row.Cli.ExitCode -eq 1) ('退出码 ' + $row.Cli.ExitCode + '，期望 1')
    Check $checks ($null -ne $one -and $one.status -eq 'SkippedUnreadable') ('0 字节卷结局 ' + (Get-StatusOf $row.Json $target) + '，期望 SkippedUnreadable')
    Check $checks ($null -ne $one -and ([string]$one.message).Contains('0 字节') -and ([string]$one.message).Contains('缺卷')) ('判词没有把 0 字节如实说成缺卷：' + $one.message)
    Check-CommonInvariants $row $checks $true $true
}
Complete-Row $row 'F15 0 字节卷' $checks '退出码 1；判词「0 字节文件：视为缺卷」；同集其它卷保留；不写盘' | Out-Null

# ==================================================================
# F16 路径冲突
# ==================================================================
$row = New-Row 'F16' @((Fixture 'f16-conflict\conflict.zip'))
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'conflict.zip'
    Invoke-Row $row @($target) | Out-Null
    $one = Get-JsonResult $row.Json $target
    Check $checks ($row.Cli.ExitCode -eq 1) ('退出码 ' + $row.Cli.ExitCode + '，期望 1')
    Check $checks ($null -ne $one -and $one.status -eq 'SkippedUnreadable') ('重名冲突结局 ' + (Get-StatusOf $row.Json $target) + '，期望 SkippedUnreadable')
    Check $checks ($null -ne $one -and ([string]$one.message).Contains('冲突')) ('判词没有报「条目名冲突」：' + $one.message)
    Check-CommonInvariants $row $checks $true $true
}
Complete-Row $row 'F16 路径冲突' $checks '退出码 1；预检报条目名冲突（大小写不敏感）；绝不静默覆盖；不写盘' | Out-Null

# ==================================================================
# F17 非空目标目录
# ==================================================================
$row = New-Row 'F17' @((Fixture 'f17-existing-target\busy.zip'), (Fixture 'f17-existing-target\busy'))
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'busy.zip'
    $blocker = Join-Path $row.Work 'busy\blocker.txt'
    $blockerBefore = Get-FileHash -LiteralPath $blocker -Algorithm SHA256
    Invoke-Row $row @($target) | Out-Null
    $one = Get-JsonResult $row.Json $target
    Check $checks ($row.Cli.ExitCode -eq 0) ('退出码 ' + $row.Cli.ExitCode + '，期望 0')
    Check $checks ($null -ne $one -and $one.status -eq 'Completed') ('结局 ' + (Get-StatusOf $row.Json $target) + '，期望 Completed（改用 name (2)）')
    Check $checks ($null -ne $one -and ([string]$one.outputDir).EndsWith('busy (2)')) ('输出去向不是 "busy (2)"，而是 ' + $one.outputDir)
    Check $checks (Test-Path -LiteralPath (Join-Path $row.Work 'busy (2)\file1.txt')) 'busy (2) 里没有 file1.txt'
    Check $checks ((Get-FileHash -LiteralPath $blocker -Algorithm SHA256).Hash -eq $blockerBefore.Hash) '预先存在的 busy\blocker.txt 被改动了'
    Check-CommonInvariants $row $checks $true $false
}
Complete-Row $row 'F17 非空目标' $checks '退出码 0；改用 busy (2)；预先存在的 busy\blocker.txt 逐字节未变' | Out-Null

# ==================================================================
# F18 大输出量
# ==================================================================
$row = New-Row 'F18' @((Fixture 'f18-bulk\bulk.zip'))
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'bulk.zip'
    Invoke-Row $row @($target) -TimeoutSec 600 | Out-Null
    $one = Get-JsonResult $row.Json $target
    $onDisk = Count-Files (Join-Path $row.Work 'bulk')
    Check $checks (-not $row.Cli.TimedOut) '子进程管道被灌满导致死锁：CLI 没有在限量时间内返回'
    Check $checks ($row.Cli.ExitCode -eq 0) ('退出码 ' + $row.Cli.ExitCode + '，期望 0')
    Check $checks ($null -ne $one -and $one.status -eq 'Completed') ('结局 ' + (Get-StatusOf $row.Json $target) + '，期望 Completed')
    Check $checks ($null -ne $one -and $one.files -eq 4000) ('报告的文件数是 ' + $one.files + '，期望 4000')
    Check $checks ($onDisk -eq 4000) ('磁盘上实际有 ' + $onDisk + ' 个文件，期望 4000（子进程输出没被吞掉/截断）')
    Check-CommonInvariants $row $checks $true $false
}
Complete-Row $row 'F18 大输出量' $checks ('4000 个成员全部解出（' + $row.Cli.ElapsedMs + ' ms），子进程管道没有死锁') | Out-Null

# ==================================================================
# A04 干净环境可运行（无本机 7-Zip 时走内嵌兜底）
# ==================================================================
$row = New-Row 'A04' @((Fixture 'f01-nested\outer.zip'))
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'outer.zip'
    $engineRootOverride = Join-Path $script:Scratch 'engine-clean'
    Invoke-Row $row @($target) -TimeoutSec 180 -Overrides @{ 'RERAR_ENGINE_LOCAL' = 'off'; 'RERAR_ENGINE_ROOT' = $engineRootOverride } | Out-Null
    $one = Get-JsonResult $row.Json $target
    Check $checks ($row.Cli.ExitCode -eq 0) ('退出码 ' + $row.Cli.ExitCode + '，期望 0')
    Check $checks ($row.Cli.StdOut.Contains('内置便携版')) '运行头没有如实报出「内置便携版」——说明用的不是内嵌副本'
    Check $checks (-not $row.Cli.StdOut.Contains('（本机安装）')) '运行头报的是本机安装的 7-Zip，不是内嵌副本（干净环境这条路没被真正走到）'
    Check $checks ($null -ne $one -and $one.status -eq 'Completed') ('结局 ' + (Get-StatusOf $row.Json $target) + '，期望 Completed')
    Check $checks (Test-Path -LiteralPath (Join-Path $row.Work 'outer\inner\hello.txt')) '内嵌引擎下没有解出 hello.txt'
    $released = @(Get-ChildItem -LiteralPath $engineRootOverride -Recurse -File -Filter '7z.exe' -ErrorAction SilentlyContinue)
    Check $checks ($released.Count -ge 1) ('内嵌副本没有被释放到 ' + $engineRootOverride + '\<buildid>\7z.exe')
    Check-CommonInvariants $row $checks $true $false
}
Complete-Row $row 'A04 干净环境可运行' $checks 'RERAR_ENGINE_LOCAL=off：如实报「内置便携版」，释放到临时根，两层嵌套照常解出' | Out-Null

# ==================================================================
# A05① 失败仍删包（原脚本缺陷 ①）
# ==================================================================
$row = New-Row 'A05a' @((Fixture 'f02-damaged\broken.zip'))
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'broken.zip'
    Invoke-Row $row @($target) -ExtraArguments @('--delete') | Out-Null
    $one = Get-JsonResult $row.Json $target
    Check $checks ($null -ne $one -and $one.status -ne 'Completed') '开了删除开关的损坏包竟然报 Completed'
    Check $checks (Test-Path -LiteralPath $target) '开了 --delete 之后，失败归档的原包被删掉了（原脚本缺陷 ① 复现）'
}
Complete-Row $row 'A05① 失败仍删包' $checks '--delete + 损坏包：原包仍在（I3：只有「完成且校验通过」才允许删除）' | Out-Null

# ==================================================================
# A05② 密码提示挂起（原脚本缺陷 ②）
# ==================================================================
$row = New-Row 'A05b' @((Fixture 'f03-aes\aes256.zip'))
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'aes256.zip'
    Invoke-Row $row @($target) -TimeoutSec 60 | Out-Null
    $one = Get-JsonResult $row.Json $target
    Check $checks (-not $row.Cli.TimedOut) '加密包把 CLI 挂住了（原脚本缺陷 ② 复现）'
    Check $checks ($row.Cli.ElapsedMs -le 45000) ('加密包用了 ' + $row.Cli.ElapsedMs + ' ms 才返回，超过 45 s 的限定时间')
    Check $checks ($null -ne $one -and $one.status -eq 'SkippedNeedsPassword') ('结局 ' + (Get-StatusOf $row.Json $target) + '，期望 SkippedNeedsPassword')
    Check $checks (Test-Path -LiteralPath $target) '加密包的原包不见了'
}
Complete-Row $row 'A05② 密码提示挂起' $checks ('无人工干预下 ' + $row.Cli.ElapsedMs + ' ms 返回「需密码」；原包保留；没有任何挂起') | Out-Null

# ==================================================================
# A05③ GBK/chcp 乱码（原脚本缺陷 ③）
# ==================================================================
$row = New-Row 'A05c' @()
$checks = New-Checks
if (-not (Test-Path -LiteralPath (Fixture 'f02-damaged\broken.zip'))) {
    Check $checks $false ('无法构造：缺少 ' + (Fixture 'f02-damaged\broken.zip') + '（先跑 tests\fixtures.ps1）')
} elseif (-not $row.Missing) {
    # 中文目录 + 中文文件名（而且是会失败的损坏包，于是中文会一路进判词与报告）。
    $chineseDir = Join-Path $row.Work '中文目录'
    Ensure-Dir $chineseDir
    $chineseZip = Join-Path $chineseDir '损坏的压缩包.zip'
    Copy-Item -LiteralPath (Fixture 'f02-damaged\broken.zip') -Destination $chineseZip -Force
    $expectedName = '损坏的压缩包.zip'
    Invoke-Row $row @($chineseZip) | Out-Null
    $one = Get-JsonResult $row.Json $chineseZip
    Check $checks ($null -ne $one) '中文路径的目标在报告里没有对象'
    Check $checks ($null -ne $one -and ([string]$one.path).Contains($expectedName)) ('报告里的路径丢了中文：' + $one.path)
    Check $checks ($null -ne $one -and ([string]$one.path) -eq $chineseZip) ('报告里的路径与原路径不是逐字符相同：' + $one.path)
    # 逐字节：报告文件里必须出现这个中文名的 UTF-8 字节序列（GBK 字节在这里会找不到）。
    $needle = [System.Text.Encoding]::UTF8.GetBytes($expectedName)
    $haystack = $row.Json.Bytes
    $found = $false
    for ($i = 0; $i + $needle.Length -le $haystack.Length; $i++) {
        $same = $true
        for ($j = 0; $j -lt $needle.Length; $j++) { if ($haystack[$i + $j] -ne $needle[$j]) { $same = $false; break } }
        if ($same) { $found = $true; break }
    }
    Check $checks $found '报告文件里找不到中文名的 UTF-8 字节序列（说明写出去的是 GBK 之类的字节）'
    # stdout 的**原始字节**（Latin-1 一一映射取回来的），同样要含这个中文名的 UTF-8 序列 ——
    # 人读面乱码与「报告对、控制台错」是两件事，两边都要钉。
    $stdoutBytes = $row.Cli.StdOutBytes
    Check $checks ($null -ne $stdoutBytes -and $stdoutBytes.Length -gt 0) '没有捕获到 stdout 字节'
    if ($null -ne $stdoutBytes) {
        $foundOut = $false
        for ($i = 0; $i + $needle.Length -le $stdoutBytes.Length; $i++) {
            $same = $true
            for ($j = 0; $j -lt $needle.Length; $j++) { if ($stdoutBytes[$i + $j] -ne $needle[$j]) { $same = $false; break } }
            if ($same) { $foundOut = $true; break }
        }
        Check $checks $foundOut 'stdout 里找不到中文名的 UTF-8 字节序列（人读面乱码）'
    }
    Check $checks (($row.Cli.StdOut + $row.Cli.StdErr + $row.Json.Raw).IndexOf([char]0xFFFD) -lt 0) '出现了替换符 U+FFFD'
}
Complete-Row $row 'A05③ GBK/chcp 乱码' $checks '中文目录 + 中文包名逐字符进报告、逐字节是 UTF-8；stdout 同字节序列；全程无 U+FFFD' | Out-Null

# ==================================================================
# A05④ 分卷残留（原脚本缺陷 ④）
# ==================================================================
$delVolumeSources = Get-FixtureFiles 'f06-volumes-ok'
if ($delVolumeSources.Count -eq 0) { $delVolumeSources = @((Fixture 'f06-volumes-ok\vol.7z.001')) }
$row = New-Row 'A05d' $delVolumeSources
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'vol.7z.001'
    $members = @(Get-ChildItem -LiteralPath $row.Work -File | Where-Object { $_.Name -like 'vol.7z.*' } | ForEach-Object { $_.FullName })
    Invoke-Row $row @($target) -ExtraArguments @('--delete') | Out-Null
    $one = Get-JsonResult $row.Json $target
    Check $checks ($row.Cli.ExitCode -eq 0) ('退出码 ' + $row.Cli.ExitCode + '，期望 0')
    Check $checks ($null -ne $one -and $one.status -eq 'Completed') ('结局 ' + (Get-StatusOf $row.Json $target) + '，期望 Completed')
    $stillThere = @($members | Where-Object { Test-Path -LiteralPath $_ })
    $gone = $members.Count - $stillThere.Count
    Check $checks ($gone -eq 0 -or $gone -eq $members.Count) ('分卷集只被处置了一部分（留下孤儿分卷）：' + $gone + '/' + $members.Count + ' 个成员消失')
    Check $checks ($null -ne $one -and ([string]$one.message).Contains('分卷集')) ('判词没有说明分卷集是「全部不处置」：' + $one.message)
    Check-CommonInvariants $row $checks ($gone -eq $members.Count) $false
}
Complete-Row $row 'A05④ 分卷残留' $checks '--delete + 分卷集：按「全部不处置」处理，成员 0 个孤儿（要么全在、要么全不在）' | Out-Null

# ==================================================================
# A05④b 删除开关对成功归档确实生效（I3 的正向半边）
# ==================================================================
$row = New-Row 'A05e' @((Fixture 'f17-existing-target\busy.zip'))
$checks = New-Checks
if (-not $row.Missing) {
    $target = Join-Path $row.Work 'busy.zip'
    Invoke-Row $row @($target) -ExtraArguments @('--delete') | Out-Null
    $one = Get-JsonResult $row.Json $target
    Check $checks ($row.Cli.ExitCode -eq 0) ('退出码 ' + $row.Cli.ExitCode + '，期望 0')
    Check $checks ($null -ne $one -and $one.status -eq 'Completed') ('结局 ' + (Get-StatusOf $row.Json $target) + '，期望 Completed')
    Check $checks (Test-Path -LiteralPath (Join-Path $row.Work 'busy\file1.txt')) 'busy\file1.txt 没有解出来'
    Check $checks (-not (Test-Path -LiteralPath $target)) '开了 --delete 的成功归档，原包居然还在（删除没有生效）'
    Check-CommonInvariants $row $checks $false $false
}
Complete-Row $row 'A05④b 删除开关生效' $checks ('--delete + 成功归档：产物落地，原包按 I3 被处置（判词：' + $one.message + '）') | Out-Null

# ==================================================================
# A06a 产物预算：exe ≤ 5 MB
# ==================================================================
$checks = New-Checks
$exeBytes = (Get-Item -LiteralPath $script:Exe).Length
Check $checks ($exeBytes -le 5 * 1024 * 1024) ('dist\Rerar.exe 有 ' + $exeBytes + ' 字节，超过 5 MB')
Complete-Row ([pscustomobject]@{ Id = 'A06a'; Missing = $null }) 'A06a 产物预算（exe ≤ 5MB）' $checks `
    ('dist\Rerar.exe = ' + $exeBytes + ' 字节（含内嵌 7z.exe + 7z.dll），上限 5 MB') | Out-Null

# ==================================================================
# A06b 冷启动 ≤ 2 s
# ==================================================================
$checks = New-Checks
$selftest = Invoke-RerarCli -Arguments @('--cli', '--selftest') -TimeoutSec 30 -Tag 'A06b'
Check $checks ($selftest.ExitCode -eq 0) ('--cli --selftest 退出码 ' + $selftest.ExitCode + '，期望 0')
Check $checks ($selftest.StdOut -match 'version=\d+') ('--cli --selftest 输出不匹配 version=<n>：' + $selftest.StdOut.Trim())
Check $checks ($selftest.ElapsedMs -le 2000) ('冷启动用了 ' + $selftest.ElapsedMs + ' ms，超过 2 s')
Complete-Row ([pscustomobject]@{ Id = 'A06b'; Missing = $null }) 'A06b 冷启动 ≤ 2s' $checks `
    ('--cli --selftest：' + $selftest.ElapsedMs + ' ms 返回 version=' + ($selftest.StdOut.Trim() -replace 'version=', '')) | Out-Null

# ==================================================================
# A06c 报告是合法 JSON、UTF-8 带 BOM，中文读回来一字不差
# ==================================================================
$checks = New-Checks
# 直接用 A05③ 那一行的报告做这份证据（它刻意含中文路径）。
$reportPath = Join-Path $script:ReportDir 'A05c.json'
if (-not (Test-Path -LiteralPath $reportPath)) {
    Check $checks $false ('缺少含中文的报告文件 ' + $reportPath + '（A05③ 没有跑成）')
} else {
    $bytes = [System.IO.File]::ReadAllBytes($reportPath)
    $bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    Check $checks $bom '报告的前三个字节不是 EF BB BF（不是 UTF-8 带 BOM）'
    # 用 PowerShell 自己的读法（等价于「用记事本打开」）读回来，再和期望的中文逐字符比。
    $readBack = Get-Content -LiteralPath $reportPath -Raw
    Check $checks (-not [string]::IsNullOrEmpty($readBack)) 'Get-Content 读不回报告内容'
    Check $checks ($readBack.IndexOf([char]0xFFFD) -lt 0) '用 Get-Content 读回来的报告里有 U+FFFD（中文没能在「记事本路径」上活下来）'
    $parsed = $null
    try { $parsed = ConvertFrom-Json $readBack } catch { }
    Check $checks ($null -ne $parsed) '报告不是合法 JSON（ConvertFrom-Json 失败）'
    if ($null -ne $parsed) {
        $one = @($parsed)[0]
        Check $checks (([string]$one.path).Contains('损坏的压缩包.zip')) ('从报告读回来的中文路径不对：' + $one.path)
        Check $checks (([string]$one.message).IndexOf('无法识别') -ge 0 -or ([string]$one.message).IndexOf('损坏') -ge 0) ('从报告读回来的中文判词不对：' + $one.message)
    }
}
Complete-Row ([pscustomobject]@{ Id = 'A06c'; Missing = $null }) 'A06c 报告 UTF-8+BOM/中文往返' $checks `
    '报告前 3 字节 EF BB BF；Get-Content 读回无 U+FFFD；ConvertFrom-Json 成功；中文路径与判词逐字符一致' | Out-Null

# ==================================================================
# A06d 许可声明随包分发（内嵌 7-Zip 的再分发义务）
# ==================================================================
$checks = New-Checks
$noticesPath = Join-Path $script:RepoRoot 'dist\THIRD-PARTY-NOTICES.txt'
Check $checks (Test-Path -LiteralPath $noticesPath) ('缺少随包分发的 ' + $noticesPath + '：内嵌 7-Zip 的二进制再分发必须随附许可信息')
if (Test-Path -LiteralPath $noticesPath) {
    $noticesBytes = [System.IO.File]::ReadAllBytes($noticesPath)
    Check $checks ($noticesBytes.Length -ge 3 -and $noticesBytes[0] -eq 0xEF -and $noticesBytes[1] -eq 0xBB -and $noticesBytes[2] -eq 0xBF) '许可声明不是 UTF-8 带 BOM（记事本打开会乱）'
    $text = [System.Text.Encoding]::UTF8.GetString($noticesBytes)
    # 逐条钉住**必须出现**的内容（缺任何一条都是许可不合规，不能只靠「文件存在」）。
    Check $checks ($text.Contains('This library is free software')) '缺 GNU LGPL 正文要点段落'
    Check $checks ($text.Contains('version 2.1 of the License, or (at your option) any later version')) '缺 LGPL 2.1 版本条款'
    Check $checks ($text.Contains('Text of the "BSD 3-clause License"')) '缺 BSD 3-clause 声明'
    Check $checks ($text.Contains('LZFSE')) 'BSD 3-clause 段落没有点名 LZFSE/ZSTD 代码'
    Check $checks ($text.Contains('Text of the "BSD 2-clause License"')) '缺 BSD 2-clause 声明'
    Check $checks ($text.Contains('XXH64')) 'BSD 2-clause 段落没有点名 XXH64 代码'
    Check $checks ($text.Contains('The unRAR sources cannot be used to re-create the RAR compression algorithm,')) '缺 unRAR 限制段落原文'
    Check $checks ($text.Contains('not be used to develop a RAR (WinRAR) compatible archiver')) 'unRAR 限制段落不完整'
    Check $checks ($text.Contains('https://www.7-zip.org')) '缺 7-Zip 源码获取地址'
    Check $checks ($text.Contains('RERAR_ENGINE_ROOT') -and $text.Contains('RERAR_ENGINE_LOCAL')) '没有记录两个用户可达的引擎环境变量'
    Check $checks ($text.Contains('刻意')) '没有说明旧 buildid 目录刻意不回收'
}
Complete-Row ([pscustomobject]@{ Id = 'A06d'; Missing = $null }) 'A06d 许可声明随包分发' $checks `
    'dist\THIRD-PARTY-NOTICES.txt 存在、UTF-8 带 BOM，含 LGPL 要点 / BSD 3-clause / BSD 2-clause / unRAR 限制段落 / 7-zip.org 源码链接 / 两个引擎环境变量与旧 buildid 说明' | Out-Null

# ==================================================================
# A07 引擎缺失/过旧：明确中文 + 非 0 退出，不崩溃、不静默成功
# ==================================================================
$row = New-Row 'A07' @((Fixture 'f01-nested\outer.zip'))
$checks = New-Checks
if (-not $row.Missing) {
    # 把释放根指到一个**被同名文件挡住**的路径：内嵌副本既释放不出来（Directory.CreateDirectory 失败），
    # 本机探测又被 RERAR_ENGINE_LOCAL=off 关掉 —— 于是「引擎真的拿不到」这件事被确定地构造出来。
    $blockerFile = Join-Path $script:Scratch 'blocked'
    Write-TextFile $blockerFile 'this is a file, not a directory'
    $blockedRoot = Join-Path $blockerFile 'bin'
    $target = Join-Path $row.Work 'outer.zip'
    Invoke-Row $row @($target) -TimeoutSec 60 -Overrides @{ 'RERAR_ENGINE_LOCAL' = 'off'; 'RERAR_ENGINE_ROOT' = $blockedRoot } | Out-Null
    $combined = $row.Cli.StdOut + $row.Cli.StdErr
    Check $checks ($row.Cli.ExitCode -eq 2) ('退出码 ' + $row.Cli.ExitCode + '，期望 2（致命：什么都没跑成）')
    Check $checks ($combined.Contains('解压引擎不可用')) ('没有明确的中文致命判词「解压引擎不可用」：' + $combined.Trim())
    Check $checks ($combined.Contains($blockedRoot)) ('判词里没有带上真正失败的路径 ' + $blockedRoot)
    Check $checks (-not $combined.Contains('Unhandled Exception')) 'CLI 崩溃了（输出里有 Unhandled Exception）'
    Check $checks (-not $combined.Contains('未处理的异常')) 'CLI 崩溃了（输出里有「未处理的异常」）'
    Check $checks (-not (Test-Path -LiteralPath (Join-Path $row.Work 'outer'))) '引擎不可用时居然还写出了产物（静默成功）'
    Check $checks (Test-Path -LiteralPath $target) '引擎不可用时原包被动过'
    Check $checks (-not (Test-Path -LiteralPath $row.JsonPath)) '引擎在解压前就不可用，机器可读报告却还是写出来了（致命档不该产出报告）'
    # 这一行**没有**报告可查（致命发生在解压之前），所以报告相关的不变式不适用。
    Check-CommonInvariants $row $checks $true $true $false
}
Complete-Row $row 'A07 引擎缺失/过旧' $checks '引擎确实拿不到时：退出码 2 + 明确中文 + 带上失败路径；不崩溃、不写盘、原包保留、不产出报告' | Out-Null

# ==================================================================
# A02 零数据丢失不变量（汇总所有失败场景）
# ==================================================================
# 放在最后：它汇总的是**全部**行（含 A04/A05*/A07）的逐行断言结果，所以必须在它们之后结算。
$script:DataLossChecked = 0
foreach ($result in $script:Results) { if ($result.Id -match '^F\d\d$') { $script:DataLossChecked++ } }
$checks = New-Checks
Check $checks ($script:DataLossViolations.Count -eq 0) ('有失败场景动了原包：' + ($script:DataLossViolations -join '；'))
Check $checks ($script:DataLossChecked -ge 15) ('只检查了 ' + $script:DataLossChecked + ' 行的原包保留，覆盖不足（期望 ≥15 行逐行断言）')
Complete-Row ([pscustomobject]@{ Id = 'A02'; Missing = $null }) 'A02 零数据丢失不变量' $checks `
    ($script:DataLossChecked.ToString() + ' 行逐行断言：运行前存在的每个原包与输入文件在运行后仍逐字节存在（含 F02/F03/F04/F07/F08/F09/F10/F12/F13/F14/F15/F16 全部失败场景）') | Out-Null

# ==================================================================
# A03 零越界写入不变量（放在最后：它汇总全部行的快照比对）
# ==================================================================
$checks = New-Checks
Check $checks ($script:EscapeDiffs.Count -eq 0) ('有越界写入：' + ($script:EscapeDiffs -join '；'))
Check $checks ($script:GuardComparisons -ge 20) ('只做了 ' + $script:GuardComparisons + ' 次快照比对，覆盖不足（期望 ≥20 次）')
$localAppDataAfter = Test-Path -LiteralPath $script:LocalAppDataRerar
Check $checks ($localAppDataAfter -eq $script:LocalAppDataBefore) ('真实应用数据根 %LOCALAPPDATA%\Rerar 的存在性变了（' + $script:LocalAppDataBefore + ' -> ' + $localAppDataAfter + '）：本次验收绝不该碰它')
Complete-Row ([pscustomobject]@{ Id = 'A03'; Missing = $null }) 'A03 零越界写入不变量' $checks `
    ($script:GuardComparisons.ToString() + ' 次「目标根父目录 + 仓库根 + tests\ + 夹具矩阵 + dist\」快照比对，全部无差异；%LOCALAPPDATA%\Rerar 存在性不变') | Out-Null

# ==================================================================
# 表格与退出码
# ==================================================================

$passed = @($script:Results | Where-Object { $_.Ok }).Count
$failed = $script:Results.Count - $passed

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('编号   用例                              结果   说明')
$lines.Add('------ ---------------------------------- ------ ------------------------------------------------------------')
foreach ($result in $script:Results) {
    $state = 'PASS'
    if (-not $result.Ok) { $state = 'FAIL' }
    $lines.Add(($result.Id.PadRight(6) + ' ' + $result.Name.PadRight(34) + ' ' + $state + '   ' + $result.Detail))
}
$lines.Add('')
$lines.Add('合计 ' + $script:Results.Count + ' 行：PASS ' + $passed + '，FAIL ' + $failed)
$lines.Add('临时根（日志/释放/报告）：' + $script:Scratch)
foreach ($line in $lines) { Note-Host $line }

$logPath = Join-Path $script:RunDir 'acceptance-report.txt'
Write-TextFile $logPath (($lines -join "`r`n") + "`r`n")
Note-Host ('本次验收表已写入：' + $logPath)

if ($failed -gt 0) { exit 1 }
exit 0
