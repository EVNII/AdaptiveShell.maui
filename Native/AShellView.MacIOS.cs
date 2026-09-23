using UIKit;
using AdaptiveShell.Controls;
using Microsoft.Maui.Platform;

namespace AdaptiveShell.Platforms.MacIOS
{
    public class AShellViewBase : UITabBarController
    {
        AShell _virtualView;

        IMauiContext _mauiContext;

        Dictionary<AShellContent, UITab> _contentToUITabMap;

        Dictionary<AShellGroup, UITabGroup> _groupToUITabGroupMap;

        Dictionary<AShellContent, PageContainerViewController> _contentToContainerMap;

        Dictionary<AShellGroup, PageContainerViewController> _groupToLandingMap;

        Dictionary<AShellGroup, UINavigationController> _groupToNavMap;

        List<PageContainerViewController> _landingContainers;

        public AShellViewBase(AShell aShell, IMauiContext mauiContext)
        {
            _virtualView = aShell;
            _mauiContext = mauiContext;
            _contentToUITabMap = new Dictionary<AShellContent, UITab>();
            _groupToUITabGroupMap = new Dictionary<AShellGroup, UITabGroup>();
            _contentToContainerMap = new Dictionary<AShellContent, PageContainerViewController>();
            _groupToLandingMap = new Dictionary<AShellGroup, PageContainerViewController>();
            _groupToNavMap = new Dictionary<AShellGroup, UINavigationController>();
            _landingContainers = new List<PageContainerViewController>();
        }

        private UIViewController CreatePage(AShellContent item, bool wrapInNavigation = true)
        {
            // 幂等:容器只创建一次,组导航 push 与 tab provider 共享同一实例
            if (_contentToContainerMap.TryGetValue(item, out var existing))
            {
                return wrapInNavigation
                    ? new UINavigationController(existing)
                    : existing;
            }

            var page = ((IAShellContentController)item).page;
            var controller = page.ToUIViewController(_mauiContext);

            controller.Title = item.Title;

            var container = new PageContainerViewController(controller);
            _contentToContainerMap[item] = container;

            return wrapInNavigation
                ? new UINavigationController(container)
                : container;
        }

        public void UpdateItems()
        {
            Delegate = _tabBarDelegate ??= new TabBarDelegate(this);

            var tabs = new List<UITab>();

            foreach (var item in _virtualView.Items)
            {
                if (item is AShellContent content)
                {
                    tabs.Add(GetOrCreateTab(content));
                }
                else if (item is AShellGroup group)
                {
                    tabs.Add(GetOrCreateGroup(group));
                }
            }

            SetTabs(tabs.ToArray(), false);
        }

        private UITab GetOrCreateTab(AShellContent content, bool wrapInNavigation = true)
        {
            if (!_contentToUITabMap.TryGetValue(content, out var tab))
            {
                tab = new UITab(
                    content.Title ?? "Untitled",
                    UIImage.GetSystemImage("doc.text"),
                    content.Id.ToString(),
                    _ => CreatePage(content, wrapInNavigation));

                _contentToUITabMap[content] = tab;
                content.FullBleedChanged += OnContentFullBleedChanged;
                LoadIconAsync(content, tab);
            }

            return tab;
        }

        private UITabGroup GetOrCreateGroup(AShellGroup group)
        {
            if (!_groupToUITabGroupMap.TryGetValue(group, out var tabGroup))
            {
                var children = new List<UITab>();
                foreach (var child in group.Items)
                {
                    // 组内子页交给组自己的导航栈展示,不再各自包导航控制器,避免 nav 嵌套
                    children.Add(GetOrCreateTab(child, wrapInNavigation: false));
                }

                var childTabs = children.ToArray();

                tabGroup = new UITabGroup(
                    group.Title ?? "Group",
                    null,
                    group.Id.ToString(),
                    childTabs,
                    _ => GetOrCreateGroupNavigation(group));

                // 组节点在任何 sidebar 里都不可点击(仅作可折叠分区标题);
                // 落地页经组 tab(tab 模式)进入,与该开关无关
                tabGroup.IsSidebarDestination = false;

                _groupToUITabGroupMap[group] = tabGroup;
                LoadIconAsync(group, tabGroup);
            }

            return tabGroup;
        }

