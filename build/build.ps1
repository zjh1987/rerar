# Rerar 构建脚本：直接调系统自带的 csc.exe（无 MSBuild、无 NuGet、无第三方库）。
# 一次编译两个目标：
#   dist\Rerar.exe  ← src\Core\*.cs + src\App\*.cs    （GUI/CLI 程序）
#   dist\tests.exe  ← src\Core\*.cs + src\Tests\*.cs  （无框架单元测试运行器）
# 任一目标编译失败即以非 0 退出，供 tests\smoke.ps1 等验收脚本判定。
# 说明：源码一律保存为「UTF-8 带 BOM」；csc 另加 /codepage:65001，
#       中文解码不再单靠「每个文件都恰好带 BOM」这一脆弱前提。
#
# Task 13 起本脚本还要负责「把 7-Zip 内嵌进应用目标」这件事（规格 §8 / §6.2）：
#   1) 找一份本机的 7z.exe + 7z.dll（构建期工具，**不是**运行期路径：运行期由 EngineLocator 负责）；
#   2) 跑一次 `7z i` 做功能自检，并卡**版本下限 25.00**（更低版本带已在野利用的符号链接目录穿越
#      问题，绝不能被嵌进这个解的是不可信压缩包的工具里）；
#   3) 算出两份文件的 SHA-256，生成 dist\obj\EmbeddedEngine.g.cs（把期望哈希写死成程序常量）；
#   4) 只给**应用目标**加 /resource: —— 测试目标绝不能跟着胖 ~2.4 MB（见 Invoke-CscTarget 的说明）。
# 找不到可用的 7-Zip 时**直接构建失败**：一个没有内嵌兜底的 Rerar.exe 违背「无 7-Zip 的干净机器
# 上双击即用」这条产品承诺，绝不能悄悄发出去。

$root = Split-Path -Parent $PSScriptRoot

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    Write-Host ("FAIL: 找不到编译器 " + $csc)
    exit 1
}

$dist = Join-Path $root 'dist'
if (-not (Test-Path -LiteralPath $dist)) {
    New-Item -ItemType Directory -Path $dist | Out-Null
}

# 先删旧产物：编译失败时不会留下过期 exe 让冒烟测试误判通过。
foreach ($stale in @((Join-Path $dist 'Rerar.exe'), (Join-Path $dist 'tests.exe'))) {
    if (Test-Path -LiteralPath $stale) {
        Remove-Item -LiteralPath $stale -Force
    }
}

# ======================================================================
# Task 13：内嵌 7-Zip 载荷的准备（找 → 自检 → 版本下限 → 哈希 → 生成常量）
# ======================================================================

function Fail([string]$message) {
    Write-Host ("FAIL: " + $message)
    exit 1
}

# 构建期找一份可用于内嵌的 7-Zip。运行期的探测顺序（规格 §6.2）是 EngineLocator 的事；这里是
# 构建机上的「去哪儿拿载荷」，两者刻意分开：构建机装了 7-Zip 不代表目标机有。
# 顺序：RERAR_7Z_DIR（显式指定，CI / 没装 7-Zip 的构建机用解压出来的目录）→ Program Files →
#       Program Files (x86) → 注册表两个视图 → PATH。
# 必须**同一个目录**下同时有 7z.exe 与 7z.dll：老版本 7z.exe 需要同目录的 7z.dll 才能工作，
# 只嵌 exe 会在那些版本上悄悄退化（这也是 EngineLocator 两个都释放的原因）。
function Find-SevenZipPayload {
    $dirs = New-Object System.Collections.Generic.List[string]

    if ($env:RERAR_7Z_DIR) { $dirs.Add($env:RERAR_7Z_DIR) }
    if ($env:ProgramW6432) { $dirs.Add((Join-Path $env:ProgramW6432 '7-Zip')) }
    if ($env:ProgramFiles) { $dirs.Add((Join-Path $env:ProgramFiles '7-Zip')) }
    $programFilesX86 = ${env:ProgramFiles(x86)}
    if ($programFilesX86) { $dirs.Add((Join-Path $programFilesX86 '7-Zip')) }

    # 注册表两个视图都显式读：64 位进程默认看不见 32 位键，反之亦然。
    foreach ($key in @('HKLM:\SOFTWARE\7-Zip', 'HKLM:\SOFTWARE\WOW6432Node\7-Zip')) {
        try {
            $value = (Get-ItemProperty -LiteralPath $key -Name 'Path' -ErrorAction Stop).Path
            if ($value) { $dirs.Add($value) }
        } catch {
            # 这个视图里没装 7-Zip（或没有这个键）：正常情况，继续找下一个。
        }
    }

    if ($env:PATH) {
        foreach ($raw in $env:PATH.Split(';')) {
            $one = $raw.Trim()
            if ($one) { $dirs.Add($one) }
        }
    }

    foreach ($dir in $dirs) {
        if (-not $dir) { continue }
        if ((Test-Path -LiteralPath (Join-Path $dir '7z.exe')) -and
            (Test-Path -LiteralPath (Join-Path $dir '7z.dll'))) {
            return $dir
        }
    }
    return $null
}

