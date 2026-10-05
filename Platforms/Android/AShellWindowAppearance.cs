using AdaptiveShell.Controls;
using Android.Content;
using Android.Views;
using AndroidX.Core.Graphics;
using AndroidX.Core.View;
using System;
using System.ComponentModel;

namespace AdaptiveShell.Platforms.Android
{
    public partial class AShellView
    {
        // Owns system bar flags and the temporary Android 16 Window background.
        // It restores only values it still owns when the view detaches or disconnects.
        private sealed class AShellWindowAppearance : IDisposable
        {
            readonly AShellView _owner;

            public AShellWindowAppearance(AShellView owner)
            {
                _owner = owner;
                owner._overlayLayout.ViewAttachedToWindow += OnRootAttachedToWindow;
                owner._overlayLayout.ViewDetachedFromWindow += OnRootDetachedFromWindow;
                owner._virtualView.PropertyChanged += OnRootBackgroundChanged;
            }

            public void Dispose()
            {
                _owner._virtualView.PropertyChanged -= OnRootBackgroundChanged;
                _owner._overlayLayout.ViewAttachedToWindow -= OnRootAttachedToWindow;
                _owner._overlayLayout.ViewDetachedFromWindow -= OnRootDetachedFromWindow;
                RestoreStatusBarAppearance();
                RestoreNavigationBarAppearance();
            }

            bool? _previousLightStatusBars;
            bool _lastLightStatusBars;
            global::Android.Views.Window? _statusAppearanceWindow;

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
                // WindowHandler initializes system bars before content is attached. Posting also
                // lets MAUI finish mapping the Page background used behind the top inset.
                _owner._overlayLayout.Post(UpdateStatusBarAppearance);
                _owner._overlayLayout.Post(UpdateNavigationBarAppearance);
            }

            private void OnRootDetachedFromWindow(object? sender,
                global::Android.Views.View.ViewDetachedFromWindowEventArgs e)
            {
                RestoreStatusBarAppearance();
                RestoreNavigationBarAppearance();
            }

