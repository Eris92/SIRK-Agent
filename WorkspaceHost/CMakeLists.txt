cmake_minimum_required(VERSION 3.24)
project(WorkspaceHost VERSION 0.2.0 LANGUAGES CXX)

add_executable(WorkspaceHost WIN32
    src/main.cpp
)

target_compile_features(WorkspaceHost PRIVATE cxx_std_20)
target_compile_definitions(WorkspaceHost PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)

if(MSVC)
    target_compile_options(WorkspaceHost PRIVATE /W4 /permissive- /EHsc)
    target_link_options(WorkspaceHost PRIVATE /SUBSYSTEM:CONSOLE)
endif()
