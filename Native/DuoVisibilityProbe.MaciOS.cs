#if IOS
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using AdaptiveShell.Controls;
using CoreGraphics;
using Foundation;
using UIKit;

namespace AdaptiveShell.Platforms.MacIOS;

// Diagnostic branch only. The ordinary app has no opt-in keys and creates no timer/log.
internal static class DuoVisibilityProbe
{
    internal const string FlagKey = "AShellDuoVisibilityDiagnostic";
    internal const string SourceKey = "AShellDuoVisibilityDiagnosticSource";
    internal const string LogName = "ashell-duo-visibility.jsonl";
    static readonly bool _enabled = IsEnabled();
    static readonly Dictionary<string, string> _lastByPhase = new();
    static readonly Dictionary<string, (long Sequence, string Hash)> _lastFullByPhase = new();
    const long MaximumLogBytes = 8 * 1024 * 1024;
    const long MaximumLogRecords = 4096;
    static long _sequence;
    static bool _loggingStopped;
    internal static bool Enabled => _enabled && !_loggingStopped;

    static bool IsEnabled()
    {
        var info = NSBundle.MainBundle.InfoDictionary;
        var source = info?[SourceKey]?.ToString();
        return info?[FlagKey] is NSNumber flag && flag.BoolValue
            && source is { Length: 40 } && source.All(Uri.IsHexDigit)
            && NSBundle.MainBundle.BundleIdentifier == "com.companyname.exampleashellapp"
            && Environment.GetEnvironmentVariable("SIMULATOR_MODEL_IDENTIFIER") == "iPhone19,4"
            && UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Phone
            && Version.TryParse(UIDevice.CurrentDevice.SystemVersion, out var version)
            && version.Major == 27 && version.Minor == 1;
    }

