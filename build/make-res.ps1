# Rerar 版本资源生成器（Task 16）：不借助 rc.exe（本机没有 Visual Studio/SDK），直接按 Win32 的
# 「已编译资源」(.res) 二进制格式，手工产出一个 VERSIONINFO 资源，供 build\build.ps1 用
# /win32res: 编进 dist\Rerar.exe。in-box 的 csc.exe 没有任何能写 Win32 版本信息的命令行开关，
# /win32res: 要求的就是这份编译好的 .res —— 所以它只能被**生成**出来。
#
# 生成的版本块内容（中英双语 + 语言中立，共三张语言表 + Translation 变量表）：
#   0409（英文）：Company/Product/FileDescription/Copyright/FileVersion/ProductVersion/
#                InternalName/OriginalFilename
#   0804（中文）：同一组键的中文对照
#   0000（语言中立，代码页仍是 1200）：同一组键，内容取中文。**必须有**：.NET 的 FileVersionInfo
#                只按 Translation 的第一项去查一张表、且几乎不做语言回退，缺了它属性页/API 上
#                公司/产品/描述/版权会一起读成空串（详见下面 $translationValues 处的说明）。
#   版本号 = -Version 参数（默认 0.0.0.0）—— **必须与程序集自报的版本一致**：--selftest 打印
#   version=<程序集版本>（tests\smoke.ps1 的契约），资源管理器的属性页显示的是本资源里的
#   FileVersion/ProductVersion，两边各报各的就是两个答案（用例 Package.VersionInfoMatchesAssembly
#   把这条一致性钉死）。本仓库没有任何 AssemblyVersion 特性，csc 默认就是 0.0.0.0，故默认值取它。
#
# 【.res 外框的两个硬要求（实测得出，缺第一条即 CS1583「不是有效的 Win32 资源文件」）】
#   1) 文件必须以一条**空资源记录**开头：头两个 DWORD 是 DataSize=0、HeaderSize=0x20，其后 24
#      字节是那条记录自己的头部。解析方把开头 DWORD 读出来要求它等于 0（判据是「RC.EXE 的产物
#      都以一条没有数据的资源开头」），随后按 HeaderSize-8 跳过那条记录的其余字节 —— 也就是
#      说，真正的资源从**偏移 32** 才开始（llvm 把这段常量写作 WIN_RES_MAGIC_SIZE(16) +
#      WIN_RES_NULL_ENTRY_SIZE(16) = 32）。csc 用的解析器是 Roslyn 的
#      src\Compilers\Core\Portable\CvtRes.cs（CvtResFile.ReadResFile，逐行译自 cvtres.cpp），
#      它第一条检查就是这条；文件头的自检把这条约束同样钉死。
#   2) 每条记录的 HeaderSize 一律写 0x20（=32），即「从本条记录起点到数据起点的距离」，
#      **含**开头那两个长度字段本身。rc.exe（经 llvm-rc 的 HeaderSize = DataLoc - HeaderLoc）、
#      Roslyn 的 CvtRes.cs（字面量 0x00000020）三处写的都是这个值。写成 24（不含那 8 字节）
#      csc 也肯收（它只要求 HeaderSize ≥ 8），但那与所有真实工具的口径不一致，自检因此按 0x20 校验。
# 本任务第一次尝试只写 24 且没有开头那条空资源，csc 直接 CS1583；诊断过程与证据见
# task-16-report.md。
#
# 本脚本自带的「拼完立刻按格式重新解析、逐项回读核对」自检，加上 csc 接受、FileVersionInfo 能
# 读回全部字段，才是「诚实尝试」的完整验收。
#
# 用法：
#   powershell -File build\make-res.ps1 [-OutPath <path>] [-Version 0.0.0.0] [-ManifestPath <path>]
#                                     [-IconPath <path>]
#   （build\build.ps1 在每次构建时以 -OutPath dist\obj\rerar.res 调用它；dist\obj 已被 gitignore。）
#   -ManifestPath 给出时，应用清单（src\App\app.manifest）会作为第二条记录（RT_MANIFEST、ID 1）
#   一起编进这份 .res —— legacy csc 的 CS1564 明令禁止 /win32res: 与 /win32manifest: 同时使用，
#   微软文档给的正解就是「把清单放进 Win32 资源文件」。
#   -IconPath 给出时，应用图标（assets\rerar.ico）按同一道理拆成 RT_ICON × N + RT_GROUP_ICON
#   记录编进去 —— CS1565 同样禁止 /win32res: 与 /win32icon: 并存。于是本脚本产出的这一份 .res
#   就是应用目标的**全部** Win32 资源：版本块 + 清单 + 图标，/win32res: 一开关带全。
# 成功：写出 .res 并以 0 退出（自检全过）。失败：抛异常 / 以非 0 退出，绝不把半成品留在正式
# 路径上（先写 <OutPath>.building，自检通过后再改名 —— 与 TestEnv.Fixture 同一个约定）。
#
# PowerShell 5.1 兼容；本文件一律保存为「UTF-8 带 BOM」。

param(
    [string]$OutPath,
    [string]$Version = '0.0.0.0',
    [string]$ManifestPath,
    [string]$IconPath
)

$ErrorActionPreference = 'Stop'

# ======================================================================
# 版本块的实际内容（与 src\Tests\PackageTests.cs 里的常量保持一致；改这里必须改那里）
# ======================================================================

$FileDescriptionChinese = 'Rerar 递归解压'
$FileDescriptionEnglish = 'Rerar Recursive Extractor'
$CompanyNameChinese     = 'Rerar 项目'
$CompanyNameEnglish     = 'Rerar Project'
$ProductNameChinese     = 'Rerar 递归解压工具'
$ProductNameEnglish     = 'Rerar Recursive Extractor'
$CopyrightChinese       = '版权所有 (C) 2026 Rerar 项目'
$CopyrightEnglish       = 'Copyright (C) 2026 Rerar Project'
$InternalName           = 'Rerar.exe'
$OriginalFilename       = 'Rerar.exe'

# ======================================================================
# 小工具：字节流拼装
# ======================================================================

function Add-Bytes($list, [byte[]]$bytes) {
    foreach ($b in $bytes) { [void]$list.Add($b) }
}

function Add-Zeros($list, [int]$count) {
    for ($i = 0; $i -lt $count; $i++) { [void]$list.Add(0) }
}

function Add-Word($list, [int]$value) {
    [void]$list.Add([byte]($value -band 0xFF))
    [void]$list.Add([byte](($value -shr 8) -band 0xFF))
}

function Add-Dword($list, [uint32]$value) {
    Add-Bytes $list ([BitConverter]::GetBytes($value))
}

