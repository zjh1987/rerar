# Rerar 冒烟测试（Task 0）：验证构建骨架能编出 CLI 入口并响应 --cli --selftest。
# 用法：powershell -File tests\smoke.ps1
# 通过：打印 PASS 并以 0 退出；失败：打印 FAIL 并以 1 退出。

$root = Split-Path -Parent $PSScriptRoot

function Fail([string]$message) {
    Write-Host ("FAIL: " + $message)
    exit 1
}

# 1) 构建脚本存在且编译成功
$buildScript = Join-Path $root 'build\build.ps1'
if (-not (Test-Path -LiteralPath $buildScript)) {
    Fail ("缺少构建脚本 " + $buildScript)
}

$buildLog = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $buildScript 2>&1 | Out-String
$buildExit = $LASTEXITCODE
if ($buildLog.Trim().Length -gt 0) { Write-Host $buildLog.Trim() }
if ($buildExit -ne 0) {
    Fail ("build.ps1 退出码 " + $buildExit + "，应为 0")
}
Write-Host "OK: build.ps1 退出 0"

# 2) 产物存在
$exe = Join-Path $root 'dist\Rerar.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    Fail ("缺少产物 " + $exe)
}
Write-Host ("OK: 产物存在 " + $exe)

# 3) CLI 契约：--cli --selftest 打印 version=<n> 并以 0 退出
$output = & $exe --cli --selftest 2>&1 | Out-String
$cliExit = $LASTEXITCODE
$line = $output.Trim()
if ($cliExit -ne 0) {
    Fail ("--cli --selftest 退出码 " + $cliExit + "，应为 0")
}
if ($line -notmatch '^version=\d+') {
    Fail ("--cli --selftest 输出 [" + $line + "] 不匹配 ^version=\d+")
}

Write-Host ("PASS: " + $line)
exit 0
