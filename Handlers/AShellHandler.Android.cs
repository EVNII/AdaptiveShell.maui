using AdaptiveShell.Controls;
using AdaptiveShell.Platforms.Android;
using Android.Widget;
using Google.Android.Material.Navigation;
using Google.Android.Material.NavigationRail;
using Microsoft.Maui.Handlers;
using System;
using System.Collections.Generic;
using System.Text;

namespace AdaptiveShell.Handlers
{
    public partial class AShellHandler : ViewHandler<AShell, FrameLayout>
    {
        private AShellView _platformViewController;

        protected override FrameLayout CreatePlatformView()
        {
            _platformViewController = new AShellView(VirtualView, Context, MauiContext);
            return _platformViewController.PlatformView;
        }
        protected override void ConnectHandler(FrameLayout platformView)
        {
            base.ConnectHandler(platformView);
            // Perform any control setup here
        }

        protected override void DisconnectHandler(FrameLayout platformView)
        {
            _platformViewController.Dispose();
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
            handler._platformViewController?.UpdateCurrentItem();
        }

        public static void MapColors(AShellHandler handler, AShell shell)
        {
            handler._platformViewController?.UpdateColors();
        }
    }
}