function Add-Byte($list, [byte]$value) {
    [void]$list.Add($value)
}

# 往 .res 缓冲里追加一条完整的资源记录（两个长度 + 头 + 数据），返回数据的独立副本
#（自检用它逐字节比对盘上结果）。数据长度必须是 4 的倍数 —— .res 的记录数据按 DWORD 对齐。
function Add-ResourceRecord($list, [byte[]]$source, [int]$dataOffset, [int]$dataLength, [int]$resourceType, [int]$resourceId) {
    if (($dataLength % 4) -ne 0) {
        throw ("资源记录（类型 " + $resourceType + "，ID " + $resourceId + "）的数据长度 " +
               $dataLength + " 不是 4 的倍数 —— .res 的记录数据必须 DWORD 对齐")
    }
    Add-Dword $list ([uint32]$dataLength)    # DataSize
    Add-Dword $list ([uint32]32)             # HeaderSize（=从记录起点到数据起点的距离，含这 8 字节）
    Add-Word $list 0xFFFF                    # Type 是**序号**
    Add-Word $list $resourceType
    Add-Word $list 0xFFFF                    # Name 是**序号**
    Add-Word $list $resourceId
    Add-Dword $list ([uint32]0)              # DataVersion
    Add-Word $list 0x0030                    # MemoryFlags = MOVEABLE | PURE
    Add-Word $list 0x0409                    # LanguageId
    Add-Dword $list ([uint32]0)              # Version
    Add-Dword $list ([uint32]0)              # Characteristics
    $slice = New-Object byte[] $dataLength
    [Array]::Copy($source, $dataOffset, $slice, 0, $dataLength)
    Add-Bytes $list $slice
    return ,$slice
}

# NUL 结尾的 UTF-16LE 字符串字节（版本资源里的 szKey / 字符串值都是这个编码）。
function Get-NullTerminatedUtf16([string]$text) {
    return [System.Text.Encoding]::Unicode.GetBytes(([string]$text + [string][char]0))
}

# 距离 4 字节对齐还差几个字节。
function Get-PadToDword([int]$length) {
    $rest = $length % 4
    if ($rest -eq 0) { return 0 }
    return 4 - $rest
}

# ======================================================================
# VERSIONINFO 数据块的节点
#
# 结构（全部 DWORD 对齐；每个节点的 wLength 都包含尾部对齐字节，于是
# 「下一个兄弟 = 本节点起点 + wLength」永远落在 4 字节边界上，子项链也正好填满父节点）：
#   VS_VERSIONINFO { wLength, wValueLength=52, wType=0, "VS_VERSION_INFO", pad,
#                    VS_FIXEDFILEINFO, StringFileInfo{...}, VarFileInfo{...} }
#   StringFileInfo { wLength, 0, 1, "StringFileInfo", pad, StringTable{...}, StringTable{...} }
#   StringTable    { wLength, 0, 1, "<8位十六进制语言+代码页>", pad, String{...}... }
#   String         { wLength, 字符数(含NUL), 1, "<键>", pad, "<值>\0", pad }
#   VarFileInfo    { wLength, 0, 1, "VarFileInfo", pad, Var{...} }
#   Var            { wLength, 字数, 0, "Translation", pad, DWORD(代码页<<16|语言ID)... }
# ======================================================================

function New-StringNode([string]$key, [string]$value) {
    $keyBytes = Get-NullTerminatedUtf16 $key
    $valueBytes = Get-NullTerminatedUtf16 $value

    $headLen = 6 + $keyBytes.Length
    $pad1 = Get-PadToDword $headLen
    $bodyLen = $headLen + $pad1 + $valueBytes.Length
    $pad2 = Get-PadToDword $bodyLen

    # 【wLength 不含尾部对齐填充】—— 这条不是风格问题：值末端必须**正好**等于 offset + wLength，
    # 填充字节虽然照样写出去（下一个兄弟节点仍从 4 字节边界开始），但不计进 wLength。
    # rc.exe 与 Roslyn 的 CvtRes.cs（SizeofVerString = PadKeyLen(cbKey) + cbValue + 6，且那句
    # 「writer.Write(new byte[PadToDword(cbVal) - cbVal])」是被注释掉的）都是这个口径，notepad.exe
    # 的版本块实测也逐节点如此（每个 String 节点的值末端 == 节点末端）。
    # 反过来（把填充计进 wLength，本任务第一次尝试的写法）VerQueryValue 仍然读得出字段，但
    # GetFileVersionInfo 会在规范校验上判定整块不可用 —— 于是 FileVersionInfo 把公司/产品/描述/
    # 版权**全部**读成空串，而单看 .res 结构和 csc 都挑不出毛病。自检因此新增「值末端 == 节点末端」。
    $node = New-Object System.Collections.Generic.List[byte]
    Add-Word $node $bodyLen                   # wLength（不含尾部对齐填充）
    Add-Word $node ($valueBytes.Length / 2)   # wValueLength：字符数（含结尾 NUL）
    Add-Word $node 1                          # wType：1 = 文本
    Add-Bytes $node $keyBytes
    Add-Zeros $node $pad1
    Add-Bytes $node $valueBytes
    Add-Zeros $node $pad2
    return ,$node.ToArray()
}

function New-ContainerNode([string]$key, [byte[][]]$children) {
    $keyBytes = Get-NullTerminatedUtf16 $key

    $headLen = 6 + $keyBytes.Length
    $pad1 = Get-PadToDword $headLen
    $childrenLen = 0
    foreach ($child in $children) { $childrenLen += $child.Length }
    $total = $headLen + $pad1 + $childrenLen
    $pad2 = Get-PadToDword $total

    $node = New-Object System.Collections.Generic.List[byte]
    Add-Word $node ($total + $pad2)
    Add-Word $node 0                          # wValueLength：容器节点没有自己的值
    Add-Word $node 1                          # wType：1 = 文本节点
    Add-Bytes $node $keyBytes
    Add-Zeros $node $pad1
    foreach ($child in $children) { Add-Bytes $node $child }
    Add-Zeros $node $pad2
    return ,$node.ToArray()
}

function New-VarNode([string]$key, [uint32[]]$values) {
    $keyBytes = Get-NullTerminatedUtf16 $key

    $headLen = 6 + $keyBytes.Length
    $pad1 = Get-PadToDword $headLen
    $bodyLen = $headLen + $pad1 + (4 * $values.Count)
    $pad2 = Get-PadToDword $bodyLen

    $node = New-Object System.Collections.Generic.List[byte]
    Add-Word $node ($bodyLen + $pad2)
    Add-Word $node (4 * $values.Count)        # wValueLength：以**字节**计（每个值是 语言ID+代码页 两个 WORD）
    Add-Word $node 0                          # wType：0 = 二进制
    Add-Bytes $node $keyBytes
    Add-Zeros $node $pad1
    foreach ($value in $values) { Add-Dword $node $value }
    Add-Zeros $node $pad2
    return ,$node.ToArray()
}

