using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using AndroidX.Core.View;
using Google.Android.Material.Shape;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using AView = global::Android.Views.View;

namespace AdaptiveShell.Platforms.Android;

public partial class AShellView
{
    // Diagnostic-only APK metadata, absent in normal builds. No appearance setters here.
    const string NavigationDiagnosticFlag = "AShellNavigationAppearanceDiagnostic";
    const string NavigationDiagnosticSource = "AShellNavigationAppearanceDiagnosticSource";
    const string NavigationDiagnosticFile = "ashell-navigation-appearance.jsonl";
    string? _navigationDiagnosticSource;
    bool _navigationDiagnosticChecked;
    string? _navigationDiagnosticLastState;
    int _navigationDiagnosticSequence;
    bool _navigationDiagnosticPendingDraw = true;
    NavigationDiagnosticPreDraw? _navigationDiagnosticListener;
    ViewTreeObserver? _navigationDiagnosticObserver;

    private void StartNavigationAppearanceDiagnostics()
    {
        if (_disposed)
            return;
        if (!_navigationDiagnosticChecked)
        {
            _navigationDiagnosticChecked = true;
            if ((int)Build.VERSION.SdkInt != 36
                || _context.PackageName != "com.companyname.exampleashellapp"
                || (_context.ApplicationInfo!.Flags & ApplicationInfoFlags.Debuggable) == 0)
                return;
            try
            {
                using var info = _context.PackageManager!.GetApplicationInfo(
                    _context.PackageName, PackageInfoFlags.MetaData);
                var metadata = info.MetaData;
                string? source = metadata?.GetString(NavigationDiagnosticSource);
                if (metadata?.GetBoolean(NavigationDiagnosticFlag, false) != true
                    || source is null || source.Length != 40)
                    return;
                foreach (char c in source)
                    if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))
                        return;
                _navigationDiagnosticSource = source;
            }
            catch { return; } // A missing metadata read must not affect normal navigation.
        }
        if (_navigationDiagnosticSource is null || _navigationDiagnosticListener is not null)
            return;
        try
        {
            _navigationDiagnosticObserver = _overlayLayout.ViewTreeObserver;
            if (_navigationDiagnosticObserver?.IsAlive != true)
                return;
            _navigationDiagnosticListener = new NavigationDiagnosticPreDraw(this);
            _navigationDiagnosticObserver.AddOnPreDrawListener(_navigationDiagnosticListener);
            RecordNavigationAppearanceDiagnostic("attached", force: true);
        }
        catch (Exception ex)
        {
            try { WriteNavigationDiagnosticRecord("attached", "read_error", null, ex.ToString()); }
            catch { }
        }
    }

    private void StopNavigationAppearanceDiagnostics()
    {
        if (_navigationDiagnosticListener is { } listener)
        {
            listener.ClearOwner();
            try
            {
                if (_navigationDiagnosticObserver?.IsAlive == true)
                    _navigationDiagnosticObserver.RemoveOnPreDrawListener(listener);
            }
            catch (Exception ex)
            {
                try { WriteNavigationDiagnosticRecord("detach-listener", "read_error", null, ex.ToString()); }
                catch { }
            }
            finally { listener.Dispose(); }
        }
        _navigationDiagnosticListener = null;
        _navigationDiagnosticObserver = null;
    }

    private void RecordNavigationAppearanceDiagnostic(string phase, bool force = false)
    {
        if (_disposed || _navigationDiagnosticSource is null || _navigationDiagnosticSequence >= 4096)
            return;
        try
        {
            if (_navigationDiagnosticSequence == 4095)
            {
                WriteNavigationDiagnosticRecord(phase, "log_limit", null, "Diagnostic record bound reached; capture is incomplete.");
                return;
            }
            if (phase == "after-update") _navigationDiagnosticPendingDraw = true;
            if (phase == "pre-draw" && _navigationDiagnosticPendingDraw)
            {
                force = true;
                _navigationDiagnosticPendingDraw = false;
            }
            using var stateStream = new MemoryStream();
            using (var state = new Utf8JsonWriter(stateStream))
            {
                WriteNavigationDiagnosticState(state);
            }
            byte[] stateBytes = stateStream.ToArray();
            string stateText = Encoding.UTF8.GetString(stateBytes);
            if (!force && stateText == _navigationDiagnosticLastState)
                return;
            _navigationDiagnosticLastState = stateText;
            WriteNavigationDiagnosticRecord(phase, "observed", stateBytes, null);
        }
        catch (Exception ex)
        {
            // Preserve a diagnostic read failure without changing the original UI operation.
            try { WriteNavigationDiagnosticRecord(phase, "read_error", null, ex.ToString()); }
            catch { /* The external collector must reject a missing or incomplete log. */ }
        }
    }

    private void WriteNavigationDiagnosticRecord(string phase, string status, byte[]? state, string? error)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteNumber("sequence", ++_navigationDiagnosticSequence);
            writer.WriteString("utc", DateTimeOffset.UtcNow.ToString("O"));
            writer.WriteNumber("uptime_ms", SystemClock.UptimeMillis());
            writer.WriteNumber("pid", global::Android.OS.Process.MyPid());
            writer.WriteString("source", _navigationDiagnosticSource);
            writer.WriteString("bundle", _context.PackageName);
            writer.WriteString("phase", phase);
            writer.WriteString("status", status);
            if (state is not null)
            {
                writer.WritePropertyName("state");
                writer.WriteRawValue(state);
            }
            if (error is not null) writer.WriteString("error", error);
            writer.WriteEndObject();
        }
        string path = Path.Combine(_context.FilesDir!.AbsolutePath, NavigationDiagnosticFile);
        File.AppendAllText(path, Encoding.UTF8.GetString(stream.ToArray()) + "\n", new UTF8Encoding(false));
    }

    private void WriteNavigationDiagnosticState(Utf8JsonWriter writer)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(36))
            throw new InvalidOperationException("Navigation diagnostic is restricted to API36.");
        writer.WriteStartObject();
        writer.WriteNumber("sdk", (int)Build.VERSION.SdkInt);
        writer.WriteNumber("target_sdk", (int)_context.ApplicationInfo!.TargetSdkVersion);
        writer.WriteNumber("ui_mode", (int)_context.Resources!.Configuration!.UiMode);
        writer.WriteString("current_item", _virtualView.CurrentItem?.AutomationId);
        writer.WriteBoolean("compact", _isBottomBar);
        writer.WriteBoolean("attached", _overlayLayout.IsAttachedToWindow);
        writer.WriteBoolean("window_focused", _overlayLayout.HasWindowFocus);
        var window = _activity?.Window;
        if (window is not null)
        {
            var controller = window.InsetsController;
            if (controller is not null)
            {
                // Android's public getter reports requested appearance, not SystemUI's
                // implied final appearance. Raw bit 512 is recorded, never written:
                // AOSP android-16.0.0_r1 WindowInsetsController FORCE_LIGHT_NAVIGATION_BARS.
                int requested = controller.SystemBarsAppearance;
                writer.WriteNumber("requested_appearance", requested);
                writer.WriteBoolean("requested_regular_light_navigation", (requested & 16) != 0);
                writer.WriteBoolean("requested_system_force_light_navigation", (requested & 512) != 0);
            }
            writer.WriteBoolean("navigation_contrast_enforced", window.NavigationBarContrastEnforced);
#pragma warning disable CA1422
            writer.WriteNumber("navigation_color_getter", window.NavigationBarColor);
            writer.WriteNumber("legacy_system_ui_visibility", (int)window.DecorView.SystemUiFlags);
#pragma warning restore CA1422
            WriteNavigationDiagnosticView(writer, "decor", window.DecorView);
            // This is the public theme-resolved WindowBackground drawable, distinct
            // from the current DecorView.Background and any private original drawable.
            using (var theme = _activity!.ObtainStyledAttributes(
                new[] { global::Android.Resource.Attribute.WindowBackground }))
            {
                using var drawable = theme.GetDrawable(0);
                writer.WritePropertyName("theme_resolved_window_background");
                WriteNavigationDiagnosticDrawable(writer, drawable, null, 0);
            }
            var insets = ViewCompat.GetRootWindowInsets(window.DecorView);
            writer.WritePropertyName("navigation_insets");
            if (insets is null) writer.WriteNullValue();
            else
            {
                var nav = insets.GetInsets(WindowInsetsCompat.Type.NavigationBars())
                    ?? throw new InvalidOperationException("Native navigation inset getter returned null.");
                writer.WriteStartObject();
                writer.WriteBoolean("visible", insets.IsVisible(WindowInsetsCompat.Type.NavigationBars()));
                writer.WriteNumber("left", nav.Left); writer.WriteNumber("top", nav.Top);
                writer.WriteNumber("right", nav.Right); writer.WriteNumber("bottom", nav.Bottom);
                writer.WriteEndObject();
            }
            bool known = TryGetNavigationInsetColor(window, out int color);
            writer.WriteBoolean("opaque_provider_covers_navigation_inset", known);
            if (known)
            {
                writer.WriteNumber("provider_argb", color);
                writer.WriteNumber("provider_luminance", AndroidX.Core.Graphics.ColorUtils.CalculateLuminance(color));
            }
        }
        WriteNavigationDiagnosticView(writer, "root", _overlayLayout);
        WriteNavigationDiagnosticView(writer, "linear", _linearLayout);
        WriteNavigationDiagnosticView(writer, "provider", _isBottomBar ? _navigationView : _overlayLayout);
        WriteNavigationDiagnosticView(writer, "drawer", _drawerPanel);
        WriteNavigationDiagnosticView(writer, "scrim", _drawerScrim);
        writer.WritePropertyName("ownership");
        writer.WriteStartObject();
        writer.WriteBoolean("same_window", _navigationAppearanceWindow is not null && ReferenceEquals(window, _navigationAppearanceWindow));
        if (_previousLightNavigationBars is bool previous) writer.WriteBoolean("previous_regular_light", previous);
        else writer.WriteNull("previous_regular_light");
        writer.WriteBoolean("last_regular_light", _lastLightNavigationBars);
        if (_previousNavigationBarContrastEnforced is bool contrast) writer.WriteBoolean("previous_contrast", contrast);
        else writer.WriteNull("previous_contrast");
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteNavigationDiagnosticView(Utf8JsonWriter writer, string name, AView? view)
    {
        writer.WritePropertyName(name);
        if (view is null) { writer.WriteNullValue(); return; }
        var location = new int[2];
        view.GetLocationOnScreen(location);
        writer.WriteStartObject();
        writer.WriteString("type", view.GetType().FullName);
        writer.WriteNumber("id", view.Id);
        writer.WriteNumber("x", location[0]); writer.WriteNumber("y", location[1]);
        writer.WriteNumber("width", view.Width); writer.WriteNumber("height", view.Height);
        writer.WriteNumber("visibility", (int)view.Visibility);
        writer.WriteNumber("alpha", view.Alpha);
        writer.WriteBoolean("attached", view.IsAttachedToWindow);
        writer.WritePropertyName("background");
        WriteNavigationDiagnosticDrawable(writer, view.Background, view.GetDrawableState(), 0);
        if (view.BackgroundTintList is { } tint)
            writer.WriteNumber("background_tint_argb", tint.GetColorForState(view.GetDrawableState(), new global::Android.Graphics.Color(tint.DefaultColor)));
        writer.WriteEndObject();
    }

    private static void WriteNavigationDiagnosticDrawable(Utf8JsonWriter writer, Drawable? drawable, int[]? state, int depth)
    {
        if (drawable is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString("type", drawable.GetType().FullName);
        writer.WriteNumber("alpha", drawable.Alpha);
        if (drawable is ColorDrawable color) writer.WriteNumber("color_argb", color.Color.ToArgb());
        if (drawable is MaterialShapeDrawable shape && shape.FillColor is { } fill)
            writer.WriteNumber("fill_argb", fill.GetColorForState(state, new global::Android.Graphics.Color(fill.DefaultColor)));
        if (depth < 4 && drawable is InsetDrawable inset)
        {
            writer.WritePropertyName("inset_child");
            WriteNavigationDiagnosticDrawable(writer, inset.Drawable, state, depth + 1);
        }
        if (depth < 4 && drawable is LayerDrawable layer)
        {
            writer.WritePropertyName("layers"); writer.WriteStartArray();
            for (int i = 0; i < layer.NumberOfLayers; i++)
                WriteNavigationDiagnosticDrawable(writer, layer.GetDrawable(i), state, depth + 1);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private sealed class NavigationDiagnosticPreDraw : Java.Lang.Object, ViewTreeObserver.IOnPreDrawListener
    {
        AShellView? _owner;
        public NavigationDiagnosticPreDraw(AShellView owner) => _owner = owner;
        public void ClearOwner() => _owner = null;
        public bool OnPreDraw()
        {
            _owner?.RecordNavigationAppearanceDiagnostic("pre-draw");
            return true;
        }
    }
}
