using Microsoft.Maui.Controls.Platform;
using Microsoft.Maui.Handlers;
using System;
using System.Collections.Generic;
using System.Text;
using Windows.Graphics.Display;

using AdaptiveShell.Controls;
using AdaptiveShell.Platforms.Windows;
using Microsoft.UI.Xaml.Controls;

namespace AdaptiveShell.Handlers
{
    public partial class AShellHandler : ViewHandler<AShell, NavigationView>
    {

        private AShellView _platformViewController;
        protected override NavigationView CreatePlatformView()
        {
            _platformViewController = new AShellView(VirtualView, MauiContext);
            return _platformViewController.PlatformView;
        }

        protected override void ConnectHandler(NavigationView platformView)
        {
            base.ConnectHandler(platformView);
            // Perform any control setup here
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
            handler._platformViewController?.UpdateCurrentItem();
        }

        public static void MapColors(AShellHandler handler, AShell shell)
        {
            handler._platformViewController?.UpdateColors();
        }
    }
}
