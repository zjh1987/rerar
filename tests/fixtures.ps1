# Rerar 验收脚本（Task 15）之一：构造规格 §9.1 的 fixture 矩阵。
#
# 用法：powershell -NoProfile -File tests\fixtures.ps1
#   退出 0 = 18 个 fixture 全部构造完成，且**每一个的形状自检**都通过；
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
$tarExe = 'C:\Program Files\Git\usr\bin\tar.exe'
if (-not (Test-Path -LiteralPath $tarExe)) {
    Fail ("F13 无法构造：找不到 GNU tar（" + $tarExe + "）。tar 软链 fixture 只能用真 tar 造，" +
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
# 汇总
# ==================================================================

Write-Host ''
Write-Host '夹具矩阵（tests\_fixtures\）：'
foreach ($row in $script:Rows) {
    Write-Host ("  " + $row.Id + "  " + $row.Name.PadRight(18) + $row.Detail)
}
Write-Host ''
if ($script:Rows.Count -ne 18) {
    Fail ("夹具数不是 18，而是 " + $script:Rows.Count + "（规格 §9.1 要求 18 个）")
}
Write-Host ("FIXTURES_OK " + $script:Rows.Count + "/18（每个都做了形状自检；没有任何一个被静默跳过）")
exit 0
