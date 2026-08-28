using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace DesktopBoxesUI.Helpers;

public static class HelperUI
{

    /// <summary>
    /// Fires an action once after timeSpan
    /// </summary>
    public static void FireOnceAfterTime(TimeSpan timeSpan, Action onElapsed)
    {
        Action? detach = null;
        var timer = new DispatcherTimer { Interval = timeSpan };

        var handler = new EventHandler((s, args) =>
        {
            onElapsed();
            // Note: When stop is called this DispatcherTimer handler will be GC'd (eventually). There is no need to unregister the event.
            timer.Stop();
            if (detach != null)
                detach();
        });
        detach = new Action(() => timer.Tick -= handler); // No need for deregistering but just for safety let's do it.

        timer.Tick += handler;
        timer.Start();
    }

    static bool? _IsInDesignMode;

    public static bool IsInDesignMode
    {
        get
        {
            if (!_IsInDesignMode.HasValue) _IsInDesignMode = DesignerProperties.GetIsInDesignMode(new DependencyObject());
            return _IsInDesignMode.Value;
        }
        //set { _Instance = value; }
    }

    //ResourceAccessor
    public static Uri GetResourceURI(string resourcePath)
    {
        var uri = string.Format(
            "pack://application:,,,/{0};component/{1}"
            , Assembly.GetExecutingAssembly().GetName().Name
            , resourcePath
        );

        return new Uri(uri);
    }
    public static byte[]? GetResourceAsBytes(string resourcePath)
    {
        //Assembly asm = Assembly.GetExecutingAssembly();
        //using Stream rStream = asm.GetManifestResourceStream(asm.GetName().Name + "." + resourceName);
        //using MemoryStream = MemoryStream.Synchronized(rStream);
        //the root namespace: MyApp.Core
        //the entended namespace: Assets.Images
        //the file name logo.png
        //return rStream.ToBye;
        //"MyApp.Core.Assets.Images.logo.png"
        var ms = Application.GetResourceStream(GetResourceURI(resourcePath))?.Stream;
        //var ms = Application.GetResourceStream(new Uri(resourcePath, uriKind: UriKind.Relative))?.Stream;
        //var sNames = typeof(HelperUI).GetTypeInfo().Assembly.GetManifestResourceNames();
        //var ms = typeof(HelperUI).GetTypeInfo().Assembly.GetManifestResourceStream(resourcePath);
        return ms?.ReadAllBytes();
    }

    public static byte[] ReadAllBytes(this Stream instream)
    {
        if (instream is MemoryStream)
            return ((MemoryStream)instream).ToArray();

        using (var memoryStream = new MemoryStream())
        {
            instream.CopyTo(memoryStream);
            return memoryStream.ToArray();
        }
    }

    public static Task<byte[]> ReadAllBytesAsync(this Stream instream)
    {
        return Task.Run(() => ReadAllBytes(instream));
    }

    public static IntPtr GetCriticalHandle(this Window window)
    {
        //return (IntPtr)typeof(Window).GetProperty("CriticalHandle", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window, new object[0]);
        return new System.Windows.Interop.WindowInteropHelper(window).Handle;
    }
    const int WM_NCLBUTTONDOWN = 0xA1;
    const int HT_CAPTION = 0x2;
    public static void MoveWindowOnMouseDown(this Window window)
    {
        var hwnd = window.GetCriticalHandle();
        if (hwnd == IntPtr.Zero)
            return;

        Win32.NativeMethods.ManualApis.ReleaseCapture();
        Win32.NativeMethods.ManualApis.SendMessage(hwnd, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
    }
}