function New-FixedFileInfo([uint32]$fileVersionMs, [uint32]$fileVersionLs) {
    # 注意：PowerShell 的参数模式（不带括号的实参位置）里 [uint32]0x123 会被当成**字符串**，
    # 强制转换必须套一层括号进表达式模式 —— 下面每一行都遵守这一点。
    # 【一律写十六进制字面量】这里第一次尝试把 0xFEEF04BD 手写成了十进制 4277111997
    # （其实等于 0xFEEF8CBD），dwFileFlagsMask 也写错成 0x3B0F。签名错了的后果是
    # GetFileVersionInfoSize 直接返回 0 并置 ERROR_INVALID_DATA(13)：FileVersionInfo 把
    # 公司/产品/描述/版权**全部**读成空串，而 .res 结构、csc、本脚本的自检（自检当时用的是
    # 同一个错常量）都看不出问题。十六进制字面量没有转写这一步。
    $fixed = New-Object System.Collections.Generic.List[byte]
    Add-Dword $fixed ($ffiSignature)          # dwSignature = 0xFEEF04BD
    Add-Dword $fixed ([uint32]0x00010000)     # dwStrucVersion = 1.0（高字 1、低字 0）
    Add-Dword $fixed $fileVersionMs           # dwFileVersionMS
    Add-Dword $fixed $fileVersionLs           # dwFileVersionLS
    Add-Dword $fixed $fileVersionMs           # dwProductVersionMS
    Add-Dword $fixed $fileVersionLs           # dwProductVersionLS
    Add-Dword $fixed ([uint32]0x0000003F)     # dwFileFlagsMask = VS_FFI_FILEFLAGSMASK（rc.exe/Roslyn 同值）
    Add-Dword $fixed ([uint32]0)              # dwFileFlags
    Add-Dword $fixed ([uint32]0x00040004)     # dwFileOS = VOS_NT_WINDOWS32
    Add-Dword $fixed ([uint32]0x00000001)     # dwFileType = VFT_APP
    Add-Dword $fixed ([uint32]0)              # dwFileSubtype
    Add-Dword $fixed ([uint32]0)              # dwFileDateMS
    Add-Dword $fixed ([uint32]0)              # dwFileDateLS
    return ,$fixed.ToArray()
}

# VS_FIXEDFILEINFO 的签名。**写作一个具名常量**：写盘方与自检方都用它，手写十进制的事故
# （写在两边、错在两边）就没有第二次机会（见 New-FixedFileInfo 上的说明）。
# 注意不能直接写 [uint32]0xFEEF04BD：PowerShell 5.1 会先把这个字面量解析成**负的 Int32**
# （-17890115），再转 UInt32 就抛「值太大或太小」；用 32 位的 -shl 也照样溢出成负数。
# 先升到 Int64 再拼，最后转回 UInt32，绕开这两条。
$ffiSignature = [uint32]([int64]0xFEEF * 65536 + [int64]0x04BD)

# "0.0.0.0" → (MS, LS)：MS = 第 1、2 段拼成的 DWORD，LS = 第 3、4 段。
function Get-VersionDwords([string]$version) {
    $parts = $version.Split('.')
    if ($parts.Count -ne 4) {
        throw ("版本号必须是 a.b.c.d 四段数字，实际是「" + $version + "」")
    }
    $numbers = New-Object System.Collections.Generic.List[int]
    foreach ($part in $parts) {
        $n = 0
        if (-not [int]::TryParse($part, [ref]$n) -or $n -lt 0 -or $n -gt 65535) {
            throw ("版本号每段必须是 0–65535 的数字，「" + $version + "」里的「" + $part + "」不是")
        }
        $numbers.Add($n)
    }
    $ms = [uint32](($numbers[0] -shl 16) -bor $numbers[1])
    $ls = [uint32](($numbers[2] -shl 16) -bor $numbers[3])
    return ,@($ms, $ls)
}

# ======================================================================
# 组装 VERSIONINFO 数据块
# ======================================================================

$versionDwords = Get-VersionDwords $Version

# 每张语言表的 8 位十六进制键 = 4 位语言 ID + 4 位代码页；0x04B0 = 1200 = UTF-16。
#
# 【中立表（000004b0）为什么必须存在】.NET 的 FileVersionInfo（以及资源管理器「属性」页的回退
# 路径）不是「按 Translation 里的语言去挑表」，而是把 Translation 那个 DWORD 拼成一个**语言中立**
# 的键去查 \StringFileInfo\000004b0\…：csc 自己合成的版本块用的就是这一张表（见 PackageTests.cs
# 文件头），而 Tests.exe 上 FileVersionInfo 确实读得出来、本脚本只给 0409/0804 两张表时
# Rerar.exe 上四个字段全读成空串。两者一比，缺的就是这张表。故三张表并存：
#   000004b0 中立（内容取中文，机器不认语言时也至少显示中文而不是空白）
#   040904b0 英文    080404b0 中文
$neutralTable = New-ContainerNode '000004b0' @(
    (New-StringNode 'CompanyName' $CompanyNameChinese)
    (New-StringNode 'FileDescription' $FileDescriptionChinese)
    (New-StringNode 'FileVersion' $Version)
    (New-StringNode 'InternalName' $InternalName)
    (New-StringNode 'LegalCopyright' $CopyrightChinese)
    (New-StringNode 'OriginalFilename' $OriginalFilename)
    (New-StringNode 'ProductName' $ProductNameChinese)
    (New-StringNode 'ProductVersion' $Version)
)
$englishTable = New-ContainerNode '040904b0' @(
    (New-StringNode 'CompanyName' $CompanyNameEnglish)
    (New-StringNode 'FileDescription' $FileDescriptionEnglish)
    (New-StringNode 'FileVersion' $Version)
    (New-StringNode 'InternalName' $InternalName)
    (New-StringNode 'LegalCopyright' $CopyrightEnglish)
    (New-StringNode 'OriginalFilename' $OriginalFilename)
    (New-StringNode 'ProductName' $ProductNameEnglish)
    (New-StringNode 'ProductVersion' $Version)
)
$chineseTable = New-ContainerNode '080404b0' @(
    (New-StringNode 'CompanyName' $CompanyNameChinese)
    (New-StringNode 'FileDescription' $FileDescriptionChinese)
    (New-StringNode 'FileVersion' $Version)
    (New-StringNode 'InternalName' $InternalName)
    (New-StringNode 'LegalCopyright' $CopyrightChinese)
    (New-StringNode 'OriginalFilename' $OriginalFilename)
    (New-StringNode 'ProductName' $ProductNameChinese)
    (New-StringNode 'ProductVersion' $Version)
)
$stringFileInfo = New-ContainerNode 'StringFileInfo' @($chineseTable, $englishTable, $neutralTable)

