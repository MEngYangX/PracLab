cmake_minimum_required(VERSION 3.20)
project(PracLabReplayEngine CXX)

# ==================== 全局构建选项 ====================
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
# MSVC 使用静态运行时（非 DLL），保证插件可独立部署
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# 强制 x64 构建：CS2 服务器仅运行在 64 位
if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "PracLabReplayEngine requires x64 build (use -A x64).")
endif()

# ==================== 读取 SDK 环境变量 ====================
# HL2SDKCS2  : HL2 SDK CS2 安装路径
# MMSOURCE_DEV : Metamod:Source 源码路径
set(HL2SDK $ENV{HL2SDKCS2})
set(MMS    $ENV{MMSOURCE_DEV})

# 校验核心 SDK 路径必须存在
foreach(v HL2SDK MMS)
    if(NOT ${v} OR NOT EXISTS "${${v}}")
        message(FATAL_ERROR "${v} env var not set or path missing: '${${v}}'")
    endif()
endforeach()

# ==================== Protobuf 集成 ====================
# eiface.h 依赖 network_connection.pb.h；usercmd/cs_usercmd.proto 提供
# CSGOUserCmdPB / CSubtickMoveStep，供 PlayerRunCommand 录制/回放 subtick_moves。
set(PROTO_DIR $ENV{CSGO_PROTO})
if(NOT PROTO_DIR OR NOT EXISTS "${PROTO_DIR}")
    message(FATAL_ERROR "CSGO_PROTO env var not set or path missing: '${PROTO_DIR}'")
endif()

if(DEFINED ENV{PROTOC} AND EXISTS "$ENV{PROTOC}")
    set(PROTOC_EXE "$ENV{PROTOC}")
else()
    find_program(PROTOC_EXE protoc REQUIRED)
endif()
execute_process(COMMAND "${PROTOC_EXE}" --version
    OUTPUT_VARIABLE PROTOC_VER OUTPUT_STRIP_TRAILING_WHITESPACE)
if(NOT PROTOC_VER MATCHES "libprotoc 3\\.21\\.")
    message(FATAL_ERROR "protoc must be 3.21.x (got '${PROTOC_VER}')")
endif()
message(STATUS "protoc: ${PROTOC_EXE} (${PROTOC_VER})")

# 需要编译的 proto 文件清单（顺序无关，protoc 通过 import 自动解析依赖）
set(PROTO_FILES
    network_connection.proto
    networkbasetypes.proto
    source2_steam_stats.proto
    netmessages.proto
    valveextensions.proto
    usercmd.proto
    cs_usercmd.proto)

# 生成文件输出目录
set(GENERATED_DIR "${CMAKE_BINARY_DIR}/generated")
file(MAKE_DIRECTORY "${GENERATED_DIR}")

# protoc 搜索路径（按优先级排列，第一个匹配的 proto 文件被采用）
set(PROTO_PATHS
    "${PROTO_DIR}"
    "${HL2SDK}/common"
    "${HL2SDK}/game/shared"
    "${HL2SDK}/game/shared/cs"
    "${HL2SDK}/thirdparty/protobuf-3.21.8/src")
set(PROTOC_PATH_ARGS "")
foreach(proto_path ${PROTO_PATHS})
    list(APPEND PROTOC_PATH_ARGS "--proto_path=${proto_path}")
endforeach()

# 计算生成文件路径并校验 proto 输入文件存在
set(PROTO_SRCS "")
set(PROTO_INPUTS "")
foreach(p ${PROTO_FILES})
    string(REGEX REPLACE "\\.proto$" ".pb.cc" cc ${p})
    string(REGEX REPLACE "\\.proto$" ".pb.h"  hh ${p})
    list(APPEND PROTO_SRCS "${GENERATED_DIR}/${cc}" "${GENERATED_DIR}/${hh}")
    set(proto_input "")
    foreach(proto_path ${PROTO_PATHS})
        if(EXISTS "${proto_path}/${p}")
            set(proto_input "${proto_path}/${p}")
            break()
        endif()
    endforeach()
    if(NOT proto_input)
        message(FATAL_ERROR "Missing proto input '${p}' in PROTO_PATHS")
    endif()
    list(APPEND PROTO_INPUTS "${proto_input}")