        // 组的视图控制器:每组一个导航控制器(缓存复用),始终以落地页为栈根。
        // 落地页始终在返回堆栈里,只是某些呈现形态下以缩减方式展示
        // (sidebar 呈现时隐藏返回入口,见 ShowGroupChild)。
        // 注意:不使用 UITabGroup.ManagingNavigationController——该 API 在 Apple
        // 官方 release notes 中列为已知缺陷,且 nav 套 nav 会导致递归崩溃
        private UINavigationController GetOrCreateGroupNavigation(AShellGroup group)
        {
            if (_groupToNavMap.TryGetValue(group, out var cached))
            {
                return cached;
            }

            var landingPage = CreateLandingPage(group);
            var controller = landingPage.ToUIViewController(_mauiContext);
            controller.Title = group.Title;

            var container = new PageContainerViewController(controller);
            _groupToLandingMap[group] = container;
            _landingContainers.Add(container);

            var navigation = new UINavigationController(container);
            _groupToNavMap[group] = navigation;

            return navigation;
        }

        // 展示组内子页:栈恒为 [落地页, 子页]。
        // 只在选择变化时调用;尺寸变化等布局事件请用
        // UpdateGroupChildPresentation(只调呈现,不重建栈,否则会撤销用户的返回)
        private void ShowGroupChild(AShellGroup group, AShellContent child, bool animated = true)
        {
            if (!_groupToNavMap.TryGetValue(group, out var navigation)
                || !_groupToLandingMap.TryGetValue(group, out var landing))
            {
                return;
            }

            // 容器惰性创建:子 tab 的 provider 可能还没被 UIKit 调用过
            var container = (PageContainerViewController)CreatePage(child, wrapInNavigation: false);

            var desired = new UIViewController[] { landing, container };
            if (!navigation.ViewControllers.SequenceEqual(desired))
            {
                navigation.SetViewControllers(desired, animated);
            }

            UpdateGroupChildPresentation(group, child, animated);
        }

        // 仅更新呈现(返回按钮显隐),不碰导航栈:
        // sidebar 呈现(规则宽度)隐藏返回按钮(缩减呈现),
        // tab bar 呈现(紧凑宽度)显示返回按钮(可返回落地页)
        private void UpdateGroupChildPresentation(AShellGroup group, AShellContent child, bool animated = true)
        {
            if (!_groupToNavMap.TryGetValue(group, out var navigation)
                || !_contentToContainerMap.TryGetValue(child, out var container)
                || !ReferenceEquals(navigation.TopViewController, container))
            {
                return;
            }

            bool sidebarPresentation =
                TraitCollection.HorizontalSizeClass == UIUserInterfaceSizeClass.Regular
                && Sidebar is not null && !Sidebar.Hidden;

            container.NavigationItem?.SetHidesBackButton(sidebarPresentation, animated);
        }

        private AShellGroup? FindGroupOf(AShellContent child)
        {
            foreach (var item in _virtualView.Items)
            {
                if (item is AShellGroup group && group.Items.Contains(child))
                {
                    return group;
                }
            }

            return null;
        }

        private Page CreateLandingPage(AShellGroup group)
        {
            Page page;

            if (group.LandingTemplate is not null)
            {
                page = (Page)group.LandingTemplate.CreateContent();
            }
            else
            {
                var layout = new VerticalStackLayout
                {
                    Padding = new Thickness(20, 12),
                    Spacing = 4,
                };

                foreach (var child in group.Items)
                {
                    layout.Add(CreateLandingRow(group, child));
                }

                page = new ContentPage
                {
                    Title = group.Title,
                    Content = new ScrollView { Content = layout },
                };
            }

            // 挂到 AShell 逻辑树,保证资源/样式解析链连通
            _virtualView.AddLogicalChild(page);

            return page;
        }

        private View CreateLandingRow(AShellGroup group, AShellContent child)
        {
            var row = new HorizontalStackLayout
            {
                Spacing = 14,
                Padding = new Thickness(4, 12),
            };

            if (child.Icon is not null)
            {
                row.Add(new Image
                {
                    Source = child.Icon,
                    WidthRequest = 24,
                    HeightRequest = 24,
                    VerticalOptions = LayoutOptions.Center,
                });
            }

            row.Add(new Label
            {
                Text = child.Title,
                FontSize = 17,
                VerticalOptions = LayoutOptions.Center,
            });

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                // 直接推入组导航栈:不能只依赖 CurrentItem 变化,
                // 否则再次点击当前已选中的子页时不会有任何反应
                ShowGroupChild(group, child);
                _virtualView.CurrentItem = child;
            };
            row.GestureRecognizers.Add(tap);

