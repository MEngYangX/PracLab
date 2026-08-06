#!/bin/bash
# PracLabReplayEngine Linux 构建脚本(在 WSL 中执行)
# 用法: wsl -d Ubuntu-24.04 -- bash /mnt/d/PracLab/replay-engine/build-linux.sh

set -e

# ==================== SDK 路径(WSL 访问 Windows deps) ====================
export HL2SDKCS2=/mnt/d/PracLab/deps/hl2sdk-cs2
export MMSOURCE_DEV=/mnt/d/PracLab/deps/metamod-source
export CSGO_PROTO=/mnt/d/PracLab/deps/hl2sdk-cs2/common
# 使用 WSL 系统自带的 protoc 3.21.12(符合 3.21.x 要求)

SRC_DIR=/mnt/d/PracLab/replay-engine
# 在 WSL 原生文件系统构建以获得更好的 IO 性能
BUILD_DIR="$HOME/build/praclab-replay-linux"

echo "=== SDK env vars ==="
echo "HL2SDKCS2    = $HL2SDKCS2"
echo "MMSOURCE_DEV = $MMSOURCE_DEV"
echo "CSGO_PROTO   = $CSGO_PROTO"
echo "BUILD_DIR    = $BUILD_DIR"

# ==================== 清理旧缓存 ====================
echo "=== Clean old cache ==="
rm -rf "$BUILD_DIR"
echo "Cleaned."

# ==================== CMake 配置 ====================
echo "=== CMake configure start (Ninja generator) ==="
cmake -B "$BUILD_DIR" -S "$SRC_DIR" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DFETCHCONTENT_SOURCE_DIR_FUNCHOOK=/mnt/d/PracLab/deps/funchook \
    -DFETCHCONTENT_SOURCE_DIR_NLOHMANN_JSON=/mnt/d/PracLab/deps/nlohmann_json

echo "=== CMake configure exit code: $? ==="

# ==================== 编译 ====================
echo "=== Build start ==="
cmake --build "$BUILD_DIR" --config Release --parallel

echo "=== Build exit code: $? ==="

# ==================== 复制产物到 Windows 可访问路径 ====================
PACKAGE_OUT="$SRC_DIR/out/build/linux-release/package"
echo "=== Copy package to $PACKAGE_OUT ==="
rm -rf "$PACKAGE_OUT"
mkdir -p "$PACKAGE_OUT"
cp -r "$BUILD_DIR/package/addons" "$PACKAGE_OUT/"

echo "=== Build artifacts ==="
find "$PACKAGE_OUT" -type f -exec ls -lh {} \;

echo "=== Linux build completed successfully ==="
