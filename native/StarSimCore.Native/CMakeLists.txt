cmake_minimum_required(VERSION 3.25)

project(StarSimCoreNative VERSION 1.0.0 LANGUAGES CXX)

find_package(PNG REQUIRED)
find_package(TIFF REQUIRED)

add_library(StarSimCore.Native SHARED
    src/starsim_core_native.cpp)

target_compile_features(StarSimCore.Native PRIVATE cxx_std_20)
target_compile_definitions(StarSimCore.Native PRIVATE STARSIM_CORE_NATIVE_EXPORTS)
target_include_directories(StarSimCore.Native
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>)
target_link_libraries(StarSimCore.Native PRIVATE PNG::PNG TIFF::TIFF)

set_target_properties(StarSimCore.Native PROPERTIES
    CXX_EXTENSIONS OFF
    OUTPUT_NAME "StarSimCore.Native")

if(MSVC)
    target_compile_options(StarSimCore.Native PRIVATE /W4 /permissive- /EHsc)
else()
    target_compile_options(StarSimCore.Native PRIVATE -Wall -Wextra -Wpedantic)
endif()

add_library(StarSimCore.SamplePlugin SHARED
    ../StarSimCore.SamplePlugin/sample_plugin.cpp)

target_compile_features(StarSimCore.SamplePlugin PRIVATE cxx_std_20)
target_compile_definitions(StarSimCore.SamplePlugin PRIVATE STARSIM_CORE_PLUGIN_EXPORTS)
target_include_directories(StarSimCore.SamplePlugin
    PRIVATE
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>)

set_target_properties(StarSimCore.SamplePlugin PROPERTIES
    CXX_EXTENSIONS OFF
    OUTPUT_NAME "starsim_diagnostic_plugin")

if(MSVC)
    target_compile_options(StarSimCore.SamplePlugin PRIVATE /W4 /permissive- /EHsc)
else()
    target_compile_options(StarSimCore.SamplePlugin PRIVATE -Wall -Wextra -Wpedantic)
endif()

install(TARGETS StarSimCore.Native StarSimCore.SamplePlugin
    RUNTIME DESTINATION bin
    LIBRARY DESTINATION lib
    ARCHIVE DESTINATION lib)
install(FILES include/starsim_core_native.h include/starsim_core_plugin.h DESTINATION include)
