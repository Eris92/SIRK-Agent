#include "capture_dxgi.h"

#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <filesystem>
#include <sstream>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace {

std::wstring HResultText(HRESULT value) {
    wchar_t* message = nullptr;
    const DWORD flags = FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS;
    FormatMessageW(flags, nullptr, static_cast<DWORD>(value), 0, reinterpret_cast<wchar_t*>(&message), 0, nullptr);
    std::wstringstream stream;
    stream << L"0x" << std::hex << static_cast<unsigned long>(value);
    if (message != nullptr) {
        stream << L" (" << message << L")";
        LocalFree(message);
    }
    return stream.str();
}

bool SavePng(const std::filesystem::path& path, const D3D11_MAPPED_SUBRESOURCE& mapped,
             UINT width, UINT height, std::wstring& error) {
    ComPtr<IWICImagingFactory> factory;
    HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                  IID_PPV_ARGS(&factory));
    if (FAILED(hr)) { error = L"WIC factory failed: " + HResultText(hr); return false; }

    ComPtr<IWICStream> stream;
    hr = factory->CreateStream(&stream);
    if (FAILED(hr)) { error = L"WIC stream failed: " + HResultText(hr); return false; }
    hr = stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE);
    if (FAILED(hr)) { error = L"Cannot create PNG file: " + HResultText(hr); return false; }

    ComPtr<IWICBitmapEncoder> encoder;
    hr = factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder);
    if (FAILED(hr)) { error = L"PNG encoder failed: " + HResultText(hr); return false; }
    hr = encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache);
    if (FAILED(hr)) { error = L"PNG encoder initialization failed: " + HResultText(hr); return false; }

    ComPtr<IWICBitmapFrameEncode> frame;
    ComPtr<IPropertyBag2> properties;
    hr = encoder->CreateNewFrame(&frame, &properties);
    if (FAILED(hr)) { error = L"PNG frame creation failed: " + HResultText(hr); return false; }
    hr = frame->Initialize(properties.Get());
    if (FAILED(hr)) { error = L"PNG frame initialization failed: " + HResultText(hr); return false; }
    hr = frame->SetSize(width, height);
    if (FAILED(hr)) { error = L"PNG size failed: " + HResultText(hr); return false; }

    WICPixelFormatGUID format = GUID_WICPixelFormat32bppBGRA;
    hr = frame->SetPixelFormat(&format);
    if (FAILED(hr) || format != GUID_WICPixelFormat32bppBGRA) {
        error = L"PNG pixel format is unsupported: " + HResultText(hr);
        return false;
    }

    const UINT rowBytes = width * 4;
    std::vector<BYTE> packed(static_cast<size_t>(rowBytes) * height);
    const auto* source = static_cast<const BYTE*>(mapped.pData);
    for (UINT row = 0; row < height; ++row) {
        memcpy(packed.data() + static_cast<size_t>(row) * rowBytes,
               source + static_cast<size_t>(row) * mapped.RowPitch,
               rowBytes);
    }

    hr = frame->WritePixels(height, rowBytes, static_cast<UINT>(packed.size()), packed.data());
    if (FAILED(hr)) { error = L"PNG write failed: " + HResultText(hr); return false; }
    hr = frame->Commit();
    if (FAILED(hr)) { error = L"PNG frame commit failed: " + HResultText(hr); return false; }
    hr = encoder->Commit();
    if (FAILED(hr)) { error = L"PNG encoder commit failed: " + HResultText(hr); return false; }
    return true;
}

}

namespace workspace {

bool CapturePrimaryOutputPng(const std::filesystem::path& outputPath, CaptureResult& result, std::wstring& error) {
    const HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool uninitialize = SUCCEEDED(com);
    if (FAILED(com) && com != RPC_E_CHANGED_MODE) {
        error = L"COM initialization failed: " + HResultText(com);
        return false;
    }

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    D3D_FEATURE_LEVEL featureLevel{};
    const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0,
                                        D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0 };
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                                   D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels,
                                   static_cast<UINT>(std::size(levels)), D3D11_SDK_VERSION,
                                   &device, &featureLevel, &context);
    if (FAILED(hr)) {
        hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr,
                               D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels,
                               static_cast<UINT>(std::size(levels)), D3D11_SDK_VERSION,
                               &device, &featureLevel, &context);
    }
    if (FAILED(hr)) {
        error = L"D3D11 device creation failed: " + HResultText(hr);
        if (uninitialize) CoUninitialize();
        return false;
    }

    ComPtr<IDXGIDevice> dxgiDevice;
    hr = device.As(&dxgiDevice);
    ComPtr<IDXGIAdapter> adapter;
    if (SUCCEEDED(hr)) hr = dxgiDevice->GetAdapter(&adapter);
    ComPtr<IDXGIOutput> output;
    if (SUCCEEDED(hr)) hr = adapter->EnumOutputs(0, &output);
    ComPtr<IDXGIOutput1> output1;
    if (SUCCEEDED(hr)) hr = output.As(&output1);
    if (FAILED(hr)) {
        error = L"DXGI primary output lookup failed: " + HResultText(hr);
        if (uninitialize) CoUninitialize();
        return false;
    }

    ComPtr<IDXGIOutputDuplication> duplication;
    hr = output1->DuplicateOutput(device.Get(), &duplication);
    if (FAILED(hr)) {
        error = L"DXGI Desktop Duplication failed: " + HResultText(hr) +
                L". The desktop must be active and unlocked.";
        if (uninitialize) CoUninitialize();
        return false;
    }

    DXGI_OUTDUPL_FRAME_INFO frameInfo{};
    ComPtr<IDXGIResource> resource;
    hr = duplication->AcquireNextFrame(2000, &frameInfo, &resource);
    if (FAILED(hr)) {
        error = L"No DXGI frame received: " + HResultText(hr);
        if (uninitialize) CoUninitialize();
        return false;
    }

    bool frameAcquired = true;
    ComPtr<ID3D11Texture2D> source;
    hr = resource.As(&source);
    D3D11_TEXTURE2D_DESC description{};
    if (SUCCEEDED(hr)) source->GetDesc(&description);

    ComPtr<ID3D11Texture2D> staging;
    if (SUCCEEDED(hr)) {
        D3D11_TEXTURE2D_DESC stagingDescription = description;
        stagingDescription.BindFlags = 0;
        stagingDescription.MiscFlags = 0;
        stagingDescription.Usage = D3D11_USAGE_STAGING;
        stagingDescription.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        hr = device->CreateTexture2D(&stagingDescription, nullptr, &staging);
    }
    if (SUCCEEDED(hr)) context->CopyResource(staging.Get(), source.Get());

    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (SUCCEEDED(hr)) hr = context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(hr)) {
        if (frameAcquired) duplication->ReleaseFrame();
        error = L"DXGI staging map failed: " + HResultText(hr);
        if (uninitialize) CoUninitialize();
        return false;
    }

    std::filesystem::create_directories(outputPath.parent_path());
    const bool saved = SavePng(outputPath, mapped, description.Width, description.Height, error);
    context->Unmap(staging.Get(), 0);
    duplication->ReleaseFrame();

    if (saved) {
        result.width = description.Width;
        result.height = description.Height;
        result.backend = L"DXGI Desktop Duplication";
    }
    if (uninitialize) CoUninitialize();
    return saved;
}

}
