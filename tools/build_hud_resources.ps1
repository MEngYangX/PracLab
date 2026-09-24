# PracLab 练习 HUD 资源构建脚本
#
# 用途：将 hud/layout/prac_hud.xml 与 hud/styles/prac_hud.css 经 CS2 resourcecompiler
#       编译为 _c 系列资源（.vxml_c / .vcss_c，即「编译产物必须带 _c 后缀」——
#       禁止把未编译 VXML/VCSS 直接打包进运行时 VPK），再用 VPKEdit 打包为 prac_hud.vpk。
#
# 动作（-Action）：
#   Validate  仅静态校验 XML/VCSS 结构与协议（不调用 CS2 编译器，本机无工具也可跑）
#   Compile   staging 源文件到 content/csgo_addons/prac_hud 并调用 resourcecompiler 编译
#   Pack      将编译产物整理为标准目录结构并用 VPKEdit 打包 dist/prac_hud.vpk
#   Build     Validate + Compile + Pack（默认）
#
# 参数：
#   -Cs2Root          CS2 根目录（默认 D:\cs2-server；需含 game\csgo 子目录）
#   -ResourceCompiler resourcecompiler.exe 完整路径（缺省时从 $Cs2Root 常见位置探测）
#   -VpkEditCli       vpkeditcli.exe 完整路径（缺省时从 PATH 探测；找不到则 Pack 报错）
#
# 用法示例：
#   .\tools\build_hud_resources.ps1 -Action Validate
#   .\tools\build_hud_resources.ps1 -Action Build `
#       -ResourceCompiler "D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\resourcecompiler.exe" `
#       -VpkEditCli "D:\tools\VPKEdit\vpkeditcli.exe"
#
# 部署：产物 dist/prac_hud.vpk 复制到服务器与客户端的 game/csgo/overrides/，
#       并在双方 gameinfo.gi 添加 "Game  csgo/overrides/prac_hud.vpk"；
#       或发布 Steam 创意工坊供玩家订阅。
#
# 扩展点（图标）：首期为纯文本面板，未使用任何图标/图片资产，故跳过 VTEX 编译步骤。
#   后续若需图标：在 hud/icons/ 放置 128×128 8-bit RGBA PNG，参照
#   .trae/research/SwiftOnlineMusicPlayerSW2/tools/build_hud_resources.ps1 的
#   Write-IconVtex 生成 VTEX 描述文件，随 VXML/VCSS 一并传给 resourcecompiler，
#   并将 .vtex_c 纳入 Pack 的打包清单（Image.src 引用逻辑 .vtex 路径而非 .vtex_c）。

param(
    [ValidateSet("Validate", "Compile", "Pack", "Build")]
    [string]$Action = "Build",
    [string]$Cs2Root = "D:\cs2-server",
    [string]$ResourceCompiler = "",
    [string]$VpkEditCli = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$addonName = "prac_hud"
$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$layoutPath = Join-Path $projectRoot "hud\layout\prac_hud.xml"
$stylePath = Join-Path $projectRoot "hud\styles\prac_hud.css"
$distRoot = Join-Path $projectRoot "dist"
$outVpk = Join-Path $distRoot "$addonName.vpk"

# 与 C# 侧（Commands/PracticeHud）约定的协议常量：面板 id、dialog variables、判定色钩子
# 根面板不允许携带 id（CCSCustomHudLayout 编译约束），显隐全部由子面板 hidden class 控制
# 弹道追踪（.recoil）无 HUD 面板：压枪轨迹仅以世界空间 beam 绘制，故无 recoil-panel/recoil-* 协议项
$panelIds = @("strafe-panel", "shot-panel", "sync-panel")
$dialogVariables = @(
    "strafe-label", "strafe-diff", "strafe-stats",
    "shot-label", "shot-error", "shot-stats",
    "sync-current", "sync-avg", "sync-best"
)
$strafeHooks = @("strafe-perfect", "strafe-good", "strafe-bad")

function Write-Log {
    param([string]$Message)
    Write-Host "[PracHud] $Message"
}

function Assert-FileExists {
    param([string]$Path, [string]$Message)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw $Message }
}

function Assert-DirectoryExists {
    param([string]$Path, [string]$Message)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw $Message }
}

function Test-PathIsChild {
    param([string]$Path, [string]$Parent)
    $resolvedPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $resolvedParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    return $resolvedPath.StartsWith(
        $resolvedParent + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)
}

