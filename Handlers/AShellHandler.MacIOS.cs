using AdaptiveShell.Controls;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Handlers;
using System;
using System.Collections.Generic;
using System.Text;

using AdaptiveShell.Platforms.MacIOS;

namespace AdaptiveShell.Handlers
{
    public partial class AShellHandler : ViewHandler<AShell, UIKit.UIView>
    {
        private AShellView _platformViewController;
        protected override UIKit.UIView CreatePlatformView()
        {
            _platformViewController = new AShellView(VirtualView, MauiContext);

            ViewController = _platformViewController;
            return _platformViewController.View;
        }
        protected override void ConnectHandler(UIKit.UIView platformView)
        {
            base.ConnectHandler(platformView);
            // Perform any control setup here
        }

        protected override void DisconnectHandler(UIKit.UIView platformView)
        {
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
