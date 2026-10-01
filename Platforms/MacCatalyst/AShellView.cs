using UIKit;
using AdaptiveShell.Controls;

namespace AdaptiveShell.Platforms.MacIOS
{
    public class AShellView : AShellViewBase
    {
        public AShellView(AShell aShell, IMauiContext mauiContext)
        :base(aShell, mauiContext)
        {
            if (!OperatingSystem.IsMacCatalystVersionAtLeast(18))
            throw new PlatformNotSupportedException(
                "需要 Mac Catalyst 18 或更新版本。");

            Mode = UITabBarControllerMode.TabSidebar;

            Sidebar.Hidden = false;
            Sidebar.PreferredLayout = UITabBarControllerSidebarLayout.Tile;
        }

        public override void ViewWillAppear(bool animated)
        {
            base.ViewWillAppear(animated);
            UpdateTitlebar();
        }

        public override void ViewDidAppear(bool animated)
        {
            base.ViewDidAppear(animated);
            // 初次呈现时,WindowScene 可能到此时才可用。
            UpdateTitlebar();
        }

        private void UpdateTitlebar()
        {
            // 移除标题栏,让系统侧栏延伸至窗口顶部。
            // 窗口控制按钮的位置和安全区仍由 UIKit 管理。
            var titlebar = View?.Window?.WindowScene?.Titlebar;
            if (titlebar is not null)
            {
                titlebar.Toolbar = null;
                titlebar.TitleVisibility = UITitlebarTitleVisibility.Hidden;
            }
        }
    }
}
