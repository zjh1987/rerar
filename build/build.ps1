# Rerar 构建脚本：直接调系统自带的 csc.exe（无 MSBuild、无 NuGet、无第三方库）。
# 一次编译两个目标：
#   dist\Rerar.exe  ← src\Core\*.cs + src\App\*.cs    （GUI/CLI 程序）
#   dist\tests.exe  ← src\Core\*.cs + src\Tests\*.cs  （无框架单元测试运行器）
# 任一目标编译失败即以非 0 退出，供 tests\smoke.ps1 等验收脚本判定。
# 说明：源码一律保存为「UTF-8 带 BOM」；csc 另加 /codepage:65001，
#       中文解码不再单靠「每个文件都恰好带 BOM」这一脆弱前提。

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

# 编译一个目标，返回 csc 退出码（异常路径一律按失败返回 1）。
# $extraRefs：可选的额外程序集引用（只给测试目标用）。测试需要 .NET 自带的
#   System.IO.Compression（BCL，不是第三方包）：Task 5 的 wheel 回归夹具必须用 zip 写库
#   逐条写**文件**、不写父目录条目 —— 7-Zip 打目录树会补写目录条目，造不出真实 wheel 的形状。
# 命令形状见 docs/superpowers/plans/2026-10-02-recursive-extractor-gui.md
# （相对计划唯一的偏离：统一的 UTF-8 代码页开关，理由见文件头）。
function Invoke-CscTarget([string]$target, [string]$outName, [string[]]$sources, [string[]]$extraRefs) {
    $cscArgs = @(
        '/nologo'
        '/codepage:65001'
        ('/target:' + $target)
        ('/out:dist\' + $outName)
    )
    if ($extraRefs) { $cscArgs += $extraRefs }
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

# 目标 1：应用（GUI / CLI 双入口）
$code = Invoke-CscTarget 'winexe' 'Rerar.exe' @('src\Core\*.cs', 'src\App\*.cs')
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
Write-Host ("OK: " + $exe)

# 目标 2：单元测试运行器（同一份 src\Core\*.cs，另加 src\Tests\*.cs）
# 额外引用只加在这里：应用目标不需要 zip 写库（见 Invoke-CscTarget 上的说明）。
$code = Invoke-CscTarget 'exe' 'tests.exe' @('src\Core\*.cs', 'src\Tests\*.cs') @('/reference:System.IO.Compression.dll')
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
Write-Host ("OK: " + $testExe)

exit 0
