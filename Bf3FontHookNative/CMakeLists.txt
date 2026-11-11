cmake_minimum_required(VERSION 3.20)
project(Bf3FontHookNative LANGUAGES C CXX)

if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 4)
    message(FATAL_ERROR "BF3 font hook must be built for Windows x86")
endif()

set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
add_subdirectory(third_party/minhook)

add_library(bf3_dinput8_proxy SHARED FontHook.cpp exports.def)
target_compile_features(bf3_dinput8_proxy PRIVATE cxx_std_17)
target_compile_definitions(bf3_dinput8_proxy PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(bf3_dinput8_proxy PRIVATE minhook)
set_target_properties(bf3_dinput8_proxy PROPERTIES OUTPUT_NAME dinput8)

add_executable(bf3_font_smoke Smoke.cpp)
target_compile_features(bf3_font_smoke PRIVATE cxx_std_17)
target_compile_definitions(bf3_font_smoke PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)

if(MSVC)
    target_compile_options(bf3_dinput8_proxy PRIVATE /W4 /EHsc /utf-8)
    target_link_options(bf3_dinput8_proxy PRIVATE /DYNAMICBASE /NXCOMPAT)
endif()