            return row;
        }

        private async void LoadIconAsync(AShellItem item, UITab tab)
        {
            if (item.Icon is null)
            {
                return;
            }

            var result = await item.Icon.GetPlatformImageAsync(_mauiContext);
            if (result?.Value is not UIImage image)
            {
                return;
            }

            bool stillCurrent = item switch
            {
                AShellContent content =>
                    _contentToUITabMap.TryGetValue(content, out var t) && ReferenceEquals(t, tab),
                AShellGroup group =>
                    _groupToUITabGroupMap.TryGetValue(group, out var t) && ReferenceEquals(t, tab),
                _ => false,
            };

            if (stillCurrent)
            {
                tab.Image = image.ImageWithRenderingMode(UIImageRenderingMode.AlwaysTemplate);
            }
        }

        public void UpdateColors()
        {
            var selected = _virtualView.GetEffectiveSelectedItemColor();
            if (selected is not null)
            {
                var tintColor = selected.ToPlatform();

                // 底部 tab bar(iOS):图标和文字一起着色
                TabBar.TintColor = tintColor;

                // sidebar(Mac/iPad):选中行由系统渲染,tint 决定其色调;
                // 选中行背景样式 Apple 不提供定制 API
                View.TintColor = tintColor;
            }

            var unselected = _virtualView.GetEffectiveUnselectedItemColor();
            if (unselected is not null)
            {
                TabBar.UnselectedItemTintColor = unselected.ToPlatform();
            }
        }

        public void UpdateCurrentItem()
        {
            var currentItem = _virtualView.CurrentItem;
            if (currentItem is AShellContent content)
            {
                if (_contentToUITabMap.TryGetValue(content, out var tab))
                {
                    SelectedTab = tab;
                }

                var group = FindGroupOf(content);
                if (group is not null)
                {
                    ShowGroupChild(group, content);
                }
            }
        }

        // 用户在 tab bar/sidebar 上的选择同步回 CurrentItem(Apple 侧之前没有回传);
        // tab 模式下点到组 tab 时回到落地页(sidebar 里组节点不可点击,不会走到这)
        private void HandleTabSelected(UITab selectedTab)
        {
            foreach (var pair in _contentToUITabMap)
            {
                if (ReferenceEquals(pair.Value, selectedTab))
                {
                    if (!ReferenceEquals(_virtualView.CurrentItem, pair.Key))
                    {
                        _virtualView.CurrentItem = pair.Key;
                    }

                    return;
                }
            }

            foreach (var pair in _groupToUITabGroupMap)
            {
                if (ReferenceEquals(pair.Value, selectedTab)
                    && _groupToNavMap.TryGetValue(pair.Key, out var navigation))
                {
                    navigation.PopToRootViewController(true);
                    return;
                }
            }
        }

        TabBarDelegate? _tabBarDelegate;

        private sealed class TabBarDelegate : UITabBarControllerDelegate
        {
            readonly AShellViewBase _owner;

            public TabBarDelegate(AShellViewBase owner)
            {
                _owner = owner;
            }

            public override void DidSelectTab(
                UITabBarController tabBarController,
                UITab selectedTab,
                UITab? previousTab)
            {
                _owner.HandleTabSelected(selectedTab);
            }
        }

        public override void ViewDidLayoutSubviews()
        {
            base.ViewDidLayoutSubviews();
            UpdateContentContainers();

            // 尺寸变化可能改变呈现形态(sidebar <-> tab bar),
            // 只重新应用当前组内子页的返回按钮显隐,不重建导航栈
            if (_virtualView.CurrentItem is AShellContent content
                && FindGroupOf(content) is { } group)
            {
                UpdateGroupChildPresentation(group, content, animated: false);
            }
        }

        private void OnContentFullBleedChanged(object? sender, EventArgs e)
        {
            UpdateContentContainers();
        }