endforeach()

# protoc 3.21 生成的 .pb.h 中类声明带 `final`，会导致 PlayerCommand 无法继承
# CSGOUserCmdPB（player_command.h）。`final` 仅是去虚化提示，移除不影响 ABI/布局。
if(WIN32)
    set(STRIP_PROTO_FINAL_COMMAND
        powershell -NoProfile -Command
        "Get-ChildItem '${GENERATED_DIR}/*.pb.h' | ForEach-Object { (Get-Content -Raw $_) -replace ' final :',' :' | Set-Content -NoNewline $_ }")
else()
    set(STRIP_PROTO_FINAL_COMMAND
        /bin/sh -c
        "perl -0pi -e 's/ final :/ :/g' '${GENERATED_DIR}'/*.pb.h")
endif()

add_custom_command(
    OUTPUT ${PROTO_SRCS}
    COMMAND "${PROTOC_EXE}" --cpp_out=${GENERATED_DIR} ${PROTOC_PATH_ARGS} ${PROTO_FILES}
    COMMAND ${STRIP_PROTO_FINAL_COMMAND}
    DEPENDS ${PROTO_INPUTS}
    WORKING_DIRECTORY "${PROTO_DIR}"
    COMMENT "protoc ${PROTO_FILES} (+ strip class final)"
    VERBATIM
)

# ==================== 第三方依赖（FetchContent） ====================
include(FetchContent)

