# Rerar 构建脚本：直接调系统自带的 csc.exe（无 MSBuild、无 NuGet、无第三方库）。
# 编译失败时以非 0 退出，供 tests\smoke.ps1 等验收脚本判定。
# 说明：源码一律保存为「UTF-8 带 BOM」，csc 与 PowerShell 5.1 才能正确读取中文。

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
$exe = Join-Path $dist 'Rerar.exe'
if (Test-Path -LiteralPath $exe) {
    Remove-Item -LiteralPath $exe -Force
}

# 构建命令形状见 docs/superpowers/plans/2026-10-02-recursive-extractor-gui.md
# 本任务只用第一式，且暂不加 /win32manifest: 与 /resource:（由后续任务补）。
$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/out:dist\Rerar.exe'
    'src\Core\*.cs'
    'src\App\*.cs'
)

$code = 1   # 保守默认值：异常路径一律按失败处理，避免 exit $null（= 0）
Push-Location $root
try {
    $log = & $csc @cscArgs 2>&1 | Out-String
    $code = $LASTEXITCODE
} finally {
    Pop-Location
}

if ($log.Trim().Length -gt 0) { Write-Host $log.Trim() }

if ($code -ne 0) {
    Write-Host ("FAIL: csc 退出码 " + $code)
    exit $code
}

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host ("FAIL: csc 未生成 " + $exe)
    exit 1
}

Write-Host ("OK: " + $exe)
exit 0
