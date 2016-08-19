using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Robmikh.Util.CompositionImageLoader;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI.Composition;

namespace Robmikh.Util.CompositionImageLoader
{
    public interface IDrawnSurface : IDisposable
    {
        //
        // Read-only Properties
        //
        IImageLoader ImageLoader { get; }
        ICompositionSurface Surface { get; }
        Size Size { get; }

        //
        // Editable Properties
        //
        float Width { get; set; }
        float Height { get; set; }

        //
        // Functions
        //
        void RedrawSurface();

        //
        // Events
        //
        event EventHandler<Object> SurfaceRedrawn;
    }
}

class DrawnSurface : IDrawnSurface
{
    public event EventHandler<Object> SurfaceRedrawn;

    private IImageLoaderInternal _imageLoader;
    private CompositionDrawingSurface _surface;
    private Action<CanvasDrawingSession> _drawProgram;

    private float _width;
    private float _height;

    #region Read-only Properties
    public IImageLoader ImageLoader
    {
        get
        {
            return _imageLoader;
        }
    }

    public ICompositionSurface Surface
    {
        get
        {
            return _surface;
        }
    }

    public Size Size
    {
        get
        {
            if (_surface != null)
            {
                return _surface.Size;
            }
            else
            {
                return Size.Empty;
            }
        }
    }
    #endregion

    #region Editable Properties

    public float Width
    {
        get { return _width; }
        set
        {
            _width = value;
            RedrawSurface();
        }
    }

    public float Height
    {
        get { return _height; }
        set
        {
            _height = value;
            RedrawSurface();
        }
    }
    #endregion

    #region Constructors
    public DrawnSurface(IImageLoaderInternal imageLoader
                       )
    {
        Initialize(imageLoader,
                   0,
                   0,
                   null);
    }

    public DrawnSurface(IImageLoaderInternal imageLoader,
                       float width,
                       float height,
                       Action<CanvasDrawingSession> drawProgram
                       )
    {
        Initialize(imageLoader,
                   width,
                   height,
                   drawProgram);
    }
    #endregion

    #region Public Methods
    public void RedrawSurface()
    {
        Task.Run(() =>
        {
            _imageLoader.DoWorkUnderLock(() =>
            {
                CanvasComposition.Resize(_surface, new Size(_width, _height));

                using (var session = CanvasComposition.CreateDrawingSession(_surface))
                {
                    _drawProgram(session);
                }
            });

            if (SurfaceRedrawn != null)
            {
                RaiseSurfaceRedrawnEvent();
            }
        });
    }

    public void Dispose()
    {
        SurfaceRedrawn = null;
        _surface.Dispose();
        _imageLoader.DeviceReplacedEvent -= OnDeviceReplaced;
        _surface = null;
        _imageLoader = null;
    }
    #endregion

    #region Private Methods
    private void Initialize(IImageLoaderInternal imageLoader,
             float width,
                                float height,
                                Action<CanvasDrawingSession> drawProgram
        )
    {
        _drawProgram = drawProgram;

        _imageLoader = imageLoader;

        _width = width;
        _height = height;

        _surface = _imageLoader.CreateSurface(new Size(_width, height));

        _imageLoader.DeviceReplacedEvent += OnDeviceReplaced;
    }

    private void OnDeviceReplaced(object sender, object e)
    {
        Debug.WriteLine("CompositionImageLoader - Redrawing TextSurface from Device Replaced");
        RedrawSurface();
    }

    private void RaiseSurfaceRedrawnEvent()
    {
        var surfaceEvent = SurfaceRedrawn;
        if (surfaceEvent != null)
        {
            surfaceEvent(this, new EventArgs());
        }
    }
    #endregion
}