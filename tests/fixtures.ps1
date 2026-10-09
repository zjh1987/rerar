# Rerar 验收脚本（Task 15）之一：构造规格 §9.1 的 fixture 矩阵。
#
# 用法：powershell -NoProfile -File tests\fixtures.ps1
#   退出 0 = 21 个 fixture 全部构造完成，且**每一个的形状自检**都通过；
#   退出 1 = 任一 fixture 构造或自检失败（中文说明，绝不静默跳过）。
#
# 【为什么必须有形状自检】本项目有一条已裁定的教训：Task 2 的 Sniffer 单测是**照着实现的常量**写的，
# 常量写错也会绿（同义反复）。所以本脚本的立场是：fixture 必须由**真的 7-Zip / .NET zip 库**产生，
# 而且在交给验收脚本之前，先自己把「它真的是我以为的那个形状吗」逐条验一遍（测量，不靠命名）。
# 例如 zip 炸弹必须**实测**解压比 > 120 倍，缺中间卷必须**实测** 7-Zip 真的打不开它。
#
# 【构造事实（本项目实测，写下来免得后人重复踩）】
#   1) 7-Zip **不能创建 rar**（那是 WinRAR 的专利实现）。所以分卷 fixture 只用 7-Zip 能产的形状：
#      `7z a -v16k vol.7z payload` → vol.7z.001 / .002 / …（`-tzip -v16k` 则产 vol.zip.001…）。
#      绝不去造 `.part1.rar` / `.z01`（造不出来，硬造只会得到一个被当成缺卷的假 fixture）。
#   2) 用 `7z a` **不带** `-p` 产出的就是明文包（本机实测 `Encrypted = -`）。项目在 Task 10 之前
#      测到的「a 也加密」是 SevenZipRunner（不变式 I5：每次调用都必须带 -p）造成的，而本脚本走的是
#      **独立的 7z 进程**，不经过那条产品路径 —— 但仍必须**实测**每个包是否加密（见 FixtureAes/ZipCrypto）。
#   3) 一个字节都不自包含的 Quine（归档含它自己的字节副本）在数学上不存在：
#      设归档总长 S、其中该条目的压缩后长度 L，则 S = H + L（H = 头部/中央目录开销 > 0）且副本要求
#      L ≥ S ⇒ S ≥ S + H，矛盾。所以 fixture 11 造的是**最近可实现的形状**：一层套一层、每一层的
#      归档文件名都叫 quine.zip（命名自指），并断言真正要钉的性质 —— 不死循环、去重、触顶显式列出。
#   4) tar 软链穿越用 GNU tar + **二进制补丁法**（本会话已验证）：MSYS tar 会把软链目标写成它自己的
#      `/e/...` 绝对形式，而穿越测试要的是**相对**目标（`..\..\escape-target`），所以打完之后把
#      linkname 字段原地改成相对路径并重算校验和 —— 再让 tar 自己读回来验证，才算改对了。
#   5) MSYS tar **拒绝 `C:\` 形式的绝对路径**（它把 `C:` 当成远程主机），所以一律用**相对路径 + 工作目录**。
#   6) 7-Zip 26.01 **只能解不能造** Compound（OLE2/CFB）形状：`7z i` 的格式表里 Compound 那一行
#      没有创建标志，实测 `7z a -tCompound out.doc x` 报不支持的格式。所以 fixture 19 的 CFB
#      只能**手搓字节**（见该节的说明：第 1 条「绝不硬造」的规矩针对的是**分卷语义**，
#      而这一份的身份就是那 8 个签名字节，且下面让 7-Zip 自己把它打开来**测量**这一点）。
#
# 【安全边界】本脚本只写 tests\_fixtures\ 下的东西，不删任何别处的文件，不提权，不碰注册表 / 回收站。

$ErrorActionPreference = 'Stop'

# 只用 .NET 自带的 BCL（无 NuGet、无第三方库）。System.IO.Compression 是**测试用**的 zip 写/读库，
# 与 build.ps1 给测试目标加的那条 /reference:System.IO.Compression.dll 是同一个程序集。
Add-Type -AssemblyName System.IO.Compression | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

$script:Root = Split-Path -Parent $PSScriptRoot
$script:FixDir = Join-Path $PSScriptRoot '_fixtures'
$script:Rows = New-Object System.Collections.Generic.List[object]

# ------------------------------------------------------------------
# 基础设施
# ------------------------------------------------------------------

function Fail([string]$message) {
    Write-Host ("FAIL: " + $message)
    exit 1
}

function Note([string]$id, [string]$name, [string]$detail) {
    $row = New-Object psobject
    $row | Add-Member -MemberType NoteProperty -Name Id -Value $id
    $row | Add-Member -MemberType NoteProperty -Name Name -Value $name
    $row | Add-Member -MemberType NoteProperty -Name Detail -Value $detail
    $script:Rows.Add($row)
}

# 断言助手：形状自检失败一律当场停（带 fixture 编号），绝不带着一个「大概是对的」fixture 往下走。
function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { Fail $message }
}

function Ensure-Dir([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { New-Item -ItemType Directory -Force -Path $path | Out-Null }
}

function Remove-Dir([string]$path) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}

function Write-TextFile([string]$path, [string]$text) {
    Ensure-Dir (Split-Path -Parent $path)
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

# 找一个可用的 7z.exe（构建期/夹具期工具，与运行期的 EngineLocator 无关）。
# 找不到就**大声失败**：一堆 fixture 的形状只能由真 7-Zip 产生，绝不能悄悄退化成「用别的东西凑一个」。
# F20 用的 csc.exe：与 build\build.ps1 是同一个 in-box 编译器。
#
# 【为什么 F20 需要一个真编译器】手搓的最小 PE（MZ + PE\0\0 + COFF + 可选头 + 节表）本机 7-Zip 26.01
# **打不开**（退出码 2，`Cannot open the file as archive`）—— 那样的夹具走不到类型门控，这条数据丢失
# 回归会退化成假通过。真编译器产出的 PE 7-Zip 报 `Type = PE` 并列出节。csc 是本项目**已经硬依赖**
# 的构建工具（同一个固定路径编 dist\），所以这里不是新引入的第三方依赖；找不到就大声失败。
function Find-Csc {
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($env:windir) {
        $candidates.Add((Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'))
        $candidates.Add((Join-Path $env:windir 'Microsoft.NET\Framework\v4.0.30319\csc.exe'))
    }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    Fail ("找不到 csc.exe（" + ($candidates -join '；') + "）：F20 需要一个**真编译器**产出的真 PE —— " +
          "手搓的最小 PE 实测 7-Zip 打不开（退出码 2），那样的夹具走不到类型门控，回归会退化成假通过。")
    return $null
}

function Find-SevenZip {
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($env:RERAR_7Z_DIR) { $candidates.Add((Join-Path $env:RERAR_7Z_DIR '7z.exe')) }
    if ($env:ProgramW6432) { $candidates.Add((Join-Path $env:ProgramW6432 '7-Zip\7z.exe')) }
    if ($env:ProgramFiles) { $candidates.Add((Join-Path $env:ProgramFiles '7-Zip\7z.exe')) }
    $x86 = ${env:ProgramFiles(x86)}
    if ($x86) { $candidates.Add((Join-Path $x86 '7-Zip\7z.exe')) }
    $candidates.Add((Join-Path $script:Root 'dist\obj\7z.exe'))

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            $probe = Invoke-Native $candidate @('i') $null
            if ($probe.Code -eq 0 -and $probe.Out -match '7-Zip \d+\.\d+') { return $candidate }
        }
    }
    Fail ("找不到可用的 7z.exe（试过 RERAR_7Z_DIR、Program Files 两处、dist\obj）。" +
          "本脚本的多数 fixture 只能由真 7-Zip 产生（规格 §9.1 的形状要求），" +
          "请安装 7-Zip 25.00 或更高版本，或把 RERAR_7Z_DIR 指向一个解压出来的 7-Zip 目录。")
    return $null
}

# 起一个原生命令并捕获两流。**PS 5.1 的坑**：`$ErrorActionPreference = 'Stop'`（本脚本必须用它，
# 免得一个拼写错误被静默吞掉）下，原生命令写到 stderr 的**每一行**都会被当成 NativeCommandError
# 抛成终止错误 —— 而 7z / tar 把大量正常进展（进度、警告、清单页眉）写在 stderr 上。所以这些调用
# 一律在本函数内临时降级为 Continue：失败与否只看**退出码**，不看它有没有往 stderr 写字。
function Invoke-Native([string]$exe, [string[]]$arguments, [string]$workingDirectory) {
    $previous = $ErrorActionPreference
    # SilentlyContinue 而不是 Continue：`native 2>&1` 在 PS 5.1 里会把 stderr 的每一行**同时**做成
    # 一条 ErrorRecord 写进错误流（我们已经把它并进 $out 了，再来一遍只是噪音）。
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

# 跑一次 7z。返回 @{ Code = <退出码>; Out = <两流合并文本> }。**不抛异常**：
# 有些断言本身就要「这一步必须失败」（例如缺卷的包必须打不开）。
function Invoke-SevenZip([string[]]$arguments, [string]$workingDirectory) {
    return Invoke-Native $script:SevenZip $arguments $workingDirectory
}

# `7z l -slt -ba` 的条目记录：-ba 去掉头部横幅段，于是每一段都以 `Path = ` 开头，正好一条一个条目。
# 返回 @{ Path; Folder(bool); Size(long); Encrypted(bool); Method(string) } 的数组。
function Get-SevenZipEntries([string]$archive) {
    $run = Invoke-SevenZip @('l', '-slt', '-ba', $archive)
    if ($run.Code -ne 0) { return $null }

    $entries = New-Object System.Collections.Generic.List[object]
    $current = $null
    foreach ($line in ($run.Out -split "`r?`n")) {
        if ($line.StartsWith('Path = ')) {
            if ($current) { $entries.Add($current) }
            $current = New-Object psobject
            $current | Add-Member -MemberType NoteProperty -Name Path -Value $line.Substring(7)
            $current | Add-Member -MemberType NoteProperty -Name Folder -Value $false
            $current | Add-Member -MemberType NoteProperty -Name Size -Value ([long]0)
            $current | Add-Member -MemberType NoteProperty -Name Encrypted -Value $false
            $current | Add-Member -MemberType NoteProperty -Name Method -Value ''
            continue
        }
        if (-not $current) { continue }
        if ($line -eq 'Folder = +') { $current.Folder = $true }
        elseif ($line.StartsWith('Size = ')) {
            $parsed = [long]0
            if ([long]::TryParse($line.Substring(7), [ref]$parsed)) { $current.Size = $parsed }
        }
        elseif ($line -eq 'Encrypted = +') { $current.Encrypted = $true }
        elseif ($line.StartsWith('Method = ')) { $current.Method = $line.Substring(9) }
    }
    if ($current) { $entries.Add($current) }
    # **逗号很重要**：不加它，PowerShell 会把 List 拆开输出，只剩 1 个条目时调用方拿到的是一个
    # 裸 PSObject —— 而裸 PSObject 没有 .Count（实测 `(New-Object psobject).Count` 是空的，
    # PSv3 的「标量也有 Count=1」不适用于它），于是 `$entries.Count -eq 1` 会假失败。
    return ,$entries
}

# .NET 的 zip 写库（System.IO.Compression）：只在 7-Zip **做不到**的形状上用 ——
# 大小写重名条目（Windows 文件系统上放不下两个只差大小写的文件）、20 层长路径（磁盘上放不下）、
# 自指命名的嵌套链。其余 fixture 一律用真 7-Zip 产。
function New-ZipBytes($entries) {
    $memory = New-Object System.IO.MemoryStream
    $zip = New-Object System.IO.Compression.ZipArchive($memory, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($entry in $entries) {
            $item = $zip.CreateEntry([string]$entry.Name, [System.IO.Compression.CompressionLevel]::Optimal)
            $stream = $item.Open()
            try { $stream.Write($entry.Bytes, 0, $entry.Bytes.Length) } finally { $stream.Dispose() }
        }
    } finally { $zip.Dispose() }
    $bytes = $memory.ToArray()
    $memory.Dispose()
    return $bytes
}

# 一个待写入的条目。$bytes 省略时用 $text 的 UTF-8 字节。
function New-ZipEntry([string]$name, [string]$text) {
    return New-ZipEntryBytes $name ([System.Text.Encoding]::UTF8.GetBytes($text))
}

function New-ZipEntryBytes([string]$name, [byte[]]$bytes) {
    $entry = New-Object psobject
    $entry | Add-Member -MemberType NoteProperty -Name Name -Value $name
    $entry | Add-Member -MemberType NoteProperty -Name Bytes -Value $bytes
    return $entry
}

function Get-ZipEntryNames([string]$zipPath) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $names = New-Object System.Collections.Generic.List[string]
        foreach ($entry in $zip.Entries) { $names.Add($entry.FullName) }
        return $names
    } finally { $zip.Dispose() }
}