# Translation 的每个 DWORD = (代码页 << 16) | 语言 ID，代码页 1200 = 0x04B0。
# **一律写十六进制字面量**：这里第一次尝试把 0x04B00409 / 0x04B00804 手工转成了十进制
# 78659081 / 78659716（其实等于 0x04B03E09 / 0x04B04084）—— .res 结构没问题、csc 也收，
# 但 FileVersionInfo 读到 Translation 后找不到任何一张语言表，公司/产品/描述/版权全部读成
# 空串：一个只在「属性」页上才看得见的静默失败。十六进制字面量没有转写这一步。
#
# 【顺序不是随意的】.NET 的 FileVersionInfo（GetLanguageAndCodePage）只取 Translation 的
# **第一个** DWORD 当作语言+代码页，再去查 \StringFileInfo\<lang><codepage>\…，并且**不做**
# 语言回退：这张表不存在，公司/产品/描述/版权就一起读成空串（本任务实测：中文表排第一 → 
# 读出中文；把中立表排到第一 → 三个字段全空，因为那台机器上没有 000004b0 之外的键）。
# 所以中英两张表都要在，且第一项 Translation 指向**中文表** —— 本工具的用户可见文案是中文，
# 属性页/FileVersionInfo 应当显示中文，英文表留给按 UI 语言挑表的调用方（资源管理器）。
$translationValues = @([uint32]0x04B00804, [uint32]0x04B00409, [uint32]0x04B00000)

# 列出三张表，资源管理器 / FileVersionInfo 据此按 UI 语言挑表。
$varFileInfo = New-ContainerNode 'VarFileInfo' @(
    (New-VarNode 'Translation' $translationValues)
)

# 根节点：头 + VS_FIXEDFILEINFO + 两个子树。
$fixedInfo = New-FixedFileInfo $versionDwords[0] $versionDwords[1]
$rootKey = Get-NullTerminatedUtf16 'VS_VERSION_INFO'
$headLen = 6 + $rootKey.Length
$pad1 = Get-PadToDword $headLen
$childrenLen = $stringFileInfo.Length + $varFileInfo.Length
$total = $headLen + $pad1 + $fixedInfo.Length + $childrenLen
$padEnd = Get-PadToDword $total

$root = New-Object System.Collections.Generic.List[byte]
Add-Word $root ($total + $padEnd)
Add-Word $root $fixedInfo.Length           # wValueLength = 52（VS_FIXEDFILEINFO 的字节数）
Add-Word $root 0                           # wType：0 = 二进制值
Add-Bytes $root $rootKey
Add-Zeros $root $pad1
Add-Bytes $root $fixedInfo
Add-Bytes $root $stringFileInfo
Add-Bytes $root $varFileInfo
Add-Zeros $root $padEnd
$versionData = $root.ToArray()

# ======================================================================
# 包一层资源记录（.res 文件 = 一串「记录」；一条记录 = 两个长度字段 + 头 + 数据）
# ======================================================================

$record = New-Object System.Collections.Generic.List[byte]

# 开头的空资源记录：DataSize=0 + HeaderSize=0x20 + 24 字节的记录头（全 0）。
# 这一条不是摆设 —— csc 的 .res 解析器读到的第一个 DWORD 必须是 0，否则直接判
# 「不是有效的 Win32 资源文件」(CS1583)；真正的资源从偏移 32 开始。详见文件头第 1 条。
Add-Dword $record ([uint32]0)     # DataSize = 0（这条记录没有数据）
Add-Dword $record ([uint32]32)    # HeaderSize = 0x20（解析方按它跳过其余 24 字节）
Add-Zeros $record 24             # 那条记录自己的头部：Type/Name/DataVersion/… 全 0

Add-Dword $record ([uint32]$versionData.Length)   # DataSize
Add-Dword $record ([uint32]32)                    # HeaderSize = 0x20（见文件头第 2 条）
Add-Word $record 0xFFFF                           # Type 是**序号**
Add-Word $record 0x0010                           # RT_VERSION = 16
Add-Word $record 0xFFFF                           # Name 是**序号**
Add-Word $record 0x0001                           # VERSIONINFO 资源的 ID 必须是 1
Add-Dword $record ([uint32]0)                     # DataVersion
Add-Word $record 0x0030                           # MemoryFlags = MOVEABLE | PURE
Add-Word $record 0x0409                           # LanguageId
Add-Dword $record ([uint32]0)                     # Version
Add-Dword $record ([uint32]0)                     # Characteristics
Add-Bytes $record $versionData

# ---------------------------------------------------------------------
# 可选的第二条记录：应用清单（RT_MANIFEST = 24，ID 1）。
#
# legacy csc 的 CS1564 禁止 /win32res: 与 /win32manifest: 同时使用（「资源冲突选项」），微软
# 文档给的正解就是把清单**放进 .res**。清单字节原样嵌入；唯一的内容改动是**在 XML 结尾补 0–3
# 个空格**，让记录数据长度凑成 4 的倍数 —— 结尾空白是合法 XML（schema 解析不受影响），却让
# 每条记录的总长都对齐 DWORD，任何按格式顺序读取 .res 的实现（包括 csc/CVTRES）都不会走歪。
# ---------------------------------------------------------------------
$manifestBytes = $null
if (-not [string]::IsNullOrEmpty($ManifestPath)) {
    if (-not (Test-Path -LiteralPath $ManifestPath)) {
        throw ("找不到清单文件 " + $ManifestPath + "：-ManifestPath 指向的文件必须存在")
    }
    $manifestBytes = [System.IO.File]::ReadAllBytes($ManifestPath)
    if ($manifestBytes.Length -eq 0) {
        throw ("清单文件 " + $ManifestPath + " 是空的")
    }
    while (($manifestBytes.Length % 4) -ne 0) {
        $manifestBytes += [byte]0x20        # 尾部空格：合法 XML 空白，只为对齐
    }

    Add-Dword $record ([uint32]$manifestBytes.Length)   # DataSize
    Add-Dword $record ([uint32]32)                      # HeaderSize = 0x20（见文件头第 2 条）
    Add-Word $record 0xFFFF
    Add-Word $record 0x0018                             # RT_MANIFEST = 24
    Add-Word $record 0xFFFF
    Add-Word $record 0x0001                             # 清单资源的 ID 必须是 1（加载器只认它）
    Add-Dword $record ([uint32]0)
    Add-Word $record 0x0030
    Add-Word $record 0x0409
    Add-Dword $record ([uint32]0)
    Add-Dword $record ([uint32]0)
    Add-Bytes $record $manifestBytes
}

