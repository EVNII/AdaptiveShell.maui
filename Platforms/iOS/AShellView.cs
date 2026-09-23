using UIKit;
using AdaptiveShell.Controls;

namespace AdaptiveShell.Platforms.MacIOS
{
    public class AShellView : AShellViewBase
    {
        public AShellView(AShell aShell, IMauiContext mauiContext)
        :base(aShell, mauiContext)
        {
            if (!OperatingSystem.IsIOSVersionAtLeast(18))
                throw new PlatformNotSupportedException(
                    "需要 iOS 18 或更新版本。");
            Mode = UITabBarControllerMode.TabSidebar;

            // iPad/Mac idiom 下显示完整 sidebar;iPhone idiom(含 iPhone Duo 展开)
            // 下 Sidebar.IsAvailable 为 false,系统自动使用悬浮 tab bar
            Sidebar.Hidden = false;
            Sidebar.PreferredLayout = UITabBarControllerSidebarLayout.Tile;
        }
    }
}