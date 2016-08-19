﻿using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Graphics.DirectX;
using Windows.Graphics.Display;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Text;

namespace Robmikh.Util.CompositionImageLoader
{
    public interface IImageLoader : IDisposable
    {
        event EventHandler<Object> DeviceReplacedEvent;
        CompositionDrawingSurface LoadImageFromUri(Uri uri);
        CompositionDrawingSurface LoadImageFromUri(Uri uri, Size size);
        IAsyncOperation<CompositionDrawingSurface> LoadImageFromUriAsync(Uri uri);
        IAsyncOperation<CompositionDrawingSurface> LoadImageFromUriAsync(Uri uri, Size size);
        IManagedSurface CreateManagedSurfaceFromUri(Uri uri);
        IManagedSurface CreateManagedSurfaceFromUri(Uri uri, Size size);
        IAsyncOperation<IManagedSurface> CreateManagedSurfaceFromUriAsync(Uri uri);
        IAsyncOperation<IManagedSurface> CreateManagedSurfaceFromUriAsync(Uri uri, Size size);
        ITextSurface CreateTextSurface(String text);
        ITextSurface CreateTextSurface(String text,
                                       float width,
                                       float height,
                                       String fontFamily,
                                       float fontSize,
                                       FontStyle fontStyle,
                                       WordWrapping wordWrapping,
                                       Padding padding,
                                       Color foreground,
                                       Color background);
        //IDrawnSurface CreateDrawnSurface(float width,
        //                                 float height,
        //                                 CanvasDrawingSession drawProgram);
        CompositionDrawingSurface LoadImageFromBytes([ReadOnlyArray()] byte[] bytes, int widthInPixels, int heightInPixels);
        CompositionDrawingSurface LoadImageFromBytes([ReadOnlyArray()] byte[] bytes, int widthInPixels, int heightInPixels, Size size);
        CompositionDrawingSurface CreateSurface(Size size);
        void DrawIntoSurface(CompositionDrawingSurface surface, DrawCallback callback);
    }

    public delegate void DrawCallback(CompositionDrawingSurface surface, CompositionGraphicsDevice device);

    interface IImageLoaderInternal : IImageLoader
    {
        void ResizeSurface(CompositionDrawingSurface surface, Size size);
        Task DrawSurface(CompositionDrawingSurface surface, Uri uri, Size size);
        void DoWorkUnderLock(LockedWork callback);
    }

    delegate void LockedWork();

    public static class ImageLoaderFactory
    {
        [DefaultOverloadAttribute]
        public static IImageLoader CreateImageLoader(Compositor compositor)
        {
            var imageLoader = new ImageLoader();
            imageLoader.Initialize(compositor);
            return imageLoader;
        }

        public static IImageLoader CreateImageLoader(CompositionGraphicsDevice graphicsDevice)
        {
            var imageLoader = new ImageLoader();
            imageLoader.Initialize(graphicsDevice);
            return imageLoader;
        }

        public static IImageLoader CreateImageLoader(CompositionGraphicsDevice graphicsDevice, Object sharedLock)
        {
            var imageLoader = new ImageLoader();
            imageLoader.Initialize(graphicsDevice, sharedLock);
            return imageLoader;
        }
    }

    class ImageLoader : IImageLoaderInternal
    {
        public event EventHandler<Object> DeviceReplacedEvent;

        private Compositor _compositor;
        private CanvasDevice _canvasDevice;
        private CompositionGraphicsDevice _graphicsDevice;
        private Object _drawingLock;

        private bool _isDeviceCreator;

        public ImageLoader() { }

        private void OnDisplayContentsInvalidated(DisplayInformation sender, object args)
        {
            Debug.WriteLine("CompositionImageLoader - Display Contents Invalidated");
            //
            // This will trigger the device lost event
            //
            CanvasDevice.GetSharedDevice();
        }

        public void Initialize(Compositor compositor)
        {
            _compositor = compositor;
            _drawingLock = new object();
            _isDeviceCreator = true;
            DisplayInformation.DisplayContentsInvalidated += OnDisplayContentsInvalidated;
            CreateDevice();
        }

        public void Initialize(CompositionGraphicsDevice graphicsDevice, Object sharedLock = null)
        {
            _graphicsDevice = graphicsDevice;
            _graphicsDevice.RenderingDeviceReplaced += RenderingDeviceReplaced;
            if (sharedLock == null)
            {
                _drawingLock = new object();
            }
            else
            {
                _drawingLock = sharedLock;
            }
            _isDeviceCreator = false;
            //
            // We don't call CreateDevice, as it wouldn't do anything
            // since we don't have a Compositor.
            //
        }

