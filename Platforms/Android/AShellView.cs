using AdaptiveShell.Controls;
using Android.Animation;
using Android.Content;
using Android.Util;
using ColorStateList = Android.Content.Res.ColorStateList;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;
using AndroidX.Core.Graphics;
using AndroidX.Core.View;
using Google.Android.Material.AppBar;
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.Navigation;
using Google.Android.Material.NavigationRail;
using Microsoft.Maui.Platform;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using ImageButton = Android.Widget.ImageButton;

namespace AdaptiveShell.Platforms.Android
{
    public partial class AShellView : IDisposable
    {
        // Material 3 window size class: 紧凑宽度(<600dp)使用底部导航栏,否则使用侧边 NavigationRail
        const int CompactWidthBreakpointDp = 600;

        readonly Context _context;
        readonly float _density;
        readonly AndroidX.Activity.ComponentActivity? _activity;

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
            _activity = FindActivity(context);
            _isNight = IsNightMode(context);

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
            _overlayLayout.ViewAttachedToWindow += OnRootAttachedToWindow;
            _overlayLayout.ViewDetachedFromWindow += OnRootDetachedFromWindow;
            _virtualView.PropertyChanged += OnRootBackgroundChanged;

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
            if (_activity is not null)
            {
                _backCallback = new GroupBackCallback(this);
                _activity.OnBackPressedDispatcher.AddCallback(_backCallback);

                _configListener = new ConfigurationChangedListener(this);
                _activity.AddOnConfigurationChangedListener(_configListener);
            }
        }

        GroupBackCallback? _backCallback;

        bool _isNight;

        ConfigurationChangedListener? _configListener;

        bool _disposed;

        bool? _previousLightStatusBars;
        bool _lastLightStatusBars;

        global::Android.Views.Window? _navigationAppearanceWindow;
        bool? _previousLightNavigationBars;
        bool _lastLightNavigationBars;
        bool? _previousNavigationBarContrastEnforced;

        global::Android.Views.Window? _themeBackgroundWindow;
        global::Android.Graphics.Drawables.ColorDrawable? _previousThemeBackground;
        global::Android.Graphics.Drawables.ColorDrawable? _ownedThemeBackground;
        int _lastThemeBackgroundColor;
        global::Android.Views.Window? _themeBackgroundHostChangedWindow;

        private void OnRootAttachedToWindow(object? sender,
            global::Android.Views.View.ViewAttachedToWindowEventArgs e)
        {
            StartNavigationAppearanceDiagnostics();
            // WindowHandler initializes system bars before content is attached. Posting also
            // lets MAUI finish mapping the Page background used behind the top inset.
            _overlayLayout.Post(UpdateStatusBarAppearance);
            _overlayLayout.Post(UpdateNavigationBarAppearance);
        }

        private void OnRootDetachedFromWindow(object? sender,
            global::Android.Views.View.ViewDetachedFromWindowEventArgs e)
        {
            RecordNavigationAppearanceDiagnostic("detached", force: true);
            StopNavigationAppearanceDiagnostics();
            RestoreStatusBarAppearance();
            RestoreNavigationBarAppearance();
        }