# ---------------------------------------------------------------------
# 可选的其余记录：应用图标（assets\rerar.ico）→ RT_ICON × N + RT_GROUP_ICON。
#
# legacy csc 的 CS1565 禁止 /win32res: 与 /win32icon: 同时使用，所以图标也必须进这份 .res。
# .ico 容器的每一帧（BITMAPINFOHEADER + XOR 位图 + AND 掩码）原样成为一条 RT_ICON 记录
#（ID 从 1 起），再写一条 RT_GROUP_ICON（ID 1）：GRPICONDIR + 每帧一条 GRPICONENTRY ——
# 与 .ico 的目录项同形，只是「数据在文件里的偏移」换成了「对应 RT_ICON 的资源 ID」。
# ---------------------------------------------------------------------
$iconFramesById = @{}          # 资源 ID → 帧字节（自检用）
$iconGroupBytes = $null        # RT_GROUP_ICON 的完整数据（自检用）
$iconFrameCount = 0
if (-not [string]::IsNullOrEmpty($IconPath)) {
    if (-not (Test-Path -LiteralPath $IconPath)) {
        throw ("找不到图标文件 " + $IconPath + "：-IconPath 指向的文件必须存在")
    }
    $icoBytes = [System.IO.File]::ReadAllBytes($IconPath)
    if ($icoBytes.Length -lt 6) {
        throw ("图标文件 " + $IconPath + " 太小（" + $icoBytes.Length + " 字节），不是 .ico")
    }
    $icoReserved = [BitConverter]::ToUInt16($icoBytes, 0)
    $icoType = [BitConverter]::ToUInt16($icoBytes, 2)
    $icoCount = [BitConverter]::ToUInt16($icoBytes, 4)
    if ($icoReserved -ne 0 -or $icoType -ne 1 -or $icoCount -lt 1) {
        throw ("图标文件 " + $IconPath + " 不是合法的 .ico（reserved=" + $icoReserved +
               "，type=" + $icoType + "，帧数=" + $icoCount + "）")
    }

    $group = New-Object System.Collections.Generic.List[byte]
    Add-Word $group 0                          # GRPICONDIR：reserved = 0
    Add-Word $group 1                          # type = 1（图标）
    Add-Word $group $icoCount                  # 帧数

    for ($i = 0; $i -lt $icoCount; $i++) {
        $e = 6 + 16 * $i
        $width = $icoBytes[$e]
        $height = $icoBytes[$e + 1]
        $colorCount = $icoBytes[$e + 2]
        $reserved = $icoBytes[$e + 3]
        $planes = [BitConverter]::ToUInt16($icoBytes, $e + 4)
        $bitCount = [BitConverter]::ToUInt16($icoBytes, $e + 6)
        $bytesInRes = [BitConverter]::ToUInt32($icoBytes, $e + 8)
        $imageOffset = [BitConverter]::ToUInt32($icoBytes, $e + 12)
        if (($imageOffset + $bytesInRes) -gt $icoBytes.Length) {
            throw ("图标文件 " + $IconPath + " 的第 " + ($i + 1) + " 帧越出了文件尾（不是合法 .ico）")
        }

        # 帧字节原样成为一条 RT_ICON 记录，ID 从 1 起（组里按这个 ID 引用）。
        $resourceId = $i + 1
        $iconFramesById[$resourceId] = Add-ResourceRecord $record $icoBytes $imageOffset $bytesInRes 0x0003 $resourceId

        # GRPICONENTRY：与 .ico 目录项同字段，dwImageOffset 换成 WORD 的资源 ID。
        Add-Byte $group $width
        Add-Byte $group $height
        Add-Byte $group $colorCount
        Add-Byte $group $reserved
        Add-Word $group $planes
        Add-Word $group $bitCount
        Add-Dword $group $bytesInRes
        Add-Word $group $resourceId
    }

    $iconGroupBytes = Add-ResourceRecord $record $group.ToArray() 0 $group.Count 0x000E 1    # RT_GROUP_ICON = 14
    $iconFrameCount = $icoCount
}

# ======================================================================
# 自检：把刚拼出来的字节**当输入重新解析一遍**，逐项核对。这是「诚实尝试」的底线：
# 任何一项对不上都当场抛异常，绝不把自检不过的 .res 写到正式路径上。
# ======================================================================

# 读 NUL 结尾的 UTF-16LE 字符串，返回 @(文本, 读取结束后的偏移)。
#（不用 [ref]：PowerShell 里 [ref] 的往返只在被调方的参数上生效，调用方的普通变量
#  不会变成引用对象 —— 用「返回新偏移」的写法反而直白。）
function Read-NullTerminatedUtf16([byte[]]$data, [int]$offset) {
    $sb = New-Object System.Text.StringBuilder
    $p = $offset
    while ($p + 1 -lt $data.Length) {
        $ch = [BitConverter]::ToUInt16($data, $p)
        $p += 2
        if ($ch -eq 0) { break }
        [void]$sb.Append([char]$ch)
    }
    return ,@($sb.ToString(), $p)
}

# 解析一个 String 节点，返回 @(键, 值, wLength)，并顺带核对 wValueLength 与 wType。
function Read-StringNode([byte[]]$data, [int]$offset, [int]$nodeEnd) {
    $wLength = [BitConverter]::ToUInt16($data, $offset)
    $wValueLength = [BitConverter]::ToUInt16($data, $offset + 2)
    $wType = [BitConverter]::ToUInt16($data, $offset + 4)
    if ($wType -ne 1) {
        throw ("自检失败：String 节点（偏移 " + $offset + "）的 wType=" + $wType + "，期望 1")
    }

    $keyPair = Read-NullTerminatedUtf16 $data ($offset + 6)
    $key = $keyPair[0]
    $p = $keyPair[1] + (Get-PadToDword ($keyPair[1] - $offset))
    $valuePair = Read-NullTerminatedUtf16 $data $p
    $value = $valuePair[0]

    if ($wValueLength -ne ($value.Length + 1)) {
        throw ("自检失败：String「" + $key + "」的 wValueLength=" + $wValueLength +
               "，与实际字符数（含 NUL）" + ($value.Length + 1) + " 不一致")
    }
    if (($offset + $wLength) -gt $nodeEnd) {
        throw ("自检失败：String「" + $key + "」越出了父表（" + $nodeEnd + "）")
    }
    # 规范要求：值的最后一个字节（含结尾 NUL）**正好**落在节点末端 —— 也就是 wLength 不含尾部
    # 对齐填充。这一条是 GetFileVersionInfo 判定整块可用的关键（见 New-StringNode 上的说明）；
    # 少了它，FileVersionInfo 会把所有字段读成空串而结构自检却全绿。
    $valueEnd = $p + (($value.Length + 1) * 2)
    if ($valueEnd -ne ($offset + $wLength)) {
        throw ("自检失败：String「" + $key + "」的值末端在 " + $valueEnd +
               "，节点末端在 " + ($offset + $wLength) + " —— wLength 不得包含尾部对齐填充")
    }
    return ,@($key, $value, $wLength)
}

