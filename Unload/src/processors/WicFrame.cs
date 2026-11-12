using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace unload;

// A decoded image frame read through the Windows Imaging Component (WIC), called directly through COM.
//
// Frame hashing runs on many threads at once (see ImageProcessor.CropAndPhashFolder), and both higher level
// image APIs fail under Wine when used that way:
//
// - System.Drawing (GDI+): every Bitmap and Graphics makes Wine create a GDI memory DC, and Wine's X11 driver
//   creates an X11 GC for each one (X11DRV_CreateCompatibleDC). libX11 has a race when several threads
//   allocate X resource IDs on one Display: _XIDHandler refills the cached ID through _XAllocIDs, which
//   releases the display lock and re-locks without checking again. The app then aborts with
//   `_XAllocID: Assertion 'ret != inval_id' failed`, intermittently, after tens of thousands of frames.
//   https://gitlab.freedesktop.org/xorg/lib/libx11/-/work_items/251
//
// - WPF imaging (BitmapFrame.Create): WPF frees the native WIC decoder behind each frame from a SafeHandle,
//   so every release runs on the single .NET finalizer thread. Under Wine, with about 16 or more threads
//   decoding (Parallel.For on 16 cores is enough), the finalizer falls behind and native memory grows by about
//   one decoded frame per decode, which ran a 32 GB machine out of memory within a minute. On Windows the
//   finalizer runs at THREAD_PRIORITY_HIGHEST and keeps up. Wine maps that priority to a small niceness
//   change, and the decoding threads and the finalizer also appear to contend on something serialized, since
//   16 threads use no more CPU than 8. Seen with Wine 11.20 and .NET 10, does not happen on Windows 10.
//   https://bugs.winehq.org/show_bug.cgi?id=60754
//
// Calling WIC directly avoids both: no GDI objects, and every COM object is released as soon as it is no
// longer needed, on the thread that created it, so nothing waits on the finalizer. Hashes are identical to the
// previous System.Drawing pipeline.
//
// The vtable slots below follow the interface declarations in wincodec.h, counting IUnknown's
// QueryInterface (0), AddRef (1) and Release (2) first:
// https://learn.microsoft.com/en-us/windows/win32/api/wincodec/nn-wincodec-iwicimagingfactory
// https://learn.microsoft.com/en-us/windows/win32/api/wincodec/nn-wincodec-iwicbitmapdecoder
// https://learn.microsoft.com/en-us/windows/win32/api/wincodec/nn-wincodec-iwicbitmapsource
// https://learn.microsoft.com/en-us/windows/win32/api/wincodec/nn-wincodec-iwicformatconverter
public sealed unsafe class WicFrame : IDisposable
{
    private static readonly Guid CLSID_WICImagingFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid IID_IWICImagingFactory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    private static readonly Guid GUID_WICPixelFormat24bppBGR = new("6fddc324-4e03-4bfe-b185-3d77768dc90c");

    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private const uint GENERIC_READ = 0x80000000;
    private const int WICDecodeMetadataCacheOnDemand = 0;
    private const int WICBitmapDitherTypeNone = 0;
    private const int WICBitmapPaletteTypeCustom = 0;

    private const int Slot_Release = 2;
    private const int Slot_Factory_CreateDecoderFromFilename = 3;
    private const int Slot_Factory_CreateFormatConverter = 10;
    private const int Slot_Decoder_GetFrame = 13;
    private const int Slot_Source_GetSize = 3;
    private const int Slot_Source_GetPixelFormat = 4;
    private const int Slot_Source_CopyPixels = 7;
    private const int Slot_Converter_Initialize = 8;

    // One factory per thread, so no COM object is ever used from a thread other than the one that created it.
    // Each factory lives as long as its thread.
    [ThreadStatic]
    private static IntPtr threadFactory;

    private IntPtr decoder;
    private IntPtr frame;
    private IntPtr converter;
    // The BGR24 view of the frame, either the frame itself or the format converter
    private readonly IntPtr bgr24Source;

    public int Width { get; }
    public int Height { get; }

    // Decodes the first frame of an image file
    public WicFrame(string path)
    {
        try
        {
            IntPtr factory = GetThreadFactory();
            fixed (char* fileName = path)
            {
                IntPtr newDecoder;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, char*, Guid*, uint, int, IntPtr*, int>)Slot(factory, Slot_Factory_CreateDecoderFromFilename))(
                    factory, fileName, null, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &newDecoder));
                decoder = newDecoder;
            }

            IntPtr newFrame;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(decoder, Slot_Decoder_GetFrame))(decoder, 0, &newFrame));
            frame = newFrame;

            uint width, height;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, uint*, int>)Slot(frame, Slot_Source_GetSize))(frame, &width, &height));
            Width = (int)width;
            Height = (int)height;

            Guid format;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, int>)Slot(frame, Slot_Source_GetPixelFormat))(frame, &format));
            if (format == GUID_WICPixelFormat24bppBGR)
            {
                bgr24Source = frame;
            }
            else
            {
                IntPtr newConverter;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(factory, Slot_Factory_CreateFormatConverter))(factory, &newConverter));
                converter = newConverter;

                Guid target = GUID_WICPixelFormat24bppBGR;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, int, IntPtr, double, int, int>)Slot(converter, Slot_Converter_Initialize))(
                    converter, frame, &target, WICBitmapDitherTypeNone, IntPtr.Zero, 0.0, WICBitmapPaletteTypeCustom));
                bgr24Source = converter;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // Copies an area of the frame as tightly packed BGR24 pixels (3 bytes per pixel, rows without padding)
    public byte[] CopyPixelsBgr24(Rectangle area)
    {
        ObjectDisposedException.ThrowIf(decoder == IntPtr.Zero, this);

        int stride = area.Width * 3;
        byte[] pixels = new byte[stride * area.Height];
        // WICRect has the same layout as 4 consecutive ints: X, Y, Width, Height
        int* rect = stackalloc int[4] { area.X, area.Y, area.Width, area.Height };
        fixed (byte* buffer = pixels)
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, uint, uint, byte*, int>)Slot(bgr24Source, Slot_Source_CopyPixels))(
                bgr24Source, rect, (uint)stride, (uint)pixels.Length, buffer));
        }

        return pixels;
    }

    public void Dispose()
    {
        // Release in reverse order of creation
        Release(ref converter);
        Release(ref frame);
        Release(ref decoder);
    }

    private static IntPtr GetThreadFactory()
    {
        if (threadFactory == IntPtr.Zero)
        {
            Check(CoCreateInstance(CLSID_WICImagingFactory, IntPtr.Zero, CLSCTX_INPROC_SERVER, IID_IWICImagingFactory, out IntPtr factory));
            threadFactory = factory;
        }

        return threadFactory;
    }

    private static void* Slot(IntPtr comObject, int slot) => (*(void***)comObject)[slot];

    private static void Release(ref IntPtr comObject)
    {
        if (comObject == IntPtr.Zero) return;

        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(comObject, Slot_Release))(comObject);
        comObject = IntPtr.Zero;
    }

    private static void Check(int hresult)
    {
        if (hresult < 0) Marshal.ThrowExceptionForHR(hresult);
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);
}