# funchook: 轻量级 inline hook 库，用于拦截引擎函数（Task 3 起使用）
set(FUNCHOOK_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(FUNCHOOK_BUILD_SHARED OFF CACHE BOOL "" FORCE)
FetchContent_Declare(funchook
    GIT_REPOSITORY https://github.com/kubo/funchook.git
    GIT_TAG v1.1.3
    GIT_SHALLOW TRUE
    UPDATE_DISCONNECTED TRUE)
FetchContent_MakeAvailable(funchook)

# nlohmann/json: JSON 解析，用于读取 gamedata.json 签名数据（Task 2 起使用）
FetchContent_Declare(nlohmann_json
    GIT_REPOSITORY https://github.com/nlohmann/json.git
    GIT_TAG v3.11.3
    GIT_SHALLOW TRUE
    UPDATE_DISCONNECTED TRUE)
FetchContent_MakeAvailable(nlohmann_json)

# ==================== HL2 SDK 源文件 ====================
# 编译进插件的 SDK 基础设施源文件：ConVar、KeyValues3、SourceHook
set(SDK_SOURCES
    ${HL2SDK}/public/tier0/memoverride.cpp
    ${HL2SDK}/tier1/keyvalues3.cpp
    ${HL2SDK}/tier1/convar.cpp
    ${MMS}/core/sourcehook/sourcehook.cpp
    ${MMS}/core/sourcehook/sourcehook_impl_chookidman.cpp
    ${MMS}/core/sourcehook/sourcehook_impl_chookmaninfo.cpp
    ${MMS}/core/sourcehook/sourcehook_impl_cvfnptr.cpp
    ${MMS}/core/sourcehook/sourcehook_impl_cproto.cpp
)

# 兼容旧版 HL2SDK（generichash.cpp 存在于 < 2026-07-17 的 commit，之后改为 header-only 内联）
if(EXISTS "${HL2SDK}/tier1/generichash.cpp")
    list(APPEND SDK_SOURCES ${HL2SDK}/tier1/generichash.cpp)
endif()

# ==================== 插件源文件 ====================
set(PLUGIN_SOURCES
    src/plugin.cpp
    src/platform.cpp
    src/sig_scan.cpp
    src/memory.cpp
    src/version_targets.cpp
    src/slot_resolver.cpp
    src/hooks.cpp
    src/recorder.cpp
    src/replayer.cpp
    src/weapon_switcher.cpp
    src/exports.cpp
    ${PROTO_SRCS}
)

# ==================== 目标定义 ====================
add_library(PracLabReplayEngine SHARED
    ${PLUGIN_SOURCES}
    ${SDK_SOURCES}
)

target_include_directories(PracLabReplayEngine PRIVATE
    src
    ${CMAKE_SOURCE_DIR}/include
    ${GENERATED_DIR}
    ${HL2SDK}/thirdparty/protobuf-3.21.8/src
    ${MMS}/core
    ${MMS}/core/sourcehook
    ${MMS}/core/sourcemm
    ${HL2SDK}/public
    ${HL2SDK}/public/engine
    ${HL2SDK}/public/game/server
    ${HL2SDK}/public/game/shared
    ${HL2SDK}/public/mathlib
    ${HL2SDK}/public/tier0
    ${HL2SDK}/public/entity2
    ${HL2SDK}/game/shared
    ${HL2SDK}/common
)

target_link_libraries(PracLabReplayEngine PRIVATE
    funchook-static
    nlohmann_json::nlohmann_json
)

# 编译宏定义
target_compile_definitions(PracLabReplayEngine PRIVATE
    META_IS_SOURCE2
    PLATFORM_64BITS
    X64BITS
    GOOGLE_PROTOBUF_NO_RTTI
)

# ==================== 平台特定配置 ====================
if(WIN32)
    set(PRL_PLATFORM_DIR win64)
    set(PRL_SUFFIX ".dll")
    set(PRL_VDF_SUFFIX "")
    set(HL2SDK_LIBDIR ${HL2SDK}/lib/public/win64)

    # Windows 无 <byteswap.h> 冲突，tier1 加入正常搜索路径
    target_include_directories(PracLabReplayEngine PRIVATE
        ${HL2SDK}/public/tier1
    )

    target_link_libraries(PracLabReplayEngine PRIVATE
        psapi
        ${HL2SDK_LIBDIR}/tier0.lib
        ${HL2SDK_LIBDIR}/mathlib.lib
        ${HL2SDK_LIBDIR}/interfaces.lib
        ${HL2SDK_LIBDIR}/2015/libprotobuf.lib
    )
    # 兼容旧版 HL2SDK：预编译 tier1.lib 已于 2026-07-17 上游移除（功能改头文件内联）
    if(EXISTS "${HL2SDK_LIBDIR}/tier1.lib")
        target_link_libraries(PracLabReplayEngine PRIVATE ${HL2SDK_LIBDIR}/tier1.lib)
    endif()
    target_link_options(PracLabReplayEngine PRIVATE /NODEFAULTLIB:LIBCMTD)

    target_compile_definitions(PracLabReplayEngine PRIVATE
        _CRT_SECURE_NO_WARNINGS
        WIN32 _WIN32 _WINDOWS
        COMPILER_MSVC COMPILER_MSVC64
        NOMINMAX _ALLOW_KEYWORD_MACROS
    )

elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    set(PRL_PLATFORM_DIR linuxsteamrt64)
    set(PRL_SUFFIX ".so")
    set(PRL_VDF_SUFFIX ".linux")
    set(HL2SDK_LIBDIR ${HL2SDK}/lib/linux64)

    target_link_libraries(PracLabReplayEngine PRIVATE
        ${HL2SDK_LIBDIR}/libtier0.so
        ${HL2SDK_LIBDIR}/mathlib.a
        ${HL2SDK_LIBDIR}/interfaces.a
        ${HL2SDK_LIBDIR}/release/libprotobuf.a
        pthread
        dl
    )
    # 兼容旧版 HL2SDK：预编译 tier1.a 已于 2026-07-17 上游移除（功能改头文件内联）
    if(EXISTS "${HL2SDK_LIBDIR}/tier1.a")
        target_link_libraries(PracLabReplayEngine PRIVATE ${HL2SDK_LIBDIR}/tier1.a)
    endif()

    target_compile_definitions(PracLabReplayEngine PRIVATE
        POSIX _LINUX LINUX
        COMPILER_GCC
        _GLIBCXX_USE_CXX11_ABI=0
        stricmp=strcasecmp
        _stricmp=strcasecmp
        _strnicmp=strncasecmp
        strnicmp=strncasecmp
        _snprintf=snprintf
        _vsnprintf=vsnprintf
        _alloca=alloca
        strcmpi=strcasecmp
    )

    target_compile_options(PracLabReplayEngine PRIVATE
        -fPIC
        -fno-rtti
        -fvisibility=hidden
        # tier1 放在搜索路径最后：glibc 的 <byteswap.h> 优先于 SDK 同名头文件
        -idirafter ${HL2SDK}/public/tier1
        -Wno-non-virtual-dtor
        -Wno-overloaded-virtual
        -Wno-register
        -Wno-narrowing
        -Wno-deprecated-copy
    )

    set_target_properties(PracLabReplayEngine PROPERTIES
        SKIP_BUILD_RPATH TRUE
        BUILD_RPATH ""
        INSTALL_RPATH ""
    )
else()
    message(FATAL_ERROR "Unsupported platform: ${CMAKE_SYSTEM_NAME}")
endif()

# ==================== 输出目录 ====================
set(PRL_PACKAGE_BIN_DIR "${CMAKE_BINARY_DIR}/package/addons/PracLabReplayEngine/bin/${PRL_PLATFORM_DIR}")

set_target_properties(PracLabReplayEngine PROPERTIES
    OUTPUT_NAME "PracLabReplayEngine"
    PREFIX ""
    SUFFIX "${PRL_SUFFIX}"
    RUNTIME_OUTPUT_DIRECTORY         "${PRL_PACKAGE_BIN_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${PRL_PACKAGE_BIN_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_DEBUG   "${PRL_PACKAGE_BIN_DIR}"
    LIBRARY_OUTPUT_DIRECTORY         "${PRL_PACKAGE_BIN_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_RELEASE "${PRL_PACKAGE_BIN_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_DEBUG   "${PRL_PACKAGE_BIN_DIR}"
)

# ==================== MSVC 编译选项 ====================
if(MSVC)
    target_compile_options(PracLabReplayEngine PRIVATE
        /utf-8 /W3 /permissive- /wd4819 /wd4267 /wd4244 /wd4146 /wd4996 /wd4661)
endif()

# ==================== POST_BUILD: 复制 VDF 和 gamedata.json ====================
# 将 Metamod VDF 和 gamedata.json 复制到 package 目录，形成完整部署结构
add_custom_command(TARGET PracLabReplayEngine POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory
        "${CMAKE_BINARY_DIR}/package/addons/metamod"
    COMMAND ${CMAKE_COMMAND} -E make_directory
        "${CMAKE_BINARY_DIR}/package/addons/PracLabReplayEngine"
    # 根据平台复制对应的 VDF 文件（Windows: .vdf, Linux: .linux.vdf）
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_SOURCE_DIR}/configs/addons/metamod/PracLabReplayEngine${PRL_VDF_SUFFIX}.vdf"
        "${CMAKE_BINARY_DIR}/package/addons/metamod/PracLabReplayEngine.vdf"
    # 复制 gamedata.json 到插件数据目录
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_SOURCE_DIR}/configs/addons/PracLabReplayEngine/gamedata.json"
        "${CMAKE_BINARY_DIR}/package/addons/PracLabReplayEngine/gamedata.json"
)