    internal static void Record(AShellViewBase owner, string phase, long providerCall,
        AShellContent? content, UIViewController? parentBefore, UIViewController? returned,
        IEnumerable<(AShellContent Item, UIViewController Container, UIViewController Page, UIEdgeInsets Insets)> pages,
        bool force = false)
    {
        if (!Enabled) return;
        var stateReadStarted = DateTimeOffset.UtcNow;
        // Called on UIKit's main thread; use existing references and ViewIfLoaded only.
        // SelectedViewController may resolve a lazy tab provider. Never query it from that provider.
        var inspectSelected = !phase.StartsWith("provider-", StringComparison.Ordinal)
            && owner.IsViewLoaded && owner.ViewIfLoaded?.Window is not null;
        var selected = inspectSelected ? owner.SelectedViewController : null;
        var state = new
        {
            phase,
            provider_call = providerCall,
            content_id = content?.AutomationId,
            content_full_bleed = content?.FullBleed,
            existing_parent_before = Id(parentBefore),
            returned_controller = Controller(returned),
            selected_tab_id = owner.SelectedTab?.AccessibilityIdentifier,
            selected_controller_inspected = inspectSelected,
            selected_controller = Controller(selected),
            returned_is_selected = !inspectSelected ? (bool?)null
                : returned is not null && selected is not null && returned.Handle == selected.Handle,
            root = Controller(owner),
            root_children = owner.ChildViewControllers.Select(Controller).ToArray(),
            pages = pages.Select(p => new
            {
                content_id = p.Item.AutomationId,
                full_bleed = p.Item.FullBleed,
                content_insets_points = new { top = (double)p.Insets.Top, left = (double)p.Insets.Left,
                    bottom = (double)p.Insets.Bottom, right = (double)p.Insets.Right },
                maui_page_type = p.Item._page?.GetType().FullName,
                maui_page_platform_view = View(p.Item._page?.Handler?.PlatformView as UIView),
                maui_counter_handler_views = MauiCounters(p.Item._page),
                container = Controller(p.Container),
                page = Controller(p.Page),
                container_is_selected_child = IsDescendant(selected, p.Container),
                native_counter_views = CounterViews(p.Page.ViewIfLoaded),
            }).ToArray(),
        };
        var stateReadFinished = DateTimeOffset.UtcNow;
        var fingerprint = JsonSerializer.Serialize(state);
        var stateHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint))).ToLowerInvariant();
        if (!force && _lastByPhase.TryGetValue(phase, out var previous) && previous == fingerprint)
        {
            // A tick still reads the loaded native hierarchy above. Record a bounded
            // heartbeat for this fresh equal state rather than hiding its timestamp.
            if (phase == "tick" && _lastFullByPhase.TryGetValue(phase, out var full))
            {
                AppendRecord(new
                {
                    schema = 1, sequence = ++_sequence, utc = DateTimeOffset.UtcNow,
                    process_id = Environment.ProcessId,
                    bundle_id = NSBundle.MainBundle.BundleIdentifier,
                    simulator_model = Environment.GetEnvironmentVariable("SIMULATOR_MODEL_IDENTIFIER"),
                    os_version = UIDevice.CurrentDevice.SystemVersion,
                    diagnostic_source_sha = NSBundle.MainBundle.InfoDictionary?[SourceKey]?.ToString(),
                    status = "fresh_equal_native_state", phase,
                    state_read_started_utc = stateReadStarted,
                    state_read_finished_utc = stateReadFinished,
                    full_state_sequence = full.Sequence,
                    full_state_sha256 = full.Hash,
                    current_state_sha256 = stateHash,
                });
            }
            return;
        }
        _lastByPhase[phase] = fingerprint;
        var fullSequence = ++_sequence;
        _lastFullByPhase[phase] = (fullSequence, stateHash);
        var record = new
        {
            schema = 1,
            sequence = fullSequence,
            utc = DateTimeOffset.UtcNow,
            process_id = Environment.ProcessId,
            bundle_id = NSBundle.MainBundle.BundleIdentifier,
            simulator_model = Environment.GetEnvironmentVariable("SIMULATOR_MODEL_IDENTIFIER"),
            os_version = UIDevice.CurrentDevice.SystemVersion,
            diagnostic_source_sha = NSBundle.MainBundle.InfoDictionary?[SourceKey]?.ToString(),
            state_read_started_utc = stateReadStarted,
            state_read_finished_utc = stateReadFinished,
            full_state_sha256 = stateHash,
            state,
        };
        // Appium skipLogCapture=true does not collect AUT stdout. Persist inside the AUT.
        AppendRecord(record);
    }

    // Record the references already read by UpdateCurrentItem. Do not resolve a
    // SelectedViewController or read SelectedTab again while observing this write.
    internal static void RecordSelection(AShellViewBase owner, string phase, long selectionCall,
        AShellContent content, UITab target, UITab? actualBefore, UITab? actualAfter, bool skipped)
    {
        if (!Enabled) return;
        var record = new
        {
            schema = 1,
            sequence = ++_sequence,
            utc = DateTimeOffset.UtcNow,
            process_id = Environment.ProcessId,
            bundle_id = NSBundle.MainBundle.BundleIdentifier,
            simulator_model = Environment.GetEnvironmentVariable("SIMULATOR_MODEL_IDENTIFIER"),
            os_version = UIDevice.CurrentDevice.SystemVersion,
            diagnostic_source_sha = NSBundle.MainBundle.InfoDictionary?[SourceKey]?.ToString(),
            state = new
            {
                phase,
                selection_call = selectionCall,
                content_id = content.AutomationId,
                root_handle = Id(owner),
                target_tab_id = target.AccessibilityIdentifier,
                target_tab_handle = Id(target),
                actual_selected_tab_before_id = actualBefore?.AccessibilityIdentifier,
                actual_selected_tab_before_handle = Id(actualBefore),
                actual_selected_tab_after_id = actualAfter?.AccessibilityIdentifier,
                actual_selected_tab_after_handle = Id(actualAfter),
                selection_write_skipped = skipped,
                selection_write_attempted = phase == "current-item-selection-after" && !skipped,
            },
        };
        AppendRecord(record);
    }

    internal static void RecordError(string phase, Exception error)
    {
        try
        {
            AppendRecord(new
            {
                schema = 1, sequence = ++_sequence, utc = DateTimeOffset.UtcNow,
                status = "native_capture_error", phase, error = error.ToString(),
            });
        }
        catch { /* Missing diagnostic evidence remains an error in the collector. */ }
    }

    static void AppendRecord(object record)
    {
        if (_loggingStopped) return;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), LogName);
        var serialized = JsonSerializer.Serialize(record) + "\n";
        var existingLength = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (_sequence > MaximumLogRecords
            || existingLength + Encoding.UTF8.GetByteCount(serialized) > MaximumLogBytes - 1024)
        {
            _loggingStopped = true;
            var limitRecord = JsonSerializer.Serialize(new
            {
                schema = 1, sequence = _sequence, utc = DateTimeOffset.UtcNow,
                process_id = Environment.ProcessId,
                status = "native_diagnostic_log_limit", maximum_records = MaximumLogRecords,
                maximum_bytes = MaximumLogBytes,
            }) + "\n";
            if (existingLength + Encoding.UTF8.GetByteCount(limitRecord) <= MaximumLogBytes)
                File.AppendAllText(path, limitRecord);
            return;
        }
        File.AppendAllText(path, serialized);
    }

    static string? Id(NSObject? value) => value?.Handle.ToString();

    static object? Controller(UIViewController? controller)
    {
        if (controller is null) return null;
        return new
        {
            id = Id(controller),
            managed_type = controller.GetType().FullName,
            parent_id = Id(controller.ParentViewController),
            navigation_controller_id = Id(controller.NavigationController),
            child_ids = controller.ChildViewControllers.Select(Id).ToArray(),
            is_view_loaded = controller.IsViewLoaded,
            moving_to_parent = controller.IsMovingToParentViewController,
            moving_from_parent = controller.IsMovingFromParentViewController,
            view = View(controller.ViewIfLoaded),
        };
    }

    static object? View(UIView? view)
    {
        if (view is null) return null;
        var window = view.Window;
        return new
        {
            id = Id(view),
            managed_type = view.GetType().FullName,
            superview_id = Id(view.Superview),
            accessibility_id = view.AccessibilityIdentifier,
            hidden = view.Hidden,
            alpha = (double)view.Alpha,
            layer_hidden = view.Layer.Hidden,
            layer_opacity = view.Layer.Opacity,
            accessibility_elements_hidden = view.AccessibilityElementsHidden,
            accessibility_view_is_modal = view.AccessibilityViewIsModal,
            accessibility_frame_screen_points = Rect(view.AccessibilityFrame),
            is_accessibility_element = view.IsAccessibilityElement,
            interaction_enabled = view.UserInteractionEnabled,
            clips_to_bounds = view.ClipsToBounds,
            frame_points = Rect(view.Frame),
            bounds_points = Rect(view.Bounds),
            transform = view.Transform.ToString(),
            window_id = Id(window),
            frame_in_window_points = window is null ? null : Rect(view.ConvertRectToView(view.Bounds, window)),
            window = window is null ? null : new
            {
                id = Id(window),
                hidden = window.Hidden,
                alpha = (double)window.Alpha,
                is_key_window = window.IsKeyWindow,
                scene_activation_state = window.WindowScene?.ActivationState.ToString(),
                frame_points = Rect(window.Frame),
                bounds_points = Rect(window.Bounds),
                screen_id = Id(window.Screen),
                screen_bounds_points = Rect(window.Screen.Bounds),
                screen_scale = (double)window.Screen.Scale,
                scene_id = window.WindowScene?.Session.PersistentIdentifier,
            },
        };
    }

    static object Rect(CGRect rect) => new
    {
        x = Finite(rect.X), y = Finite(rect.Y), width = Finite(rect.Width), height = Finite(rect.Height),
        raw = rect.ToString(),
    };

    static double? Finite(nfloat value) => double.IsFinite((double)value) ? (double)value : null;

    static bool IsDescendant(UIViewController? ancestor, UIViewController descendant)
    {
        for (var current = descendant; current is not null; current = current.ParentViewController)
            if (ancestor is not null && current.Handle == ancestor.Handle) return true;
        return false;
    }

    static object[] MauiCounters(Microsoft.Maui.Controls.Page? page)
    {
        if (page is null) return Array.Empty<object>();
        return Microsoft.Maui.VisualTreeElementExtensions.GetVisualTreeDescendants(page)
            .OfType<Microsoft.Maui.Controls.Element>()
            .Where(element => element.AutomationId == "counterBtn")
            .Select(element => (object)new
            {
                maui_type = element.GetType().FullName,
                automation_id = element.AutomationId,
                existing_handler_type = element.Handler?.GetType().FullName,
                platform_view = View(element.Handler?.PlatformView as UIView),
                native_view_ancestors = Ancestors(element.Handler?.PlatformView as UIView),
                window_hit_test = CounterHitTest(element.Handler?.PlatformView as UIView),
            }).ToArray();
    }

    static object?[] Ancestors(UIView? view)
    {
        var chain = new List<object?>();
        for (var current = view; current is not null; current = current.Superview)
            chain.Add(View(current));
        return chain.ToArray();
    }

    static object? CounterHitTest(UIView? counter)
    {
        var window = counter?.Window;
        if (counter is null || window is null) return null;
        var bounds = counter.Bounds;
        var point = counter.ConvertPointToView(new CGPoint(
            bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), window);
        if (!double.IsFinite((double)point.X) || !double.IsFinite((double)point.Y)) return null;
        var hit = window.HitTest(point, null);
        return new
        {
            counter_handle = Id(counter), window_handle = Id(window),
            point_in_window_points = new { x = (double)point.X, y = (double)point.Y },
            hit_view = View(hit),
            hit_is_counter_or_descendant = hit is not null
                && (hit.Handle == counter.Handle || hit.IsDescendantOfView(counter)),
            counter_is_hit_or_descendant = hit is not null
                && (hit.Handle == counter.Handle || counter.IsDescendantOfView(hit)),
        };
    }

    static object[] CounterViews(UIView? root)
    {
        if (root is null) return Array.Empty<object>();
        var result = new List<object>();
        var pending = new Stack<UIView>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var view = pending.Pop();
            if (view.AccessibilityIdentifier == "counterBtn")
            {
                var chain = new List<object?>();
                for (UIView? current = view; current is not null; current = current.Superview)
                    chain.Add(View(current));
                result.Add(new { counter = View(view), native_view_ancestors = chain,
                    window_hit_test = CounterHitTest(view) });
            }
            foreach (var child in view.Subviews) pending.Push(child);
        }
        return result.ToArray();
    }
}
#endif
