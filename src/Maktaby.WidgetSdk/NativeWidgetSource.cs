namespace Maktaby.WidgetSdk;

/// <summary>Where a native widget folder lives. App widgets ship with the install
/// (read-only); user widgets are dropping folders into %AppData%.</summary>
public enum NativeWidgetSource
{
    App = 0,
    User = 1,
}