        private void CreateDevice()
        {
            if (_compositor != null)
            {
                if (_canvasDevice == null)
                {
                    _canvasDevice = CanvasDevice.GetSharedDevice();
                    _canvasDevice.DeviceLost += DeviceLost;
                }

                if (_graphicsDevice == null)
                {
                    _graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(_compositor, _canvasDevice);
                    _graphicsDevice.RenderingDeviceReplaced += RenderingDeviceReplaced;
                }
            }
        }

        private void DeviceLost(CanvasDevice sender, object args)
        {
            Debug.WriteLine("CompositionImageLoader - Canvas Device Lost");
            sender.DeviceLost -= DeviceLost;

            _canvasDevice = CanvasDevice.GetSharedDevice();
            _canvasDevice.DeviceLost += DeviceLost;

            CanvasComposition.SetCanvasDevice(_graphicsDevice, _canvasDevice);
        }

        private void RenderingDeviceReplaced(CompositionGraphicsDevice sender, RenderingDeviceReplacedEventArgs args)
        {
            Debug.WriteLine("CompositionImageLoader - Rendering Device Replaced");
            Task.Run(() =>
            {
                if (DeviceReplacedEvent != null)
                {
                    RaiseDeviceReplacedEvent();
                }
            });
        }

        private void RaiseDeviceReplacedEvent()
        {
            var deviceEvent = DeviceReplacedEvent;
            if (deviceEvent != null)
            {
                deviceEvent(this, new EventArgs());
            }
        }

        public CompositionDrawingSurface LoadImageFromUri(Uri uri)
        {
            return LoadImageFromUri(uri, Size.Empty);
        }

        public CompositionDrawingSurface LoadImageFromUri(Uri uri, Size size)
        {
            var surface = CreateSurface(size);

            //
            // We don't await this call, as we don't want to block
            // the caller.
            //
            var ignored = DrawSurface(surface, uri, size);

            return surface;
        }

        private async Task<CompositionDrawingSurface> LoadImageFromUriAsyncWorker(Uri uri, Size size)
        {
            var surface = CreateSurface(size);

            await DrawSurface(surface, uri, size);

            return surface;
        }

        public IAsyncOperation<CompositionDrawingSurface> LoadImageFromUriAsync(Uri uri)
        {
            return LoadImageFromUriAsync(uri, Size.Empty);
        }
        public IAsyncOperation<CompositionDrawingSurface> LoadImageFromUriAsync(Uri uri, Size size)
        {
            return LoadImageFromUriAsyncWorker(uri, size).AsAsyncOperation<CompositionDrawingSurface>();
        }

        public IManagedSurface CreateManagedSurfaceFromUri(Uri uri)
        {
            return CreateManagedSurfaceFromUri(uri, Size.Empty);
        }

        public IManagedSurface CreateManagedSurfaceFromUri(Uri uri, Size size)
        {
            var managedSurface = new ManagedSurface(this, uri, size);
            var ignored = managedSurface.RedrawSurface();

            return managedSurface;
        }

        public async Task<IManagedSurface> CreateManagedSurfaceFromUriAsyncWorker(Uri uri, Size size)
        {
            var managedSurface = new ManagedSurface(this, uri, size);
            await managedSurface.RedrawSurface();

            return managedSurface;
        }

        public IAsyncOperation<IManagedSurface> CreateManagedSurfaceFromUriAsync(Uri uri)
        {
            return CreateManagedSurfaceFromUriAsyncWorker(uri, Size.Empty).AsAsyncOperation<IManagedSurface>();
        }

        public IAsyncOperation<IManagedSurface> CreateManagedSurfaceFromUriAsync(Uri uri, Size size)
        {
            return CreateManagedSurfaceFromUriAsyncWorker(uri, size).AsAsyncOperation<IManagedSurface>();
        }

        public ITextSurface CreateTextSurface(String text)
        {
            var textSurface = new TextSurface(this, text);
            textSurface.RedrawSurface();

            return textSurface;
        }

        public ITextSurface CreateTextSurface(String text,
                                              float width,
                                              float height,
                                              String fontFamily,
                                              float fontSize,
                                              FontStyle fontStyle,
                                              WordWrapping wordWrapping,
                                              Padding padding,
                                              Color foreground,
                                              Color background)
        {
            var textSurface = new TextSurface(this,
                                              text,
                                              width,
                                              height,
                                              fontFamily,
                                              fontSize,
                                              fontStyle,
                                              wordWrapping,
                                              padding,
                                              foreground,
                                              background);
            textSurface.RedrawSurface();

            return textSurface;
        }

        //public IDrawnSurface CreateDrawnSurface(float width,
        //                                        float height,
        //                                        CanvasDrawingSession drawProgram)
        //{
        //    var drawnSurface = new DrawnSurface(this,
        //        width,
        //        height,
        //        drawProgram
        //        );

