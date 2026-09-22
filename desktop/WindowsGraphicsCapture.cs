using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace MSGuide.Desktop;

internal sealed class WindowsGraphicsCaptureFrameCapture : ISelectedWindowFrameCapture
{
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint hwnd, in Guid iid);
        nint CreateForMonitor(nint monitor, in Guid iid);
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out nint buffer, out uint capacity);
    }

    private sealed record Result(bool Blank, int SampleMinimum, int SampleMaximum, BitmapSource? Frame);

    private static readonly Guid GraphicsCaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceId = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    public string Name => "WindowsGraphicsCapture";

    public BitmapSource Capture(WindowChoice window, Native.RECT rect, CancellationToken ct)
    {
        try
        {
            var result = Execute(window, rect, includeFrame: true, ct);
            if (result.Blank)
                throw new InvalidOperationException("Capture appears blank, protected, or unsupported. Nothing was sent. Try the built-in demo; there is no desktop fallback.");
            return result.Frame!;
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex) when (ex.Message is
            "Windows Graphics Capture is unavailable. No desktop or PrintWindow fallback is used."
            or "Windows Graphics Capture did not return a frame. Nothing was sent."
            or "Capture appears blank, protected, or unsupported. Nothing was sent. Try the built-in demo; there is no desktop fallback.")
        { throw; }
        catch (Exception ex) when (ex is COMException or ExternalException or PlatformNotSupportedException
            or InvalidCastException or ArgumentException)
        {
            throw new InvalidOperationException("Windows Graphics Capture is unavailable. No desktop or PrintWindow fallback is used.");
        }
    }

    internal CaptureAttemptDiagnostic Probe(WindowChoice window, Native.RECT rect, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var result = Execute(window, rect, includeFrame: false, ct);
            return new(Name, null, true, !result.Blank, result.Blank ? "blank-or-protected" : "accepted",
                result.SampleMinimum, result.SampleMaximum, clock.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return new(Name, null, false, false, "capture-error", -1, -1, clock.ElapsedMilliseconds);
        }
    }

    private static Result Execute(WindowChoice window, Native.RECT rect, bool includeFrame, CancellationToken ct) =>
        ExecuteAsync(window, rect, includeFrame, ct).GetAwaiter().GetResult();

    private static async Task<Result> ExecuteAsync(WindowChoice window, Native.RECT rect,
        bool includeFrame, CancellationToken ct)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new InvalidOperationException("Windows Graphics Capture is unavailable. No desktop or PrintWindow fallback is used.");

        GraphicsCaptureItem? item = null;
        IDirect3DDevice? device = null;
        try
        {
            item = CreateItem(window.Handle);
            if (item.Size.Width <= 0 || item.Size.Height <= 0
                || item.Size.Width > 12000 || item.Size.Height > 12000
                || (long)item.Size.Width * item.Size.Height > 32_000_000)
                throw new InvalidOperationException("Windows Graphics Capture did not return a frame. Nothing was sent.");
            device = CreateDevice();
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            using var session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            var frameSource = new TaskCompletionSource<Direct3D11CaptureFrame>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            TypedEventHandler<Direct3D11CaptureFramePool, object> arrived = (sender, _) =>
            {
                var frame = sender.TryGetNextFrame();
                if (frame is null) return;
                if (!frameSource.TrySetResult(frame)) frame.Dispose();
            };
            pool.FrameArrived += arrived;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var registration = timeout.Token.Register(() => frameSource.TrySetCanceled(timeout.Token));
            try
            {
                session.StartCapture();
                using var frame = await frameSource.Task.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                using var software = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface)
                    .AsTask(timeout.Token).ConfigureAwait(false);
                return ReadSoftwareBitmap(software, includeFrame);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException("Windows Graphics Capture did not return a frame. Nothing was sent.");
            }
            finally { pool.FrameArrived -= arrived; }
        }
        finally
        {
            (device as IDisposable)?.Dispose();
        }
    }

    private static Result ReadSoftwareBitmap(SoftwareBitmap software, bool includeFrame)
    {
        using var buffer = software.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        var access = reference.As<IMemoryBufferByteAccess>();
        access.GetBuffer(out var data, out var capacity);
        var plane = buffer.GetPlaneDescription(0);
        int width = plane.Width, height = plane.Height;
        if (width <= 0 || height <= 0 || width > 12000 || height > 12000
            || (long)width * height > 32_000_000 || plane.Stride < width * 4)
            throw new InvalidOperationException("Windows Graphics Capture did not return a frame. Nothing was sent.");
        int rowBytes = checked(width * 4);
        long finalByte = (long)plane.StartIndex + (long)(height - 1) * plane.Stride + rowBytes;
        if (plane.StartIndex < 0 || finalByte > capacity)
            throw new InvalidOperationException("Windows Graphics Capture did not return a frame. Nothing was sent.");

        byte[] pixels = new byte[checked(rowBytes * height)];
        try
        {
            for (int y = 0; y < height; y++)
                Marshal.Copy(nint.Add(data, checked(plane.StartIndex + y * plane.Stride)),
                    pixels, y * rowBytes, rowBytes);
            var quality = CaptureQuality.Measure(pixels, width, height);
            BitmapSource? frame = null;
            if (includeFrame && !quality.Blank)
            {
                frame = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32,
                    null, pixels, rowBytes);
                frame.Freeze();
            }
            return new(quality.Blank, quality.SampleMinimum, quality.SampleMaximum, frame);
        }
        finally { Array.Clear(pixels); }
    }

    private static GraphicsCaptureItem CreateItem(nint hwnd)
    {
        using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem",
            typeof(IGraphicsCaptureItemInterop).GUID);
        var pointer = factory.AsInterface<IGraphicsCaptureItemInterop>()
            .CreateForWindow(hwnd, GraphicsCaptureItemId);
        if (pointer == 0)
            throw new InvalidOperationException("Windows Graphics Capture is unavailable. No desktop or PrintWindow fallback is used.");
        try { return MarshalInterface<GraphicsCaptureItem>.FromAbi(pointer); }
        finally { MarshalInterface<GraphicsCaptureItem>.DisposeAbi(pointer); }
    }

    private static IDirect3DDevice CreateDevice()
    {
        int result = D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7,
            out var nativeDevice, out _, out var context);
        if (result < 0)
        {
            if (context != 0) Marshal.Release(context);
            if (nativeDevice != 0) Marshal.Release(nativeDevice);
            result = D3D11CreateDevice(0, 5, 0, 0x20, 0, 0, 7,
                out nativeDevice, out _, out context);
        }
        Marshal.ThrowExceptionForHR(result);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(nativeDevice, in DxgiDeviceId, out var dxgiDevice));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var graphicsDevice));
                try { return MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice); }
                finally { MarshalInterface<IDirect3DDevice>.DisposeAbi(graphicsDevice); }
            }
            finally { Marshal.Release(dxgiDevice); }
        }
        finally
        {
            if (context != 0) Marshal.Release(context);
            if (nativeDevice != 0) Marshal.Release(nativeDevice);
        }
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software,
        uint flags, nint featureLevels, uint featureLevelCount, uint sdkVersion,
        out nint device, out uint featureLevel, out nint immediateContext);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice,
        out nint graphicsDevice);
}