function Test-VersionData([byte[]]$data, [string]$expectedVersion) {
    if (($data.Length % 4) -ne 0) { throw ("自检失败：数据块总长 " + $data.Length + " 不是 4 的倍数") }

    # --- 根节点 ---
    $rootLength = [BitConverter]::ToUInt16($data, 0)
    if ($rootLength -ne $data.Length) {
        throw ("自检失败：根节点 wLength=" + $rootLength + "，与数据块总长 " + $data.Length + " 不一致")
    }
    $rootValueLength = [BitConverter]::ToUInt16($data, 2)
    if ($rootValueLength -ne 52) {
        throw ("自检失败：根节点 wValueLength=" + $rootValueLength + "，期望 52（VS_FIXEDFILEINFO）")
    }

    $rootPair = Read-NullTerminatedUtf16 $data 6
    if ($rootPair[0] -ne 'VS_VERSION_INFO') {
        throw ("自检失败：根键是「" + $rootPair[0] + "」，期望 VS_VERSION_INFO")
    }
    $cursor = $rootPair[1] + (Get-PadToDword $rootPair[1])

    # --- VS_FIXEDFILEINFO ---
    $signature = [BitConverter]::ToUInt32($data, $cursor)
    if ($signature -ne $ffiSignature) {
        throw ("自检失败：VS_FIXEDFILEINFO 签名是 0x" + $signature.ToString('X8') +
               "，期望 0x" + $ffiSignature.ToString('X8'))
    }
    $expectedDwords = Get-VersionDwords $expectedVersion
    $fileVersionMs = [BitConverter]::ToUInt32($data, $cursor + 8)
    $fileVersionLs = [BitConverter]::ToUInt32($data, $cursor + 12)
    $productVersionMs = [BitConverter]::ToUInt32($data, $cursor + 16)
    $productVersionLs = [BitConverter]::ToUInt32($data, $cursor + 20)
    if ($fileVersionMs -ne $expectedDwords[0] -or $fileVersionLs -ne $expectedDwords[1] -or
        $productVersionMs -ne $expectedDwords[0] -or $productVersionLs -ne $expectedDwords[1]) {
        throw ("自检失败：二进制版本号与期望「" + $expectedVersion + "」不一致")
    }
    $cursor += 52

    # --- 根的两个子树：StringFileInfo（双语字符串表）与 VarFileInfo（Translation） ---
    $strings = @{}
    $tableKeys = @()
    $translations = @()

    $child = $cursor
    while ($child + 6 -le $data.Length) {
        $nodeLength = [BitConverter]::ToUInt16($data, $child)
        if ($nodeLength -le 0) { throw ("自检失败：偏移 " + $child + " 处出现长度为 0 的节点") }
        $nodeEnd = $child + $nodeLength

        $nodePair = Read-NullTerminatedUtf16 $data ($child + 6)
        if ($nodePair[0] -ne 'StringFileInfo' -and $nodePair[0] -ne 'VarFileInfo') {
            throw ("自检失败：根下出现了意外的节点「" + $nodePair[0] + "」")
        }
        $p = $nodePair[1] + (Get-PadToDword ($nodePair[1] - $child))

        if ($nodePair[0] -eq 'StringFileInfo') {
            while ($p + 6 -le $nodeEnd) {
                $tableLength = [BitConverter]::ToUInt16($data, $p)
                if ($tableLength -le 0) { throw ("自检失败：语言表（偏移 " + $p + "）长度为 0") }
                $tableEnd = $p + $tableLength

                $tablePair = Read-NullTerminatedUtf16 $data ($p + 6)
                $tableKey = $tablePair[0]
                $tableKeys += $tableKey
                $q = $tablePair[1] + (Get-PadToDword ($tablePair[1] - $p))

                while ($q + 6 -le $tableEnd) {
                    $entry = Read-StringNode $data $q $tableEnd
                    $strings[$entry[0] + '@' + $tableKey] = $entry[1]
                    # wLength 不含尾部填充，故下一个兄弟节点要**对齐到 4 字节边界**再找
                    #（rc.exe / Roslyn 的写法；直接 $q += wLength 会落在填充字节中间）。
                    $q += $entry[2]
                    $q += (Get-PadToDword $q)
                }
                if ($q -ne $tableEnd) {
                    throw ("自检失败：语言表 " + $tableKey + " 的子项链停在 " + $q + "，没有正好填满 " + $tableEnd)
                }
                $p = $tableEnd
            }
            if ($p -ne $nodeEnd) {
                throw ("自检失败：StringFileInfo 的语言表链停在 " + $p + "，没有正好填满 " + $nodeEnd)
            }
        }
        else {
            while ($p + 6 -le $nodeEnd) {
                $varLength = [BitConverter]::ToUInt16($data, $p)
                if ($varLength -le 0) { throw ("自检失败：Var 节点（偏移 " + $p + "）长度为 0") }
                $varEnd = $p + $varLength

                $varPair = Read-NullTerminatedUtf16 $data ($p + 6)
                if ($varPair[0] -ne 'Translation') {
                    throw ("自检失败：Var 键是「" + $varPair[0] + "」，期望 Translation")
                }
                $varValueLength = [BitConverter]::ToUInt16($data, $p + 2)
                if ($varValueLength -ne (4 * $translationValues.Count)) {
                    throw ("自检失败：Var「Translation」的 wValueLength=" + $varValueLength +
                           "，期望 " + (4 * $translationValues.Count) + "（每个 语言ID+代码页 对占 4 字节）")
                }
                $q = $varPair[1] + (Get-PadToDword ($varPair[1] - $p))
                $valueEnd = $q + $varValueLength
                if ($valueEnd -gt $varEnd) {
                    throw ("自检失败：Var「Translation」的值（" + $varValueLength + " 字节）越出了节点 " + $varEnd)
                }
                while ($q + 4 -le $valueEnd) {
                    $translations += [BitConverter]::ToUInt32($data, $q)
                    $q += 4
                }
                $p = $varEnd
            }
            if ($p -ne $nodeEnd) {
                throw ("自检失败：VarFileInfo 的子链停在 " + $p + "，没有正好填满 " + $nodeEnd)
            }
        }

        $child = $nodeEnd
    }
    if ($child -ne $data.Length) {
        throw ("自检失败：根的子节点链停在 " + $child + "，没有正好填满数据块（" + $data.Length + "）")
    }

    # --- 逐项核对内容 ---
    foreach ($t in @('000004b0', '040904b0', '080404b0')) {
        if ($tableKeys -notcontains $t) {
            throw ("自检失败：缺少语言表 " + $t + "（实际：" + ($tableKeys -join '、') + "）")
        }
    }
    $expectedStrings = @{
        'CompanyName@000004b0'      = $CompanyNameChinese
        'FileDescription@000004b0'  = $FileDescriptionChinese
        'FileVersion@000004b0'      = $expectedVersion
        'InternalName@000004b0'     = $InternalName
        'LegalCopyright@000004b0'   = $CopyrightChinese
        'OriginalFilename@000004b0' = $OriginalFilename
        'ProductName@000004b0'      = $ProductNameChinese
        'ProductVersion@000004b0'   = $expectedVersion
        'CompanyName@040904b0'      = $CompanyNameEnglish
        'FileDescription@040904b0'  = $FileDescriptionEnglish
        'FileVersion@040904b0'      = $expectedVersion
        'InternalName@040904b0'     = $InternalName
        'LegalCopyright@040904b0'   = $CopyrightEnglish
        'OriginalFilename@040904b0' = $OriginalFilename
        'ProductName@040904b0'      = $ProductNameEnglish
        'ProductVersion@040904b0'   = $expectedVersion
        'CompanyName@080404b0'      = $CompanyNameChinese
        'FileDescription@080404b0'  = $FileDescriptionChinese
        'FileVersion@080404b0'      = $expectedVersion
        'InternalName@080404b0'     = $InternalName
        'LegalCopyright@080404b0'   = $CopyrightChinese
        'OriginalFilename@080404b0' = $OriginalFilename
        'ProductName@080404b0'      = $ProductNameChinese
        'ProductVersion@080404b0'   = $expectedVersion
    }
    foreach ($key in $expectedStrings.Keys) {
        if (-not $strings.ContainsKey($key)) {
            throw ("自检失败：缺少字符串 " + $key)
        }
        if ($strings[$key] -ne $expectedStrings[$key]) {
            throw ("自检失败：" + $key + " 是「" + $strings[$key] + "」，期望「" + $expectedStrings[$key] + "」")
        }
    }
    if ($strings.Count -ne $expectedStrings.Count) {
        throw ("自检失败：字符串条目数是 " + $strings.Count + "，期望 " + $expectedStrings.Count + "（多了意外的键）")
    }
    if ($translations.Count -ne $translationValues.Count) {
        throw ("自检失败：Translation 表有 " + $translations.Count + " 项，期望 " + $translationValues.Count + " 项")
    }
    for ($i = 0; $i -lt $translationValues.Count; $i++) {
        if ($translations[$i] -ne $translationValues[$i]) {
            throw ("自检失败：Translation 表是 " + (($translations | ForEach-Object { '0x' + $_.ToString('X8') }) -join '、') +
                   "，期望 " + (($translationValues | ForEach-Object { '0x' + $_.ToString('X8') }) -join '、'))
        }
    }
}

