@echo off
chcp 65001 >nul 2>&1
setlocal enabledelayedexpansion

:: 配置
set "SEVENZIP=C:\Program Files\7-Zip\7z.exe"
set "FORMATS=*.zip *.rar *.7z *.z01 *.001 *.part1.rar"
set "MAX_LOOP=20"
set "DELETE_AFTER=0"  ::1删原包，0保留

echo ==============================================
echo 递归解压-自动新建同名文件夹存放
echo 每个压缩包单独建文件夹，杜绝同名覆盖
echo ==============================================
echo.

:loop
set "FOUND=0"
for /r %%i in (%FORMATS%) do (
    set "FOUND=1"
    :: 获取压缩包纯文件名（不含后缀）
    set "NAME=%%~ni"
    set "SAVE_DIR=%%~dpi!NAME!\"
    echo 正在解压：%%i  存放至：!SAVE_DIR!
    md "!SAVE_DIR!" >nul 2>&1
    :: 解压到新建文件夹
    "!SEVENZIP!" x "%%i" -o"!SAVE_DIR!" -y -bd
    :: 可选删除原压缩包
    if !DELETE_AFTER! equ 1 (
        del /f "%%i" >nul 2>&1
    )
)

if !FOUND! equ 1 (
    set /a MAX_LOOP-=1
    if !MAX_LOOP! gtr 0 goto loop
)

echo.
echo 全部嵌套压缩包解压完成！
echo ==============================================
pause