        private void OnRootBackgroundChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AShell.BackgroundColor)
                || e.PropertyName == nameof(AShell.Background))
            {
                _overlayLayout.Post(UpdateStatusBarAppearance);
                _overlayLayout.Post(UpdateNavigationBarAppearance);
            }
        }

        private void UpdateStatusBarAppearance()
        {
            if (_disposed || !_overlayLayout.IsAttachedToWindow
                || !OperatingSystem.IsAndroidVersionAtLeast(23)
                || _activity?.Window is not { } window)
            {
                return;
            }

            // Target 35+ is edge-to-edge unless its actual window theme opts out.
            // Android 16 / target 36 also disables that opt-out. Earlier transparent
            // windows are supported without changing legacy opaque purple bars.
            int targetSdk = (int)(_context.ApplicationInfo?.TargetSdkVersion ?? 0);
            bool forcedTransparent = OperatingSystem.IsAndroidVersionAtLeast(36) && targetSdk >= 36;
#pragma warning disable CA1422 // The opt-out still applies on Android 15 / target 35.
            if (!forcedTransparent && OperatingSystem.IsAndroidVersionAtLeast(35) && targetSdk >= 35)
            {
                using var windowStyle = _activity.ObtainStyledAttributes(
                    new[] { global::Android.Resource.Attribute.WindowOptOutEdgeToEdgeEnforcement });
                forcedTransparent = !windowStyle.GetBoolean(0, false);
            }
#pragma warning restore CA1422
#pragma warning disable CA1422 // Reading the legacy color is needed only when it is still honored.
            bool transparent = forcedTransparent || ((uint)window.StatusBarColor >> 24) == 0;
#pragma warning restore CA1422
            if (!transparent
                || _overlayLayout.Background is not global::Android.Graphics.Drawables.ColorDrawable background
                || background.Color.A != 255)
            {
                // A gradient or translucent root has no single known status-area color.
                RestoreStatusBarAppearance();
                return;
            }

            if (WindowCompat.GetInsetsController(window, window.DecorView) is not { } controller)
            {
                return;
            }
            bool lightBackground = ColorUtils.CalculateLuminance(background.Color.ToArgb()) > 0.5;
            if (_previousLightStatusBars is null && controller.AppearanceLightStatusBars == lightBackground)
            {
                return;
            }

            _previousLightStatusBars ??= controller.AppearanceLightStatusBars;
            controller.AppearanceLightStatusBars = lightBackground;
            _lastLightStatusBars = lightBackground;
        }

        private void RestoreStatusBarAppearance()
        {
            if (_previousLightStatusBars is bool previous && _activity?.Window is { } window)
            {
                var controller = WindowCompat.GetInsetsController(window, window.DecorView);
                // Do not overwrite a later appearance change made by the host or another page.
                if (controller is not null && controller.AppearanceLightStatusBars == _lastLightStatusBars)
                {
                    controller.AppearanceLightStatusBars = previous;
                }
            }
            _previousLightStatusBars = null;
        }

        private void UpdateNavigationBarAppearance()
        {
            RecordNavigationAppearanceDiagnostic("before-update", force: true);
            try
            {
                UpdateNavigationBarAppearanceCore();
            }
            finally
            {
                RecordNavigationAppearanceDiagnostic("after-update", force: true);
            }
        }

        private void UpdateNavigationBarAppearanceCore()
        {
            if (_disposed || !_overlayLayout.IsAttachedToWindow
                || !OperatingSystem.IsAndroidVersionAtLeast(26)
                || _activity?.Window is not { } window)
            {
                return;
            }

            if (_navigationAppearanceWindow is not null
                && !ReferenceEquals(_navigationAppearanceWindow, window))
            {
                // Ownership cannot transfer to a different host window.
                RestoreNavigationBarAppearance();
            }

            int targetSdk = (int)(_context.ApplicationInfo?.TargetSdkVersion ?? 0);
            bool edgeToEdge = OperatingSystem.IsAndroidVersionAtLeast(36) && targetSdk >= 36;
#pragma warning disable CA1422 // The actual Android 15 theme may still opt out.
            if (!edgeToEdge && OperatingSystem.IsAndroidVersionAtLeast(35) && targetSdk >= 35)
            {
                using var style = _activity.ObtainStyledAttributes(
                    new[] { global::Android.Resource.Attribute.WindowOptOutEdgeToEdgeEnforcement });
                edgeToEdge = !style.GetBoolean(0, false);
            }
            int legacyColor = window.NavigationBarColor;
#pragma warning restore CA1422
            int color;
            bool transparent = edgeToEdge || ((uint)legacyColor >> 24) == 0;
            if (transparent)
            {
                // In enforced edge-to-edge, getNavigationBarColor returns zero even
                // while DecorView keeps a colored scrim internally. Do not save/restore
                // that getter as the scrim's color. Own the readable contrast switch
                // instead, and only when an opaque Shell surface covers the real inset.
                if (!TryGetNavigationInsetColor(window, out color))
                {
                    RestoreNavigationBarAppearance();
                    return;
                }
            }
            else if (((uint)legacyColor >> 24) == 255)
            {
                // Legacy opaque host colors remain the actual button background.
                color = legacyColor;
            }
            else
            {
                RestoreNavigationBarAppearance();
                return;
            }

            UpdateThemeWindowBackground(window,
                OperatingSystem.IsAndroidVersionAtLeast(36) && targetSdk >= 36 && transparent);

            var controller = WindowCompat.GetInsetsController(window, window.DecorView);
            if (controller is null)
            {
                return;
            }
            if (transparent && OperatingSystem.IsAndroidVersionAtLeast(29)
                && window.NavigationBarContrastEnforced)
            {
                _navigationAppearanceWindow ??= window;
                _previousNavigationBarContrastEnforced ??= window.NavigationBarContrastEnforced;
                window.NavigationBarContrastEnforced = false;
            }
            bool lightBackground = ColorUtils.CalculateLuminance(color) > 0.5;
            if (_previousLightNavigationBars is not null
                || controller.AppearanceLightNavigationBars != lightBackground)
            {
                _navigationAppearanceWindow ??= window;
                _previousLightNavigationBars ??= controller.AppearanceLightNavigationBars;
                if (controller.AppearanceLightNavigationBars != lightBackground)
                {
                    controller.AppearanceLightNavigationBars = lightBackground;
                }
                _lastLightNavigationBars = lightBackground;
            }
        }

        private bool TryGetNavigationInsetColor(global::Android.Views.Window window, out int color)
        {
            color = 0;
            var insets = ViewCompat.GetRootWindowInsets(window.DecorView);
            if (insets is null || !insets.IsVisible(WindowInsetsCompat.Type.NavigationBars()))
            {
                return false;
            }
            var navigation = insets.GetInsets(WindowInsetsCompat.Type.NavigationBars());
            if (navigation.Bottom <= 0 || navigation.Left != 0 || navigation.Right != 0)
            {
                return false;
            }

            global::Android.Views.View provider = _isBottomBar ? _navigationView : _overlayLayout;
            if (provider.Visibility != ViewStates.Visible || provider.Alpha != 1f
                || _overlayLayout.Alpha != 1f || _linearLayout.Alpha != 1f
                || _drawerPanel.Visibility == ViewStates.Visible
                || _drawerScrim.Visibility == ViewStates.Visible)
            {
                return false;
            }
            var decorLocation = new int[2];
            var providerLocation = new int[2];
            window.DecorView.GetLocationOnScreen(decorLocation);
            provider.GetLocationOnScreen(providerLocation);
            int left = decorLocation[0];
            int right = left + window.DecorView.Width;
            int bottom = decorLocation[1] + window.DecorView.Height;
            int top = bottom - navigation.Bottom;
            if (window.DecorView.Width <= 0 || top < decorLocation[1]
                || providerLocation[0] > left || providerLocation[1] > top
                || providerLocation[0] + provider.Width < right
                || providerLocation[1] + provider.Height < bottom)
            {
                return false;
            }

            if (_isBottomBar
                && _navigationView.Background is global::Google.Android.Material.Shape.MaterialShapeDrawable shape
                && shape.Alpha == 255 && shape.FillColor is { } fill
                && _navigationView.BackgroundTintList is { } tint)
            {
                int fillColor = fill.GetColorForState(_navigationView.GetDrawableState(),
                    new global::Android.Graphics.Color(fill.DefaultColor));
                if (((uint)fillColor >> 24) != 255)
                {
                    return false;
                }
                color = tint.GetColorForState(_navigationView.GetDrawableState(),
                    new global::Android.Graphics.Color(tint.DefaultColor));
            }
            else if (!_isBottomBar
                && _overlayLayout.Background is global::Android.Graphics.Drawables.ColorDrawable background)
            {
                color = background.Color.ToArgb();
            }
            else
            {
                return false;
            }
            return ((uint)color >> 24) == 255;
        }

        private void UpdateThemeWindowBackground(global::Android.Views.Window window, bool forcedEdgeToEdge)
        {
            if (_themeBackgroundWindow is not null && !ReferenceEquals(_themeBackgroundWindow, window))
            {
                RestoreThemeWindowBackground();
            }
            if (ReferenceEquals(_themeBackgroundHostChangedWindow, window))
            {
                return;
            }
            var owned = _ownedThemeBackground;
            if (owned is not null
                && (window.DecorView.Background is not global::Android.Graphics.Drawables.ColorDrawable current
                    || !current.Equals(owned) || current.Color.ToArgb() != _lastThemeBackgroundColor))
            {
                // A later host background wins, including edits to the same Drawable.
                _themeBackgroundHostChangedWindow = window;
                RestoreThemeWindowBackground();
                return;
            }
            if (!forcedEdgeToEdge
                || !RootCoversWindow(window)
                || window.DecorView.Background is not global::Android.Graphics.Drawables.ColorDrawable original
                || original.Alpha != 255 || original.Color.A != 255)
            {
                RestoreThemeWindowBackground();
                return;
            }

            using var style = _activity!.ObtainStyledAttributes(
                new[] { global::Android.Resource.Attribute.WindowBackground });
            // Read the current native theme; never mutate or dispose its shared Drawable.
            if (style.GetDrawable(0) is not global::Android.Graphics.Drawables.ColorDrawable theme
                || theme.Alpha != 255 || theme.Color.A != 255)
            {
                RestoreThemeWindowBackground();
                return;
            }
            int color = theme.Color.ToArgb();
            if (original.Color.ToArgb() == color)
            {
                return;
            }

            // Android 16 derives its edge-to-edge navigation policy from the Window
            // background. A fresh Drawable updates that policy through the public API;
            // changing the color of the existing Drawable would skip that update.
            var replacement = new global::Android.Graphics.Drawables.ColorDrawable(
                new global::Android.Graphics.Color(color));
            window.SetBackgroundDrawable(replacement);
            if (window.DecorView.Background is not global::Android.Graphics.Drawables.ColorDrawable applied
                || !applied.Equals(replacement))
            {
                // A composite background is not evidence of the Window's original
                // Drawable. Do not take ownership or overwrite it on a later callback.
                _themeBackgroundHostChangedWindow = window;
                RestoreThemeWindowBackground();
                return;
            }
            _themeBackgroundWindow ??= window;
            _previousThemeBackground ??= original;
            _ownedThemeBackground = replacement;
            _lastThemeBackgroundColor = color;
            // Only our replaced instance is released, after the Window stopped using it.
            owned?.Dispose();
        }

        private bool RootCoversWindow(global::Android.Views.Window window)
        {
            var decor = window.DecorView;
            if (!_overlayLayout.IsAttachedToWindow || _overlayLayout.Visibility != ViewStates.Visible
                || _overlayLayout.Alpha != 1f || decor.Visibility != ViewStates.Visible || decor.Alpha != 1f
                || _overlayLayout.Background is not global::Android.Graphics.Drawables.ColorDrawable root
                || root.Alpha != 255 || root.Color.A != 255
                || decor.Width <= 0 || decor.Height <= 0
                || _overlayLayout.Width != decor.Width || _overlayLayout.Height != decor.Height)
            {
                return false;
            }
            var decorLocation = new int[2];
            var rootLocation = new int[2];
            decor.GetLocationOnScreen(decorLocation);
            _overlayLayout.GetLocationOnScreen(rootLocation);
            return rootLocation[0] == decorLocation[0] && rootLocation[1] == decorLocation[1];
        }

        private void RestoreThemeWindowBackground()
        {
            if (_themeBackgroundWindow is { } window && _ownedThemeBackground is { } owned)
            {
                var current = window.DecorView.Background;
                if (ReferenceEquals(_activity?.Window, window)
                    && current is global::Android.Graphics.Drawables.ColorDrawable color
                    && color.Equals(owned) && color.Color.ToArgb() == _lastThemeBackgroundColor
                    && _previousThemeBackground is { } previous)
                {
                    window.SetBackgroundDrawable(previous);
                    current = window.DecorView.Background;
                }
                // Never dispose a host/theme Drawable, or our instance while the host
                // still references it (for example after editing its color in place).
                if (current is null || !current.Equals(owned))
                {
                    owned.Dispose();
                }
            }
            _themeBackgroundWindow = null;
            _previousThemeBackground = null;
            _ownedThemeBackground = null;
        }

        private void RestoreNavigationBarAppearance()
        {
            RestoreThemeWindowBackground();
            if (_navigationAppearanceWindow is { } window
                && ReferenceEquals(_activity?.Window, window))
            {
                var controller = WindowCompat.GetInsetsController(window, window.DecorView);
                if (_previousLightNavigationBars is bool light
                    && controller is not null
                    && controller.AppearanceLightNavigationBars == _lastLightNavigationBars)
                {
                    controller.AppearanceLightNavigationBars = light;
                }
                if (_previousNavigationBarContrastEnforced is bool contrast
                    && OperatingSystem.IsAndroidVersionAtLeast(29)
                    && !window.NavigationBarContrastEnforced)
                {
                    window.NavigationBarContrastEnforced = contrast;
                }
            }
            _navigationAppearanceWindow = null;
            _previousLightNavigationBars = null;
            _previousNavigationBarContrastEnforced = null;
        }

        private static AndroidX.Activity.ComponentActivity? FindActivity(Context context)
        {
            while (context is ContextWrapper wrapper)
            {
                if (context is AndroidX.Activity.ComponentActivity activity)
                {
                    return activity;
                }

                if (wrapper.BaseContext is not { } baseContext
                    || ReferenceEquals(baseContext, context))
                {
                    break;
                }

                context = baseContext;
            }

            return context as AndroidX.Activity.ComponentActivity;
        }

        private static bool IsNightMode(Context context) =>
            (context.Resources!.Configuration!.UiMode
                & global::Android.Content.Res.UiMode.NightMask)
            == global::Android.Content.Res.UiMode.NightYes;

        private sealed class ConfigurationChangedListener
            : Java.Lang.Object, AndroidX.Core.Util.IConsumer
        {
            readonly AShellView _owner;

            public ConfigurationChangedListener(AShellView owner)
            {
                _owner = owner;
            }

            public void Accept(Java.Lang.Object? value)
            {
                if (_owner._disposed)
                {
                    return;
                }

                // ComponentActivity 先通知 listener,AppCompat 随后才更新主题。
                // 等本次配置回调完成后再读取颜色,且不重建导航/页面以保留当前状态。
                _owner._linearLayout.Post(() =>
                {
                    if (_owner._disposed)
                    {
                        return;
                    }

                    bool isNight = IsNightMode(_owner._context);
                    if (_owner._isNight != isNight)
                    {
                        _owner._isNight = isNight;
                        _owner.UpdateColors();
                    }
                });
            }
        }

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
                _owner._activity?.OnBackPressedDispatcher.OnBackPressed();
            }
        }

        // 抽屉容器色:与 rail 同为一体,取 surfaceContainer(M3 rail 容器色),
        // 依次退回 surface / colorBackground
        private static int ResolveDrawerColor(Context context)
        {
            var typed = new TypedValue();
            int background = global::Android.Graphics.Color.Transparent;
            if (context.Theme!.ResolveAttribute(
                global::Android.Resource.Attribute.ColorBackground, typed, true))
            {
                background = ResolveColorValue(context, typed);
            }

            return ResolveThemeColor(context, background,
                "colorSurfaceContainer", "colorSurface");
        }

        private static int ResolveThemeColor(Context context, int fallback, params string[] names)
        {
            var typed = new TypedValue();
            foreach (var name in names)
            {
                int attrId = context.Resources!.GetIdentifier(
                    name, "attr", context.PackageName);
                if (attrId != 0
                    && context.Theme!.ResolveAttribute(attrId, typed, true)
                    && typed.Type != 0)
                {
                    return ResolveColorValue(context, typed);
                }
            }

            return fallback;
        }

        private static int ResolveColorValue(Context context, TypedValue value) =>
            value.ResourceId != 0
                ? global::AndroidX.Core.Content.ContextCompat.GetColor(context, value.ResourceId)
                : value.Data;

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
                _owner._overlayLayout.Post(_owner.UpdateNavigationBarAppearance);

                return insets;
            }
        }

        private void OnRootLayoutChange(object? sender, global::Android.Views.View.LayoutChangeEventArgs e)
        {
            _overlayLayout.Post(UpdateNavigationBarAppearance);
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
                row.ContentDescription = child.AutomationId ?? child.Title;
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetPadding(Dp(16), 0, Dp(16), 0);

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
                ApplyDrawerRowColors(row, ResolveOnSurfaceColor());

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
                Content = new Microsoft.Maui.Controls.ScrollView
                {
                    AutomationId = $"landing-{group.AutomationId ?? group.Title}-body",
                    Content = layout,
                },
            };
        }

        private Microsoft.Maui.Controls.View CreateLandingRow(AShellContent child)
        {
            var row = new HorizontalStackLayout
            {
                Spacing = 14,
                Padding = new Thickness(4, 12),
                // 落地页行与子页条目区分定位,加 landing- 前缀
                AutomationId = $"landing-{child.AutomationId ?? child.Title}",
            };

            var title = new Label
            {
                AutomationId = $"{row.AutomationId}-title",
                Text = child.Title,
                FontSize = 17,
                VerticalOptions = LayoutOptions.Center,
            };

            if (child.Icon is not null)
            {
                var icon = new Image
                {
                    AutomationId = $"{row.AutomationId}-icon",
                    Source = child.Icon,
                    WidthRequest = 24,
                    HeightRequest = 24,
                    VerticalOptions = LayoutOptions.Center,
                };
                AutomationProperties.SetIsInAccessibleTree(icon, true);
                icon.Behaviors.Add(new DefaultLandingIconTint(title));
                row.Add(icon);
            }

            row.Add(title);

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
            if (_disposed)
            {
                return;
            }

            StopNavigationAppearanceDiagnostics();
            _disposed = true;
            _virtualView.PropertyChanged -= OnRootBackgroundChanged;
            _overlayLayout.ViewAttachedToWindow -= OnRootAttachedToWindow;
            _overlayLayout.ViewDetachedFromWindow -= OnRootDetachedFromWindow;
            RestoreStatusBarAppearance();
            RestoreNavigationBarAppearance();
            if (_configListener is not null)
            {
                _activity?.RemoveOnConfigurationChangedListener(_configListener);
                _configListener.Dispose();
                _configListener = null;
            }

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

            // 显式分配 itemId(Menu.Add(title) 默认全为 0):
            // 选中态同步 SelectedItemId 与无障碍注入都依赖稳定 id
            int index = 0;
            foreach (var item in _virtualView.Items)
            {
                if (item is AShellContent content)
                {
                    AddMenuItem(content, index++);
                }
                else if (item is AShellGroup group)
                {
                    var menuItem = _navigationView.Menu.Add(0, index + 1, index, group.Title);
                    index++;
                    ApplyMenuItemAccessibility(menuItem, group);
                    _groupToMenuItemMap[group] = menuItem;
                    LoadIconAsync(group, menuItem);
                }
            }
        }

        // 显式设置了 AutomationId 时用它作 content-desc(无障碍/测试定位),
        // 否则保持 Title 作朗读标签
        private static void ApplyMenuItemAccessibility(IMenuItem menuItem, AShellItem item)
        {
            menuItem.SetContentDescription(new Java.Lang.String(item.AutomationId ?? item.Title));
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

        private void AddMenuItem(AShellContent content, int index)
        {
            var menuItem = _navigationView.Menu.Add(0, index + 1, index, content.Title);
            ApplyMenuItemAccessibility(menuItem, content);
            _contentToMenuItemMap[content] = menuItem;
            LoadIconAsync(content, menuItem);
        }

        public void UpdateColors()
        {
            int onSurface = ResolveOnSurfaceColor();
            int container = ResolveDrawerColor(_context);

            // 原生控件缓存构造时的色值,配置变化后显式刷新;页面内容由 MAUI
            // 的 AppThemeBinding 处理,这里不重建也不改动页面/导航状态。
            _navigationView.BackgroundTintList = ColorStateList.ValueOf(
                new global::Android.Graphics.Color(container));
            _toolbar.BackgroundTintList = ColorStateList.ValueOf(
                new global::Android.Graphics.Color(
                    ResolveThemeColor(_context, container, "colorSurface")));
            _toolbar.SetTitleTextColor(new global::Android.Graphics.Color(onSurface));
            _toolbar.SetNavigationIconTint(onSurface);

            if (_drawerPanel.Background is global::Android.Graphics.Drawables.GradientDrawable drawerBackground)
            {
                drawerBackground.SetColor(container);
            }

            if (_navHeaderFrameLayout?.GetChildAt(0) is ImageButton menuButton)
            {
                menuButton.ImageTintList = ColorStateList.ValueOf(
                    new global::Android.Graphics.Color(onSurface));
            }

            if (_drawerPanel.GetChildAt(0) is LinearLayout drawerList)
            {
                for (int i = 0; i < drawerList.ChildCount; i++)
                {
                    if (drawerList.GetChildAt(i) is LinearLayout row)
                    {
                        ApplyDrawerRowColors(row, onSurface);
                    }
                }
            }

            var selected = _virtualView.GetEffectiveSelectedItemColor(_isNight);
            var unselected = _virtualView.GetEffectiveUnselectedItemColor();
            int selectedInt = selected?.ToPlatform()
                ?? ResolveThemeColor(_context, onSurface,
                    "colorOnSecondaryContainer", "colorPrimary");
            int uncheckedInt = unselected?.ToPlatform()
                ?? ResolveThemeColor(_context, onSurface, "colorOnSurfaceVariant");

            var states = new[]
            {
                new[] { global::Android.Resource.Attribute.StateChecked },
                new[] { -global::Android.Resource.Attribute.StateChecked },
            };
            var tintList = new ColorStateList(states, new[] { selectedInt, uncheckedInt });

            _navigationView.ItemIconTintList = tintList;
            _navigationView.ItemTextColor = tintList;
            _navigationView.ItemActiveIndicatorColor =
                ColorStateList.ValueOf(new global::Android.Graphics.Color(
                    selected?.WithAlpha(0.2f).ToPlatform()
                        ?? ResolveThemeColor(_context, container, "colorSecondaryContainer")));
            _navigationView.ItemRippleColor = ColorStateList.ValueOf(
                new global::Android.Graphics.Color(
                    ResolveThemeColor(_context, (onSurface & 0x00ffffff) | 0x1f000000,
                        "colorControlHighlight")));
            UpdateStatusBarAppearance();
            UpdateNavigationBarAppearance();
        }

        private int ResolveOnSurfaceColor() =>
            ResolveThemeColor(_context,
                _isNight ? global::Android.Graphics.Color.White : global::Android.Graphics.Color.Black,
                "colorOnSurface");

        private void ApplyDrawerRowColors(LinearLayout row, int onSurface)
        {
            var ripple = new TypedValue();
            _context.Theme!.ResolveAttribute(
                global::Android.Resource.Attribute.SelectableItemBackground, ripple, true);
            if (ripple.ResourceId != 0)
            {
                row.SetBackgroundResource(ripple.ResourceId);
            }

            for (int i = 0; i < row.ChildCount; i++)
            {
                switch (row.GetChildAt(i))
                {
                    case TextView label:
                        label.SetTextColor(new global::Android.Graphics.Color(onSurface));
                        break;
                    case ImageView icon:
                        icon.ImageTintList = ColorStateList.ValueOf(
                            new global::Android.Graphics.Color(onSurface));
                        break;
                }
            }
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
                _toolbar.NavigationContentDescription = "Back";
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
