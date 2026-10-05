using AdaptiveShell.Controls;
using AdaptiveShell.Platforms.Android;
using Android.Widget;
using Microsoft.Maui.Handlers;
using System;

namespace AdaptiveShell.Handlers
{
    public partial class AShellHandler : ViewHandler<AShell, FrameLayout>
    {
        private AShellView? _platformViewController;

        protected override FrameLayout CreatePlatformView()
        {
            _platformViewController = new AShellView(VirtualView, Context,
                MauiContext ?? throw new InvalidOperationException("A MAUI context is required."));
            return _platformViewController.PlatformView;
        }
        protected override void DisconnectHandler(FrameLayout platformView)
        {
            _platformViewController?.Dispose();
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
            if (shell.IsUpdatingItems)
                return;
            handler._platformViewController?.UpdateCurrentItem();
        }

        public static void MapColors(AShellHandler handler, AShell shell)
        {
            handler._platformViewController?.UpdateColors();
        }
    }
}
