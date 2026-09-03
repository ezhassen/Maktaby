using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace DesktopBoxesUI.Views.Containers;

/// <summary>
/// base windows used for common props and methods for the Containers
/// </summary>
public abstract class WidgetWindow : Window
{

    public abstract void UpdateChrome();
    /*private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 0x0003;
    private const int MA_ACTIVATE = 1;
    const int WM_NCHITTEST = 0x0084; // 132
    const int WM_MOUSEMOVE = 0x0200; // 512

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        //if (PresentationSource.FromVisual(this) is HwndSource source)
        //{
        //    source.AddHook(WndProc);
        //}
    }

    private IntPtr WndProc(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {

        *//*switch (msg)
        {
            case WM_NCHITTEST:
                {
                    var result = Win32.NativeMethods.ManualApis.DefWindowProc(hwnd, (uint)msg, wParam, lParam);

                    Debug.WriteLine($"NCHITTEST: {result}");

                    return result;
                }

            case WM_MOUSEMOVE:
                {
                    Debug.WriteLine("WM_MOUSEMOVE");
                    break;
                }

            case WM_MOUSEACTIVATE:
                {
                    Debug.WriteLine("WM_MOUSEACTIVATE");
                    break;
                }
        }*//*

        //
        //when ShowActivated is false there is an issue with mouse move events not working
        //so this is the workaround for it
        //if (!ShowActivated && msg == WM_MOUSEACTIVATE)
        //{
        //    handled = true;
        //    return (IntPtr)MA_ACTIVATE;
        //}
        //this is for never activate the window
        *//*if (!ShowActivated && msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return (IntPtr)MA_NOACTIVATE;
        }*//*

        return IntPtr.Zero;
    }*/
}
