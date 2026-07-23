cmake_minimum_required(VERSION 3.24)
project(WorkspaceHost VERSION 0.7.0 LANGUAGES CXX)

add_executable(WorkspaceHost
    src/main.cpp
)

target_compile_features(WorkspaceHost PRIVATE cxx_std_20)
target_compile_definitions(WorkspaceHost PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(WorkspaceHost PRIVATE Wtsapi32 Userenv Advapi32)

if(MSVC)
    # /MT for Release and /MTd for Debug. The resulting EXE does not require
    # VCRUNTIME140_1.dll, MSVCP140.dll or the Visual C++ Redistributable.
    set_property(TARGET WorkspaceHost PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

    target_compile_options(WorkspaceHost PRIVATE /W4 /permissive- /EHsc)
endif()