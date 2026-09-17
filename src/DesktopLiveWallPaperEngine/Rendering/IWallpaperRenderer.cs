namespace DesktopLiveWallPaperEngine.Rendering;

public interface IWallpaperRenderer : IDisposable
{
    /// <summary>True when presentation flows through the composition host's GPU objects
    /// (swapchain/backbuffer). Switching to a renderer reporting false lets the engine
    /// release the host's GPU state — and the shared device with it when no other host
    /// needs it. Windows are reused across wallpaper changes, so without this the device
    /// would stay pinned after the last video/image is gone.</summary>
    bool IsGPURender { get; }
    void Load(string path);
    bool IsLoaded();
    void Pause();
    bool IsPaused();
    void Resume();
    bool IsPlaying();
    /// <summary>GDI paint path (WM_PAINT). D3D-backed renderers ignore this.</summary>
    void Paint(IntPtr hdc);
}
