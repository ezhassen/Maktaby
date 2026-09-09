using System.Runtime.InteropServices;

namespace WindowsNative;

/// <summary>d3d11 P/Invoke that needs no managed wrappers. Named to avoid clashing with
/// Vortice.Direct3D11.D3D11 in consumers that reference both.</summary>
public static class D3D11Native
{
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11SurfaceFromDXGISurface", ExactSpelling = true)]
    public static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr inspectable);
}
