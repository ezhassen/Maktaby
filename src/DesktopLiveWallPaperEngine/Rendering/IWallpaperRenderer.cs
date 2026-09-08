namespace DesktopLiveWallPaperEngine.Rendering;

public interface IWallpaperRenderer : IDisposable
{
    void Load(string path);
    bool IsLoaded();
    void Pause();
    bool IsPaused();
    void Resume();
    bool IsPlaying();
    /// <summary>GDI paint path (WM_PAINT). D3D-backed renderers ignore this.</summary>
    void Paint(IntPtr hdc);
}
