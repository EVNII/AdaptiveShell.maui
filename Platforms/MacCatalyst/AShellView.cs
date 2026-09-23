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
    }
}