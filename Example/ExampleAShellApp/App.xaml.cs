using Microsoft.Extensions.DependencyInjection;

#if IOS
using Foundation;
using System.Text.Json;
using UIKit;
#endif

namespace ExampleAShellApp
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
#if IOS
            if (TryGetOfficialShellControlSource(out var source))
            {
                var window = new Window(CreateOfficialShellControl());
                WriteOfficialShellControlEvidence(window, source, "managed-root-created");
                window.Created += (_, _) =>
                    WriteOfficialShellControlEvidence(window, source, "window-created");
                return window;
            }
#endif
            return new Window(new AppShell());
        }

#if IOS
        const string OfficialShellControlFlag = "AShellDuoOfficialShellControl";
        const string OfficialShellControlSource = "AShellDuoOfficialShellControlSource";
        const string OfficialShellControlLog = "ashell-duo-official-shell-control.jsonl";

        static bool TryGetOfficialShellControlSource(out string source)
        {
            source = string.Empty;
            var info = NSBundle.MainBundle.InfoDictionary;
            var flag = info?[OfficialShellControlFlag];
            var rawSource = info?[OfficialShellControlSource];
            if (flag is null && rawSource is null)
            {
                return false;
            }

            // The producer/consumer must independently verify this signed
            // source against their actual GITHUB_SHA. The AUT has no runner env.
            using var explicitTrue = NSNumber.FromBoolean(true);
            if (flag is not NSNumber number
                || number.Handle != explicitTrue.Handle
                || !number.BoolValue
                || rawSource is not NSString text
                || text.ToString() is not { Length: 40 } value
                || !value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                throw new InvalidOperationException("Official Shell control requires explicit true and canonical signed source.");
            }

            if (NSBundle.MainBundle.BundleIdentifier != "com.companyname.exampleashellapp"
                || Microsoft.Maui.Devices.DeviceInfo.Current.Idiom != Microsoft.Maui.Devices.DeviceIdiom.Phone
                || UIDevice.CurrentDevice.UserInterfaceIdiom != UIUserInterfaceIdiom.Phone
                || Environment.GetEnvironmentVariable("SIMULATOR_MODEL_IDENTIFIER") != "iPhone19,4"
                || !Version.TryParse(UIDevice.CurrentDevice.SystemVersion, out var version)
                || version.Major != 27 || version.Minor != 1)
            {
                throw new InvalidOperationException("Official Shell control is restricted to the actual Phone Duo 27.1 simulator.");
            }

            source = value;
            return true;
        }

        static Shell CreateOfficialShellControl()
        {
            var tabs = new TabBar();
            tabs.Items.Add(CreateOfficialControlTab("home", "Home", "home.svg"));
            tabs.Items.Add(CreateOfficialControlTab("home2", "Home2", "favorite.svg"));

            var media = new Tab
            {
                AutomationId = "media",
                Title = "媒体",
                Icon = "folder.svg",
            };
            media.Items.Add(new ShellContent
            {
                AutomationId = "music",
                Title = "音乐",
                Icon = "star.svg",
                ContentTemplate = new DataTemplate(typeof(MainPage)),
            });
            media.Items.Add(new ShellContent
            {
                AutomationId = "photos",
                Title = "相册",
                Icon = "star.svg",
                ContentTemplate = new DataTemplate(typeof(MainPage)),
            });
            tabs.Items.Add(media);

            var shell = new Shell
            {
                Title = "ExampleAShellApp",
                FlyoutBehavior = FlyoutBehavior.Disabled,
            };
            shell.Items.Add(tabs);
            return shell;
        }

        static Tab CreateOfficialControlTab(string id, string title, string icon)
        {
            // MAUI 10.0.100 ShellSectionRenderer maps this Tab AutomationId
            // to UITabBarItem.AccessibilityIdentifier, rather than the content ID.
            var tab = new Tab { AutomationId = id, Title = title, Icon = icon };
            tab.Items.Add(new ShellContent
            {
                Title = title,
                ContentTemplate = new DataTemplate(typeof(MainPage)),
            });
            return tab;
        }

        static void WriteOfficialShellControlEvidence(Window window, string source, string phase)
        {
            // Read only existing handlers/native objects. Do not create a page
            // handler, read CurrentPage, access an unloaded View, or set traits.
            var nativeWindow = window.Handler?.PlatformView as UIWindow;
            var record = new
            {
                schema = 1,
                phase,
                utc = DateTimeOffset.UtcNow,
                process_id = Environment.ProcessId,
                bundle_id = NSBundle.MainBundle.BundleIdentifier,
                signed_control_flag = true,
                signed_control_source = source,
                managed_root_type = window.Page?.GetType().FullName,
                managed_root_handler_type = window.Page?.Handler?.GetType().FullName,
                native_window_type = nativeWindow?.GetType().FullName,
                native_root_controller_type = nativeWindow?.RootViewController?.GetType().FullName,
                native_idiom = UIDevice.CurrentDevice.UserInterfaceIdiom.ToString(),
                maui_idiom = Microsoft.Maui.Devices.DeviceInfo.Current.Idiom.ToString(),
                simulator_model = Environment.GetEnvironmentVariable("SIMULATOR_MODEL_IDENTIFIER"),
                os_version = UIDevice.CurrentDevice.SystemVersion,
                ui_acceptance = false,
                scope = "official Shell control identity only; original Displayed/click/counter assertions determine UI results",
            };
            var directory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                throw new InvalidOperationException("Official Shell control cannot persist its actual identity evidence.");
            File.AppendAllText(Path.Combine(directory, OfficialShellControlLog), JsonSerializer.Serialize(record) + "\n");
        }
#endif
    }
}