        // 把悬浮 tab bar 的遮挡区域折算成各页面容器的内缩边距:
        // 默认页面容器收缩避让悬浮导航栏,FullBleed 的页面容器铺满
        private void UpdateContentContainers()
        {
            var insets = UIEdgeInsets.Zero;

            if (TabBar is not null && !TabBar.Hidden && !TabBar.Frame.IsEmpty
                && TabBar.Superview is not null)
            {
                var frameInView = View.ConvertRectFromView(TabBar.Frame, TabBar.Superview);
                var bounds = View.Bounds;

                double bLeft = (double)bounds.Left, bTop = (double)bounds.Top;
                double bRight = (double)bounds.Right, bBottom = (double)bounds.Bottom;
                double fLeft = (double)frameInView.Left, fTop = (double)frameInView.Top;
                double fRight = (double)frameInView.Right, fBottom = (double)frameInView.Bottom;

                // 布局未就绪时坐标可能含 NaN/Infinity,直接放弃本次计算,
                // 避免把非法数值传给 CoreGraphics
                bool finite = IsFinite(bLeft) && IsFinite(bTop) && IsFinite(bRight) && IsFinite(bBottom)
                    && IsFinite(fLeft) && IsFinite(fTop) && IsFinite(fRight) && IsFinite(fBottom);

                bool overlaps = finite
                    && Math.Min(bRight, fRight) > Math.Max(bLeft, fLeft)
                    && Math.Min(bBottom, fBottom) > Math.Max(bTop, fTop);

                if (overlaps)
                {
                    const double dockThreshold = 24;
                    double top = 0, left = 0, bottom = 0, right = 0;
                    double width = fRight - fLeft, height = fBottom - fTop;
                    double boundsWidth = bRight - bLeft, boundsHeight = bBottom - bTop;

                    if (width >= boundsWidth * 0.5 && height < boundsHeight * 0.5)
                    {
                        // 上下边缘的横向条(底部 tab bar / 顶部 bar):按行避让
                        if (fTop - bTop <= dockThreshold)
                            top = fBottom - bTop;
                        if (bBottom - fBottom <= dockThreshold)
                            bottom = bBottom - fTop;
                    }
                    else
                    {
                        // 左右边缘的竖条或角落 pill:按列避让
                        if (fLeft - bLeft <= dockThreshold)
                            left = fRight - bLeft;
                        if (bRight - fRight <= dockThreshold)
                            right = bRight - fLeft;
                    }

                    insets = new UIEdgeInsets(
                        (nfloat)Math.Max(0, top),
                        (nfloat)Math.Max(0, left),
                        (nfloat)Math.Max(0, bottom),
                        (nfloat)Math.Max(0, right));
                }
            }

            foreach (var pair in _contentToContainerMap)
            {
                var target = pair.Key.FullBleed ? UIEdgeInsets.Zero : insets;
                if (!UIEdgeInsets.Equals(pair.Value.ContentInsets, target))
                {
                    pair.Value.ContentInsets = target;
                }
            }

            foreach (var container in _landingContainers)
            {
                if (!UIEdgeInsets.Equals(container.ContentInsets, insets))
                {
                    container.ContentInsets = insets;
                }
            }
        }

        // 页面容器:承载 MAUI 页面视图,按 ContentInsets 内缩布局,
        // 壳直接控制其尺寸,不依赖 MAUI 对原生 safe area 的传导
        private sealed class PageContainerViewController : UIViewController
        {
            readonly UIViewController _content;

            UIEdgeInsets _contentInsets;

            public PageContainerViewController(UIViewController content)
            {
                _content = content;
            }

            public UIEdgeInsets ContentInsets
            {
                get => _contentInsets;
                set
                {
                    _contentInsets = value;
                    View?.SetNeedsLayout();
                    View?.LayoutIfNeeded();
                }
            }

            public override void ViewDidLoad()
            {
                base.ViewDidLoad();

                AddChildViewController(_content);
                View.AddSubview(_content.View);
                _content.DidMoveToParentViewController(this);
            }

            public override void ViewDidLayoutSubviews()
            {
                base.ViewDidLayoutSubviews();

                var bounds = View.Bounds;
                var x = (double)bounds.X + (double)_contentInsets.Left;
                var y = (double)bounds.Y + (double)_contentInsets.Top;
                var width = Math.Max(0,
                    (double)bounds.Width - (double)_contentInsets.Left - (double)_contentInsets.Right);
                var height = Math.Max(0,
                    (double)bounds.Height - (double)_contentInsets.Top - (double)_contentInsets.Bottom);

                // 布局未就绪时数值可能为 NaN/Infinity,跳过本次赋值,
                // 避免把非法数值传给 CoreGraphics
                if (!IsFinite(x) || !IsFinite(y) || !IsFinite(width) || !IsFinite(height))
                {
                    return;
                }

                _content.View.Frame = new CoreGraphics.CGRect(x, y, width, height);
            }
        }

        static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
