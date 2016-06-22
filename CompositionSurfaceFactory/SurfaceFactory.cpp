﻿#include "pch.h"
#include "SurfaceFactory.h"
#include "SharedLock.h"
#include "UriSurface.h"
#include "TextSurface.h"

using namespace Robmikh::CompositionSurfaceFactory;
using namespace Platform;
using namespace Windows::Graphics::Display;

namespace CSF = Robmikh::CompositionSurfaceFactory;

SurfaceFactory^ SurfaceFactory::CreateFromCompositor(WUC::Compositor^ compositor)
{
    auto surfaceFactory = ref new SurfaceFactory(compositor);
    return surfaceFactory;
}

SurfaceFactory^ SurfaceFactory::CreateFromGraphicsDevice(CompositionGraphicsDevice^ graphicsDevice)
{
    auto surfaceFactory = ref new SurfaceFactory(graphicsDevice, nullptr);
    return surfaceFactory;
}

SurfaceFactory^ SurfaceFactory::CreateFromGraphicsDevice(CompositionGraphicsDevice^ graphicsDevice, SharedLock^ lock)
{
    auto surfaceFactory = ref new SurfaceFactory(graphicsDevice, lock);
    return surfaceFactory;
}

SurfaceFactory::SurfaceFactory(WUC::Compositor^ compositor)
{
    m_compositor = compositor;
    m_drawingLock = ref new SharedLock();
    m_isDeviceCreator = true;
    OnDeviceLostHandler = DisplayInformation::DisplayContentsInvalidated 
        += ref new TypedEventHandler<DisplayInformation ^, Object ^>(this, &SurfaceFactory::OnDisplayContentsInvalidated);
    CreateDevice();
}

SurfaceFactory::SurfaceFactory(CompositionGraphicsDevice^ graphicsDevice, SharedLock^ lock)
{
    m_compositor = graphicsDevice->Compositor;
    m_graphicsDevice = graphicsDevice;
    m_isDeviceCreator = false;
    if (lock == nullptr)
    {
        m_drawingLock = ref new SharedLock();
    }
    else
    {
        m_drawingLock = lock;
    }

    OnRenderingDeviceReplacedHandler = m_graphicsDevice->RenderingDeviceReplaced 
        += ref new TypedEventHandler<CompositionGraphicsDevice ^, RenderingDeviceReplacedEventArgs ^>(this, &SurfaceFactory::OnRenderingDeviceReplaced);
}

SurfaceFactory::~SurfaceFactory()
{
    Uninitialize();
}

void SurfaceFactory::OnDisplayContentsInvalidated(DisplayInformation ^sender, Object ^args)
{
    OutputDebugString(L"CompositionSurfaceFactory - Display Contents Invalidated");
    // This will trigger the device lost event
    CanvasDevice::GetSharedDevice();
}

void SurfaceFactory::OnRenderingDeviceReplaced(CompositionGraphicsDevice ^sender, RenderingDeviceReplacedEventArgs ^args)
{
    OutputDebugString(L"CompositionSurfaceFactory - Rendering Device Replaced");
    concurrency::create_async([this, args]
    {
        this->RaiseDeviceReplacedEvent(args);
    });
}

void SurfaceFactory::RaiseDeviceReplacedEvent(RenderingDeviceReplacedEventArgs ^args)
{
    DeviceReplaced(this, args);
}

void SurfaceFactory::CreateDevice()
{
    if (m_compositor != nullptr && m_isDeviceCreator)
    {
        if (m_canvasDevice == nullptr)
        {
            m_canvasDevice = CanvasDevice::GetSharedDevice();
            OnDeviceLostHandler = m_canvasDevice->DeviceLost
                += ref new TypedEventHandler<CanvasDevice ^, Object ^>(this, &SurfaceFactory::OnDeviceLost);
        }

        if (m_graphicsDevice == nullptr)
        {
            m_graphicsDevice = CanvasComposition::CreateCompositionGraphicsDevice(m_compositor, m_canvasDevice);
            OnRenderingDeviceReplacedHandler = m_graphicsDevice->RenderingDeviceReplaced
                += ref new TypedEventHandler<CompositionGraphicsDevice ^, RenderingDeviceReplacedEventArgs ^>(this, &SurfaceFactory::OnRenderingDeviceReplaced);
        }
    }
}