$payloadDir = Find-SevenZipPayload
if (-not $payloadDir) {
    Fail ("找不到可用于内嵌的 7-Zip（需要同一个目录下同时有 7z.exe 与 7z.dll）。" +
          "请安装 7-Zip 25.00 或更高版本，或把 RERAR_7Z_DIR 指向一个解压出来的 7-Zip 目录。" +
          "没有内嵌兜底的 exe 违背「无 7-Zip 的机器上双击即用」这条承诺，因此这里直接构建失败。")
}

$payloadExe = Join-Path $payloadDir '7z.exe'
$payloadDll = Join-Path $payloadDir '7z.dll'

# 功能自检 + 版本下限 25.00。绝不能只查文件存在：一个被截断/缺 DLL/根本不是 7-Zip 的同名文件
# 一旦被嵌进产品，用户的每一个包都会倒在一个「跑不起来的内嵌引擎」上。
# （直接起进程跑 `i`，不是走 SevenZipRunner —— 后者是不变式 I5 的产品路径，且 `7z i` 不读 stdin。）
$probe = & $payloadExe i 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Fail ("内嵌用的 7-Zip 功能自检失败：" + $payloadExe + " 的 `7z i` 退出码 " + $LASTEXITCODE)
}
if ($probe -match '7-Zip (\d+)\.(\d+)') {
    $payloadMajor = [int]$Matches[1]
    $payloadMinor = [int]$Matches[2]
} else {
    Fail ("内嵌用的 7-Zip 报不出可解析的版本号：" + $payloadExe + "（`7z i` 输出里没有 7-Zip <版本> 横幅）")
}
if (($payloadMajor * 100 + $payloadMinor) -lt 2500) {
    Fail ("内嵌用的 7-Zip 版本 " + $payloadMajor + "." + $payloadMinor + " 低于下限 25.00" +
          "（该版本存在已被利用的符号链接目录穿越问题，而本工具解的是不可信压缩包）：" +
          "请改用 7-Zip 25.00 或更高版本，或用 RERAR_7Z_DIR 指向一份 ≥ 25.00 的副本。")
}

# 把载荷复制到 dist\obj（**相对路径且不含空格**，这样 /resource: 的写法在所有机器上都稳），
# 并就地算哈希 —— 算的是**真正会被嵌进去的那份字节**。
$objDir = Join-Path $dist 'obj'
if (-not (Test-Path -LiteralPath $objDir)) {
    New-Item -ItemType Directory -Path $objDir | Out-Null
}
$payloadExeCopy = Join-Path $objDir '7z.exe'
$payloadDllCopy = Join-Path $objDir '7z.dll'
Copy-Item -LiteralPath $payloadExe -Destination $payloadExeCopy -Force
Copy-Item -LiteralPath $payloadDll -Destination $payloadDllCopy -Force

$exeSha = (Get-FileHash -LiteralPath $payloadExeCopy -Algorithm SHA256).Hash.ToLowerInvariant()
$dllSha = (Get-FileHash -LiteralPath $payloadDllCopy -Algorithm SHA256).Hash.ToLowerInvariant()
$payloadBytes = (Get-Item -LiteralPath $payloadExeCopy).Length + (Get-Item -LiteralPath $payloadDllCopy).Length

