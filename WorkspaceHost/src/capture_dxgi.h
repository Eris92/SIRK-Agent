#pragma once

#include <filesystem>
#include <string>

namespace workspace {

struct CaptureResult {
    unsigned int width = 0;
    unsigned int height = 0;
    std::wstring backend;
};

bool CapturePrimaryOutputPng(const std::filesystem::path& outputPath, CaptureResult& result, std::wstring& error);

}
