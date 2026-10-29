cmake_minimum_required(VERSION 3.24)
project(commonfury_audio LANGUAGES C)

include(FetchContent)
# The release archive also carries stb_vorbis (extras/stb_vorbis.c) at the matching revision.
FetchContent_Declare(miniaudio
    URL https://github.com/mackron/miniaudio/archive/refs/tags/0.11.25.tar.gz
    URL_HASH SHA256=b900edcffe979816e2560a0580b9b1216d674b4f17fbadeca8f777a7f8ab0274
    DOWNLOAD_EXTRACT_TIMESTAMP FALSE
    SOURCE_SUBDIR commonfury-source-only)
FetchContent_MakeAvailable(miniaudio)

add_library(commonfury_audio SHARED commonfury_audio.c)
target_include_directories(commonfury_audio PRIVATE "${miniaudio_SOURCE_DIR}")
set_target_properties(commonfury_audio PROPERTIES C_VISIBILITY_PRESET hidden INTERPROCEDURAL_OPTIMIZATION ON)

if(MSVC)
    # Built as MinSizeRel, statically linked to the C runtime, so the DLL only needs Windows system libraries.
    set_target_properties(commonfury_audio PROPERTIES MSVC_RUNTIME_LIBRARY "MultiThreaded")
    target_compile_definitions(commonfury_audio PRIVATE _CRT_SECURE_NO_WARNINGS)
    target_compile_options(commonfury_audio PRIVATE /W0)
    target_link_options(commonfury_audio PRIVATE /OPT:REF /OPT:ICF)
else()
    target_compile_options(commonfury_audio PRIVATE -w)
    target_link_libraries(commonfury_audio PRIVATE m)
endif()