# 生成的常量文件（部分类 EngineLocator）：期望哈希在**构建时**写死，运行期每次释放/使用前校验。
# 写进 dist\obj（构建产物目录，已被 .gitignore 覆盖），绝不落进 src\ —— 它是生成的，不是源码。
$generatedSource = 'dist\obj\EmbeddedEngine.g.cs'
# 注意每个拼接出来的元素都要**加括号**：在 PowerShell 的数组字面量里逗号比 + 结合得更紧，
# 不括号化的话 '前缀' + $hash + '后缀', 会被解析成三个数组元素（生成出的 .cs 直接编译失败，
# 报 CS1010「常量中有换行符」）。
$generatedLines = @(
    '// 本文件由 build\build.ps1 自动生成，请勿手工编辑（每次构建都会覆盖）。',
    '//',
    '// 内嵌 7-Zip 载荷的**期望 SHA-256**：运行期每次释放/使用前都拿它校验盘上那份实际文件，',
    '// 于是「被 AV 隔离、被截断、被替换」会当场暴露，而不是悄悄跑一个未经验证的二进制。',
    '// 值来自构建时真正被 /resource: 嵌进去的那两个文件。',
    'namespace Rerar.Core',
    '{',
    '    public static partial class EngineLocator',
    '    {',
    ('        public const string EmbeddedSha256 = "' + $exeSha + '";'),
    ('        public const string EmbeddedDllSha256 = "' + $dllSha + '";'),
    '    }',
    '}')
$generatedPath = Join-Path $root $generatedSource
Set-Content -LiteralPath $generatedPath -Value $generatedLines -Encoding UTF8

# /resource: 的名字（EngineLocator 按这两个名字回读资源）。路径用 dist\obj 下的相对形式：
# 既不含空格（不用赌 csc 对带空格路径的解析），也与 csc 的工作目录一致（Invoke-CscTarget 会
# Push-Location 到仓库根）。
$resourceExeRelative = 'dist\obj\7z.exe'
$resourceDllRelative = 'dist\obj\7z.dll'
$appResourceSwitches = @(
    ('/resource:' + $resourceExeRelative + ',sz.7z.exe'),
    ('/resource:' + $resourceDllRelative + ',sz.7z.dll')
)

Write-Host ("内嵌 7-Zip：" + $payloadDir + "（版本 " + ("{0}.{1:00}" -f $payloadMajor, $payloadMinor) +
            "，7z.exe " + $exeSha.Substring(0, 16) + "…，7z.dll " + $dllSha.Substring(0, 16) + "…）")