function Get-FirstBytes([string]$path, [int]$count) {
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $buffer = New-Object byte[] $count
        $read = $stream.Read($buffer, 0, $count)
        $exact = New-Object byte[] $read
        [Array]::Copy($buffer, $exact, $read)
        return $exact
    } finally { $stream.Dispose() }
}

function Get-Sha256Hex([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# ------------------------------------------------------------------
# 开始：清掉上一轮的 fixture 目录（只清 fNN-* 这些，绝不动别的东西）
# ------------------------------------------------------------------

Ensure-Dir $script:FixDir
Get-ChildItem -LiteralPath $script:FixDir -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^f\d\d-' } |
    ForEach-Object { Remove-Dir $_.FullName }

$script:SevenZip = Find-SevenZip
$sevenZipVersion = ([regex]::Match((Invoke-SevenZip @('i')).Out, '7-Zip \d+\.\d+')).Value
Write-Host ("夹具工具：" + $sevenZipVersion + "：" + $script:SevenZip)

function New-FixtureDir([string]$name) {
    $path = Join-Path $script:FixDir $name
    Ensure-Dir $path
    return $path
}

# ==================================================================
# 1) 正常嵌套：outer.zip → inner.zip → hello.txt（规格 §9.1 行 1）
# ==================================================================
$f01 = New-FixtureDir 'f01-nested'
$build = Join-Path $f01 '.build'
Ensure-Dir $build
Write-TextFile (Join-Path $build 'hello.txt') "hello rerar`r`n"
$run = Invoke-SevenZip @('a', '-tzip', '-mx=9', (Join-Path $build 'inner.zip'), 'hello.txt') $build
Assert-True ($run.Code -eq 0) ("F01: 7z 造 inner.zip 失败（退出码 " + $run.Code + "）：" + $run.Out)
Push-Location $build
try {
    $run = Invoke-SevenZip @('a', '-tzip', '-mx=9', (Join-Path $f01 'outer.zip'), 'inner.zip')
} finally { Pop-Location }
Assert-True ($run.Code -eq 0) ("F01: 7z 造 outer.zip 失败（退出码 " + $run.Code + "）：" + $run.Out)
$outer = Get-SevenZipEntries (Join-Path $f01 'outer.zip')
Assert-True ($null -ne $outer -and $outer.Count -eq 1) 'F01: 自检失败：outer.zip 的条目数不是 1'
Assert-True ($outer[0].Path -eq 'inner.zip') ("F01: 自检失败：outer.zip 里不是 inner.zip，而是 " + $outer[0].Path)
# 注意顺序：inner.zip 就住在 .build 里，必须**先**验它再删 .build（删完再验会得到一个空的 $null）。
$inner = Get-SevenZipEntries (Join-Path $build 'inner.zip')
Assert-True ($null -ne $inner -and $inner.Count -eq 1 -and $inner[0].Path -eq 'hello.txt') 'F01: 自检失败：inner.zip 里不是单个 hello.txt'
Remove-Dir $build
Assert-True ((Get-Sha256Hex (Join-Path $f01 'outer.zip')) -ne '') 'F01: 自检失败：outer.zip 不可读'
Note 'F01' '正常嵌套' '真 7-Zip 造的两层 zip；自检：outer 恰含 1 条 inner.zip，inner 恰含 1 条 hello.txt'

# ==================================================================
# 2) 损坏包：随机字节、后缀 .zip（规格 §9.1 行 2）
# ==================================================================
$f02 = New-FixtureDir 'f02-damaged'
$broken = Join-Path $f02 'broken.zip'
$random = New-Object byte[] 8192
(New-Object System.Random 20261003).NextBytes($random)
# 前 4 字节刻意避开所有已知签名（随机 4 字节撞上 PK\x03\x04 的概率是 1/2^32，但「大概率」不是验收标准）：
# 这里明确写成 DE AD BE EF，于是「它是损坏包而不是伪装成 zip 的别的东西」是确定的。
$random[0] = 0xDE; $random[1] = 0xAD; $random[2] = 0xBE; $random[3] = 0xEF
[System.IO.File]::WriteAllBytes($broken, $random)
$head = Get-FirstBytes $broken 4
Assert-True ($head[0] -eq 0xDE -and $head[1] -eq 0xAD -and $head[2] -eq 0xBE -and $head[3] -eq 0xEF) 'F02: 自检失败：头部字节不符'
Assert-True ((Get-Item -LiteralPath $broken).Length -eq 8192) 'F02: 自检失败：长度不是 8192'
$tailBytes = [System.IO.File]::ReadAllBytes($broken)
$hasEocd = $false
for ($i = 0; $i + 4 -le $tailBytes.Length; $i++) {
    if ($tailBytes[$i] -eq 0x50 -and $tailBytes[$i + 1] -eq 0x4B) { $hasEocd = $true; break }
}
Assert-True (-not $hasEocd) 'F02: 自检失败：随机字节里出现了 PK，它就不再是「无法识别的损坏包」'
Note 'F02' '损坏包' '8192 字节随机内容、头部固定 DE AD BE EF（避开所有签名）、尾部无 PK；自检：长度/头部/无 PK'

# ==================================================================
# 3) AES-256 加密 zip（规格 §9.1 行 3）
# ==================================================================
$f03 = New-FixtureDir 'f03-aes'
$build = Join-Path $f03 '.build'
Ensure-Dir $build
Write-TextFile (Join-Path $build 'secret.txt') ("AES-256 payload`r`n" + ('A' * 4096))
$run = Invoke-SevenZip @('a', '-tzip', '-mx=9', '-mem=AES256', '-pSECRET', (Join-Path $f03 'aes256.zip'), 'secret.txt') $build
Assert-True ($run.Code -eq 0) ("F03: 7z 造 AES zip 失败（退出码 " + $run.Code + "）：" + $run.Out)
Remove-Dir $build
$entries = Get-SevenZipEntries (Join-Path $f03 'aes256.zip')
Assert-True ($null -ne $entries -and $entries.Count -eq 1) 'F03: 自检失败：AES zip 的条目数不是 1'
Assert-True ($entries[0].Encrypted) 'F03: 自检失败：`7z l -slt` 没有报 Encrypted = +，这不是加密包'
Assert-True ($entries[0].Method -like 'AES-256*') ("F03: 自检失败：Method 不是 AES-256，而是 " + $entries[0].Method)
Note 'F03' 'AES-256 加密 zip' '真 7-Zip `-mem=AES256 -pSECRET`；自检：Encrypted = + 且 Method = AES-256 Deflate'

# ==================================================================
# 4) ZipCrypto 加密 zip（规格 §9.1 行 4）
# ==================================================================
$f04 = New-FixtureDir 'f04-zipcrypto'
$build = Join-Path $f04 '.build'
Ensure-Dir $build
Write-TextFile (Join-Path $build 'secret.txt') ("ZipCrypto payload`r`n" + ('B' * 4096))
$run = Invoke-SevenZip @('a', '-tzip', '-mx=9', '-mem=ZipCrypto', '-pSECRET', (Join-Path $f04 'zipcrypto.zip'), 'secret.txt') $build
Assert-True ($run.Code -eq 0) ("F04: 7z 造 ZipCrypto zip 失败（退出码 " + $run.Code + "）：" + $run.Out)
Remove-Dir $build
$entries = Get-SevenZipEntries (Join-Path $f04 'zipcrypto.zip')
Assert-True ($null -ne $entries -and $entries.Count -eq 1) 'F04: 自检失败：ZipCrypto zip 的条目数不是 1'
Assert-True ($entries[0].Encrypted) 'F04: 自检失败：`7z l -slt` 没有报 Encrypted = +'
Assert-True ($entries[0].Method -like 'ZipCrypto*') ("F04: 自检失败：Method 不是 ZipCrypto，而是 " + $entries[0].Method)
Note 'F04' 'ZipCrypto 加密 zip' '真 7-Zip `-mem=ZipCrypto -pSECRET`；自检：Encrypted = + 且 Method = ZipCrypto Deflate'

# ==================================================================
# 5) 伪装后缀：真 zip 叫 .jpg，与一个叫 .zip删 的（规格 §9.1 行 5）
# ==================================================================
$f05 = New-FixtureDir 'f05-camouflage'
$build = Join-Path $f05 '.build'
Ensure-Dir $build
Write-TextFile (Join-Path $build 'hidden.txt') "camouflaged payload`r`n"
$run = Invoke-SevenZip @('a', '-tzip', '-mx=9', (Join-Path $build 'plain.zip'), 'hidden.txt') $build
Assert-True ($run.Code -eq 0) ("F05: 7z 造伪装载荷失败（退出码 " + $run.Code + "）：" + $run.Out)
Copy-Item -LiteralPath (Join-Path $build 'plain.zip') -Destination (Join-Path $f05 'photo.jpg') -Force
Copy-Item -LiteralPath (Join-Path $build 'plain.zip') -Destination (Join-Path $f05 'data.zip删') -Force
Remove-Dir $build
foreach ($name in @('photo.jpg', 'data.zip删')) {
    $path = Join-Path $f05 $name
    $head = Get-FirstBytes $path 4
    Assert-True ($head[0] -eq 0x50 -and $head[1] -eq 0x4B -and $head[2] -eq 0x03 -and $head[3] -eq 0x04) ("F05: 自检失败：" + $name + " 不是 PK\x03\x04 开头的真 zip")
    $entries = Get-SevenZipEntries $path
    Assert-True ($null -ne $entries -and $entries.Count -eq 1 -and $entries[0].Path -eq 'hidden.txt') ("F05: 自检失败：" + $name + " 的内容不是单个 hidden.txt")
}
Note 'F05' '伪装后缀' '同一个真 zip 复制成 photo.jpg 与 data.zip删；自检：两者头部均为 PK\x03\x04 且内部条目是 hidden.txt'

# ==================================================================
# 6) 分卷正常集（7-Zip 能产的形状：vol.7z.001/.002/…）（规格 §9.1 行 6）
# ==================================================================
$f06 = New-FixtureDir 'f06-volumes-ok'
$build = Join-Path $f06 '.build'
Ensure-Dir $build
$payload = New-Object byte[] 50000
(New-Object System.Random 6001).NextBytes($payload)
[System.IO.File]::WriteAllBytes((Join-Path $build 'payload.bin'), $payload)
Push-Location $build
try {
    $run = Invoke-SevenZip @('a', '-mx=0', '-v16k', (Join-Path $f06 'vol.7z'), 'payload.bin')
} finally { Pop-Location }
Assert-True ($run.Code -eq 0) ("F06: 7z 造分卷失败（退出码 " + $run.Code + "）：" + $run.Out)
Remove-Dir $build
$volumes = @(Get-ChildItem -LiteralPath $f06 -File | Where-Object { $_.Name -like 'vol.7z.*' } | Sort-Object Name)
Assert-True ($volumes.Count -ge 3) ("F06: 自检失败：分卷数只有 " + $volumes.Count + "，分卷集至少要有 3 卷")
Assert-True ($volumes[0].Name -eq 'vol.7z.001') ("F06: 自检失败：第一卷不是 vol.7z.001，而是 " + $volumes[0].Name)
$head = Get-FirstBytes $volumes[0].FullName 6
Assert-True ($head[0] -eq 0x37 -and $head[1] -eq 0x7A -and $head[2] -eq 0xBC -and $head[3] -eq 0xAF -and $head[4] -eq 0x27 -and $head[5] -eq 0x1C) 'F06: 自检失败：第一卷不是 7z 签名'
$listing = Invoke-SevenZip @('l', '-ba', $volumes[0].FullName)
Assert-True ($listing.Code -eq 0) ("F06: 自检失败：完整的卷集却打不开（退出码 " + $listing.Code + "）")
Assert-True ($listing.Out -match 'payload\.bin') 'F06: 自检失败：卷集里看不到 payload.bin'
Note 'F06' '分卷正常集' ("真 7-Zip a -v16k 切出 " + $volumes.Count + " 卷 vol.7z.001…；自检：.001 是 7z 签名、整集可列出 payload.bin")

# ==================================================================
# 7) 分卷缺中间卷（规格 §9.1 行 7）
# ==================================================================
$f07 = New-FixtureDir 'f07-volumes-missing'
$build = Join-Path $f07 '.build'
Ensure-Dir $build
[System.IO.File]::WriteAllBytes((Join-Path $build 'payload.bin'), $payload)
Push-Location $build
try {
    $run = Invoke-SevenZip @('a', '-mx=0', '-v16k', (Join-Path $f07 'vol.7z'), 'payload.bin')
} finally { Pop-Location }
Assert-True ($run.Code -eq 0) ("F07: 7z 造分卷失败（退出码 " + $run.Code + "）：" + $run.Out)
Remove-Dir $build
$volumes = @(Get-ChildItem -LiteralPath $f07 -File | Where-Object { $_.Name -like 'vol.7z.*' } | Sort-Object Name)
Assert-True ($volumes.Count -ge 3) ("F07: 自检失败：分卷数只有 " + $volumes.Count + "，删掉中间卷后必须还剩 ≥2 卷")
$middle = Join-Path $f07 'vol.7z.002'
Assert-True (Test-Path -LiteralPath $middle) 'F07: 自检失败：没有 vol.7z.002 可以删'
Remove-Item -LiteralPath $middle -Force
Assert-True (-not (Test-Path -LiteralPath $middle)) 'F07: 自检失败：vol.7z.002 没删掉'
$listing = Invoke-SevenZip @('l', '-ba', (Join-Path $f07 'vol.7z.001'))
Assert-True ($listing.Code -ne 0) 'F07: 自检失败：缺了中间卷 7-Zip 竟然还能列出内容 —— 那这个 fixture 证明不了「缺卷」'
$remaining = @(Get-ChildItem -LiteralPath $f07 -File | Where-Object { $_.Name -like 'vol.7z.*' })
Assert-True ($remaining.Count -ge 2) ("F07: 自检失败：删完只剩 " + $remaining.Count + " 卷，7-Zip 会把它当独立文件而不是缺卷集")
Note 'F07' '分卷缺中间卷' ("同一形状删掉 vol.7z.002，还剩 " + $remaining.Count + " 卷；自检：7z l vol.7z.001 退出码 " + $listing.Code + "（真的打不开）")

# ==================================================================
# 8) OOXML 陷阱：真 .docx 形状（[Content_Types].xml）（规格 §9.1 行 8）
# ==================================================================
$f08 = New-FixtureDir 'f08-ooxml'
$build = Join-Path $f08 '.build'
Ensure-Dir (Join-Path $build '_rels')
Ensure-Dir (Join-Path $build 'word')
Write-TextFile (Join-Path $build '[Content_Types].xml') '<?xml version="1.0" encoding="UTF-8"?><Types/>'
Write-TextFile (Join-Path $build '_rels\.rels') '<?xml version="1.0" encoding="UTF-8"?><Relationships/>'
Write-TextFile (Join-Path $build 'word\document.xml') '<?xml version="1.0" encoding="UTF-8"?><document/>'
Push-Location $build
try {
    $run = Invoke-SevenZip @('a', '-tzip', '-mx=9', (Join-Path $f08 'trap.docx'), '[Content_Types].xml', '_rels', 'word')
} finally { Pop-Location }
Assert-True ($run.Code -eq 0) ("F08: 7z 造 docx 失败（退出码 " + $run.Code + "）：" + $run.Out)
Remove-Dir $build
$entries = Get-SevenZipEntries (Join-Path $f08 'trap.docx')
Assert-True ($null -ne $entries) 'F08: 自检失败：trap.docx 读不出条目'
$names = @($entries | ForEach-Object { $_.Path })
Assert-True (($names | Where-Object { $_ -like '*Content_Types*' }).Count -ge 1) 'F08: 自检失败：清单里没有 [Content_Types].xml'
Assert-True (($names | Where-Object { $_ -like '*_rels*' }).Count -ge 1) 'F08: 自检失败：清单里没有 _rels 路径段'
Note 'F08' 'OOXML 陷阱' '真 zip 含 [Content_Types].xml + _rels\.rels + word\document.xml；自检：清单里两个身份特征都在'

# ==================================================================
# 9) APK 陷阱（规格 §9.1 行 9）
# ==================================================================
$f09 = New-FixtureDir 'f09-apk'
$build = Join-Path $f09 '.build'
Ensure-Dir $build
Write-TextFile (Join-Path $build 'AndroidManifest.xml') '<manifest package="com.example.rerar"/>'
[System.IO.File]::WriteAllBytes((Join-Path $build 'classes.dex'), (New-Object byte[] 64))
Write-TextFile (Join-Path $build 'resources.arsc') 'RES'
Push-Location $build
try {
    $run = Invoke-SevenZip @('a', '-tzip', '-mx=9', (Join-Path $f09 'trap.apk'), 'AndroidManifest.xml', 'classes.dex', 'resources.arsc')
} finally { Pop-Location }
Assert-True ($run.Code -eq 0) ("F09: 7z 造 apk 失败（退出码 " + $run.Code + "）：" + $run.Out)
Remove-Dir $build
$entries = Get-SevenZipEntries (Join-Path $f09 'trap.apk')
Assert-True ($null -ne $entries) 'F09: 自检失败：trap.apk 读不出条目'
$names = @($entries | ForEach-Object { $_.Path })
Assert-True (($names | Where-Object { $_ -eq 'AndroidManifest.xml' }).Count -eq 1) 'F09: 自检失败：清单里没有根级 AndroidManifest.xml'
Assert-True (($names | Where-Object { $_ -eq 'classes.dex' }).Count -eq 1) 'F09: 自检失败：清单里没有根级 classes.dex'
Note 'F09' 'APK 陷阱' '真 zip 含 AndroidManifest.xml + classes.dex + resources.arsc；自检：两个身份特征都在根级'

# ==================================================================
# 10) zip 炸弹：高压缩比（规格 §9.1 行 10）
# ==================================================================
$f10 = New-FixtureDir 'f10-bomb'
$build = Join-Path $f10 '.build'
Ensure-Dir $build
# 16 MiB 全零：deflate 之后只剩几十 KB。零填充是「高压缩比」最省事也最真实的形状。
[System.IO.File]::WriteAllBytes((Join-Path $build 'zeros.bin'), (New-Object byte[] (16 * 1024 * 1024)))
Push-Location $build
try {
    $run = Invoke-SevenZip @('a', '-tzip', '-mx=9', (Join-Path $f10 'bomb.zip'), 'zeros.bin')
} finally { Pop-Location }
Assert-True ($run.Code -eq 0) ("F10: 7z 造炸弹失败（退出码 " + $run.Code + "）：" + $run.Out)
Remove-Dir $build
$bomb = Join-Path $f10 'bomb.zip'
$entries = Get-SevenZipEntries $bomb
Assert-True ($null -ne $entries -and $entries.Count -eq 1) 'F10: 自检失败：炸弹包的条目数不是 1'
$bombBytes = (Get-Item -LiteralPath $bomb).Length
$uncompressed = $entries[0].Size
$ratio = $uncompressed / [double]$bombBytes
# 产品侧的上限是 100 倍（Preflight.MaxExpansionRatio），这里要求 fixture 至少 120 倍：留出余量，
# 免得「刚好 101 倍」这种边缘夹具在别的机器上变成不触发。
Assert-True ($ratio -gt 120) ("F10: 自检失败：解压比只有 " + [math]::Round($ratio, 1) + " 倍（" + $uncompressed + " / " + $bombBytes + "），不足以触发 100 倍上限")
Note 'F10' 'zip 炸弹' ("16 MiB 全零 → " + $bombBytes + " 字节的 zip；自检：实测解压比 " + [math]::Round($ratio, 1) + " 倍 > 120（产品上限 100 倍）")

# ==================================================================
# 11) Quine（自指命名链）（规格 §9.1 行 11）
# ==================================================================
$f11 = New-FixtureDir 'f11-quine'
# 由内向外叠 11 层；每一层的归档文件都叫 quine.zip（命名自指），内容只含下层的 sub\quine.zip。
# 说明见文件头第 3 条：字节级自包含的归档在数学上不存在，所以这里钉的是可观察的性质。
$leaf = New-ZipEntry 'leaf.txt' 'leaf'
$bytes = New-ZipBytes @($leaf)
for ($level = 1; $level -le 11; $level++) {
    $bytes = New-ZipBytes @((New-ZipEntryBytes 'sub/quine.zip' $bytes))
}
[System.IO.File]::WriteAllBytes((Join-Path $f11 'quine.zip'), $bytes)
# 自检：从外层逐层剥进去，必须恰好 11 层、每层单条目、且最后一层是 leaf.txt。
$zipPath = Join-Path $f11 'quine.zip'
$depth = 0
while ($true) {
    $names = @(Get-ZipEntryNames $zipPath)
    if ($names.Count -eq 1 -and $names[0] -eq 'leaf.txt') { break }
    Assert-True ($names.Count -eq 1 -and $names[0] -eq 'sub/quine.zip') ("F11: 自检失败：第 " + ($depth + 1) + " 层的条目是 " + ($names -join ','))
    $depth++
    Assert-True ($depth -le 20) 'F11: 自检失败：剥层没有收敛'
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entryStream = $zip.Entries[0].Open()
        try {
            $memory = New-Object System.IO.MemoryStream
            $entryStream.CopyTo($memory)
            $bytes = $memory.ToArray()
            $memory.Dispose()
        } finally { $entryStream.Dispose() }
    } finally { $zip.Dispose() }
    $zipPath = Join-Path $env:TEMP ('rerar-quine-' + [guid]::NewGuid().ToString('N') + '.zip')
    [System.IO.File]::WriteAllBytes($zipPath, $bytes)
}
Remove-Item -LiteralPath $zipPath -Force
Assert-True ($depth -eq 11) ("F11: 自检失败：剥出 " + $depth + " 层嵌套（期望 11，即整链 11 个归档）")
Note 'F11' 'Quine（自指命名链）' ("11 个归档层层相套、每层都叫 quine.zip；自检：逐层剥开恰好 11 层嵌套后到达 leaf.txt")

# ==================================================================
# 12) 深嵌套长路径（20 层 + 长名）（规格 §9.1 行 12）
# ==================================================================
$f12 = New-FixtureDir 'f12-longpath'
$segments = New-Object System.Collections.Generic.List[string]
for ($i = 1; $i -le 20; $i++) {
    $segments.Add(('d{0:D2}_' -f $i) + ('n' * 50))
}
$longName = ($segments -join '/') + '/payload.txt'
$entry = New-Object psobject
$entry | Add-Member -MemberType NoteProperty -Name Name -Value $longName
$entry | Add-Member -MemberType NoteProperty -Name Bytes -Value ([System.Text.Encoding]::UTF8.GetBytes('deep'))
[System.IO.File]::WriteAllBytes((Join-Path $f12 'long.zip'), (New-ZipBytes @($entry)))
Assert-True ($longName.Length -ge 240) ("F12: 自检失败：条目路径只有 " + $longName.Length + " 字符，不足以保证写入路径超过 259")
Assert-True (($longName -split '/').Count -eq 21) 'F12: 自检失败：路径段数不是 20 层 + 1 个文件'
$entries = Get-SevenZipEntries (Join-Path $f12 'long.zip')
Assert-True ($null -ne $entries -and $entries.Count -eq 1) 'F12: 自检失败：long.zip 的条目数不是 1'
Assert-True ($entries[0].Path.Length -ge 240) 'F12: 自检失败：7-Zip 看到的条目路径没有 240 字符'
Note 'F12' '深嵌套长路径' ("单条目 " + $longName.Length + " 字符 / 20 层；自检：7-Zip 也看到 ≥240 字符的路径（加上任何输出根都会 >259）")

# ==================================================================
# 13) tar 软链穿越（GNU tar + 二进制补丁法）（规格 §9.1 行 13）
# ==================================================================
$f13 = New-FixtureDir 'f13-symlink-tar'
$escapeTarget = Join-Path $f13 'escape-target'
Ensure-Dir $escapeTarget
# GNU tar 位置：默认装在 C:\Program Files\Git；Git 在 PATH 上时从 git.exe 反推
# （<git>\cmd\git.exe → <git>\usr\bin\tar.exe，覆盖便携版 / 非标准盘符的安装）。
$tarExe = 'C:\Program Files\Git\usr\bin\tar.exe'
if (-not (Test-Path -LiteralPath $tarExe)) {
    $gitExe = (Get-Command git.exe -ErrorAction SilentlyContinue).Source
    if ($gitExe) {
        $gitRoot = Split-Path -Parent (Split-Path -Parent $gitExe)   # ...\git\cmd\git.exe → ...\git
        $nearbyTar = Join-Path $gitRoot 'usr\bin\tar.exe'
        if (Test-Path -LiteralPath $nearbyTar) { $tarExe = $nearbyTar }
    }
}
if (-not (Test-Path -LiteralPath $tarExe)) {
    Fail ("F13 无法构造：找不到 GNU tar（试过 C:\Program Files\Git\usr\bin\tar.exe 与 PATH 上 git.exe 同源的 usr\bin\tar.exe）。tar 软链 fixture 只能用真 tar 造，" +
          "绝不用手拼字节凑一个「看起来像」的 tar（那证明不了 7-Zip 会怎么处理它）。" +
          "请安装 Git for Windows（含 usr\bin\tar.exe）。")
}
$src = Join-Path $f13 '.build'
Ensure-Dir $src
Write-TextFile (Join-Path $src 'payload.txt') "inside`r`n"
Ensure-Dir (Join-Path $src 'holder')
try {
    # 目标用**绝对路径**建：PowerShell 解析相对 Target 时用的是进程当前目录，而不是链接所在目录
    # （实测它会去找 <仓库根>\holder）。真正会进 tar 的目标字符串随后由二进制补丁改成相对穿越路径。
    New-Item -ItemType SymbolicLink -Path (Join-Path $src 'link-out') -Target (Join-Path $src 'holder') -ErrorAction Stop | Out-Null
} catch {
    Fail ("F13 无法构造：本机不允许创建符号链接（" + $_.Exception.Message + "）。" +
          "Windows 需要「开发者模式」或管理员权限才能建真软链；" +
          "软链 fixture 绝不能用一个普通文件或目录冒充（那证明不了任何事）。")
}
Write-TextFile (Join-Path $src 'link-out\pwned.txt') 'PWNED'
# MSYS tar **拒绝 `C:\` 形式的绝对路径**（会把 `C:` 当成远程主机），所以让它以 $src 为工作目录、
# 用相对路径指定归档（`..\trav.tar` 正好落在 fixture 目录里）。
$tarRun = Invoke-Native $tarExe @('-cf', '..\trav.tar', 'payload.txt', 'link-out', 'link-out/pwned.txt') $src
Assert-True ($tarRun.Code -eq 0) ("F13: GNU tar 打包失败（退出码 " + $tarRun.Code + "）：" + $tarRun.Out)

# --- 二进制补丁：把软链条目的 linkname 改成相对穿越目标，并重算 tar 头部校验和 ---
$archive = Join-Path $f13 'trav.tar'
$raw = [System.IO.File]::ReadAllBytes($archive)
$patched = $false
$offset = 0
while ($offset + 512 -le $raw.Length) {
    $allZero = $true
    for ($i = 0; $i -lt 512; $i++) { if ($raw[$offset + $i] -ne 0) { $allZero = $false; break } }
    if ($allZero) { break }

    $typeFlag = [int]$raw[$offset + 156]
    $sizeField = ([System.Text.Encoding]::ASCII.GetString($raw, $offset + 124, 12)).Trim([char]0, [char]32)
    $size = 0
    if ($sizeField.Length -gt 0) { $size = [Convert]::ToInt64($sizeField, 8) }

    if ($typeFlag -eq [int][char]'2') {
        $newLink = '..\..\escape-target'
        $linkBytes = [System.Text.Encoding]::ASCII.GetBytes($newLink)
        for ($i = 0; $i -lt 100; $i++) { $raw[$offset + 157 + $i] = 0 }
        [Array]::Copy($linkBytes, 0, $raw, $offset + 157, $linkBytes.Length)
        # 校验和字段按 8 个空格参与求和，结果写成 6 位八进制 + NUL + 空格。
        for ($i = 0; $i -lt 8; $i++) { $raw[$offset + 148 + $i] = 0x20 }
        $sum = 0
        for ($i = 0; $i -lt 512; $i++) { $sum += $raw[$offset + $i] }
        $octal = [Convert]::ToString($sum, 8).PadLeft(6, '0')
        [Array]::Copy([System.Text.Encoding]::ASCII.GetBytes($octal), 0, $raw, $offset + 148, 6)
        $raw[$offset + 154] = 0
        $raw[$offset + 155] = 0x20
        $patched = $true
    }
    $offset += 512 + ([math]::Ceiling($size / 512.0) * 512)
}
Assert-True $patched 'F13: 自检失败：tar 里没有任何 typeflag=2 的软链条目可补丁 —— fixture 形状不对'
[System.IO.File]::WriteAllBytes($archive, $raw)
# 软链本身先删掉再删构建目录：PS 5.1 的 `Remove-Item` 遇到目录软链会抛 NullReferenceException
#（已知的 .NET/PowerShell 缺陷），所以这里直接用 Directory.Delete 删**链接本身**（不递归、不跟随）。
try { [System.IO.Directory]::Delete((Join-Path $src 'link-out'), $false) } catch { }
Remove-Dir $src
# 补丁之后**让 tar 自己读回来**：校验和改错的话 tar 会直接报错，这是最硬的验证。
$verify = Invoke-Native $tarExe @('-tvf', 'trav.tar') $f13
Assert-True ($verify.Code -eq 0) ("F13: 自检失败：补丁后的 tar 校验和不对，GNU tar 读不回来（退出码 " + $verify.Code + "）：" + $verify.Out)
Assert-True ($verify.Out -match 'escape-target') 'F13: 自检失败：补丁后的 tar 里看不到 escape-target'
# 再直接读原始字节比对 linkname 字段（tar 的显示会把反斜杠转义成两个，不能拿显示当证据）。
$rawCheck = [System.IO.File]::ReadAllBytes($archive)
$offset = 0
$linkName = ''
while ($offset + 512 -le $rawCheck.Length) {
    $allZero = $true
    for ($i = 0; $i -lt 512; $i++) { if ($rawCheck[$offset + $i] -ne 0) { $allZero = $false; break } }
    if ($allZero) { break }
    $sizeField = ([System.Text.Encoding]::ASCII.GetString($rawCheck, $offset + 124, 12)).Trim([char]0, [char]32)
    $size = 0
    if ($sizeField.Length -gt 0) { $size = [Convert]::ToInt64($sizeField, 8) }
    if ([int]$rawCheck[$offset + 156] -eq [int][char]'2') {
        $linkName = ([System.Text.Encoding]::ASCII.GetString($rawCheck, $offset + 157, 100)).Trim([char]0)
    }
    $offset += 512 + ([math]::Ceiling($size / 512.0) * 512)
}
Assert-True ($linkName -eq '..\..\escape-target') ("F13: 自检失败：补丁后的 linkname 字段是 [" + $linkName + "]，期望 [..\..\escape-target]")
$entries = Get-SevenZipEntries $archive
Assert-True ($null -ne $entries) 'F13: 自检失败：7-Zip 读不出 trav.tar 的条目'
$names = @($entries | ForEach-Object { $_.Path })
Assert-True (($names | Where-Object { $_ -like '*pwned*' }).Count -eq 1) 'F13: 自检失败：tar 里没有穿过软链写入的 pwned.txt 条目'
Note 'F13' 'tar 软链穿越' 'GNU tar 打真软链 + 二进制补丁把 linkname 改成 ..\..\escape-target，tar 自己校验和通过；另含穿过软链写入的 pwned.txt'

# ==================================================================
# 14) HTML 假包（规格 §9.1 行 14）
# ==================================================================
$f14 = New-FixtureDir 'f14-html'
$html = "<!DOCTYPE html>`r`n<html><head><meta charset=`"utf-8`"><title>404 Not Found</title></head>`r`n" +
        "<body><h1>404</h1><p>您访问的页面不存在，下载已失败。</p></body></html>`r`n"
Write-TextFile (Join-Path $f14 'fake.zip') $html
$head = Get-FirstBytes (Join-Path $f14 'fake.zip') 15
$headText = [System.Text.Encoding]::ASCII.GetString($head)
Assert-True ($headText.ToLowerInvariant().StartsWith('<!doctype html')) 'F14: 自检失败：fake.zip 不是以 <!DOCTYPE html 开头的网页'
Note 'F14' 'HTML 假包' '后缀 .zip、内容是 404 网页；自检：头部小写后以 <!doctype html 开头（Sniffer 的 Html 档判据）'

# ==================================================================
# 15) 0 字节卷（规格 §9.1 行 15）
# ==================================================================
$f15 = New-FixtureDir 'f15-zero-volume'
$build = Join-Path $f15 '.build'
Ensure-Dir $build
[System.IO.File]::WriteAllBytes((Join-Path $build 'payload.bin'), $payload)
Push-Location $build
try {
    $run = Invoke-SevenZip @('a', '-mx=0', '-v16k', (Join-Path $f15 'zero.7z'), 'payload.bin')
} finally { Pop-Location }
Assert-True ($run.Code -eq 0) ("F15: 7z 造分卷失败（退出码 " + $run.Code + "）：" + $run.Out)
Remove-Dir $build
$volumes = @(Get-ChildItem -LiteralPath $f15 -File | Where-Object { $_.Name -like 'zero.7z.*' } | Sort-Object Name)
Assert-True ($volumes.Count -ge 3) ("F15: 自检失败：分卷数只有 " + $volumes.Count + "，不足以做出「中间某一卷是 0 字节」的形状")
$zero = Join-Path $f15 'zero.7z.002'
$stream = [System.IO.File]::Open($zero, [System.IO.FileMode]::Truncate, [System.IO.FileAccess]::Write)
$stream.Dispose()
Assert-True ((Get-Item -LiteralPath $zero).Length -eq 0) 'F15: 自检失败：zero.7z.002 不是 0 字节'
Assert-True ((Get-Item -LiteralPath (Join-Path $f15 'zero.7z.001')).Length -gt 0) 'F15: 自检失败：zero.7z.001 不该是 0 字节'
Note 'F15' '0 字节卷' ("同一分卷集的 zero.7z.002 被截成 0 字节（.001 仍有 " + (Get-Item -LiteralPath (Join-Path $f15 'zero.7z.001')).Length + " 字节）；自检：长度=0 且同集还有其它卷")

# ==================================================================
# 16) 路径冲突：Readme.txt + README.TXT（规格 §9.1 行 16）
# ==================================================================
$f16 = New-FixtureDir 'f16-conflict'
$conflictBytes = New-ZipBytes @((New-ZipEntry 'Readme.txt' 'lower'), (New-ZipEntry 'README.TXT' 'upper'))
[System.IO.File]::WriteAllBytes((Join-Path $f16 'conflict.zip'), $conflictBytes)
$names = @(Get-ZipEntryNames (Join-Path $f16 'conflict.zip'))
Assert-True ($names.Count -eq 2) ("F16: 自检失败：条目数不是 2，而是 " + $names.Count)
Assert-True (($names -ccontains 'Readme.txt') -and ($names -ccontains 'README.TXT')) 'F16: 自检失败：两个只差大小写的条目没有同时进包（大小写敏感比较）'
Note 'F16' '路径冲突' '（.NET zip 写库）造的 .zip，含 Readme.txt 与 README.TXT；自检：读回的两个名字大小写敏感地都在'

# ==================================================================
# 17) 非空目标目录（规格 §9.1 行 17）
# ==================================================================
$f17 = New-FixtureDir 'f17-existing-target'
$build = Join-Path $f17 '.build'
Ensure-Dir $build
Write-TextFile (Join-Path $build 'file1.txt') "one`r`n"
Push-Location $build
try {
    $run = Invoke-SevenZip @('a', '-tzip', '-mx=9', (Join-Path $f17 'busy.zip'), 'file1.txt')
} finally { Pop-Location }
Assert-True ($run.Code -eq 0) ("F17: 7z 造 busy.zip 失败（退出码 " + $run.Code + "）：" + $run.Out)
Remove-Dir $build
# 目标名 busy 已经被一个**非空**目录占住（这是 fixture 的一半，必须一起随矩阵发出去）。
Ensure-Dir (Join-Path $f17 'busy')
Write-TextFile (Join-Path $f17 'busy\blocker.txt') 'pre-existing, must not be touched'
$entries = Get-SevenZipEntries (Join-Path $f17 'busy.zip')
Assert-True ($null -ne $entries -and $entries.Count -eq 1 -and $entries[0].Path -eq 'file1.txt') 'F17: 自检失败：busy.zip 的内容不是单个 file1.txt'
Assert-True ((Get-Item -LiteralPath (Join-Path $f17 'busy\blocker.txt')).Length -gt 0) 'F17: 自检失败：非空目标目录里的占位文件不存在'
Note 'F17' '非空目标' 'busy.zip 旁边先放一个非空的 busy\ 目录（含 blocker.txt）；自检：包内是 file1.txt、占位目录非空'

# ==================================================================
# 18) 大输出量：4000 个成员（灌满子进程管道）（规格 §9.1 行 18）
# ==================================================================
$f18 = New-FixtureDir 'f18-bulk'
$build = Join-Path $f18 '.build'
Ensure-Dir $build
$bulkCount = 4000
$filler = 'x' * 900
for ($i = 1; $i -le $bulkCount; $i++) {
    Write-TextFile (Join-Path $build ('f{0:D4}.txt' -f $i)) (("line {0}`r`n" -f $i) + $filler)
}
Push-Location $build
try {
    $run = Invoke-SevenZip @('a', '-tzip', '-mx=1', (Join-Path $f18 'bulk.zip'), '*')
} finally { Pop-Location }
Assert-True ($run.Code -eq 0) ("F18: 7z 造大包失败（退出码 " + $run.Code + "）：" + $run.Out)
Remove-Dir $build
$entries = Get-SevenZipEntries (Join-Path $f18 'bulk.zip')
Assert-True ($null -ne $entries) 'F18: 自检失败：bulk.zip 读不出条目'
Assert-True ($entries.Count -eq $bulkCount) ("F18: 自检失败：条目数是 " + $entries.Count + "，期望 " + $bulkCount)
Note 'F18' '大输出量' ($bulkCount.ToString() + " 个成员（每个约 1 KB）；自检：7-Zip 列出的条目数恰好 " + $bulkCount)

# ==================================================================
# 19) OLE2/CFB 复合文档：真 .doc/.xls/.ppt/.msi 一族的容器形状（数据丢失事故回归）
#
# 【为什么这一份必须手搓字节】7-Zip 26.01 只能**解**不能**造** Compound（见文件头第 6 条），
# 所以「夹具一律由真打包器产生」这条规矩对这一族根本执行不了。
# 这与文件头第 1 条（绝不硬造 .part1.rar / .z01）**不冲突**，两者的差别是**证明力**：
#   * 假 rar 证明不了任何事 —— 分卷语义（哪一片权威、缺哪一卷）只有真 WinRAR 造的包才携带；
#   * 这一份被测的判定依据**就是**头部 8 字节 D0 CF 11 E0 A1 B1 1A E1，而那 8 个字节正是
#     手搓出来的全部身份；并且下面第 2 条自检让 **7-Zip 自己**把这份手搓容器真打开一次
#     （列得出 WordDocument / 1Table / [1]CompObj 才算数）—— 于是「它是 7-Zip 眼里的
#     Compound 容器、它的条目会被当成可解压成员」是**测量**出来的，不是假定的。
# 换句话说：这里不是「用手搓的东西假装有真工具的证明力」，而是「手搓就是这一族唯一的构造方式，
# 再用真工具把它的形状复核一遍」。
#
# 手搓时的构造事实（本机 7-Zip 26.01 实测，写下来免得后人重复踩）：
#   * 扇区 512 字节、major version 3 ⇒ 头里 `numDirSectors` 必须是 **0**（写 1 => Headers Error）；
#   * 文件必须**恰好**是 头 + 被 FAT 覆盖的那些扇区：头 + FAT + 目录 + 各条流；
#     在 FAT 之外多挂空闲扇区会让 7-Zip 报 Headers Error（这是定型前的最后一步）；
#   * 流长度取 4096 == mini stream cutoff ⇒ 走普通扇区，于是不需要 mini FAT / mini stream；
#   * 目录项名按 [MS-CFB] 的排序规则（先比名字长度、再比大写后的名字）排成右倾树即可被读出。
# ==================================================================
$f19 = New-FixtureDir 'f19-ole2'
$ole2Path = Join-Path $f19 'legacy.doc'

$cfbSectorSize = 512
$cfbFreeSector = [uint32]'0xFFFFFFFF'
$cfbEndOfChain = [uint32]'0xFFFFFFFE'
$cfbFatSector = [uint32]'0xFFFFFFFD'

function Set-CfbU16([byte[]]$buffer, [int]$offset, [uint16]$value) {
    [Array]::Copy([BitConverter]::GetBytes($value), 0, $buffer, $offset, 2)
}
function Set-CfbU32([byte[]]$buffer, [int]$offset, [uint32]$value) {
    [Array]::Copy([BitConverter]::GetBytes($value), 0, $buffer, $offset, 4)
}
function Set-CfbDirEntry([byte[]]$buffer, [int]$index, [string]$name, [byte]$type, [byte]$color,
                         [uint32]$left, [uint32]$right, [uint32]$child, [uint32]$start, [uint64]$size) {
    $offset = $index * 128
    $nameBytes = [System.Text.Encoding]::Unicode.GetBytes($name)
    [Array]::Copy($nameBytes, 0, $buffer, $offset, $nameBytes.Length)
    Set-CfbU16 $buffer ($offset + 64) ([uint16]($nameBytes.Length + 2))   # 名字长度含结尾的 UTF-16 NUL
    $buffer[$offset + 66] = $type                                        # 0=空 1=存储 2=流 5=根
    $buffer[$offset + 67] = $color                                       # 0=红 1=黑
    Set-CfbU32 $buffer ($offset + 68) $left
    Set-CfbU32 $buffer ($offset + 72) $right
    Set-CfbU32 $buffer ($offset + 76) $child
    Set-CfbU32 $buffer ($offset + 116) $start
    [Array]::Copy([BitConverter]::GetBytes($size), 0, $buffer, $offset + 120, 8)
}

$cfbStreams = @('WordDocument', '1Table', '[1]CompObj')
$cfbStreamBytes = 4096
$cfbSectorsPerStream = $cfbStreamBytes / $cfbSectorSize                 # 8
$cfbTotalSectors = 2 + $cfbSectorsPerStream * $cfbStreams.Count          # 扇区 0 = FAT、扇区 1 = 目录

# ---- 头（512 字节）----
$cfbHeader = New-Object byte[] $cfbSectorSize
[Array]::Copy([byte[]](0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1), 0, $cfbHeader, 0, 8)
Set-CfbU16 $cfbHeader 24 ([uint16]0x003E)          # minor version
Set-CfbU16 $cfbHeader 26 ([uint16]0x0003)          # major version 3（512 字节扇区）
Set-CfbU16 $cfbHeader 28 ([uint16]0xFFFE)          # byte order mark
Set-CfbU16 $cfbHeader 30 ([uint16]0x0009)          # sector shift（2^9 = 512）
Set-CfbU16 $cfbHeader 32 ([uint16]0x0006)          # mini sector shift（2^6 = 64）
Set-CfbU32 $cfbHeader 40 ([uint32]0)               # v3 的目录扇区数必须是 0
Set-CfbU32 $cfbHeader 44 ([uint32]1)               # FAT 扇区数
Set-CfbU32 $cfbHeader 48 ([uint32]1)               # 目录起始扇区
Set-CfbU32 $cfbHeader 52 ([uint32]0)               # 事务签名
Set-CfbU32 $cfbHeader 56 ([uint32]4096)            # mini stream cutoff
Set-CfbU32 $cfbHeader 60 $cfbEndOfChain            # 第一条 mini FAT 扇区（本夹具没有 mini 流）
Set-CfbU32 $cfbHeader 64 ([uint32]0)
Set-CfbU32 $cfbHeader 68 $cfbEndOfChain            # 第一条 DIFAT 扇区（109 项以内够用）
Set-CfbU32 $cfbHeader 72 ([uint32]0)
for ($i = 0; $i -lt 109; $i++) {
    Set-CfbU32 $cfbHeader (76 + $i * 4) $(if ($i -eq 0) { [uint32]0 } else { $cfbFreeSector })
}

# ---- FAT（扇区 0）----
$cfbFat = New-Object byte[] $cfbSectorSize
for ($i = 0; $i -lt 128; $i++) { Set-CfbU32 $cfbFat ($i * 4) $cfbFreeSector }
Set-CfbU32 $cfbFat 0 $cfbFatSector                 # 扇区 0 就是 FAT 自己
Set-CfbU32 $cfbFat 4 $cfbEndOfChain                # 扇区 1 是单扇区目录
for ($k = 0; $k -lt $cfbStreams.Count; $k++) {
    $start = 2 + $k * $cfbSectorsPerStream
    for ($s = 0; $s -lt $cfbSectorsPerStream - 1; $s++) {
        Set-CfbU32 $cfbFat (($start + $s) * 4) ([uint32]($start + $s + 1))
    }
    Set-CfbU32 $cfbFat (($start + $cfbSectorsPerStream - 1) * 4) $cfbEndOfChain
}

# ---- 目录（扇区 1）----
$cfbDir = New-Object byte[] $cfbSectorSize
Set-CfbDirEntry $cfbDir 0 'Root Entry' 5 1 $cfbFreeSector $cfbFreeSector ([uint32]1) $cfbEndOfChain ([uint64]0)
for ($k = 0; $k -lt $cfbStreams.Count; $k++) {
    $cfbRight = if ($k + 1 -lt $cfbStreams.Count) { [uint32]($k + 2) } else { $cfbFreeSector }
    Set-CfbDirEntry $cfbDir ($k + 1) $cfbStreams[$k] 2 1 $cfbFreeSector $cfbRight $cfbFreeSector `
        ([uint32](2 + $k * $cfbSectorsPerStream)) ([uint64]$cfbStreamBytes)
}

