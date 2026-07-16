# PracLabReplayEngine CMake 配置脚本
# 手动注入 MSVC 编译器环境(替代 vcvars64.bat),然后用 Ninja 生成器配置 CMake

# ==================== MSVC 工具链路径 ====================
$msvcRoot = "D:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231"
$vsRoot   = "D:\Program Files\Microsoft Visual Studio\18\Community"
$winSdk   = "C:\Program Files (x86)\Windows Kits\10"
$winSdkVer = "10.0.26100.0"

# ==================== 设置 PATH(让 cl.exe / link.exe / rc.exe / ninja.exe 可被找到)====================
$ninjaDir = "$vsRoot\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja"
$env:PATH = "$msvcRoot\bin\Hostx64\x64;$winSdk\bin\$winSdkVer\x64;$winSdk\bin\x64;$vsRoot\Common7\IDE;$vsRoot\MSBuild\Current\Bin\amd64;$ninjaDir;$env:PATH"

# ==================== 设置 INCLUDE(头文件搜索路径)====================
$env:INCLUDE = "$msvcRoot\include;$msvcRoot\ATLMFC\include;$vsRoot\VC\Auxiliary\VS\include;$winSdk\Include\$winSdkVer\ucrt;$winSdk\Include\$winSdkVer\um;$winSdk\Include\$winSdkVer\shared;$winSdk\Include\$winSdkVer\winrt;$winSdk\Include\$winSdkVer\cppwinrt"

# ==================== 设置 LIB(库文件搜索路径)====================
$env:LIB = "$msvcRoot\lib\x64;$msvcRoot\ATLMFC\lib\x64;$winSdk\Lib\$winSdkVer\ucrt\x64;$winSdk\Lib\$winSdkVer\um\x64"

# ==================== 验证 cl.exe 和 ninja.exe 可用 ====================
Write-Host "=== Verify cl.exe ==="
$cl = Get-Command cl.exe -ErrorAction SilentlyContinue
if ($cl) {
    Write-Host "cl.exe found: $($cl.Source)"
} else {
    Write-Host "ERROR: cl.exe not found in PATH"
    exit 1
}

Write-Host "=== Verify ninja.exe ==="
$ninja = Get-Command ninja.exe -ErrorAction SilentlyContinue
if ($ninja) {
    Write-Host "ninja.exe found: $($ninja.Source)"
} else {
    Write-Host "ERROR: ninja.exe not found in PATH"
    exit 1
}

# ==================== 设置 SDK 环境变量 ====================
$env:HL2SDKCS2    = "d:\PracLab\deps\hl2sdk-cs2"
$env:MMSOURCE_DEV = "d:\PracLab\deps\metamod-source"
$env:CSGO_PROTO   = "d:\PracLab\deps\hl2sdk-cs2\common"
$env:PROTOC       = "d:\PracLab\deps\protoc-3.21.8\bin\protoc.exe"

Write-Host "=== SDK env vars ==="
Write-Host "HL2SDKCS2    = $env:HL2SDKCS2"
Write-Host "MMSOURCE_DEV = $env:MMSOURCE_DEV"
Write-Host "CSGO_PROTO   = $env:CSGO_PROTO"
Write-Host "PROTOC       = $env:PROTOC"

# ==================== 清理旧缓存 ====================
Write-Host "=== Clean old cache ==="
Remove-Item -Recurse -Force "d:\PracLab\replay-engine\out" -ErrorAction SilentlyContinue
Write-Host "Cleaned."

# ==================== CMake 配置(Ninja 生成器)====================
$cmakeExe = "$vsRoot\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
$buildDir = "d:\PracLab\replay-engine\out\build\x64-Release"
$srcDir   = "d:\PracLab\replay-engine"

Write-Host "=== CMake configure start (Ninja generator) ==="
# 使用本地源码,跳过 FetchContent 网络下载
& $cmakeExe -B $buildDir -S $srcDir -G "Ninja" -DCMAKE_BUILD_TYPE=Release `
    -DCMAKE_MAKE_PROGRAM="$ninjaDir\ninja.exe" `
    -DFETCHCONTENT_SOURCE_DIR_FUNCHOOK="d:\PracLab\deps\funchook" `
    -DFETCHCONTENT_SOURCE_DIR_NLOHMANN_JSON="d:\PracLab\deps\nlohmann_json"
$exitCode = $LASTEXITCODE
Write-Host "=== CMake exit code: $exitCode ==="
exit $exitCode
