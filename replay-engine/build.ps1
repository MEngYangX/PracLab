# PracLabReplayEngine 编译脚本
# 复用 configure.ps1 的 MSVC 环境设置,执行 cmake --build

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

# ==================== 设置 SDK 环境变量 ====================
$env:HL2SDKCS2    = "d:\PracLab\deps\hl2sdk-cs2"
$env:MMSOURCE_DEV = "d:\PracLab\deps\metamod-source"
$env:CSGO_PROTO   = "d:\PracLab\deps\hl2sdk-cs2\common"
$env:PROTOC       = "d:\PracLab\deps\protoc-3.21.8\bin\protoc.exe"

# ==================== 编译 ====================
$cmakeExe = "$vsRoot\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
$buildDir = "d:\PracLab\replay-engine\out\build\x64-Release"

Write-Host "=== Build start (Release) ==="
& $cmakeExe --build $buildDir --config Release
$exitCode = $LASTEXITCODE
Write-Host "=== Build exit code: $exitCode ==="

# ==================== 验证产物 ====================
if ($exitCode -eq 0) {
    $dllPath = "$buildDir\package\addons\PracLabReplayEngine\bin\win64\PracLabReplayEngine.dll"
    if (Test-Path $dllPath) {
        $size = (Get-Item $dllPath).Length
        Write-Host "=== SUCCESS ==="
        Write-Host "DLL: $dllPath"
        Write-Host "Size: $([math]::Round($size / 1KB, 2)) KB"
    } else {
        Write-Host "WARNING: Build succeeded but DLL not found at $dllPath"
        Write-Host "=== Searching for DLL ==="
        Get-ChildItem $buildDir -Filter "*.dll" -Recurse -ErrorAction SilentlyContinue | Select-Object FullName
    }
}

exit $exitCode
