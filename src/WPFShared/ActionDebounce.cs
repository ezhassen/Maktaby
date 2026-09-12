using System.Windows;

namespace WPFShared;

public class ActionDebounce : IDisposable
{
    private System.Windows.Threading.DispatcherTimer? _tDebounce;

    public ActionDebounce(Action debounceAction, int scheduleMilliseconds)
    {
        DebounceAction = debounceAction;
        ScheduleMilliseconds = scheduleMilliseconds;
    }

    public int ScheduleMilliseconds { get; set; } = 800;
    public Action DebounceAction { get; set; }

    public void ScheduleDebounce()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.InvokeAsync(ScheduleDebounce);
            return;
        }

        _tDebounce ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(800)
        };
        _tDebounce.Tick -= TDebounceTick;
        _tDebounce.Tick += TDebounceTick;
        _tDebounce.Stop();
        _tDebounce.Start();
    }

    private void TDebounceTick(object? sender, EventArgs e)
    {
        if (_tDebounce != null)
        {
            _tDebounce.Stop();
            _tDebounce.Tick -= TDebounceTick;
        }
        DebounceAction?.Invoke();
    }

    #region Dispose pattern

    public bool IsDisposed { get; private set; }

    protected virtual void Dispose(bool disposing)
    {
        if (!IsDisposed)
        {
            if (disposing)
            {
                // dispose managed state (managed objects)
            }
            _tDebounce?.Stop();
            _tDebounce = null;

            // free unmanaged resources (unmanaged objects) and override finalizer
            // set large fields to null

            IsDisposed = true;
        }
    }

    // override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
    // ~ActionDebounce()
    // {
    //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
    //     Dispose(disposing: false);
    // }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    #endregion Dispose pattern


}
