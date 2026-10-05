using Microsoft.Maui.Handlers;

using AdaptiveShell.Controls;
using AdaptiveShell.Platforms.Windows;
using Microsoft.UI.Xaml.Controls;

namespace AdaptiveShell.Handlers
{
    public partial class AShellHandler : ViewHandler<AShell, NavigationView>
    {

        private AShellView? _platformViewController;
        protected override NavigationView CreatePlatformView()
        {
            _platformViewController = new AShellView(VirtualView,
                MauiContext ?? throw new InvalidOperationException("MAUI context is required for AShell."));
            return _platformViewController.PlatformView;
        }

        protected override void DisconnectHandler(NavigationView platformView)
        {
            _platformViewController?.Dispose();
            _platformViewController = null;
            base.DisconnectHandler(platformView);
        }

        public static void MapItems(AShellHandler handler, AShell shell)
        {
            handler._platformViewController?.UpdateItems();
            handler._platformViewController?.UpdateCurrentItem();
        }

        public static void MapCurrentItem(AShellHandler handler, AShell shell)
        {
            if (shell.IsUpdatingItems) return;
            handler._platformViewController?.UpdateCurrentItem();
        }

        public static void MapColors(AShellHandler handler, AShell shell)
        {
            handler._platformViewController?.UpdateColors();
        }
    }
}