void SurfaceFactory::OnDeviceLost(CanvasDevice ^sender, Object ^args)
{
    OutputDebugString(L"CompositionSurfaceFactory - Canvas Device Lost");
    sender->DeviceLost -= OnDeviceLostHandler;

    m_canvasDevice = CanvasDevice::GetSharedDevice();
    OnDeviceLostHandler = m_canvasDevice->DeviceLost
        += ref new TypedEventHandler<CanvasDevice ^, Object ^>(this, &SurfaceFactory::OnDeviceLost);

    CanvasComposition::SetCanvasDevice(m_graphicsDevice, m_canvasDevice);
}

CompositionDrawingSurface^ SurfaceFactory::CreateSurfaceFromUri(Uri^ uri)
{
    return CreateSurfaceFromUri(uri, Size::Empty);
}

CompositionDrawingSurface^ SurfaceFactory::CreateSurfaceFromUri(Uri^ uri, Size size)
{
    auto surface = CreateSurface(size);

    // We don't await this call, as we don't want to block the caller
    auto ignored = DrawSurface(surface, uri, size);

    return surface;
}

IAsyncOperation<CompositionDrawingSurface^>^ SurfaceFactory::CreateSurfaceFromUriAsync(Uri^ uri)
{
    return CreateSurfaceFromUriAsync(uri, Size::Empty);
}

IAsyncOperation<CompositionDrawingSurface^>^ SurfaceFactory::CreateSurfaceFromUriAsync(Uri^ uri, Size size)
{
    return concurrency::create_async([=]() -> CompositionDrawingSurface^
    {
        auto surface = CreateSurface(size);

        auto drawTask = DrawSurface(surface, uri, size).then([surface]()
        {
            return surface;
        });

        return drawTask.get();
    });
}

CompositionDrawingSurface^ SurfaceFactory::CreateSurface(Size size)
{
    Size surfaceSize = size;
    if (surfaceSize.IsEmpty)
    {
        // We start out with a size of 0,0 for the surface, because we don't know
        // the size of the image at this time. We resize the surface later.
        surfaceSize = { 0, 0 };
    }

    CompositionGraphicsDevice^ graphicsDevice = m_graphicsDevice;
    CompositionDrawingSurface^ surface = nullptr;
    m_drawingLock->Lock(ref new SharedLockWork([&surface, graphicsDevice, surfaceSize]() mutable
    {
        surface = graphicsDevice->CreateDrawingSurface(
            surfaceSize,
            Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,
            Windows::Graphics::DirectX::DirectXAlphaMode::Premultiplied);
    }));

    return surface;
}

concurrency::task<void> SurfaceFactory::DrawSurface(CompositionDrawingSurface^ surface, Uri^ uri, Size size) __resumable
{
    auto canvasDevice = CanvasComposition::GetCanvasDevice(m_graphicsDevice);
    auto canvasBitmap = __await CanvasBitmap::LoadAsync(canvasDevice, uri);
    DrawBitmap(surface, canvasBitmap, size);
}

void SurfaceFactory::DrawBitmap(CompositionDrawingSurface^ surface, CanvasBitmap^ canvasBitmap, Size size)
{
    auto bitmapSize = canvasBitmap->Size;

    // Because the drawing is done asynchronously and multiple threads could
    // be trying to get access to the device/surface at the same time, we need
    // to do any device/surface work under a lock.
    m_drawingLock->Lock(ref new SharedLockWork([=]()
    {
        Size surfaceSize = size;
        if (surfaceSize.IsEmpty)
        {
            // Resize the surface to the size of the image
            CanvasComposition::Resize(surface, bitmapSize);
            surfaceSize = bitmapSize;
        }
        
        auto session = CanvasComposition::CreateDrawingSession(surface);
        Rect surfaceRect = { 0, 0, surfaceSize.Width, surfaceSize.Height };
        Rect bitmapRect = { 0, 0, bitmapSize.Width, bitmapSize.Height };
        session->Clear(Windows::UI::Colors::Transparent);
        session->DrawImage(canvasBitmap, surfaceRect, bitmapRect);
    }));
}