        //    drawnSurface.RedrawSurface();

        //    return drawnSurface;
        //}

        public CompositionDrawingSurface LoadImageFromBytes(byte[] bytes, int widthInPixels, int heightInPixels)
        {
            return LoadImageFromBytes(bytes, widthInPixels, heightInPixels, Size.Empty);
        }

        public CompositionDrawingSurface LoadImageFromBytes(byte[] bytes, int widthInPixels, int heightInPixels, Size size)
        {
            var surface = CreateSurface(size);

            DrawBytes(surface, bytes, widthInPixels, heightInPixels, size);

            return surface;
        }

        public void DrawIntoSurface(CompositionDrawingSurface surface, DrawCallback callback)
        {
            if (surface.Compositor != _graphicsDevice.Compositor)
            {
                throw new ArgumentException("Surface belongs to a different Compositor.");
            }

            lock(_drawingLock)
            {
                callback.Invoke(surface, _graphicsDevice);
            }
        }

        public async Task DrawSurface(CompositionDrawingSurface surface, Uri uri, Size size)
        {
            var canvasDevice = CanvasComposition.GetCanvasDevice(_graphicsDevice);
            using (var canvasBitmap = await CanvasBitmap.LoadAsync(canvasDevice, uri))
            {
                DrawBitmap(surface, canvasBitmap, size);
            }
        }

        public void DrawBytes(CompositionDrawingSurface surface, byte[] bytes, int widthInPixels, int heightInPixels, Size size)
        {
            var canvasDevice = CanvasComposition.GetCanvasDevice(_graphicsDevice);
            using (var canvasBitmap = CanvasBitmap.CreateFromBytes(canvasDevice, bytes, widthInPixels, heightInPixels, DirectXPixelFormat.B8G8R8A8UIntNormalized))
            {
                DrawBitmap(surface, canvasBitmap, size);
            }
        }

        public void DrawBitmap(CompositionDrawingSurface surface, CanvasBitmap canvasBitmap, Size size)
        {
            var bitmapSize = canvasBitmap.Size;

            //
            // Because the drawing is done asynchronously and multiple threads could
            // be trying to get access to the device/surface at the same time, we need
            // to do any device/surface work under a lock.
            //
            lock (_drawingLock)
            {
                Size surfaceSize = size;
                if (surfaceSize.IsEmpty)
                {
                    // Resize the surface to the size of the image
                    CanvasComposition.Resize(surface, bitmapSize);
                    surfaceSize = bitmapSize;
                }

                // Draw the image to the surface
                using (var session = CanvasComposition.CreateDrawingSession(surface))
                {
                    session.Clear(Color.FromArgb(0, 0, 0, 0));
                    session.DrawImage(canvasBitmap, new Rect(0, 0, surfaceSize.Width, surfaceSize.Height), new Rect(0, 0, bitmapSize.Width, bitmapSize.Height));
                }
            }
        }

        public CompositionDrawingSurface CreateSurface(Size size)
        {
            Size surfaceSize = size;
            if (surfaceSize.IsEmpty)
            {
                //
                // We start out with a size of 0,0 for the surface, because we don't know
                // the size of the image at this time. We resize the surface later.
                //
                surfaceSize = new Size(0, 0);
            }

            CompositionDrawingSurface surface;
            lock(_drawingLock)
            {
                surface = _graphicsDevice.CreateDrawingSurface(surfaceSize, DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
            }

            return surface;
        }

        public void ResizeSurface(CompositionDrawingSurface surface, Size size)
        {
            if (!size.IsEmpty)
            {
                lock (_drawingLock)
                {
                    CanvasComposition.Resize(surface, size);
                }
            }
        }
        
        public void DoWorkUnderLock(LockedWork callback)
        {
            if (callback != null)
            {
                lock (_drawingLock)
                {
                    callback();
                }
            }
        }

        public void Dispose()
        {
            lock (_drawingLock)
            {
                _compositor = null;
                DisplayInformation.DisplayContentsInvalidated -= OnDisplayContentsInvalidated;

                if (_canvasDevice != null)
                {
                    _canvasDevice.DeviceLost -= DeviceLost;
                    //
                    // Only dispose the canvas device if we own the device.
                    //
                    if (_isDeviceCreator)
                    {
                        _canvasDevice.Dispose();
                    }
                    _canvasDevice = null;
                }

                if (_graphicsDevice != null)
                {
                    _graphicsDevice.RenderingDeviceReplaced -= RenderingDeviceReplaced;
                    //
                    // Only dispose the composition graphics device if we own the device.
                    //
                    if (_isDeviceCreator)
                    {
                        _graphicsDevice.Dispose();
                    }
                    _graphicsDevice = null;
                }
            }
        }
    }
}