            private void OnRootBackgroundChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(AShell.BackgroundColor)
                    || e.PropertyName == nameof(AShell.Background))
                {
                    _owner._overlayLayout.Post(UpdateStatusBarAppearance);
                    _owner._overlayLayout.Post(UpdateNavigationBarAppearance);
                }
            }

            public void UpdateStatusBarAppearance()
            {
                if (_owner._disposed || !_owner._overlayLayout.IsAttachedToWindow
                    || !OperatingSystem.IsAndroidVersionAtLeast(23)
                    || _owner._activity?.Window is not { } window)
                {
                    return;
                }
                if (_statusAppearanceWindow is not null
                    && !ReferenceEquals(_statusAppearanceWindow, window))
                    RestoreStatusBarAppearance();

                // Target 35+ is edge-to-edge unless its actual window theme opts out.
                // Android 16 / target 36 also disables that opt-out. Earlier transparent
                // windows are supported without changing legacy opaque purple bars.
                int targetSdk = (int)(_owner._context.ApplicationInfo?.TargetSdkVersion ?? 0);
                bool forcedTransparent = OperatingSystem.IsAndroidVersionAtLeast(36) && targetSdk >= 36;
    #pragma warning disable CA1422 // The opt-out still applies on Android 15 / target 35.
                if (!forcedTransparent && OperatingSystem.IsAndroidVersionAtLeast(35) && targetSdk >= 35)
                {
                    using var windowStyle = _owner._activity.ObtainStyledAttributes(
                        new[] { global::Android.Resource.Attribute.WindowOptOutEdgeToEdgeEnforcement });
                    forcedTransparent = !windowStyle.GetBoolean(0, false);
                }
    #pragma warning restore CA1422
    #pragma warning disable CA1422 // Reading the legacy color is needed only when it is still honored.
                bool transparent = forcedTransparent || ((uint)window.StatusBarColor >> 24) == 0;
    #pragma warning restore CA1422
                if (!transparent
                    || _owner._overlayLayout.Background is not global::Android.Graphics.Drawables.ColorDrawable background
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
                _statusAppearanceWindow ??= window;
                controller.AppearanceLightStatusBars = lightBackground;
                _lastLightStatusBars = lightBackground;
            }

            public void RestoreStatusBarAppearance()
            {
                if (_previousLightStatusBars is bool previous
                    && _statusAppearanceWindow is { } window
                    && ReferenceEquals(_owner._activity?.Window, window))
                {
                    var controller = WindowCompat.GetInsetsController(window, window.DecorView);
                    // Do not overwrite a later appearance change made by the host or another page.
                    if (controller is not null && controller.AppearanceLightStatusBars == _lastLightStatusBars)
                    {
                        controller.AppearanceLightStatusBars = previous;
                    }
                }
                _previousLightStatusBars = null;
                _statusAppearanceWindow = null;
            }

            public void UpdateNavigationBarAppearance()
            {
                if (_owner._disposed || !_owner._overlayLayout.IsAttachedToWindow
                    || !OperatingSystem.IsAndroidVersionAtLeast(26)
                    || _owner._activity?.Window is not { } window)
                {
                    return;
                }

                if (_navigationAppearanceWindow is not null
                    && !ReferenceEquals(_navigationAppearanceWindow, window))
                {
                    // Ownership cannot transfer to a different host window.
                    RestoreNavigationBarAppearance();
                }

                int targetSdk = (int)(_owner._context.ApplicationInfo?.TargetSdkVersion ?? 0);
                bool edgeToEdge = OperatingSystem.IsAndroidVersionAtLeast(36) && targetSdk >= 36;
    #pragma warning disable CA1422 // The actual Android 15 theme may still opt out.
                if (!edgeToEdge && OperatingSystem.IsAndroidVersionAtLeast(35) && targetSdk >= 35)
                {
                    using var style = _owner._activity.ObtainStyledAttributes(
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
                if (navigation is null || navigation.Bottom <= 0
                    || navigation.Left != 0 || navigation.Right != 0)
                {
                    return false;
                }

                global::Android.Views.View provider = _owner._isBottomBar ? _owner._navigationView : _owner._overlayLayout;
                if (provider.Visibility != ViewStates.Visible || provider.Alpha != 1f
                    || _owner._overlayLayout.Alpha != 1f || _owner._linearLayout.Alpha != 1f
                    || _owner._drawerPanel.Visibility == ViewStates.Visible
                    || _owner._drawerScrim.Visibility == ViewStates.Visible)
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

                if (_owner._isBottomBar
                    && _owner._navigationView.Background is global::Google.Android.Material.Shape.MaterialShapeDrawable shape
                    && shape.Alpha == 255 && shape.FillColor is { } fill
                    && _owner._navigationView.BackgroundTintList is { } tint)
                {
                    int fillColor = fill.GetColorForState(_owner._navigationView.GetDrawableState(),
                        new global::Android.Graphics.Color(fill.DefaultColor));
                    if (((uint)fillColor >> 24) != 255)
                    {
                        return false;
                    }
                    color = tint.GetColorForState(_owner._navigationView.GetDrawableState(),
                        new global::Android.Graphics.Color(tint.DefaultColor));
                }
                else if (!_owner._isBottomBar
                    && _owner._overlayLayout.Background is global::Android.Graphics.Drawables.ColorDrawable background)
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

                using var style = _owner._activity!.ObtainStyledAttributes(
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
                if (!_owner._overlayLayout.IsAttachedToWindow || _owner._overlayLayout.Visibility != ViewStates.Visible
                    || _owner._overlayLayout.Alpha != 1f || decor.Visibility != ViewStates.Visible || decor.Alpha != 1f
                    || _owner._overlayLayout.Background is not global::Android.Graphics.Drawables.ColorDrawable root
                    || root.Alpha != 255 || root.Color.A != 255
                    || decor.Width <= 0 || decor.Height <= 0
                    || _owner._overlayLayout.Width != decor.Width || _owner._overlayLayout.Height != decor.Height)
                {
                    return false;
                }
                var decorLocation = new int[2];
                var rootLocation = new int[2];
                decor.GetLocationOnScreen(decorLocation);
                _owner._overlayLayout.GetLocationOnScreen(rootLocation);
                return rootLocation[0] == decorLocation[0] && rootLocation[1] == decorLocation[1];
            }

            private void RestoreThemeWindowBackground()
            {
                if (_themeBackgroundWindow is { } window && _ownedThemeBackground is { } owned)
                {
                    var current = window.DecorView.Background;
                    if (ReferenceEquals(_owner._activity?.Window, window)
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

            public void RestoreNavigationBarAppearance()
            {
                RestoreThemeWindowBackground();
                if (_navigationAppearanceWindow is { } window
                    && ReferenceEquals(_owner._activity?.Window, window))
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

        }
    }
}
