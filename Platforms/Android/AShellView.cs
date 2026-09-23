using AdaptiveShell.Controls;
using Android.Animation;
using Android.Content;
using Android.Util;
using ColorStateList = Android.Content.Res.ColorStateList;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;
using AndroidX.Core.View;
using Google.Android.Material.AppBar;
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.Navigation;
using Google.Android.Material.NavigationRail;
using Microsoft.Maui.Platform;
using System;
using System.Collections.Generic;
using ImageButton = Android.Widget.ImageButton;

namespace AdaptiveShell.Platforms.Android
{
    public class AShellView : IDisposable
    {
        // Material 3 window size class: 紧凑宽度(<600dp)使用底部导航栏,否则使用侧边 NavigationRail
        const int CompactWidthBreakpointDp = 600;

        readonly Context _context;
        readonly float _density;

        LinearLayout _linearLayout;
        LinearLayout _contentColumnLayout;
        MaterialToolbar _toolbar;
        AShellGroup? _toolbarGroup;
        FrameLayout _overlayLayout;
        FrameLayout _drawerPanel;
        global::Android.Views.View _drawerScrim;
        FrameLayout _contentFrameLayout;
        NavigationBarView _navigationView;
        FrameLayout? _navHeaderFrameLayout;
        ValueAnimator? _menuAnimator;
        bool _isBottomBar;

        AShell _virtualView;

        IMauiContext _mauiContext;

        // 根视图:主布局 + 抽屉浮层(scrim + 面板)叠在同一 FrameLayout 里
        public FrameLayout PlatformView => _overlayLayout;

        Dictionary<AShellContent, IMenuItem> _contentToMenuItemMap;

        Dictionary<AShellGroup, IMenuItem> _groupToMenuItemMap;

        Dictionary<AShellGroup, global::Android.Views.View> _groupToLandingViewMap;

        public AShellView(AShell virtualView, Context context, IMauiContext mauiContext)
        {
            _virtualView = virtualView;
            _mauiContext = mauiContext;
            _context = context;
            _density = context.Resources!.DisplayMetrics!.Density;

            _linearLayout = new LinearLayout(context);

            _contentFrameLayout = new FrameLayout(context)
            {
                LayoutParameters = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    0,
                    1.0f)
            };

            // 组落地页/组内子页的 top app bar(M3 返回导航)
            _toolbar = new MaterialToolbar(context)
            {
                LayoutParameters = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.WrapContent),
                Visibility = ViewStates.Gone,
            };

            _contentColumnLayout = new LinearLayout(context)
            {
                Orientation = Orientation.Vertical,
            };
            _contentColumnLayout.AddView(_toolbar);
            _contentColumnLayout.AddView(_contentFrameLayout);

            // rail 模式下组子项的二级抽屉:与 rail 一体的浮层(M3 二级面板),
            // 从 rail 边缘弹出盖在内容上方,不挤压视口;无 elevation 阴影,
            // 与 rail 同色,视觉上连成一体
            _drawerPanel = new FrameLayout(context)
            {
                LayoutParameters = new FrameLayout.LayoutParams(
                    Dp(360),
                    ViewGroup.LayoutParams.MatchParent,
                    GravityFlags.Top | GravityFlags.Start),
                Visibility = ViewStates.Gone,
            };
            var drawerBackground = new global::Android.Graphics.Drawables.GradientDrawable();
            drawerBackground.SetColor(ResolveDrawerColor(context));
            // trailing 侧圆角(M3 modal drawer 16dp)
            float corner = Dp(16);
            drawerBackground.SetCornerRadii(new float[]
            {
                0, 0, corner, corner, corner, corner, 0, 0,
            });
            _drawerPanel.Background = drawerBackground;
            _drawerPanel.ClipToOutline = true;

