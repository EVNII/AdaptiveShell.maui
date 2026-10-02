using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;
using UIKit;
using System.Text.Json;

namespace ExampleAShellApp;

[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
    NSTimer? _themeTimer;
    UIWindowScene? _observedScene;
    Microsoft.Maui.Controls.Application? _observedApplication;
    string? _previousState;
    string? _previousError;
    int _themeEvents;
    bool _disposed;

    public override void OnActivated(UIScene scene)
    {
        base.OnActivated(scene);
        // Diagnostic branch only. Observe existing simulator UI; never change
        // appearance, load a controller's View or alter its containment.
        if (scene is not UIWindowScene windowScene
            || !UIDevice.CurrentDevice.SystemVersion.StartsWith("18.", StringComparison.Ordinal)
            || NSBundle.MainBundle.BundleIdentifier != "com.companyname.exampleashellapp")
            return;
        _observedScene = windowScene;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_disposed || !ReferenceEquals(_observedScene, windowScene))
                return;
            CaptureTheme("sceneDidBecomeActive", true);
            _themeTimer ??= NSTimer.CreateRepeatingScheduledTimer(
                TimeSpan.FromSeconds(1), _ => CaptureTheme("timer", false));
        });
    }

    public override void DidDisconnect(UIScene scene)
    {
        StopThemeObservation();
        base.DidDisconnect(scene);
    }

    void StopThemeObservation()
    {
        _observedScene = null;
        _themeTimer?.Invalidate();
        _themeTimer?.Dispose();
        _themeTimer = null;
        if (_observedApplication is not null)
            _observedApplication.RequestedThemeChanged -= OnRequestedThemeChanged;
        _observedApplication = null;
    }

    void OnRequestedThemeChanged(object? sender, Microsoft.Maui.Controls.AppThemeChangedEventArgs args)
    {
        Interlocked.Increment(ref _themeEvents);
        MainThread.BeginInvokeOnMainThread(() =>
            CaptureTheme("RequestedThemeChanged", true, args.RequestedTheme.ToString()));
    }

    static object? ControllerState(UIViewController? controller, HashSet<nint> seen, int depth = 0)
    {
        if (controller is null || controller.Handle == nint.Zero || depth > 16 || !seen.Add(controller.Handle))
            return null;
        var loaded = controller.IsViewLoaded;
        var view = loaded ? controller.ViewIfLoaded : null;
        return new
        {
            type = controller.GetType().FullName, handle = controller.Handle.ToString(),
            trait_style = controller.TraitCollection.UserInterfaceStyle.ToString(),
            override_style = controller.OverrideUserInterfaceStyle.ToString(), is_view_loaded = loaded,
            loaded_view_type = view?.GetType().FullName,
            loaded_view_style = view?.TraitCollection.UserInterfaceStyle.ToString(),
            loaded_view_override_style = view?.OverrideUserInterfaceStyle.ToString(),
            children = controller.ChildViewControllers.Select(child => ControllerState(child, seen, depth + 1)).ToArray(),
        };
    }

    void CaptureTheme(string stage, bool force, string? eventTheme = null)
    {
        if (_disposed)
            return;
        var scene = _observedScene;
        if (scene is null || scene.Handle == nint.Zero)
            return;
        try
        {
            var app = Microsoft.Maui.Controls.Application.Current;
            if (!ReferenceEquals(app, _observedApplication))
            {
                if (_observedApplication is not null)
                    _observedApplication.RequestedThemeChanged -= OnRequestedThemeChanged;
                _observedApplication = app;
                if (app is not null)
                    app.RequestedThemeChanged += OnRequestedThemeChanged;
            }
            var state = new
            {
                scene_style = scene.TraitCollection.UserInterfaceStyle.ToString(),
                requested_theme = app?.RequestedTheme.ToString(), user_app_theme = app?.UserAppTheme.ToString(),
                requested_theme_event_count = _themeEvents,
                windows = scene.Windows.Where(window => window.Handle != nint.Zero).Select(window => new
                {
                    type = window.GetType().FullName, handle = window.Handle.ToString(),
                    is_key = window.IsKeyWindow, hidden = window.Hidden,
                    trait_style = window.TraitCollection.UserInterfaceStyle.ToString(),
                    override_style = window.OverrideUserInterfaceStyle.ToString(),
                    root = ControllerState(window.RootViewController, new HashSet<nint>()),
                }).ToArray(),
            };
            var serialized = JsonSerializer.Serialize(state);
            if (!force && serialized == _previousState)
                return;
            _previousState = serialized;
            WriteThemeRecord(new { utc = DateTime.UtcNow.ToString("O"), stage, event_theme = eventTheme,
                pid = Environment.ProcessId, bundle_id = NSBundle.MainBundle.BundleIdentifier,
                os_version = UIDevice.CurrentDevice.SystemVersion,
                scene_id = scene.Session.PersistentIdentifier, state });
        }
        catch (Exception error)
        {
            if (error.Message == _previousError)
                return;
            _previousError = error.Message;
            try { WriteThemeRecord(new { utc = DateTime.UtcNow.ToString("O"), stage = "diagnostic-error",
                type = error.GetType().FullName, error = error.Message }); } catch { }
        }
    }

    static void WriteThemeRecord(object record)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Directory.CreateDirectory(documents);
        File.AppendAllText(Path.Combine(documents, "ios-native-theme.jsonl"),
            JsonSerializer.Serialize(record) + "\n");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            StopThemeObservation();
        }
        base.Dispose(disposing);
    }
}
