using WindowsNative;
using DesktopLiveWallPaperEngine.Playback;
using System.Runtime.InteropServices;

namespace DesktopLiveWallPaperEngine;

public static class WallpaperFilePicker
{
    private static string BuildFilter()
    {
        // Single source of truth: exactly what the engine can render.
        // Images -> ImageRenderer, videos -> VideoRenderer (MediaPlayer).
        string images = string.Join(";", Engine.ImageExtensions.Select(e => "*" + e));
        string videos = string.Join(";", CodecSupport.VideoExtensions.Select(e => "*" + e));
        string all = images + ";" + videos;
        return
            "Images & videos\0" + all + "\0" +
            "Images\0" + images + "\0" +
            "Videos\0" + videos + "\0" +
            "All files\0*.*\0\0";
    }

    /// <summary>No-owner overload for WPF callers (e.g. tray menu) that have no HWND.
    /// Passes <see cref="IntPtr.Zero"/> so the dialog is top-level.</summary>
    public static string? PickMedia() => PickMedia(IntPtr.Zero);

    /// <summary>Shows the open-file dialog filtered to engine-supported formats.</summary>
    /// <param name="owner">Owning <c>HWND</c> (e.g. from <c>WindowInteropHelper.Handle</c>),
    /// or <see cref="IntPtr.Zero"/> for no owner. NOT a process handle.</param>
    public static string? PickMedia(IntPtr owner)
    {
        const int bufferChars = 4096;
        IntPtr buffer = Marshal.AllocHGlobal(bufferChars * 2);
        // The legacy dialog switches the calling thread to system-DPI awareness and does not
        // reliably restore it; any monitor enumeration after a pick would then come back scaled
        // (secondary 1920x1200 reported as 2400x1500 on a 125%-primary box). Save and restore.
        var awareness = User32.GetThreadDpiAwarenessContext();
        try
        {
            // zero the buffer so the dialog sees an empty initial filename
            for (int i = 0; i < bufferChars; i++) Marshal.WriteInt16(buffer, i * 2, 0);

            var ofn = new OPENFILENAME
            {
                StructSize = (uint)Marshal.SizeOf<OPENFILENAME>(),
                HwndOwner = owner,
                Filter = BuildFilter(),
                File = buffer,
                MaxFile = bufferChars,
                Title = "Choose a wallpaper (image or video)",
                Flags = ComDlg32.OFN_EXPLORER | ComDlg32.OFN_ENABLESIZING |
                        ComDlg32.OFN_FILEMUSTEXIST | ComDlg32.OFN_PATHMUSTEXIST | ComDlg32.OFN_NOCHANGEDIR,
            };
            return ComDlg32.GetOpenFileNameW(ref ofn) ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            if (awareness != IntPtr.Zero)
                User32.SetThreadDpiAwarenessContext(awareness);
            Marshal.FreeHGlobal(buffer);
        }
    }
}