# ---- 流数据（扇区 2 起）----
$cfbPayload = New-Object byte[] (($cfbTotalSectors - 2) * $cfbSectorSize)
for ($i = 0; $i -lt $cfbPayload.Length; $i++) { $cfbPayload[$i] = [byte](0x30 + ($i % 10)) }

$cfbAll = New-Object System.Collections.Generic.List[byte]
$cfbAll.AddRange($cfbHeader); $cfbAll.AddRange($cfbFat); $cfbAll.AddRange($cfbDir); $cfbAll.AddRange($cfbPayload)
[System.IO.File]::WriteAllBytes($ole2Path, $cfbAll.ToArray())

# 自检 1：头部 8 字节逐字节等于 OLE2/CFB 魔数 —— 这是这份夹具的**全部身份**。
$cfbHead = Get-FirstBytes $ole2Path 8
Assert-True ($cfbHead[0] -eq 0xD0 -and $cfbHead[1] -eq 0xCF -and $cfbHead[2] -eq 0x11 -and $cfbHead[3] -eq 0xE0 -and
             $cfbHead[4] -eq 0xA1 -and $cfbHead[5] -eq 0xB1 -and $cfbHead[6] -eq 0x1A -and $cfbHead[7] -eq 0xE1) `
    'F19: 自检失败：头部不是 OLE2/CFB 签名 D0 CF 11 E0 A1 B1 1A E1'

# 自检 2（**关键**）：让 7-Zip 自己把它当 Compound 容器打开，并列出目录里的流。
# 少了这一条，这份夹具就只是「一段以 CFB 魔数开头的字节」—— 那样「被强制后会真的被解压出来」
# 这条回归就变成假通过（7-Zip 读不出清单 ⇒ 早在 I1 那一档就失败了）。
$cfbListing = Invoke-SevenZip @('l', '-slt', '-p', $ole2Path)
Assert-True ($cfbListing.Code -eq 0) ("F19: 自检失败：7-Zip 打不开这份 CFB（退出码 " + $cfbListing.Code + "）：" + $cfbListing.Out)
Assert-True ($cfbListing.Out -like '*Type = Compound*') ("F19: 自检失败：7-Zip 没把它识别成 Compound 容器：" + $cfbListing.Out)
foreach ($cfbName in $cfbStreams) {
    # 用 .Contains 而不是 -like：流名 `[1]CompObj` 里的方括号在 PowerShell 的 -like 里是**通配符**
    #（`[1]` 匹配单个字符 '1'），拿它当模式会假失败 —— 这里要的是字面子串匹配。
    Assert-True ($cfbListing.Out.Contains($cfbName)) ("F19: 自检失败：7-Zip 的清单里没有流「" + $cfbName + "」")
}
Note 'F19' 'OLE2 复合文档' ("手搓的真 [MS-CFB] 容器（头 + FAT + 目录 + " + $cfbStreams.Count + " 条 4096 字节的流）；" +
    "自检：头部 8 字节是 OLE2 魔数，且 7-Zip 自己把它列出为 Type = Compound 并给出 " + ($cfbStreams -join '/'))

# ==================================================================
# 20) 非白名单容器类型（真 PE + 尾部 zip 标记）：本轮 Critical 的数据丢失回归
#
# 事故形状（复审用一份真 shipped 文件复现，正常路径、不强制、不删除）：
#   真 PE（头 4D 5A 90 00）的**尾部 64KB 里恰好有 zip 标记** ⇒ Sniffer 判 DamagedHeader
#（IsArchiveKind 故意保留这一档给「防和谐」抢救）⇒ 7-Zip 按真身打开它（`Type = PE`，条目是
#   .text/.rsrc/…）⇒ ArchiveGater 的标记全是 zip 内容身份、PE 一个都没有 ⇒ Allow ⇒
#   可执行文件被「成功解压」成「<名字>.dll (2)\」；开删除即回收、不开删除也原位拆散。
#
# 修法是**按 7-Zip 自己报的归档类型放行白名单**（不是再加头部签名）。F20 钉住「非白名单类型被拒」。
#
# 【构造事实（本机 7-Zip 26.01 实测，写下来免得后人重复踩）】
#   * 手搓的最小 PE 7-Zip 打不开（退出码 2）⇒ 必须用真编译器（csc）产出的真 PE；
#   * 在 PE 末尾**追加**任何字节（哪怕 4 个）7-Zip 也会退出码 2（它的 PE 处理器要求文件长度与节表
#     一致），所以尾部那 4 个字节是**原地改写**最后一组字节 —— 改完 7-Zip 照旧 `Type = PE`、退出码 0。
#     复审那份真 DLL 天然就在尾部带 PK\x05\x06，正是同一形状。
#   * 那 4 个字节是**故意的标记注入**（与 F13 的 tar 软链二进制补丁同一手法）：被测的判定依据就是
#     「7-Zip 报的类型不在白名单里」，而下面第 3 条自检让**真 7-Zip**把这件事测量出来。
# ==================================================================
$f20 = New-FixtureDir 'f20-pe-container'
$pePath = Join-Path $f20 'legacy-pe.dll'

$script:Csc = Find-Csc
$peSource = Join-Path $f20 'pe-probe.cs'
Write-TextFile $peSource "internal static class RerarPeFixture { private static void Main() { } }"
$peCompiled = Join-Path $f20 'pe-probe.exe'
$peBuild = Invoke-Native $script:Csc @('/nologo', '/target:exe', ('/out:' + $peCompiled), $peSource) $null
Assert-True ($peBuild.Code -eq 0) ("F20: csc 编译失败（退出码 " + $peBuild.Code + "）：" + $peBuild.Out)
Assert-True (Test-Path -LiteralPath $peCompiled) 'F20: csc 没有产出 PE'

$peBytes = [System.IO.File]::ReadAllBytes($peCompiled)
Assert-True ($peBytes.Length -ge 64) 'F20: 编译产物太小，不像一个 PE'
[Array]::Copy([byte[]](0x50, 0x4B, 0x05, 0x06), 0, $peBytes, $peBytes.Length - 4, 4)   # 尾部写入 zip 的 EOCD 标记
[System.IO.File]::WriteAllBytes($pePath, $peBytes)

# 自检 1：头部必须是 PE（MZ 90 00）—— 这是「事故里那个形状」的字面要求。
$peHead = Get-FirstBytes $pePath 4
Assert-True ($peHead[0] -eq 0x4D -and $peHead[1] -eq 0x5A -and $peHead[2] -eq 0x90 -and $peHead[3] -eq 0x00) `
    'F20: 自检失败：头部不是 PE 的 4D 5A 90 00'