            // 遮罩:点空白处收起抽屉
            _drawerScrim = new global::Android.Views.View(context)
            {
                LayoutParameters = new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.MatchParent),
                Visibility = ViewStates.Gone,
            };
            _drawerScrim.SetBackgroundColor(
                new global::Android.Graphics.Color(0, 0, 0, 0x52));
            _drawerScrim.Click += (_, _) => CloseDrawer();

            _overlayLayout = new FrameLayout(context)
            {
                LayoutParameters = new ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.MatchParent)
            };
            _overlayLayout.AddView(_linearLayout);
            _overlayLayout.AddView(_drawerScrim);
            _overlayLayout.AddView(_drawerPanel);

            // 安全区各自处理:内容列顶出状态栏;底栏模式的导航栏避开手势区;
            // 抽屉内容顶/底部避让。rail 内部已自行 inset,无需处理
            ViewCompat.SetOnApplyWindowInsetsListener(
                _linearLayout, new SafeAreaInsetsListener(this));

            _contentToMenuItemMap = new Dictionary<AShellContent, IMenuItem>();
            _groupToMenuItemMap = new Dictionary<AShellGroup, IMenuItem>();
            _groupToLandingViewMap = new Dictionary<AShellGroup, global::Android.Views.View>();

            RebuildNavigation();

            _linearLayout.LayoutChange += OnRootLayoutChange;

            // 系统返回键与返回堆栈打通:抽屉打开先关抽屉,
            // 组内子页回落地页;其余情况放行 MAUI 默认返回行为
            if (context is AndroidX.Activity.ComponentActivity componentActivity)
            {
                _backCallback = new GroupBackCallback(this);
                componentActivity.OnBackPressedDispatcher.AddCallback(_backCallback);
            }
        }

        GroupBackCallback? _backCallback;

        private void UpdateBackCallbackState()
        {
            if (_backCallback is null)
            {
                return;
            }

            bool onGroupChild =
                _toolbarGroup is not null
                && _toolbar.Visibility == ViewStates.Visible
                && _toolbar.NavigationIcon is not null;

            _backCallback.Enabled =
                _drawerPanel.Visibility == ViewStates.Visible || onGroupChild;
        }

        // 返回 true 表示系统返回已被我们消费
        private bool HandleSystemBack()
        {
            if (_drawerPanel.Visibility == ViewStates.Visible)
            {
                CloseDrawer();
                return true;
            }

            if (_toolbarGroup is not null
                && _toolbar.Visibility == ViewStates.Visible
                && _toolbar.NavigationIcon is not null)
            {
                ShowGroupLanding(_toolbarGroup);
                return true;
            }

            return false;
        }

        private sealed class GroupBackCallback : AndroidX.Activity.OnBackPressedCallback
        {
            readonly AShellView _owner;

            public GroupBackCallback(AShellView owner) : base(false)
            {
                _owner = owner;
            }

            public override void HandleOnBackPressed()
            {
                if (_owner.HandleSystemBack())
                {
                    return;
                }

                // 状态已过期:禁用后重新分发,交回默认行为
                Enabled = false;
                if (_owner._context is AndroidX.Activity.ComponentActivity activity)
                {
                    activity.OnBackPressedDispatcher.OnBackPressed();
                }
            }
        }

        // 抽屉容器色:与 rail 同为一体,取 surfaceContainer(M3 rail 容器色),
        // 依次退回 surface / colorBackground
        private static int ResolveDrawerColor(Context context)
        {
            var typed = new TypedValue();
            foreach (var name in new[] { "colorSurfaceContainer", "colorSurface" })
            {
                int attrId = context.Resources!.GetIdentifier(
                    name, "attr", context.PackageName);
                if (attrId != 0
                    && context.Theme!.ResolveAttribute(attrId, typed, true)
                    && typed.Type != 0)
                {
                    return typed.Data;
                }
            }

            context.Theme!.ResolveAttribute(
                global::Android.Resource.Attribute.ColorBackground, typed, true);
            return typed.Data;
        }

        private sealed class SafeAreaInsetsListener
            : Java.Lang.Object, IOnApplyWindowInsetsListener
        {
            readonly AShellView _owner;

            public SafeAreaInsetsListener(AShellView owner)
            {
                _owner = owner;
            }

            public WindowInsetsCompat OnApplyWindowInsets(
                global::Android.Views.View v, WindowInsetsCompat insets)
            {
                var top = insets.GetInsets(WindowInsetsCompat.Type.StatusBars()).Top;
                var bottom = insets.GetInsets(WindowInsetsCompat.Type.NavigationBars()).Bottom;

                // 内容列:顶出状态栏,底部交给页面/底栏各自避让
                _owner._contentColumnLayout.SetPadding(0, top, 0, 0);

                // 底栏模式:导航栏内容避开手势/三键区(背景仍延伸到底,edge-to-edge)
                if (_owner._isBottomBar && _owner._navigationView is not null)
                {
                    _owner._navigationView.SetPadding(0, 0, 0, bottom);
                }

                // 抽屉浮层:面板背景全高,内容避让状态栏与手势区
                _owner._drawerPanel.SetPadding(0, top, 0, bottom);

                return insets;
            }
        }

        private void OnRootLayoutChange(object? sender, global::Android.Views.View.LayoutChangeEventArgs e)
        {
            var widthDp = (int)((e.Right - e.Left) / _density);
            var useBottomBar = widthDp < CompactWidthBreakpointDp;
            if (useBottomBar != _isBottomBar)
            {
                RebuildNavigation(widthDp);
            }
        }

        private void RebuildNavigation(int? widthDp = null)
        {
            if (_navigationView != null)
            {
                _navigationView.ItemSelected -= OnPlatformViewItemInvoked;
                DisposeMenuAnimator();
                _navHeaderFrameLayout?.Dispose();
                _navHeaderFrameLayout = null;
                _navigationView.Dispose();
            }

            CloseDrawer();
            _linearLayout.RemoveAllViews();

            _isBottomBar =
                (widthDp ?? _context.Resources!.Configuration!.ScreenWidthDp) < CompactWidthBreakpointDp;

            if (_isBottomBar)
            {
                _linearLayout.Orientation = Orientation.Vertical;
                _contentColumnLayout.LayoutParameters = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    0,
                    1.0f);
                _navigationView = new BottomNavigationView(_context)
                {
                    LayoutParameters = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.MatchParent,
                        ViewGroup.LayoutParams.WrapContent),
                };
                _linearLayout.AddView(_contentColumnLayout);
                _linearLayout.AddView(_navigationView);
            }
            else
            {
                _linearLayout.Orientation = Orientation.Horizontal;
                _contentColumnLayout.LayoutParameters = new LinearLayout.LayoutParams(
                    0,
                    ViewGroup.LayoutParams.MatchParent,
                    1.0f);
                var rail = new NavigationRailView(_context)
                {
                    LayoutParameters = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.WrapContent,
                        ViewGroup.LayoutParams.MatchParent),
                    MenuGravity = ((int)GravityFlags.Center),
                };
                rail.ItemActiveIndicatorExpandedWidth =
                    NavigationBarView.ActiveIndicatorWidthMatchParent;
                rail.AddHeaderView(CreateNavHeader());

                var headerParameters =
                    (FrameLayout.LayoutParams)_navHeaderFrameLayout!.LayoutParameters!;
                headerParameters.Gravity = GravityFlags.Top | GravityFlags.Start;
                headerParameters.MarginStart = Dp(16);
                _navHeaderFrameLayout.LayoutParameters = headerParameters;

                _navigationView = rail;
                _linearLayout.AddView(_navigationView);
                _linearLayout.AddView(_contentColumnLayout);

                // rail 展开/收起时宽度变化,抽屉与遮罩跟随其右缘
                rail.LayoutChange += OnRailLayoutChange;
            }

            _navigationView.ItemSelected += OnPlatformViewItemInvoked;

            UpdateItems();
            UpdateColors();
            UpdateCurrentItem();

            // 新建的导航视图需要重新拿到 insets(底栏避让手势区/rail 自 inset)
            ViewCompat.RequestApplyInsets(_linearLayout);
        }

        private FrameLayout CreateNavHeader()
        {
            _navHeaderFrameLayout = new FrameLayout(_context);

            var menuButton = new ImageButton(_context)
            {
                LayoutParameters = new FrameLayout.LayoutParams(
                    Dp(48),
                    Dp(48),
                    GravityFlags.Center),
            };
            _navHeaderFrameLayout.AddView(menuButton);
            menuButton.SetImageResource(Resource.Drawable.material_symbols_menu_24);
            menuButton.ContentDescription = "Open navigation menu";
            var padding = Dp(6);
            menuButton.SetPadding(padding, padding, padding, padding);
            menuButton.Background = null;
            menuButton.Rotation = 180f;

            menuButton.Click += (_, _) =>
            {
                var rail = (NavigationRailView)_navigationView;
                var expanding = !rail.IsExpanded;

                menuButton.ContentDescription =
                    expanding
                        ? "Collapse navigation rail"
                        : "Expand navigation rail";

                float currentAngle = menuButton.Rotation;
                float targetAngle = expanding ? 360 : 180f;
                DisposeMenuAnimator();
                _menuAnimator = ValueAnimator.OfFloat(currentAngle, targetAngle);
                _menuAnimator.SetInterpolator(new OvershootInterpolator(1.0f));
                _menuAnimator.Update += (s, e) =>
                {
                    float animatedValue = (float)e.Animation.AnimatedValue;
                    menuButton.Rotation = animatedValue;

                    if (menuButton.Rotation > 270f)
                    {
                        menuButton.SetImageResource(Resource.Drawable.material_symbols_menu_open_24);
                    }
                    else
                    {
                        menuButton.SetImageResource(Resource.Drawable.material_symbols_menu_24);
                    }
                };

                _menuAnimator.SetDuration(300);
                _menuAnimator.Start();

                if (expanding)
                {
                    rail.Expand();
                }
                else
                {
                    rail.Collapse();
                }
            };

            return _navHeaderFrameLayout;
        }

        private void OnPlatformViewItemInvoked(object? sender, NavigationBarView.ItemSelectedEventArgs e)
        {
            if (e.Item != null)
            {
                var content = _contentToMenuItemMap.
                    FirstOrDefault(
                    a => ReferenceEquals(a.Value, e.Item
                    )).Key;

                if (content is not null)
                {
                    e.Handled = true;

                    if (!ReferenceEquals(_virtualView.CurrentItem, content))
                    {
                        _virtualView.CurrentItem = content;
                    }

                    return;
                }

                // 组条目:rail 弹子菜单;底栏进落地页(落地页为栈顶,子页压上)
                var group = _groupToMenuItemMap.
                    FirstOrDefault(
                    a => ReferenceEquals(a.Value, e.Item
                    )).Key;

                if (group is not null)
                {
                    e.Handled = true;

                    if (_isBottomBar)
                    {
                        ShowGroupLanding(group);
                    }
                    else
                    {
                        ToggleGroupDrawer(group);
                    }

                    return;
                }

                e.Handled = false;
            }
        }

        // rail 模式:组子项以浮层抽屉呈现(纯子菜单列表),
        // 从 rail 边缘弹出盖在内容上方(不挤压视口);再点组条目切换开合
        private void ToggleGroupDrawer(AShellGroup group)
        {
            if (_drawerPanel.Visibility == ViewStates.Visible)
            {
                CloseDrawer();
                return;
            }

            BuildDrawerMenu(group);

            // 贴齐 rail 右缘弹出;rail 展开时跟随其当前宽度
            UpdateDrawerAnchor();

            _drawerScrim.Visibility = ViewStates.Visible;
            _drawerPanel.Visibility = ViewStates.Visible;
            _drawerPanel.TranslationX = -Dp(80);
            _drawerPanel.Animate()?.TranslationX(0)?.SetDuration(200)?.Start();
            UpdateBackCallbackState();
        }

        // 子菜单列表:56dp 行高、图标+标题、ripple,点击选中子页
        private void BuildDrawerMenu(AShellGroup group)
        {
            _drawerPanel.RemoveAllViews();

            var list = new LinearLayout(_context)
            {
                Orientation = Orientation.Vertical,
            };
            list.SetPadding(0, Dp(8), 0, Dp(8));

            foreach (var child in group.Items)
            {
                var row = new LinearLayout(_context)
                {
                    Orientation = Orientation.Horizontal,
                    LayoutParameters = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.MatchParent, Dp(56)),
                };
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetPadding(Dp(16), 0, Dp(16), 0);

                var ripple = new TypedValue();
                _context.Theme!.ResolveAttribute(
                    global::Android.Resource.Attribute.SelectableItemBackground,
                    ripple, true);
                if (ripple.ResourceId != 0)
                {
                    row.SetBackgroundResource(ripple.ResourceId);
                }

                var icon = new ImageView(_context)
                {
                    LayoutParameters = new LinearLayout.LayoutParams(Dp(24), Dp(24)),
                };
                row.AddView(icon);
                LoadRowIconAsync(child, icon);

                var label = new TextView(_context)
                {
                    LayoutParameters = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.WrapContent,
                        ViewGroup.LayoutParams.WrapContent)
                    {
                        MarginStart = Dp(16),
                    },
                    Text = child.Title,
                };
                label.SetTextSize(ComplexUnitType.Sp, 16);
                row.AddView(label);

                row.Click += (_, _) =>
                {
                    if (!ReferenceEquals(_virtualView.CurrentItem, child))
                    {
                        _virtualView.CurrentItem = child;
                    }
                };

                list.AddView(row);
            }

            _drawerPanel.AddView(list, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent));
        }

        private async void LoadRowIconAsync(AShellContent item, ImageView iconView)
        {
            if (item.Icon is null)
            {
                return;
            }

            var result = await item.Icon.GetPlatformImageAsync(_mauiContext);
            if (result?.Value is global::Android.Graphics.Drawables.Drawable drawable)
            {
                iconView.SetImageDrawable(drawable);
            }
        }

        private void OnRailLayoutChange(object? sender, global::Android.Views.View.LayoutChangeEventArgs e)
        {
            if (_drawerPanel.Visibility == ViewStates.Visible)
            {
                UpdateDrawerAnchor();
            }
        }

        // 抽屉/遮罩贴齐 rail 右缘
        private void UpdateDrawerAnchor()
        {
            int railWidth = _navigationView.Width > 0 ? _navigationView.Width : Dp(80);

            var panelParameters = (FrameLayout.LayoutParams)_drawerPanel.LayoutParameters!;
            if (panelParameters.MarginStart != railWidth)
            {
                panelParameters.MarginStart = railWidth;
                _drawerPanel.LayoutParameters = panelParameters;
            }

            var scrimParameters = (FrameLayout.LayoutParams)_drawerScrim.LayoutParameters!;
            if (scrimParameters.MarginStart != railWidth)
            {
                scrimParameters.MarginStart = railWidth;
                _drawerScrim.LayoutParameters = scrimParameters;
            }
        }

        private void CloseDrawer()
        {
            _drawerPanel.Visibility = ViewStates.Gone;
            _drawerScrim.Visibility = ViewStates.Gone;
            UpdateBackCallbackState();
        }

        // 底栏模式:在内容区展示组落地页(子项列表)。
        // 落地页视图创建后始终 attach 在内容区,切到子页只是被遮盖(缩减呈现),
        // 不是从栈中移除
        private void ShowGroupLanding(AShellGroup group)
        {
            var landingView = GetOrCreateLandingView(group);

            if (!ReferenceEquals(landingView.Parent, _contentFrameLayout))
            {
                (landingView.Parent as ViewGroup)?.RemoveView(landingView);
                _contentFrameLayout.AddView(landingView);
            }

            for (int i = 0; i < _contentFrameLayout.ChildCount; i++)
            {
                var child = _contentFrameLayout.GetChildAt(i);
                if (child != null)
                {
                    child.Visibility = ReferenceEquals(child, landingView)
                        ? ViewStates.Visible
                        : ViewStates.Gone;
                }
            }

            // top app bar:落地页显示组标题,无返回箭头
            _toolbarGroup = group;
            _toolbar.Title = group.Title;
            _toolbar.NavigationIcon = null;
            _toolbar.Visibility = ViewStates.Visible;
            UpdateBackCallbackState();
        }

        private global::Android.Views.View GetOrCreateLandingView(AShellGroup group)
        {
            if (!_groupToLandingViewMap.TryGetValue(group, out var view))
            {
                var page = CreateLandingPage(group);

                // 挂到 AShell 逻辑树,保证资源/样式解析链连通
                _virtualView.AddLogicalChild(page);

                view = page.ToPlatform(_mauiContext);
                _groupToLandingViewMap[group] = view;
            }

            return view;
        }

        private Page CreateLandingPage(AShellGroup group)
        {
            if (group.LandingTemplate is not null)
            {
                return (Page)group.LandingTemplate.CreateContent();
            }

            var layout = new VerticalStackLayout
            {
                Padding = new Thickness(20, 12),
                Spacing = 4,
            };

            foreach (var child in group.Items)
            {
                layout.Add(CreateLandingRow(child));
            }

            return new ContentPage
            {
                Title = group.Title,
                Content = new Microsoft.Maui.Controls.ScrollView { Content = layout },
            };
        }

        private Microsoft.Maui.Controls.View CreateLandingRow(AShellContent child)
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
                if (!ReferenceEquals(_virtualView.CurrentItem, child))
                {
                    _virtualView.CurrentItem = child;
                }
            };
            row.GestureRecognizers.Add(tap);

            return row;
        }

        // 设置/清除导航图标时 toolbar 会重建导航按钮视图,
        // 因此每次显示返回箭头后直接在该按钮上挂 Click(每次图标设置都要重新挂)
        private void AttachBackClick()
        {
            // 导航按钮要到布局阶段才挂进 toolbar,需 post 到布局完成后再找
            _toolbar.Post(() =>
            {
                for (int i = 0; i < _toolbar.ChildCount; i++)
                {
                    if (_toolbar.GetChildAt(i) is ImageButton navButton)
                    {
                        navButton.Click -= OnToolbarBackClick;
                        navButton.Click += OnToolbarBackClick;
                        return;
                    }
                }
            });
        }

        private void OnToolbarBackClick(object? sender, EventArgs e)
        {
            if (_toolbarGroup is not null)
            {
                ShowGroupLanding(_toolbarGroup);
            }
        }

        public void Dispose()
        {
            _linearLayout.LayoutChange -= OnRootLayoutChange;

            if (_navigationView != null)
            {
                _navigationView.ItemSelected -= OnPlatformViewItemInvoked;
            }

            DisposeMenuAnimator();

            _backCallback?.Remove();
            _navigationView?.Dispose();
            _navHeaderFrameLayout?.Dispose();
            _toolbar?.Dispose();
            _drawerPanel?.Dispose();
            _drawerScrim?.Dispose();
            _contentFrameLayout?.Dispose();
            _contentColumnLayout?.Dispose();
            _linearLayout?.Dispose();
            _overlayLayout?.Dispose();
        }

        private void DisposeMenuAnimator()
        {
            _menuAnimator?.Cancel();
            _menuAnimator?.RemoveAllUpdateListeners();
            _menuAnimator?.Dispose();
            _menuAnimator = null;
        }

        public void UpdateItems()
        {
            _navigationView.Menu.Clear();
            _contentToMenuItemMap.Clear();
            _groupToMenuItemMap.Clear();

            foreach (var item in _virtualView.Items)
            {
                if (item is AShellContent content)
                {
                    AddMenuItem(content);
                }
                else if (item is AShellGroup group)
                {
                    var menuItem = _navigationView.Menu.Add(group.Title);
                    _groupToMenuItemMap[group] = menuItem;
                    LoadIconAsync(group, menuItem);
                }
            }
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

        private void AddMenuItem(AShellContent content)
        {
            var menuItem = _navigationView.Menu.Add(content.Title);
            _contentToMenuItemMap[content] = menuItem;
            LoadIconAsync(content, menuItem);
        }

        public void UpdateColors()
        {
            var selected = _virtualView.GetEffectiveSelectedItemColor();
            if (selected is null)
            {
                return;
            }

            int selectedInt = selected.ToPlatform();
            var unselected = _virtualView.GetEffectiveUnselectedItemColor();

            int uncheckedInt = unselected?.ToPlatform()
                ?? _navigationView.ItemIconTintList?.GetColorForState(
                    new[] { -global::Android.Resource.Attribute.StateChecked },
                    global::Android.Graphics.Color.Gray)
                ?? global::Android.Graphics.Color.Gray;

            var states = new[]
            {
                new[] { global::Android.Resource.Attribute.StateChecked },
                new[] { -global::Android.Resource.Attribute.StateChecked },
            };
            var tintList = new ColorStateList(states, new[] { selectedInt, uncheckedInt });

            _navigationView.ItemIconTintList = tintList;
            _navigationView.ItemTextColor = tintList;
            _navigationView.ItemActiveIndicatorColor =
                ColorStateList.ValueOf(selected.WithAlpha(0.2f).ToPlatform());
        }

        private async void LoadIconAsync(AShellItem item, IMenuItem menuItem)
        {
            if (item.Icon is null)
            {
                return;
            }

            var result = await item.Icon.GetPlatformImageAsync(_mauiContext);
            if (result?.Value is not global::Android.Graphics.Drawables.Drawable drawable)
            {
                return;
            }

            bool stillCurrent = item switch
            {
                AShellContent content =>
                    _contentToMenuItemMap.TryGetValue(content, out var t) && ReferenceEquals(t, menuItem),
                AShellGroup group =>
                    _groupToMenuItemMap.TryGetValue(group, out var t) && ReferenceEquals(t, menuItem),
                _ => false,
            };

            if (stillCurrent)
            {
                menuItem.SetIcon(drawable);
            }
        }

        public void UpdateCurrentItem()
        {
            if (_virtualView.CurrentItem is null)
            {
                return;
            }

            // 选中项:叶子页面对应自身条目;组内子页面对应组条目(保持组高亮)
            IMenuItem? selectedMenuItem = null;
            if (_contentToMenuItemMap.TryGetValue(_virtualView.CurrentItem, out var contentItem))
            {
                selectedMenuItem = contentItem;
            }
            else if (FindGroupOf(_virtualView.CurrentItem) is { } group
                && _groupToMenuItemMap.TryGetValue(group, out var groupItem))
            {
                selectedMenuItem = groupItem;
            }

            if (selectedMenuItem is not null
                && _navigationView.SelectedItemId != selectedMenuItem.ItemId)
            {
                _navigationView.SelectedItemId = selectedMenuItem.ItemId;
            }

            var targetView = ((IAShellContentController)_virtualView.CurrentItem)?.page.ToPlatform(_mauiContext);
            if (targetView is null)
            {
                return;
            }

            if (!ReferenceEquals(targetView.Parent, _contentFrameLayout))
            {
                (targetView.Parent as ViewGroup)?.RemoveView(targetView);
                _contentFrameLayout.AddView(targetView);
            }

            // 已加载的页面保持 attach,切换只改 Visibility,避免移除再添加导致的整页重新布局和闪跳
            for (int i = 0; i < _contentFrameLayout.ChildCount; i++)
            {
                var child = _contentFrameLayout.GetChildAt(i);
                if (child != null)
                {
                    child.Visibility = ReferenceEquals(child, targetView)
                        ? ViewStates.Visible
                        : ViewStates.Gone;
                }
            }

            // top app bar:底栏模式下组内子页显示子页标题 + 返回箭头(返回落地页);
            // 叶子页面或 rail 模式隐藏(rail 用抽屉导航,无需返回)
            var currentGroup = FindGroupOf(_virtualView.CurrentItem);
            if (currentGroup is not null && _isBottomBar)
            {
                _toolbarGroup = currentGroup;
                _toolbar.Title = _virtualView.CurrentItem.Title;
                _toolbar.NavigationIcon = _context.GetDrawable(
                    Resource.Drawable.material_symbols_arrow_back_24);
                _toolbar.Visibility = ViewStates.Visible;
                AttachBackClick();
            }
            else
            {
                _toolbarGroup = null;
                _toolbar.Visibility = ViewStates.Gone;
            }

            // 选中子页后收起二级抽屉
            CloseDrawer();
        }

        private int Dp(float value)
        {
            return (int)TypedValue.ApplyDimension(
                ComplexUnitType.Dip,
                value,
                _context.Resources!.DisplayMetrics);
        }
    }
}