# 仅允许删除预期父目录内的目录，防止误删
function Remove-ChildDirectory {
    param([string]$Path, [string]$Parent)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if (-not (Test-PathIsChild -Path $Path -Parent $Parent)) {
        throw "Refusing to remove path outside expected parent: $Path"
    }
    Remove-Item -LiteralPath $Path -Recurse -Force
}

# 以 UTF-8 无 BOM 写文本（resourcecompiler 要求）
function Write-TextNoBom {
    param([string]$Path, [string]$Value)
    [System.IO.File]::WriteAllText($Path, $Value, [System.Text.UTF8Encoding]::new($false))
}

# 以 UTF-8 显式读文本：Windows PowerShell 5.1 的 Get-Content 对无 BOM 文件按 ANSI 解码，
# 会把源文件里的中文注释/标题读成乱码并破坏 XML 属性引号，必须绕开
function Read-TextUtf8 {
    param([string]$Path)
    [System.IO.File]::ReadAllText($Path, [System.Text.UTF8Encoding]::new($false))
}

function Resolve-ResourceCompiler {
    # 参数优先；否则探测 $Cs2Root 下常见位置，再尝试 PATH
    if (-not [string]::IsNullOrWhiteSpace($ResourceCompiler)) {
        Assert-FileExists -Path $ResourceCompiler -Message "resourcecompiler.exe not found: $ResourceCompiler"
        return $ResourceCompiler
    }
    $candidates = @(
        (Join-Path $Cs2Root "game\bin\win64\resourcecompiler.exe"),
        (Join-Path $Cs2Root "game\bin\win64\workshop\resourcecompiler.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    $onPath = Get-Command "resourcecompiler.exe" -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw ("resourcecompiler.exe not found under '{0}'. " +
           "Pass -ResourceCompiler <full path> (e.g. a CS2 install with workshop tools).") -f $Cs2Root
}

function Resolve-VpkEditCli {
    # 参数优先；否则从 PATH 探测
    if (-not [string]::IsNullOrWhiteSpace($VpkEditCli)) {
        Assert-FileExists -Path $VpkEditCli -Message "vpkeditcli.exe not found: $VpkEditCli"
        return $VpkEditCli
    }
    $onPath = Get-Command "vpkeditcli.exe" -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw "vpkeditcli.exe not found on PATH. Pass -VpkEditCli <full path> (see https://github.com/craftablescience/VPKEdit)."
}

# 静态校验：CCSCustomHudLayout 受限模型 + 与 C# 侧的协议一致性
function Test-HudSources {
    foreach ($path in @($layoutPath, $stylePath)) {
        Assert-FileExists -Path $path -Message "Required HUD source is missing: $path"
    }

    [xml]$layout = Read-TextUtf8 -Path $layoutPath

    # 受限元素/属性白名单：无 scripts、无 VJS/HTML/audio 等任何受限模型之外的节点
    $allowedNodes = @{
        "root"    = @()
        "styles"  = @()
        "include" = @("src")
        "Panel"   = @("id", "class", "hittest")
        "Label"   = @("id", "class", "hittest", "text")
    }
    foreach ($node in $layout.SelectNodes("//*")) {
        if (-not $allowedNodes.ContainsKey($node.Name)) {
            throw "HUD layout contains disallowed node: $($node.Name)"
        }
        foreach ($attribute in $node.Attributes) {
            if ($allowedNodes[$node.Name] -notcontains $attribute.Name) {
                throw "HUD layout contains disallowed attribute '$($attribute.Name)' on <$($node.Name)>"
            }
        }
    }

    # 样式表引用与禁用项
    $stylesheet = $layout.SelectSingleNode("/root/styles/include")
    if (-not $stylesheet -or
        $stylesheet.GetAttribute("src") -ne "s2r://panorama/styles/custom_game/prac_hud.vcss_c") {
        throw "HUD layout must include s2r://panorama/styles/custom_game/prac_hud.vcss_c"
    }
    if ($layout.SelectSingleNode("/root/scripts")) {
        throw "CCSCustomHudLayout does not permit a scripts node."
    }

    # 面板 id 与初始 hidden class（显隐由服务器 SetHasClassForPlayer 控制）
    foreach ($panelId in $panelIds) {
        $panel = $layout.SelectSingleNode("//Panel[@id='$panelId']")
        if (-not $panel) { throw "HUD layout is missing Panel id: $panelId" }
        $class = $panel.GetAttribute("class")
        if ($class -notmatch '(^|\s)hidden(\s|$)') {
            throw "Panel '$panelId' must start with the 'hidden' class."
        }
    }

    # dialog variable 引用（{s:name} 绑定写法，与参考实现一致）
    $layoutText = $layout.OuterXml
    foreach ($variable in $dialogVariables) {
        if ($layoutText -notmatch [regex]::Escape("{s:$variable}")) {
            throw "HUD layout is missing dialog variable reference: {s:$variable}"
        }
    }

    # 样式表关键选择器：显隐开关、面板结构与判定色钩子
    $style = Read-TextUtf8 -Path $stylePath
    $requiredSelectors = @(".hidden", ".PracHudRoot", ".PracHudPanel", ".PracHudTitle",
                           ".PracHudVerdict", ".PracHudDetail", ".PracHudStats") + $strafeHooks
    foreach ($selector in $requiredSelectors) {
        if ($style -notmatch [regex]::Escape($selector)) {
            throw "Stylesheet is missing required selector: $selector"
        }
    }

    Write-Log "Source validation passed."
    Write-Log ("Verified panels: {0}" -f ($panelIds -join ', '))
    Write-Log ("Verified dialog variables: {0}" -f ($dialogVariables -join ', '))
}

function Get-AddonPaths {
    Assert-DirectoryExists -Path $Cs2Root -Message "CS2 root not found: $Cs2Root"
    $contentAddonsRoot = Join-Path $Cs2Root "content\csgo_addons"
    $gameAddonsRoot = Join-Path $Cs2Root "game\csgo_addons"
    return [pscustomobject]@{
        ContentAddonsRoot = $contentAddonsRoot
        GameAddonsRoot = $gameAddonsRoot
        ContentAddon = Join-Path $contentAddonsRoot $addonName
        GameAddon = Join-Path $gameAddonsRoot $addonName
        GameDir = Join-Path $Cs2Root "game\csgo"
    }
}

# 编译 VXML/VCSS -> _c 系列（图标 VTEX 步骤首期省略，扩展点见文件头注释）
function Compile-HudResources {
    Test-HudSources
    $rc = Resolve-ResourceCompiler
    $paths = Get-AddonPaths
    Write-Log "Using resourcecompiler: $rc"

    Remove-ChildDirectory -Path $paths.ContentAddon -Parent $paths.ContentAddonsRoot
    Remove-ChildDirectory -Path $paths.GameAddon -Parent $paths.GameAddonsRoot
    New-Item -ItemType Directory -Force -Path $paths.ContentAddonsRoot, $paths.GameAddonsRoot | Out-Null

    $contentLayoutDir = Join-Path $paths.ContentAddon "panorama\layout\custom_game"
    $contentStyleDir = Join-Path $paths.ContentAddon "panorama\styles\custom_game"
    New-Item -ItemType Directory -Force -Path $contentLayoutDir, $contentStyleDir | Out-Null

    # panorama 预处理器空配置（参照参考实现）
    Write-TextNoBom -Path (Join-Path $paths.ContentAddon "panorama\preprocessor_config.txt") -Value @'
"PanzipCfg"
{
    "BlockDefs"
    {
    }
}
'@

    # addoninfo.txt：声明 panorama 布局挂载（内容与参考实现一致）
    $addonInfoPath = Join-Path $paths.ContentAddon "addoninfo.txt"
    Set-Content -LiteralPath $addonInfoPath -Encoding ASCII -Value @'
<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->
{
    IsPlayable = false
    Panorama =
    {
        AllowCustomGameUI = true
        AddonLayoutPath = "panorama/layout/custom_game/"
    }
}
'@

    $contentVxml = Join-Path $contentLayoutDir "prac_hud.vxml"
    $contentVcss = Join-Path $contentStyleDir "prac_hud.vcss"
    Write-TextNoBom -Path $contentVxml -Value (Read-TextUtf8 -Path $layoutPath)
    Write-TextNoBom -Path $contentVcss -Value (Read-TextUtf8 -Path $stylePath)

    # 编译：输出位于 game 侧 addon 目录（不同版本可能直接输出 panorama\... 或带 compiled\ 前缀，下方统一归一）
    & $rc -game $paths.GameDir -i $contentVcss -i $contentVxml -f -nop4 -v
    if ($LASTEXITCODE -ne 0) { throw "resourcecompiler failed with exit code $LASTEXITCODE" }

    if (-not (Test-Path -LiteralPath $paths.GameAddon -PathType Container)) {
        throw "resourcecompiler did not create output directory: $($paths.GameAddon)"
    }
    $compiledVxml = Get-ChildItem -LiteralPath $paths.GameAddon -Recurse -Filter "prac_hud.vxml_c" |
        Select-Object -First 1
    $compiledVcss = Get-ChildItem -LiteralPath $paths.GameAddon -Recurse -Filter "prac_hud.vcss_c" |
        Select-Object -First 1
    if (-not $compiledVxml) { throw "resourcecompiler did not produce prac_hud.vxml_c under $($paths.GameAddon)" }
    if (-not $compiledVcss) { throw "resourcecompiler did not produce prac_hud.vcss_c under $($paths.GameAddon)" }
    Write-Log "Compiled: $($compiledVxml.FullName)"
    Write-Log "Compiled: $($compiledVcss.FullName)"

    # 归一化：无论编译器输出到 panorama\... 还是 compiled\panorama\...，
    # 最终 VPK 内容固定为 addoninfo.txt + panorama/layout/custom_game/*.vxml_c + panorama/styles/custom_game/*.vcss_c
    $stash = Join-Path $paths.ContentAddon "_stash"
    Remove-ChildDirectory -Path $stash -Parent $paths.ContentAddon
    New-Item -ItemType Directory -Force -Path $stash | Out-Null
    Copy-Item -LiteralPath $compiledVxml.FullName -Destination (Join-Path $stash "prac_hud.vxml_c") -Force
    Copy-Item -LiteralPath $compiledVcss.FullName -Destination (Join-Path $stash "prac_hud.vcss_c") -Force

    Remove-ChildDirectory -Path $paths.GameAddon -Parent $paths.GameAddonsRoot
    $gameLayoutDir = Join-Path $paths.GameAddon "panorama\layout\custom_game"
    $gameStyleDir = Join-Path $paths.GameAddon "panorama\styles\custom_game"
    New-Item -ItemType Directory -Force -Path $gameLayoutDir, $gameStyleDir | Out-Null
    Copy-Item -LiteralPath (Join-Path $stash "prac_hud.vxml_c") -Destination (Join-Path $gameLayoutDir "prac_hud.vxml_c") -Force
    Copy-Item -LiteralPath (Join-Path $stash "prac_hud.vcss_c") -Destination (Join-Path $gameStyleDir "prac_hud.vcss_c") -Force
    Copy-Item -LiteralPath $addonInfoPath -Destination (Join-Path $paths.GameAddon "addoninfo.txt") -Force
    Remove-ChildDirectory -Path $stash -Parent $paths.ContentAddon

    foreach ($output in @(
        (Join-Path $gameLayoutDir "prac_hud.vxml_c"),
        (Join-Path $gameStyleDir "prac_hud.vcss_c"))) {
        Assert-FileExists -Path $output -Message "Expected compiled resource not found: $output"
    }
    Write-Log "Compiled HUD resources: $($paths.GameAddon)"
}

# 打包：VPK v2 单文件，VPK 内仅含编译后 _c 资源
function Pack-HudVpk {
    $vpkEdit = Resolve-VpkEditCli
    $paths = Get-AddonPaths
    Assert-DirectoryExists -Path $paths.GameAddon -Message "Compiled addon not found: $($paths.GameAddon). Run Compile first."
    foreach ($relativePath in @(
        "addoninfo.txt",
        "panorama\layout\custom_game\prac_hud.vxml_c",
        "panorama\styles\custom_game\prac_hud.vcss_c")) {
        Assert-FileExists -Path (Join-Path $paths.GameAddon $relativePath) -Message "Compiled addon is missing: $relativePath"
    }
    Write-Log "Using vpkeditcli: $vpkEdit"

    New-Item -ItemType Directory -Force -Path $distRoot | Out-Null
    & $vpkEdit --output $outVpk --type vpk --version 2 --single-file $paths.GameAddon
    if ($LASTEXITCODE -ne 0) { throw "vpkeditcli failed with exit code $LASTEXITCODE" }
    Assert-FileExists -Path $outVpk -Message "Expected VPK was not created: $outVpk"

    $tree = (& $vpkEdit --file-tree $outVpk | Out-String)
    foreach ($fileName in @("addoninfo.txt", "prac_hud.vxml_c", "prac_hud.vcss_c")) {
        if ($tree -notmatch [regex]::Escape($fileName)) { throw "Packed VPK is missing: $fileName" }
    }
    Write-Log "Packed HUD VPK: $outVpk"
}

switch ($Action) {
    "Validate" { Test-HudSources }
    "Compile"  { Compile-HudResources }
    "Pack"     { Pack-HudVpk }
    "Build"    { Compile-HudResources; Pack-HudVpk }
}