Test-VersionData $versionData $Version

# ======================================================================
# 写盘：先写 <OutPath>.building，对**盘上那份字节**再自检一次，然后才改名到位
#（排除「拼装正确但写盘出了岔子」这种意外；半成品绝不以正式名存在）。
# ======================================================================

if ([string]::IsNullOrEmpty($OutPath)) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $OutPath = Join-Path (Join-Path $repoRoot 'dist\obj') 'rerar.res'
}
$outDir = Split-Path -Parent $OutPath
if (-not [string]::IsNullOrEmpty($outDir) -and -not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

$buildingPath = $OutPath + '.building'
[System.IO.File]::WriteAllBytes($buildingPath, $record.ToArray())

# 盘上自检：按 .res 的记录结构**顺序走完整个文件** —— 每条记录的头、对齐、类型、ID、数据都要
# 说得通：VERSIONINFO 数据重新过一遍 Test-VersionData；清单数据与源文件逐字节一致（BOM 在内）。
$written = [System.IO.File]::ReadAllBytes($buildingPath)
if ($written.Length -ne $record.Count) {
    throw ("自检失败：写盘后的文件大小变了（" + $written.Length + " ≠ " + $record.Count + "）")
}

$pos = 0
$seenVersion = 0
$seenManifest = 0
$seenIcons = 0
$seenGroup = 0

# 开头必须恰好是那条空资源记录：DataSize=0、HeaderSize=0x20，其后 24 字节为它的头部。
# 这正是 csc 判「是不是 .RES」的第一条依据（见文件头第 1 条），所以这里逐项核对而不是跳过。
if ($written.Length -lt 32) {
    throw ("自检失败：写出的 .res 只有 " + $written.Length + " 字节，连开头的空资源记录都放不下")
}
$nullDataSize = [BitConverter]::ToUInt32($written, 0)
$nullHeaderSize = [BitConverter]::ToUInt32($written, 4)
if ($nullDataSize -ne 0) {
    throw ("自检失败：文件开头的 DataSize=" + $nullDataSize + "，期望 0（.res 必须以空资源记录开头）")
}
if ($nullHeaderSize -ne 32) {
    throw ("自检失败：开头空资源记录的 HeaderSize=" + $nullHeaderSize + "，期望 32")
}
for ($i = 8; $i -lt 32; $i++) {
    if ($written[$i] -ne 0) {
        throw ("自检失败：开头空资源记录的第 " + ($i - 8) + " 个头部字节是 " + $written[$i] + "，期望 0")
    }
}
$pos = 32

while ($pos -lt $written.Length) {
    if ($pos + 8 -gt $written.Length) {
        throw ("自检失败：偏移 " + $pos + " 处放不下记录头（文件尾有残缺字节）")
    }
    $recordDataSize = [BitConverter]::ToUInt32($written, $pos)
    $recordHeaderSize = [BitConverter]::ToUInt32($written, $pos + 4)
    if ($recordHeaderSize -ne 32) {
        throw ("自检失败：偏移 " + $pos + " 处的记录 HeaderSize=" + $recordHeaderSize + "，期望 32")
    }
    if (($recordDataSize % 4) -ne 0) {
        throw ("自检失败：偏移 " + $pos + " 处的记录 DataSize=" + $recordDataSize + " 不是 4 的倍数")
    }
    $recordTypeFlag = [BitConverter]::ToUInt16($written, $pos + 8)
    $recordType = [BitConverter]::ToUInt16($written, $pos + 10)
    $recordNameFlag = [BitConverter]::ToUInt16($written, $pos + 12)
    $recordNameId = [BitConverter]::ToUInt16($written, $pos + 14)
    if ($recordTypeFlag -ne 0xFFFF -or $recordNameFlag -ne 0xFFFF) {
        throw ("自检失败：偏移 " + $pos + " 处的记录 Type/Name 不是序号写法")
    }

    # HeaderSize 就是「记录起点 → 数据起点」的距离（含开头那两个长度字段），故数据从 pos+HeaderSize 起。
    $dataStart = $pos + [int]$recordHeaderSize
    $dataEnd = $dataStart + [int]$recordDataSize
    if ($dataEnd -gt $written.Length) {
        throw ("自检失败：偏移 " + $pos + " 处的记录数据越出了文件尾")
    }

    if ($recordType -eq 0x0010) {
        # VERSIONINFO 资源的 ID 必须是 1（csc/CVTRES 按 RT_VERSION + ID 1 取它合成 Win32 版本块）。
        if ([int]$recordNameId -ne 1) {
            throw ("自检失败：VERSIONINFO 记录的 ID 是 " + $recordNameId + "，期望 1")
        }
        $writtenData = New-Object byte[] $recordDataSize
        [Array]::Copy($written, $dataStart, $writtenData, 0, $recordDataSize)
        Test-VersionData $writtenData $Version
        $seenVersion++
    }
    elseif ($recordType -eq 0x0018) {
        # 清单资源的 ID 必须是 1（加载器只认 RT_MANIFEST + ID 1）。
        if ([int]$recordNameId -ne 1) {
            throw ("自检失败：RT_MANIFEST 记录的 ID 是 " + $recordNameId + "，期望 1")
        }
        if ($null -eq $manifestBytes) {
            throw ("自检失败：文件里出现了 RT_MANIFEST 记录，但本次没有要求嵌入清单")
        }
        if ($recordDataSize -ne $manifestBytes.Length) {
            throw ("自检失败：清单记录的数据长 " + $recordDataSize + "，与源文件（对齐后）" +
                   $manifestBytes.Length + " 不一致")
        }
        for ($i = 0; $i -lt $manifestBytes.Length; $i++) {
            if ($written[$dataStart + $i] -ne $manifestBytes[$i]) {
                throw ("自检失败：清单记录的数据在偏移 " + $i + " 处与源文件不一致")
            }
        }
        $seenManifest++
    }
    elseif ($recordType -eq 0x0003) {
        # RT_ICON：帧字节必须与 .ico 里对应的那一帧逐字节一致。
        #（键一律转 [int]：UInt16 的 1 与 Int32 的 1 在 PowerShell 哈希表里是两个不相等的键。）
        if ($iconFramesById.Count -eq 0) {
            throw ("自检失败：文件里出现了 RT_ICON 记录，但本次没有要求嵌入图标")
        }
        if (-not $iconFramesById.ContainsKey([int]$recordNameId)) {
            throw ("自检失败：RT_ICON 记录的 ID " + $recordNameId + " 不在生成的 ID 集合里")
        }
        $frame = $iconFramesById[[int]$recordNameId]
        if ($recordDataSize -ne $frame.Length) {
            throw ("自检失败：RT_ICON（ID " + $recordNameId + "）的数据长 " + $recordDataSize +
                   "，与帧字节 " + $frame.Length + " 不一致")
        }
        for ($i = 0; $i -lt $frame.Length; $i++) {
            if ($written[$dataStart + $i] -ne $frame[$i]) {
                throw ("自检失败：RT_ICON（ID " + $recordNameId + "）的数据在偏移 " + $i + " 处与帧字节不一致")
            }
        }
        $seenIcons++
    }
    elseif ($recordType -eq 0x000E) {
        # RT_GROUP_ICON：GRPICONDIR + GRPICONENTRY 逐字节一致；资源 ID 固定为 1（第一个图标组）。
        if ($null -eq $iconGroupBytes) {
            throw ("自检失败：文件里出现了 RT_GROUP_ICON 记录，但本次没有要求嵌入图标")
        }
        if ([int]$recordNameId -ne 1) {
            throw ("自检失败：RT_GROUP_ICON 记录的 ID 是 " + $recordNameId + "，期望 1")
        }
        if ($recordDataSize -ne $iconGroupBytes.Length) {
            throw ("自检失败：RT_GROUP_ICON 的数据长 " + $recordDataSize + "，期望 " + $iconGroupBytes.Length)
        }
        for ($i = 0; $i -lt $iconGroupBytes.Length; $i++) {
            if ($written[$dataStart + $i] -ne $iconGroupBytes[$i]) {
                throw ("自检失败：RT_GROUP_ICON 的数据在偏移 " + $i + " 处与生成时不一致")
            }
        }
        $seenGroup++
    }
    else {
        throw ("自检失败：偏移 " + $pos + " 处出现了意外的资源类型 " + $recordType)
    }

    $pos = $dataEnd
}
if ($seenVersion -ne 1) {
    throw ("自检失败：VERSIONINFO 记录有 " + $seenVersion + " 条，期望恰好 1 条")
}
$expectedManifestCount = 0
if ($null -ne $manifestBytes) { $expectedManifestCount = 1 }
if ($seenManifest -ne $expectedManifestCount) {
    throw ("自检失败：RT_MANIFEST 记录有 " + $seenManifest + " 条，期望 " + $expectedManifestCount + " 条")
}
if ($seenIcons -ne $iconFrameCount) {
    throw ("自检失败：RT_ICON 记录有 " + $seenIcons + " 条，期望 " + $iconFrameCount + " 条")
}
$expectedGroupCount = 0
if ($null -ne $iconGroupBytes) { $expectedGroupCount = 1 }
if ($seenGroup -ne $expectedGroupCount) {
    throw ("自检失败：RT_GROUP_ICON 记录有 " + $seenGroup + " 条，期望 " + $expectedGroupCount + " 条")
}

if (Test-Path -LiteralPath $OutPath) { Remove-Item -LiteralPath $OutPath -Force }
Move-Item -LiteralPath $buildingPath -Destination $OutPath

$manifestNote = ''
if ($null -ne $manifestBytes) { $manifestNote = ' + RT_MANIFEST' }
$iconNote = ''
if ($iconFrameCount -gt 0) { $iconNote = ' + 图标 ' + $iconFrameCount + ' 帧 + RT_GROUP_ICON' }
Write-Host ("OK: " + $OutPath + "（" + (Get-Item -LiteralPath $OutPath).Length + " 字节，版本 " + $Version +
            "，中英双语 VERSIONINFO + Translation" + $manifestNote + $iconNote + "，自检通过）")
exit 0