# 编译一个目标，返回 csc 退出码（异常路径一律按失败返回 1）。
# $extraRefs：可选的额外程序集引用（都是 .NET 自带的 BCL 程序集，不是第三方包、不装 NuGet）：
#   * Microsoft.VisualBasic.dll —— Task 9 的 src\Core\RecycleBinGuard.cs 用
#     FileSystem.DeleteFile(..., SendToRecycleBin) 走回收站删除（规格 §6.3 指定的机制）。
#     src\Core\*.cs 同时编进两个目标，故**应用目标和测试目标都要引**；csc.rsp 的默认引用里
#     没有 Microsoft.VisualBasic.dll，必须显式 /r:，否则 CS0234。
#   * System.IO.Compression.dll（仅测试目标）—— Task 5 的 wheel 回归夹具必须用 zip 写库
#     逐条写**文件**、不写父目录条目 —— 7-Zip 打目录树会补写目录条目，造不出真实 wheel 的形状。
# 命令形状见 docs/superpowers/plans/2026-10-02-recursive-extractor-gui.md
# （相对计划唯一的偏离：统一的 UTF-8 代码页开关，理由见文件头）。
#
# $extraSwitches：只给**某一个目标**的额外开关。Task 13 的 /resource: 就走这里 —— **只给应用目标**。
# 为什么不放进 $cscArgs 的公共部分（那是本任务最容易踩错的一步）：两个目标共享这一段，把
# /resource: 放进去就等于把 ~2.4 MB 的 7z.exe + 7z.dll 也塞进 dist\tests.exe —— 测试 exe 白白
# 胖一倍多，而任何人也看不出它为什么胖。测试目标不需要那份载荷：EngineLocator 在测试里从同目录的
# Rerar.exe 读同一份资源（见 src\Core\EngineLocator.cs 的 OpenResource），
# 于是 Engine.PayloadEmbeddedInAppTargetOnly 那条用例能把这条约束钉死。
function Invoke-CscTarget([string]$target, [string]$outName, [string[]]$sources, [string[]]$extraRefs, [string[]]$extraSwitches) {
    $cscArgs = @(
        '/nologo'
        '/codepage:65001'
        ('/target:' + $target)
        ('/out:dist\' + $outName)
    )
    if ($extraRefs) { $cscArgs += $extraRefs }
    if ($extraSwitches) { $cscArgs += $extraSwitches }
    $cscArgs += $sources

    $code = 1   # 保守默认值：异常路径一律按失败处理，避免 exit $null（= 0）
    Push-Location $root
    try {
        $log = & $csc @cscArgs 2>&1 | Out-String
        $code = $LASTEXITCODE
    } finally {
        Pop-Location
    }

    if ($log.Trim().Length -gt 0) { Write-Host $log.Trim() }
    if ($null -eq $code) { return 1 }
    return $code
}

# Microsoft.VisualBasic：Task 9 起 src\Core\RecycleBinGuard.cs 需要它（回收站删除），
# 而 src\Core\*.cs 两个目标都编，所以这条引用必须同时给两个目标。
$bclRefs = @('/reference:Microsoft.VisualBasic.dll')

# 目标 1：应用（GUI / CLI 双入口）。$appResourceSwitches 只在这一行出现 —— 见 Invoke-CscTarget。
$code = Invoke-CscTarget 'winexe' 'Rerar.exe' @('src\Core\*.cs', 'src\App\*.cs', $generatedSource) $bclRefs $appResourceSwitches
if ($code -ne 0) {
    Write-Host ("FAIL: 应用目标 csc 退出码 " + $code)
    if ($code -gt 0) { exit $code }
    exit 1
}

$exe = Join-Path $dist 'Rerar.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host ("FAIL: csc 未生成 " + $exe)
    exit 1
}

# /resource: 没生效的兜底检查：应用目标比内嵌载荷还小就一定有问题。查体积是刻意的「粗但可靠」——
# 它不需要反射、不依赖语言模式，代价是只能抓住「载荷完全没进去」这一档（资源名写错、字节被截断
# 这些更细的情形由测试用例 Engine.EmbeddedHashMatches / EmbeddedBinaryIsFunctional 钉住）。
$appBytes = (Get-Item -LiteralPath $exe).Length
if ($appBytes -le $payloadBytes) {
    Write-Host ("FAIL: " + $exe + " 只有 " + $appBytes + " 字节，比内嵌载荷 " + $payloadBytes +
                " 还小：/resource: 没有生效。")
    exit 1
}
Write-Host ("OK: " + $exe + "（" + $appBytes + " 字节，其中内嵌载荷 " + $payloadBytes + " 字节）")

# 目标 2：单元测试运行器（同一份 src\Core\*.cs，另加 src\Tests\*.cs）
# 测试目标比应用目标多一条引用：System.IO.Compression（zip 写库，见 Invoke-CscTarget 上的说明）。
# 第 5 个参数（额外开关）刻意**不传**：/resource: 只属于应用目标。
$code = Invoke-CscTarget 'exe' 'tests.exe' @('src\Core\*.cs', 'src\Tests\*.cs', $generatedSource) ($bclRefs + @('/reference:System.IO.Compression.dll'))
if ($code -ne 0) {
    Write-Host ("FAIL: 测试目标 csc 退出码 " + $code)
    if ($code -gt 0) { exit $code }
    exit 1
}

$testExe = Join-Path $dist 'tests.exe'
if (-not (Test-Path -LiteralPath $testExe)) {
    Write-Host ("FAIL: csc 未生成 " + $testExe)
    exit 1
}
Write-Host ("OK: " + $testExe + "（" + (Get-Item -LiteralPath $testExe).Length + " 字节，不含内嵌载荷）")

exit 0
