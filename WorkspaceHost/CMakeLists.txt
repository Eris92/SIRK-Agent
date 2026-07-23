cmake_minimum_required(VERSION 3.24)
project(WorkspaceHost VERSION 0.7.1 LANGUAGES CXX)

add_executable(WorkspaceHost
    src/main.cpp
)

add_executable(WorkspaceCapture
    src/capture_main.cpp
    src/capture_dxgi.cpp
    src/capture_dxgi.h
)

target_compile_features(WorkspaceHost PRIVATE cxx_std_20)
target_compile_features(WorkspaceCapture PRIVATE cxx_std_20)
target_compile_definitions(WorkspaceHost PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_compile_definitions(WorkspaceCapture PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(WorkspaceHost PRIVATE Wtsapi32 Userenv Advapi32)
target_link_libraries(WorkspaceCapture PRIVATE D3D11 Dxgi Windowscodecs Ole32)

if(MSVC)
    foreach(target WorkspaceHost WorkspaceCapture)
        set_property(TARGET ${target} PROPERTY
            MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
        target_compile_options(${target} PRIVATE /W4 /permissive- /EHsc)
    endforeach()
endif()
