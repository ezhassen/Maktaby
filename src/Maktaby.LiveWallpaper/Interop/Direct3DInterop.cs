using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using Maktaby.Native;

namespace Maktaby.LiveWallpaper.Interop;

/// <summary>Bridges a DXGI surface (our swapchain backbuffer) to the WinRT
/// IDirect3DSurface that MediaPlayer.CopyFrameToVideoSurface expects.</summary>
public static class Direct3DInterop
{
    public static IDirect3DSurface CreateSurfaceFromDxgi(IntPtr dxgiSurfacePtr)
    {
        int hr = D3D11Native.CreateDirect3D11SurfaceFromDXGISurface(dxgiSurfacePtr, out IntPtr inspectable);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            return WinRT.MarshalInterface<IDirect3DSurface>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }
}
