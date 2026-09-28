using Maktaby.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Controls;
using Maktaby.Shared.Interfaces;

namespace Maktaby.Helpers;

/// <summary>One-time hint shown when a container is hidden from its own context menu, telling
/// the user where to bring it back. Suppressed when hiding from Settings (that IS the
/// bring-back UI) and permanently once the user ticks "Do not show this again". Only the
/// two container context-menu handlers call this — never <see cref="DesktopManager"/>
/// directly.</summary>
public static class ContainerHideHint
{
    public static async Task MaybeShowAsync(Window owner)
    {
        ISettingsService settings;
        try { settings = App.Services.GetRequiredService<ISettingsService>(); }
        catch { return; }
        try
        {
            if (settings.UserSettings.HideContainerHintDismissed) return;
        }
        catch { return; }

        var dontShow = new CheckBox
        {
            Content = "Do not show this again",
            Margin = new Thickness(0, 12, 0, 0),
        };
        var panel = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = "This container is now hidden.\nTo show it again, open Settings → Containers and tick its Visible checkbox.",
                    TextWrapping = TextWrapping.Wrap,
                },
                dontShow,
            }
        };
        try
        {
            var dialogs = App.Services.GetRequiredService<IDialogService>();
            await dialogs.ShowMessageAsync("", new DialogOptions { Title = "Container hidden", Content = panel }, owner);
        }
        catch { return; }

        try
        {
            if (dontShow.IsChecked == true)
            {
                settings.UserSettings.HideContainerHintDismissed = true;
                settings.Save();
            }
        }
        catch { }
    }
}
