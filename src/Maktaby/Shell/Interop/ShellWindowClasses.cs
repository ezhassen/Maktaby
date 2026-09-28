namespace Maktaby.Shell.Interop;

/// <summary>
/// Well-known window class names used when locating the Windows Explorer desktop hierarchy
/// (Progman -&gt; WorkerW -&gt; SHELLDLL_DefView -&gt; SysListView32). Centralized here so the
/// string literals are not scattered through Views/ViewModels. True Shell COM interop (and the
/// matching native declarations) will live in this folder later.
/// </summary>
internal static class ShellWindowClasses
{
    public const string Progman = "Progman";
    public const string WorkerW = "WorkerW";
    public const string ShellDefView = "SHELLDLL_DefView";
    public const string SysListView32 = "SysListView32";
}