# 自检 2：尾部 64KB 里必须真的有 zip 标记（否则 Sniffer 判不出 DamagedHeader，夹具就走不到那扇门）。
$peAll = [System.IO.File]::ReadAllBytes($pePath)
$peTailStart = [Math]::Max(0, $peAll.Length - 65536)
$peTailHasZipMarker = $false
for ($i = $peTailStart; $i + 4 -le $peAll.Length; $i++) {
    if ($peAll[$i] -eq 0x50 -and $peAll[$i + 1] -eq 0x4B -and
        (($peAll[$i + 2] -eq 0x05 -and $peAll[$i + 3] -eq 0x06) -or
         ($peAll[$i + 2] -eq 0x01 -and $peAll[$i + 3] -eq 0x02))) { $peTailHasZipMarker = $true; break }
}
Assert-True $peTailHasZipMarker 'F20: 自检失败：尾部 64KB 里没有 zip 标记（Sniffer 不会判 DamagedHeader）'

# 自检 3（**关键**）：让真 7-Zip 自己把这份文件打开，并报出 `Type = PE`。
# 少了这一条，这份夹具就只是「一段 MZ 开头的字节」，回归会在 I1 那一档就 Failed、永远走不到类型门控。
$peListing = Invoke-SevenZip @('l', '-slt', '-p', $pePath)
Assert-True ($peListing.Code -eq 0) ("F20: 自检失败：7-Zip 打不开这份 PE（退出码 " + $peListing.Code + "）：" + $peListing.Out)
Assert-True ($peListing.Out.Contains('Type = PE')) ("F20: 自检失败：7-Zip 没把它报成 PE 容器：" + $peListing.Out)
Note 'F20' '非白名单容器(PE)' ("csc 编译的真 PE（" + $peBytes.Length + " 字节）+ 尾部原地写入 zip 标记 PK\x05\x06；" +
    "自检：头部 4D 5A 90 00、尾部有 zip 标记，且 7-Zip 自己把它列出为 Type = PE（非白名单 ⇒ 必须被拒）")

