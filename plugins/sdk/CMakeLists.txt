cmake_minimum_required(VERSION 3.24)
project(StarSimCoreSamplePlugin LANGUAGES CXX)

add_library(starsim_diagnostic_plugin SHARED sample_plugin.cpp)
target_compile_features(starsim_diagnostic_plugin PRIVATE cxx_std_20)
target_compile_definitions(starsim_diagnostic_plugin PRIVATE STARSIM_CORE_PLUGIN_EXPORTS)
target_include_directories(starsim_diagnostic_plugin PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
set_target_properties(starsim_diagnostic_plugin PROPERTIES CXX_EXTENSIONS OFF)

if(MSVC)
    target_compile_options(starsim_diagnostic_plugin PRIVATE /W4 /permissive- /EHsc)
else()
    target_compile_options(starsim_diagnostic_plugin PRIVATE -Wall -Wextra -Wpedantic)
endif()
