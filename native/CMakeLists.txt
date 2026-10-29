# downcraft_native - dependency-free Windows helper DLL for DownCraft.
#
# Requirements: MSVC (x64), CMake >= 3.20, Windows SDK.
# The DLL is built with the static CRT (/MT) and therefore needs no VC++
# redistributable at runtime.
#
# All paths are derived from the source/binary directories, so the tree is
# relocatable. Build output:
#   <build>/out/downcraft_native.dll
#   <build>/out/downcraft_native_test.exe

cmake_minimum_required(VERSION 3.20)

project(downcraft_native VERSION 1.0.0 LANGUAGES CXX)

if(NOT WIN32)
  message(FATAL_ERROR "downcraft_native is a Windows-only component")
endif()

if(NOT MSVC)
  message(FATAL_ERROR "downcraft_native must be built with MSVC (cl.exe)")
endif()

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "downcraft_native is x64-only; the current toolchain targets a 32-bit ABI")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Static C/C++ runtime: no VCRUNTIME140.dll / MSVCP140.dll dependency.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# Keep both deliverables next to each other, for Ninja (single-config) and for
# the Visual Studio generators (multi-config).
set(DC_OUTPUT_DIR "${CMAKE_BINARY_DIR}/out")
set(DC_LIBRARY_DIR "${CMAKE_BINARY_DIR}/lib")
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${DC_OUTPUT_DIR}")
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY "${DC_LIBRARY_DIR}")
foreach(config DEBUG RELEASE RELWITHDEBINFO MINSIZEREL)
  set(CMAKE_RUNTIME_OUTPUT_DIRECTORY_${config} "${DC_OUTPUT_DIR}")
  set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY_${config} "${DC_LIBRARY_DIR}")
endforeach()

function(dc_apply_common_settings target)
  # WIN32_LEAN_AND_MEAN / NOMINMAX are set inside the sources themselves.
  target_compile_definitions(${target} PRIVATE
    UNICODE
    _UNICODE
    _CRT_SECURE_NO_WARNINGS)
  target_compile_options(${target} PRIVATE
    /W4
    /permissive-
    /EHsc
    /utf-8
    /Zc:__cplusplus)
  set_target_properties(${target} PROPERTIES
    CXX_STANDARD 20
    CXX_STANDARD_REQUIRED ON
    CXX_EXTENSIONS OFF)
endfunction()

# ---------------------------------------------------------------------------
# downcraft_native.dll
# ---------------------------------------------------------------------------
add_library(downcraft_native SHARED src/downcraft_native.cpp)
dc_apply_common_settings(downcraft_native)
target_include_directories(downcraft_native PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(downcraft_native PRIVATE bcrypt ntdll)
set_target_properties(downcraft_native PROPERTIES
  OUTPUT_NAME "downcraft_native"
  PREFIX "")

# ---------------------------------------------------------------------------
# Self-test executable
# ---------------------------------------------------------------------------
add_executable(downcraft_native_test src/downcraft_native_test.cpp)
dc_apply_common_settings(downcraft_native_test)
target_include_directories(downcraft_native_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
# The test loads downcraft_native.dll dynamically from its own directory; only the
# build ordering matters.
add_dependencies(downcraft_native_test downcraft_native)