# ==================================================================
# 21) 「防和谐」抢救形状（头部清零、仍能被 7-Zip 完整解出的真 zip）：白名单**不能把功能关死**
#
# 头部 1024 字节清零 ⇒ Sniffer 看不到 PK\x03\x04 签名 ⇒ DamagedHeader（IsArchiveKind 故意放行，
# 给 7-Zip 一次抢救机会）；而中央目录里的偏移同步 +1024 ⇒ 归档本体仍然自洽，7-Zip 照旧
# `Type = zip`（白名单命中）并**完整解出**三个成员。F21 钉住这条抢救能力活着。
#
# 【为什么不是「把第一个局部头清零」】实测：把 zip 开头的字节（哪怕只 1 个）清零会打断**第一个成员**
# 的局部文件头，7-Zip 解压退出码 2、那个成员落成 0 字节 —— 产品在 I1 那一档就判 Failed，根本走不到
# 「抢救成功」这条断言。所以这里造的是一份**仍然可读**的受损 zip。
# ==================================================================
$f21 = New-FixtureDir 'f21-zeroed-head'
$zeroedPath = Join-Path $f21 'zeroed-head.zip'

$zeroEntries = @(
    (New-ZipEntry 'a.txt' 'alpha'),
    (New-ZipEntry 'b.txt' 'bravo'),
    (New-ZipEntry 'docs/readme.md' '# readme'))
