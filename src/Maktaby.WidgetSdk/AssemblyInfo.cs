using System.Runtime.CompilerServices;

// The host (Maktaby) drives the plugin lifecycle and must call the SDK's internal
// localization seam (NativeWidgetControl.SetLocalization / RegisterLocTarget). Plugin
// assemblies never see these internals — only the host does.
[assembly: InternalsVisibleTo("Maktaby")]
