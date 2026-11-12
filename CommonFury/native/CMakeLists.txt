cmake_minimum_required(VERSION 3.24)
project(commonfury_texture VERSION 2.0.0 LANGUAGES CXX)

if(NOT MSVC OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8 OR
   (CMAKE_GENERATOR_PLATFORM AND NOT CMAKE_GENERATOR_PLATFORM STREQUAL "x64"))
    message(FATAL_ERROR "commonfury_texture currently supports Windows x64 with MSVC.")
endif()

include(FetchContent)
set(CFTEX_ISPC_EXECUTABLE "" CACHE FILEPATH "Optional ISPC 1.26.0 compiler override")
option(CFTEX_BUILD_TESTS "Build the small native ABI smoke test" OFF)

# Internal codec of CommonFury's managed texture layer; its ABI is not public and nothing is installed.
# Fetch sources only; the upstream command-line tools and their container I/O
# are deliberately excluded. All revisions and downloaded bytes are pinned.
FetchContent_Declare(cftex_bc7
    URL https://codeload.github.com/richgel999/bc7enc_rdo/tar.gz/dbe416d28a5530b4e8cc45b14bf034dc6b96bbde
    URL_HASH SHA256=e997e7682259d5e2efefea4c4b626fafbe2c37b13853f93dbf5d7c29ee787171
    DOWNLOAD_EXTRACT_TIMESTAMP FALSE
    SOURCE_SUBDIR commonfury-source-only)
FetchContent_Declare(cftex_stb
    URL https://codeload.github.com/nothings/stb/tar.gz/28d546d5eb77d4585506a20480f4de2e706dff4c
    URL_HASH SHA256=4ef16a0e174bc33887fec582b01ca239155466e0b48081cc27304298556bed47
    DOWNLOAD_EXTRACT_TIMESTAMP FALSE
    SOURCE_SUBDIR commonfury-source-only)
FetchContent_MakeAvailable(cftex_bc7 cftex_stb)

if(NOT CFTEX_ISPC_EXECUTABLE)
    FetchContent_Declare(cftex_ispc
        URL https://github.com/ispc/ispc/releases/download/v1.26.0/ispc-v1.26.0-windows.zip
        URL_HASH SHA256=cffe9904d32260994fa264f8fca60eac7be9f8995c122bacf456a1c66ac72987
        DOWNLOAD_EXTRACT_TIMESTAMP FALSE
        SOURCE_SUBDIR commonfury-source-only)
    FetchContent_MakeAvailable(cftex_ispc)
    set(CFTEX_ISPC_EXECUTABLE "${cftex_ispc_SOURCE_DIR}/bin/ispc.exe")
endif()
execute_process(COMMAND "${CFTEX_ISPC_EXECUTABLE}" --version
    OUTPUT_VARIABLE cftex_ispc_version ERROR_VARIABLE cftex_ispc_error RESULT_VARIABLE cftex_ispc_result)
if(NOT cftex_ispc_result EQUAL 0 OR NOT cftex_ispc_version MATCHES "1\\.26\\.0")
    message(FATAL_ERROR "ISPC 1.26.0 is required: ${cftex_ispc_error}${cftex_ispc_version}")
endif()

set(cftex_generated "${CMAKE_CURRENT_BINARY_DIR}/generated")
file(MAKE_DIRECTORY "${cftex_generated}")
set(cftex_ispc_objects "${cftex_generated}/bc7e.obj")
foreach(cftex_isa sse2 sse4 avx avx2)
    list(APPEND cftex_ispc_objects "${cftex_generated}/bc7e_${cftex_isa}.obj")
endforeach()
add_custom_command(
    OUTPUT ${cftex_ispc_objects} "${cftex_generated}/bc7e_ispc.h"
    COMMAND "${CFTEX_ISPC_EXECUTABLE}" "${cftex_bc7_SOURCE_DIR}/bc7e.ispc"
        -O2 --arch=x86-64 --target-os=windows --target=sse2,sse4,avx,avx2
        --opt=disable-assertions --wno-perf
        -o "${cftex_generated}/bc7e.obj" -h "${cftex_generated}/bc7e_ispc.h"
    DEPENDS "${cftex_bc7_SOURCE_DIR}/bc7e.ispc" "${CFTEX_ISPC_EXECUTABLE}"
    COMMENT "Compiling pinned BC7 encoder with SSE2/SSE4/AVX/AVX2 runtime dispatch"
    VERBATIM)
set_source_files_properties(${cftex_ispc_objects} PROPERTIES EXTERNAL_OBJECT TRUE GENERATED TRUE)

add_library(commonfury_texture_codecs OBJECT "${cftex_bc7_SOURCE_DIR}/rgbcx.cpp" src/stb_implementations.cpp)
target_compile_features(commonfury_texture_codecs PRIVATE cxx_std_17)
target_include_directories(commonfury_texture_codecs SYSTEM PRIVATE "${cftex_bc7_SOURCE_DIR}" "${cftex_stb_SOURCE_DIR}")
target_compile_definitions(commonfury_texture_codecs PRIVATE _CRT_SECURE_NO_WARNINGS NOMINMAX)
target_compile_options(commonfury_texture_codecs PRIVATE /W0 /EHsc)

add_library(commonfury_texture SHARED
    src/boundary.cpp src/stb_memory.cpp src/image_codec.cpp src/image_resize.cpp
    src/block_surface.cpp src/bc1_alpha.cpp src/block_encode.cpp src/block_decode.cpp
    ${cftex_ispc_objects} "${cftex_generated}/bc7e_ispc.h"
    $<TARGET_OBJECTS:commonfury_texture_codecs>)
target_compile_features(commonfury_texture PRIVATE cxx_std_17)
target_compile_definitions(commonfury_texture PRIVATE COMMONFURY_TEXTURE_EXPORTS NOMINMAX _CRT_SECURE_NO_WARNINGS)
target_include_directories(commonfury_texture PUBLIC src)
target_include_directories(commonfury_texture SYSTEM PRIVATE
    "${cftex_bc7_SOURCE_DIR}" "${cftex_stb_SOURCE_DIR}" "${cftex_generated}" vendor/bcdec)
target_compile_options(commonfury_texture PRIVATE /W4 /EHsc)
set_target_properties(commonfury_texture commonfury_texture_codecs PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
set_target_properties(commonfury_texture PROPERTIES CXX_VISIBILITY_PRESET hidden VISIBILITY_INLINES_HIDDEN YES)

if(CFTEX_BUILD_TESTS)
    enable_testing()
    add_executable(commonfury_texture_smoke tests/abi_smoke.cpp)
    target_compile_features(commonfury_texture_smoke PRIVATE cxx_std_17)
    target_link_libraries(commonfury_texture_smoke PRIVATE commonfury_texture)
    set_target_properties(commonfury_texture_smoke PROPERTIES MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    add_test(NAME commonfury_texture_abi COMMAND commonfury_texture_smoke)
endif()