$zeroBody = New-ZipBytes $zeroEntries

$zeroEocd = -1
for ($i = $zeroBody.Length - 22; $i -ge 0; $i--) {
    if ($zeroBody[$i] -eq 0x50 -and $zeroBody[$i + 1] -eq 0x4B -and
        $zeroBody[$i + 2] -eq 0x05 -and $zeroBody[$i + 3] -eq 0x06) { $zeroEocd = $i; break }
}
Assert-True ($zeroEocd -ge 0) 'F21: 找不到 EOCD 记录'

$zeroPrefix = 1024
$zeroCentral = [BitConverter]::ToUInt32($zeroBody, $zeroEocd + 16)
$zeroCount = [BitConverter]::ToUInt16($zeroBody, $zeroEocd + 10)
$zeroAll = New-Object byte[] ($zeroPrefix + $zeroBody.Length)
[Array]::Copy($zeroBody, 0, $zeroAll, $zeroPrefix, $zeroBody.Length)
[Array]::Copy([BitConverter]::GetBytes([uint32]($zeroCentral + $zeroPrefix)), 0, $zeroAll, $zeroEocd + $zeroPrefix + 16, 4)

$zeroAt = [int]$zeroCentral + $zeroPrefix
for ($k = 0; $k -lt $zeroCount; $k++) {
    Assert-True ($zeroAll[$zeroAt] -eq 0x50 -and $zeroAll[$zeroAt + 1] -eq 0x4B -and
                 $zeroAll[$zeroAt + 2] -eq 0x01 -and $zeroAll[$zeroAt + 3] -eq 0x02) `
        'F21: 自检失败：中央目录记录的签名不在预期位置'
    $zeroNameLen = [BitConverter]::ToUInt16($zeroAll, $zeroAt + 28)
    $zeroExtraLen = [BitConverter]::ToUInt16($zeroAll, $zeroAt + 30)
    $zeroCommentLen = [BitConverter]::ToUInt16($zeroAll, $zeroAt + 32)
    $zeroLocal = [BitConverter]::ToUInt32($zeroAll, $zeroAt + 42)
    [Array]::Copy([BitConverter]::GetBytes([uint32]($zeroLocal + $zeroPrefix)), 0, $zeroAll, $zeroAt + 42, 4)
    $zeroAt += 46 + $zeroNameLen + $zeroExtraLen + $zeroCommentLen
}
[System.IO.File]::WriteAllBytes($zeroedPath, $zeroAll)

# 自检 1：头部真的被清零了（前 4 字节是 0x00 ⇒ Sniffer 看不到 zip 签名）。
$zeroHead = Get-FirstBytes $zeroedPath 4
Assert-True ($zeroHead[0] -eq 0 -and $zeroHead[1] -eq 0 -and $zeroHead[2] -eq 0 -and $zeroHead[3] -eq 0) `
    'F21: 自检失败：头部没有被清零'

# 自检 2：7-Zip 必须仍能读它，且报 `Type = zip`（白名单命中 ⇒ 抢救路径必须走通）。
$zeroListing = Invoke-SevenZip @('l', '-slt', '-p', $zeroedPath)
Assert-True ($zeroListing.Code -eq 0) ("F21: 自检失败：7-Zip 读不出这份零头 zip（退出码 " + $zeroListing.Code + "）：" + $zeroListing.Out)
Assert-True ($zeroListing.Out.Contains('Type = zip')) ("F21: 自检失败：7-Zip 没把它报成 zip：" + $zeroListing.Out)

# 自检 3（**关键**）：7-Zip 必须能**完整解出**三个成员（退出码 0）。少了这一条，「抢救仍然可用」
# 这条断言就可能建立在一份 7-Zip 其实解不了的输入上。
$zeroProbe = Join-Path $f21 '.probe'
Assert-True (-not (Test-Path -LiteralPath $zeroProbe)) 'F21: 探测目录已存在（上一轮的残留）'
$zeroExtract = Invoke-SevenZip @('x', $zeroedPath, ('-o' + $zeroProbe), '-p', '-y')
Assert-True ($zeroExtract.Code -eq 0) ("F21: 自检失败：7-Zip 解不出这份零头 zip（退出码 " + $zeroExtract.Code + "）：" + $zeroExtract.Out)
foreach ($zeroMember in @('a.txt', 'b.txt', ('docs' + [System.IO.Path]::DirectorySeparatorChar + 'readme.md'))) {
    Assert-True (Test-Path -LiteralPath (Join-Path $zeroProbe $zeroMember)) ("F21: 自检失败：7-Zip 没解出成员 " + $zeroMember)
}
Remove-Dir $zeroProbe
Note 'F21' '零头 zip(抢救)' ("真 zip（" + $zeroEntries.Count + " 个成员）头部 1024 字节清零、中央目录偏移同步修正；" +
    "自检：头部无 zip 签名、7-Zip 报 Type = zip，且**完整解出**三个成员（抢救能力没有被白名单关死）")

# ==================================================================
# 汇总
# ==================================================================

Write-Host ''
Write-Host '夹具矩阵（tests\_fixtures\）：'
foreach ($row in $script:Rows) {
    Write-Host ("  " + $row.Id + "  " + $row.Name.PadRight(18) + $row.Detail)
}
Write-Host ''
if ($script:Rows.Count -ne 21) {
    Fail ("夹具数不是 21，而是 " + $script:Rows.Count + "（规格 §9.1 的 18 个 + OLE2 事故回归 1 个 + 本轮非 zip 容器回归 2 个）")
}
Write-Host ("FIXTURES_OK " + $script:Rows.Count + "/21（每个都做了形状自检；没有任何一个被静默跳过）")
exit 0