void SurfaceFactory::ResizeSurface(CompositionDrawingSurface^ surface, Size size)
{
    m_drawingLock->Lock(ref new SharedLockWork([=]()
    {
        CanvasComposition::Resize(surface, size);
    }));
}

UriSurface^ SurfaceFactory::CreateUriSurface(Uri^ uri)
{
    return CreateUriSurface(uri, Size::Empty);
}

UriSurface^ SurfaceFactory::CreateUriSurface(Uri^ uri, Size size)
{
    auto uriSurface = UriSurface::Create(this, uri, size);
    auto ignored = uriSurface->RedrawSurface();

    return uriSurface;
}

IAsyncOperation<UriSurface^>^ SurfaceFactory::CreateUriSurfaceAsync(Uri^ uri)
{
    return CreateUriSurfaceAsync(uri, Size::Empty);
}

IAsyncOperation<UriSurface^>^ SurfaceFactory::CreateUriSurfaceAsync(Uri^ uri, Size size)
{
    return concurrency::create_async([=]() -> UriSurface^
    {
        auto uriSurface = UriSurface::Create(this, uri, size);

        auto drawTask = concurrency::create_task(uriSurface->RedrawSurface()).then([uriSurface]() -> UriSurface^
        {
            return uriSurface;
        });

        return drawTask.get();
    });
}

TextSurface^ SurfaceFactory::CreateTextSurface(Platform::String^ text)
{
    auto textSurface = TextSurface::Create(this, text);
    textSurface->RedrawSurface();

    return textSurface;
}

TextSurface^ SurfaceFactory::CreateTextSurface(Platform::String^ text,
                                               float width,
                                               float height,
                                               Platform::String^ fontFamily,
                                               float fontSize,
                                               WUT::FontStyle fontStyle,
                                               TextHorizontalAlignment horizontalAlignment,
                                               TextVerticalAlignment verticalAlignment,
                                               CSF::WordWrapping wordWrapping,
                                               CSF::Padding padding,
                                               Windows::UI::Color foregroundColor,
                                               Windows::UI::Color backgroundColor)
{
    auto textSurface = TextSurface::Create(this, 
                                           text,
                                           width,
                                           height,
                                           fontFamily,
                                           fontSize,
                                           fontStyle,
                                           horizontalAlignment,
                                           verticalAlignment,
                                           wordWrapping,
                                           padding,
                                           foregroundColor,
                                           backgroundColor);
    textSurface->RedrawSurface();

    return textSurface;
}

CompositionDrawingSurface^ SurfaceFactory::CreateSurfaceFromBytes(const Array<byte>^ bytes, int widthInPixels, int heightInPixels)
{
    return CreateSurfaceFromBytes(bytes, widthInPixels, heightInPixels, Size::Empty);
}

CompositionDrawingSurface^ SurfaceFactory::CreateSurfaceFromBytes(const Array<byte>^ bytes, int widthInPixels, int heightInPixels, Size size)
{
    auto surface = CreateSurface(size);

    auto canvasDevice = CanvasComposition::GetCanvasDevice(m_graphicsDevice);
    auto canvasBitmap = CanvasBitmap::CreateFromBytes(canvasDevice, bytes, widthInPixels, heightInPixels, Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized);
    DrawBitmap(surface, canvasBitmap, size);

    return surface;
}

void SurfaceFactory::Uninitialize()
{
    m_drawingLock->Lock(ref new SharedLockWork([=]() mutable
    {
        m_compositor = nullptr;
        DisplayInformation::DisplayContentsInvalidated -= OnDisplayContentsInvalidatedHandler;
        if (m_canvasDevice != nullptr)
        {
            m_canvasDevice->DeviceLost -= OnDeviceLostHandler;
            // Only dispose the canvas device if we own the device.
            if (m_isDeviceCreator)
            {
                m_canvasDevice->~CanvasDevice();
            }
            m_canvasDevice = nullptr;
        }
        
        if (m_graphicsDevice != nullptr)
        {
            m_graphicsDevice->RenderingDeviceReplaced -= OnRenderingDeviceReplacedHandler;
            // Only dispose the composition graphics device if we own the device.
            if (m_isDeviceCreator)
            {
                m_graphicsDevice->~CompositionGraphicsDevice();
            }
            m_graphicsDevice = nullptr;
        }
    }));
}