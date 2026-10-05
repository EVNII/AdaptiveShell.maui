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
        readonly Dictionary<AShellGroup, AShellContent[]> _groupChildren = new();
        readonly Dictionary<AShellGroup, UITab[]> _groupChildTabs = new();
        readonly Dictionary<AShellGroup, DataTemplate?> _landingTemplates = new();
        readonly Dictionary<AShellGroup, string?[]> _landingIdentities = new();
        readonly Dictionary<AShellContent, Page> _containerPages = new();
        readonly Dictionary<AShellContent, bool> _contentNavigationMode = new();
        readonly Dictionary<AShellGroup, Page> _landingPages = new();
        readonly Dictionary<AShellItem, int> _iconVersions = new();
        readonly Dictionary<AShellItem, ImageSource?> _iconSources = new();
        readonly List<UITab> _retiredTabs = new();
        readonly List<PageContainerViewController> _retiredContainers = new();
        AShellContent? _lastCurrentItem;
        AShellGroup? _landingGroupBeforeItems;
        UIColor? _defaultTabTint, _defaultViewTint, _defaultUnselectedTint;
        bool _colorsCaptured;
        bool _disconnected;

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

        public void Disconnect()
        {
            if (_disconnected) return;
            _disconnected = true;
            Delegate = null!;
            SetTabs(Array.Empty<UITab>(), false);
            foreach (var content in _contentToUITabMap.Keys)
                content.FullBleedChanged -= OnContentFullBleedChanged;
            foreach (var container in _contentToContainerMap.Values)
                RetireContainer(container);
            foreach (var navigation in _groupToNavMap.Values)
            {
                navigation.SetViewControllers(Array.Empty<UIViewController>(), false);
                navigation.Dispose();
            }
            foreach (var group in _groupToLandingMap.Keys.ToArray()) RemoveLanding(group);
            foreach (var tab in _contentToUITabMap.Values.Concat<UITab>(_groupToUITabGroupMap.Values))
                _retiredTabs.Add(tab);
            DisposeRetiredTabs();
            _contentToUITabMap.Clear();
            _groupToUITabGroupMap.Clear();
            _contentToContainerMap.Clear();
            _containerPages.Clear();
            _contentNavigationMode.Clear();
            _landingPages.Clear();
            _groupToNavMap.Clear();
            _groupChildren.Clear();
            _groupChildTabs.Clear();
            _landingTemplates.Clear();
            _landingIdentities.Clear();
            _iconVersions.Clear();
            _iconSources.Clear();
            _tabBarDelegate?.Dispose();
            _tabBarDelegate = null;
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
            _containerPages[item] = page;

            return wrapInNavigation
                ? new UINavigationController(container)
                : container;
        }

        public void UpdateItems()
        {
            if (_disconnected) return;
            _landingGroupBeforeItems = _groupToNavMap.Keys.FirstOrDefault(group =>
                _groupToLandingMap.TryGetValue(group, out var landing)
                && ReferenceEquals(_groupToNavMap[group].TopViewController, landing)
                && ((_groupToUITabGroupMap.TryGetValue(group, out var groupTab)
                        && ReferenceEquals(SelectedTab, groupTab))
                    || group.Items.Any(child => _contentToUITabMap.TryGetValue(child, out var childTab)
                        && ReferenceEquals(SelectedTab, childTab))));
            Delegate = _tabBarDelegate ??= new TabBarDelegate(this);

            var tabs = new List<UITab>();
            var desiredContents = new HashSet<AShellContent>();
            var desiredGroups = new HashSet<AShellGroup>();

            foreach (var item in _virtualView.Items)
            {
                if (item is AShellContent content)
                {
                    desiredContents.Add(content);
                    tabs.Add(GetOrCreateTab(content));
                }
                else if (item is AShellGroup group)
                {
                    desiredGroups.Add(group);
                    foreach (var child in group.Items) desiredContents.Add(child);
                    tabs.Add(GetOrCreateGroup(group));
                }
            }

            SetTabs(tabs.ToArray(), false);
            foreach (var container in _retiredContainers) RetireContainer(container);
            _retiredContainers.Clear();
            foreach (var content in _contentToUITabMap.Keys.Where(x => !desiredContents.Contains(x)).ToArray())
            {
                content.FullBleedChanged -= OnContentFullBleedChanged;
                _retiredTabs.Add(_contentToUITabMap[content]);
                _contentToUITabMap.Remove(content);
                if (_contentToContainerMap.Remove(content, out var container)) RetireContainer(container);
                _containerPages.Remove(content);
                _contentNavigationMode.Remove(content);
                _iconVersions.Remove(content);
                _iconSources.Remove(content);
            }
            foreach (var group in _groupToUITabGroupMap.Keys.Where(x => !desiredGroups.Contains(x)).ToArray())
            {
                _retiredTabs.Add(_groupToUITabGroupMap[group]);
                _groupToUITabGroupMap.Remove(group);
                if (_groupToNavMap.Remove(group, out var navigation))
                {
                    navigation.SetViewControllers(Array.Empty<UIViewController>(), false);
                    navigation.Dispose();
                }
                RemoveLanding(group);
                _groupChildren.Remove(group);
                _groupChildTabs.Remove(group);
                _landingTemplates.Remove(group);
                _landingIdentities.Remove(group);
                _iconVersions.Remove(group);
                _iconSources.Remove(group);
            }
            DisposeRetiredTabs();
            UpdateContentContainers();
        }

        private void RetireContainer(PageContainerViewController container)
        {
            if (container.NavigationController is { } navigation)
            {
                var remaining = (navigation.ViewControllers ?? Array.Empty<UIViewController>())
                    .Where(c => !ReferenceEquals(c, container)).ToArray();
                navigation.SetViewControllers(remaining, false);
            }
            container.DetachContent();
            container.Dispose();
        }

        private void DisposeRetiredTabs()
        {
            foreach (var tab in _retiredTabs.Distinct()) tab.Dispose();
            _retiredTabs.Clear();
        }

        private UITab GetOrCreateTab(AShellContent content, bool wrapInNavigation = true)
        {
            if (_containerPages.TryGetValue(content, out var oldPage)
                && !ReferenceEquals(oldPage, content.CachedPage))
            {
                if (_contentToContainerMap.Remove(content, out var oldContainer))
                    _retiredContainers.Add(oldContainer);
                _containerPages.Remove(content);
                if (_contentToUITabMap.Remove(content, out var oldTab)) _retiredTabs.Add(oldTab);
            }
            if (_contentToUITabMap.ContainsKey(content)
                && _contentNavigationMode.TryGetValue(content, out var oldMode)
                && oldMode != wrapInNavigation)
            {
                if (_contentToUITabMap.Remove(content, out var oldTab)) _retiredTabs.Add(oldTab);
            }
            if (!_contentToUITabMap.TryGetValue(content, out var tab))
            {
                tab = new UITab(
                    content.Title ?? "Untitled",
                    UIImage.GetSystemImage("doc.text"),
                    content.Id.ToString(),
                    _ => CreatePage(content, wrapInNavigation));
                tab.AccessibilityIdentifier = content.AutomationId ?? content.Title;

                _contentToUITabMap[content] = tab;
                if (!_contentNavigationMode.ContainsKey(content))
                {
                    content.FullBleedChanged += OnContentFullBleedChanged;
                }
                _contentNavigationMode[content] = wrapInNavigation;
                RefreshIcon(content, tab, force: true);
            }

            tab.Title = content.Title ?? "Untitled";
            tab.AccessibilityIdentifier = content.AutomationId ?? content.Title;
            if (_contentToContainerMap.TryGetValue(content, out var container))
                container.Title = content.Title;
            RefreshIcon(content, tab);

            return tab;
        }

        private UITabGroup GetOrCreateGroup(AShellGroup group)
        {
            var children = new List<UITab>();
            foreach (var child in group.Items)
                children.Add(GetOrCreateTab(child, wrapInNavigation: false));

            bool childrenChanged = !_groupChildren.TryGetValue(group, out var previous)
                || !previous.SequenceEqual(group.Items);
            childrenChanged |= !_groupChildTabs.TryGetValue(group, out var priorTabs)
                || !priorTabs.SequenceEqual(children);
            bool templateChanged = _landingTemplates.TryGetValue(group, out var oldTemplate)
                && !ReferenceEquals(oldTemplate, group.LandingTemplate);
            var identity = new[] { group.AutomationId ?? group.Title }
                .Concat(group.Items.Select(child => child.AutomationId ?? child.Title)).ToArray();
            bool identityChanged = group.LandingTemplate is null && oldTemplate is null
                && _landingIdentities.TryGetValue(group, out var oldIdentity)
                && !oldIdentity.SequenceEqual(identity);
            if (childrenChanged)
            {
                if (_groupToUITabGroupMap.Remove(group, out var oldTab)) _retiredTabs.Add(oldTab);
                if (group.LandingTemplate is not null
                    && _groupToNavMap.TryGetValue(group, out var navigation)
                    && _groupToLandingMap.TryGetValue(group, out var retainedLanding)
                    && navigation.TopViewController is { } top
                    && !ReferenceEquals(top, retainedLanding)
                    && !group.Items.Any(child => _contentToContainerMap.TryGetValue(child, out var container)
                        && ReferenceEquals(container, top)))
                {
                    navigation.SetViewControllers(new UIViewController[] { retainedLanding }, false);
                }
            }
            if (templateChanged || (group.LandingTemplate is null && (childrenChanged || identityChanged)))
            {
                if (_groupToNavMap.ContainsKey(group)) ReplaceLanding(group);
            }
            if (!_groupToUITabGroupMap.TryGetValue(group, out var tabGroup))
            {
                var childTabs = children.ToArray();

                tabGroup = new UITabGroup(
                    group.Title ?? "Group",
                    null,
                    group.Id.ToString(),
                    childTabs,
                    _ => GetOrCreateGroupNavigation(group));

                // 组节点在任何 sidebar 里都不可点击(仅作可折叠分区标题);
                // 落地页经组 tab(tab 模式)进入,与该开关无关。
                // IsSidebarDestination 从 iOS/Mac Catalyst 26.1 起可用。
                // 同时检查系统版本与 selector，兼容旧系统和不同 UIKit 实现。
                if ((OperatingSystem.IsIOSVersionAtLeast(26, 1)
                        || OperatingSystem.IsMacCatalystVersionAtLeast(26, 1))
                    && tabGroup.RespondsToSelector(new ObjCRuntime.Selector("setIsSidebarDestination:")))
                {
                    tabGroup.IsSidebarDestination = false;
                }
                tabGroup.AccessibilityIdentifier = group.AutomationId ?? group.Title;

                _groupToUITabGroupMap[group] = tabGroup;
                RefreshIcon(group, tabGroup, force: true);
            }

            _groupChildren[group] = group.Items.ToArray();
            _groupChildTabs[group] = children.ToArray();
            _landingTemplates[group] = group.LandingTemplate;
            _landingIdentities[group] = identity;
            tabGroup.Title = group.Title ?? "Group";
            tabGroup.AccessibilityIdentifier = group.AutomationId ?? group.Title;
            if (_groupToLandingMap.TryGetValue(group, out var landing)) landing.Title = group.Title;
            RefreshIcon(group, tabGroup);

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
            _landingPages[group] = landingPage;
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

            // sidebar 真的在呈现才算 sidebar 形态:
            // iPhone idiom(含 iPhone Duo 展开、Plus/Pro Max 横屏)下
            // HorizontalSizeClass 也是 Regular,但 sidebar 不可用,
            // 系统用悬浮 tab bar,必须显示返回按钮
            bool sidebarPresentation =
                TraitCollection.HorizontalSizeClass == UIUserInterfaceSizeClass.Regular
                && UIDevice.CurrentDevice.UserInterfaceIdiom != UIUserInterfaceIdiom.Phone
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
            var page = DefaultLandingPage.Create(group, child =>
            {
                ShowGroupChild(group, child);
                _virtualView.CurrentItem = child;
            });
            _virtualView.AddLogicalChild(page);
            return page;
        }

        private void RemoveLanding(AShellGroup group)
        {
            if (_groupToLandingMap.Remove(group, out var container))
            {
                _landingContainers.Remove(container);
                container.DetachContent();
                container.Dispose();
            }
            if (_landingPages.Remove(group, out var page))
            {
                page.Handler?.DisconnectHandler();
                _virtualView.RemoveLogicalChild(page);
            }
        }

        private void ReplaceLanding(AShellGroup group)
        {
            if (!_groupToNavMap.TryGetValue(group, out var navigation)) return;
            var oldLanding = _groupToLandingMap[group];
            var page = CreateLandingPage(group);
            var controller = page.ToUIViewController(_mauiContext);
            controller.Title = group.Title;
            var landing = new PageContainerViewController(controller);
            var top = navigation.TopViewController;
            bool keepChild = top is not null && !ReferenceEquals(top, oldLanding)
                && group.Items.Any(child => _contentToContainerMap.TryGetValue(child, out var c)
                    && ReferenceEquals(c, top));
            navigation.SetViewControllers(keepChild
                ? new UIViewController[] { landing, top! }
                : new UIViewController[] { landing }, false);
            RemoveLanding(group);
            _groupToLandingMap[group] = landing;
            _landingPages[group] = page;
            _landingContainers.Add(landing);
        }

        private void RefreshIcon(AShellItem item, UITab tab, bool force = false)
        {
            if (!force && _iconSources.TryGetValue(item, out var oldSource)
                && ReferenceEquals(oldSource, item.Icon)) return;
            _iconSources[item] = item.Icon;
            LoadIconAsync(item, tab);
        }

        private async void LoadIconAsync(AShellItem item, UITab tab)
        {
            var source = item.Icon;
            int version = _iconVersions.TryGetValue(item, out var previous) ? previous + 1 : 1;
            _iconVersions[item] = version;
            if (source is null)
            {
                tab.Image = item is AShellContent ? UIImage.GetSystemImage("doc.text") : null;
                return;
            }

            try
            {
                using var result = await source.GetPlatformImageAsync(_mauiContext);
                if (result?.Value is not UIImage image) return;

                bool stillCurrent = !_disconnected && ReferenceEquals(item.Icon, source)
                    && _iconVersions.TryGetValue(item, out var latest) && latest == version
                    && (item switch
                    {
                        AShellContent content =>
                            _contentToUITabMap.TryGetValue(content, out var t) && ReferenceEquals(t, tab),
                        AShellGroup group =>
                            _groupToUITabGroupMap.TryGetValue(group, out var t) && ReferenceEquals(t, tab),
                        _ => false,
                    });

                if (stillCurrent)
                    tab.Image = image.ImageWithRenderingMode(UIImageRenderingMode.AlwaysTemplate);
            }
            catch (Exception)
            {
                // Keep the native fallback if a file/stream image is unavailable.
            }
        }

        public void UpdateColors()
        {
            if (_disconnected) return;
            var view = View;
            if (view is null) return;

            if (!_colorsCaptured)
            {
                _defaultTabTint = TabBar.TintColor;
                _defaultViewTint = view.TintColor;
                _defaultUnselectedTint = TabBar.UnselectedItemTintColor;
                _colorsCaptured = true;
            }
            var selected = _virtualView.GetEffectiveSelectedItemColor();
            if (selected is not null)
            {
                var tintColor = selected.ToPlatform();

                // 底部 tab bar(iOS):图标和文字一起着色
                TabBar.TintColor = tintColor;

                // sidebar(Mac/iPad):选中行由系统渲染,tint 决定其色调;
                // 选中行背景样式 Apple 不提供定制 API
                view.TintColor = tintColor;
            }
            else
            {
                TabBar.TintColor = _defaultTabTint;
                view.TintColor = _defaultViewTint;
            }

            var unselected = _virtualView.GetEffectiveUnselectedItemColor();
            if (unselected is not null)
            {
                TabBar.UnselectedItemTintColor = unselected.ToPlatform();
            }
            else TabBar.UnselectedItemTintColor = _defaultUnselectedTint;
        }

        public void UpdateCurrentItem(bool preserveLanding = false)
        {
            var currentItem = _virtualView.CurrentItem;
            if (currentItem is null)
            {
                SelectedTab = null;
                _lastCurrentItem = null;
                return;
            }
            if (currentItem is AShellContent content)
            {
                bool sameSelection = ReferenceEquals(content, _lastCurrentItem);
                _lastCurrentItem = content;
                var group = FindGroupOf(content);
                if (preserveLanding && sameSelection && _landingGroupBeforeItems is { } landingGroup
                    && _groupToUITabGroupMap.TryGetValue(landingGroup, out var landingTab))
                {
                    SelectedTab = landingTab;
                    return;
                }
                if (_contentToUITabMap.TryGetValue(content, out var tab))
                {
                    SelectedTab = tab;
                }
                else return;

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
            if (_disconnected || _virtualView.IsUpdatingItems) return;
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
            if (_disconnected) return;
            var view = View;
            if (view is null) return;

            var insets = UIEdgeInsets.Zero;

            if (TabBar is not null && !TabBar.Hidden && !TabBar.Frame.IsEmpty
                && TabBar.Superview is not null)
            {
                var frameInView = view.ConvertRectFromView(TabBar.Frame, TabBar.Superview);
                var bounds = view.Bounds;

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
            bool _detached;

            UIEdgeInsets _contentInsets;

            public PageContainerViewController(UIViewController content)
            {
                _content = content;
            }

            public void DetachContent()
            {
                if (_detached) return;
                _detached = true;
                if (!ReferenceEquals(_content.ParentViewController, this)) return;
                _content.WillMoveToParentViewController(null);
                if (_content.IsViewLoaded) _content.View?.RemoveFromSuperview();
                _content.RemoveFromParentViewController();
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

                if (_detached) return;

                var rootView = View
                    ?? throw new InvalidOperationException("The page container view is unavailable.");
                var contentView = _content.View
                    ?? throw new InvalidOperationException("The content view is unavailable.");

                AddChildViewController(_content);
                rootView.AddSubview(contentView);
                _content.DidMoveToParentViewController(this);
            }

            public override void ViewDidLayoutSubviews()
            {
                base.ViewDidLayoutSubviews();

                if (_detached || View is not { } rootView || _content.View is not { } contentView)
                    return;

                var bounds = rootView.Bounds;
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

                contentView.Frame = new CoreGraphics.CGRect(x, y, width, height);
            }
        }

        static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
