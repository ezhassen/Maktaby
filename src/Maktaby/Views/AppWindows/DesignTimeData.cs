using Maktaby.Core.Models;
using Maktaby.Settings;
using Maktaby.ViewModels;
using System;
using System.Diagnostics;

namespace Maktaby.Views.AppWindows;

/// <summary>
/// Design-time factories for <c>d:DataContext</c> bindings (<c>{Binding Source={x:Static …}}</c>
/// instead of <c>{d:DesignInstance}</c>): full control over construction — argumentful VMs,
/// exception-safe singletons — with zero runtime cost (<c>d:</c> is <c>mc:Ignorable</c>).
/// For windows whose DataContext is themselves, bind <c>RelativeSource Self</c> instead.
/// </summary>
public static class DesignTimeData
{
    private static PerformanceMonitorViewModel? _performance;

    /// <summary>Sample performance monitor (its ctor fills design rows itself).</summary>
    public static PerformanceMonitorViewModel? SamplePerformance
    {
        get
        {
            try { return _performance ??= new PerformanceMonitorViewModel(); }
            catch (Exception ex)
            {
                try { Debug.WriteLine($"[design] SamplePerformance failed: {ex}"); } catch { }
                return null;
            }
        }
    }

    private static SettingsViewModel? _settings;

    /// <summary>Real settings VM over faked services with sample containers.</summary>
    public static SettingsViewModel? SampleSettings
    {
        get
        {
            try
            {
                if (_settings != null) return _settings;
                var settings = new DesignSettingsService();
                var mainVm = new MainViewModel(null!, null!, null!, null!);
                mainVm.Containers.Add(new ContainerViewModel(
                    new DesktopItemContainer { Type = DesktopItemContainerType.WebWidget, WebWidgetName = "Analog Clock", IsVisible = true }, null!, null!));
                mainVm.Containers.Add(new ContainerViewModel(
                    new DesktopItemContainer { Type = DesktopItemContainerType.NativeWidget, NativeWidgetName = "Calendar", IsVisible = true }, null!, null!));
                return _settings = new SettingsViewModel(settings, mainVm, null!);
            }
            catch (Exception ex)
            {
                try { Debug.WriteLine($"[design] SampleSettings failed: {ex}"); } catch { }
                return null;
            }
        }
    }

    private sealed class DesignSettingsService : Core.Interfaces.ISettingsService
    {
        public UserSettings UserSettings { get; set; } = new();
        public AppJSettings AppJSettings => AppJSettings.Instance;
        public void Load() { }
        public void Save() { }
    }

    private static ViewModels.WidgetDataViewModel? _widgetData;

    /// <summary>Sample widget editor (manifest fields + editor documents).</summary>
    public static ViewModels.WidgetDataViewModel? SampleWidgetData
    {
        get
        {
            try
            {
                if (_widgetData != null) return _widgetData;
                var vm = new ViewModels.WidgetDataViewModel();
                vm.LoadSampleData();
                return _widgetData = vm;
            }
            catch (Exception ex)
            {
                try { Debug.WriteLine($"[design] SampleWidgetData failed: {ex}"); } catch { }
                return null;
            }
        }
    }

    private static UpdatePromptViewModel? _updatePrompt;

    /// <summary>Sample update prompt. <see cref="UpdatePromptViewModel"/> takes the release and
    /// the installed version, and derives every label from them, so a fabricated
    /// <see cref="UpdateInfo"/> renders the whole window.</summary>
    public static UpdatePromptViewModel? SampleUpdatePrompt
    {
        get
        {
            try
            {
                if (_updatePrompt != null) return _updatePrompt;
                var vm = new UpdatePromptViewModel(
                    new UpdateInfo
                    {
                        Version = "1.0.33-beta.1",
                        TagName = "v1.0.33-beta.1",
                        DownloadUrl = "https://localhost.invalid/Maktaby-1.0.33-beta.1-x64-setup.exe",
                        Sha256 = new string('0', 64),
                        SizeBytes = 15 * 1024 * 1024,
                        IsPreRelease = true,
                        PublishedAt = new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero),
                        ReleasePageUrl = "https://localhost.invalid/releases/v1.0.33-beta.1",
                        ReleaseNotes = "* Fixed the update prompt layout\n* Faster startup check",
                    },
                    currentVersion: "1.0.32-beta.2");
                return _updatePrompt = vm;
            }
            catch (Exception ex)
            {
                try { Debug.WriteLine($"[design] SampleUpdatePrompt failed: {ex}"); } catch { }
                return null;
            }
        }
    }

    private static System.Collections.Generic.IReadOnlyList<Views.WidgetGalleryItem>? _gallery;

    /// <summary>Sample gallery rows. Bound via <c>d:ItemsSource</c> so the list renders even
    /// if the window ctor never runs in the host; the window ctor reuses the same source.</summary>
    public static System.Collections.Generic.IReadOnlyList<Views.WidgetGalleryItem> SampleGallery
    {
        get
        {
            try
            {
                return _gallery ??= new System.Collections.Generic.List<Views.WidgetGalleryItem>
                {
                    new() { Kind = Views.WidgetGalleryKind.Web, Slug = "analog-clock", Source = Core.Models.WebWidgetSource.App, SourceLabel = "App", IsBuiltIn = true, DisplayName = "Analog Clock", DisplayAuthor = "Maktaby", Manifest = new Core.Models.WebWidgetManifest { Name = "Analog Clock", Author = "Maktaby", Description = "Smooth sweeping clock face." }, CanPlace = true },
                    new() { Kind = Views.WidgetGalleryKind.Web, Slug = "black-hole", Source = Core.Models.WebWidgetSource.User, SourceLabel = "User", IsBuiltIn = false, DisplayName = "black hole 01", DisplayAuthor = "ezz", Manifest = new Core.Models.WebWidgetManifest { Name = "black hole 01", Author = "ezz" }, CanPlace = true },
                    new() { Kind = Views.WidgetGalleryKind.Native, Slug = "calendar", NativeSource = Maktaby.WidgetSdk.NativeWidgetSource.App, Source = Core.Models.WebWidgetSource.User, SourceLabel = "App", IsBuiltIn = true, DisplayName = "Calendar", DisplayAuthor = "Maktaby", Manifest = new Core.Models.WebWidgetManifest { Name = "Calendar" }, CanPlace = true },
                };
            }
            catch (Exception ex)
            {
                try { Debug.WriteLine($"[design] SampleGallery failed: {ex}"); } catch { }
                return System.Array.Empty<Views.WidgetGalleryItem>();
            }
        }
    }
}
