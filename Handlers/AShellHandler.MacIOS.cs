using AdaptiveShell.Controls;
using Microsoft.Maui.Handlers;

using AdaptiveShell.Platforms.MacIOS;

namespace AdaptiveShell.Handlers
{
    public partial class AShellHandler : ViewHandler<AShell, UIKit.UIView>
    {
        private AShellView? _platformViewController;
        protected override UIKit.UIView CreatePlatformView()
        {
            _platformViewController = new AShellView(VirtualView,
                MauiContext ?? throw new InvalidOperationException("MAUI context is required for AShell."));

            ViewController = _platformViewController;
            return _platformViewController.View
                ?? throw new InvalidOperationException("AShell native view is unavailable.");
        }
        protected override void DisconnectHandler(UIKit.UIView platformView)
        {
            _platformViewController?.Disconnect();
            _platformViewController = null;
            base.DisconnectHandler(platformView);
        }

        public static void MapItems(AShellHandler handler, AShell shell)
        {
            handler._platformViewController?.UpdateItems();
            handler._platformViewController?.UpdateCurrentItem(preserveLanding: true);
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
