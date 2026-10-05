using AdaptiveShell.Controls;
using Microsoft.Maui.Platform;
using Microsoft.UI.Xaml.Controls;
using Page = Microsoft.Maui.Controls.Page;

namespace AdaptiveShell.Platforms.Windows
{
    public class AShellView : IDisposable
    {
        NavigationView _platformView;
        readonly XamlControlsResources _defaultResources;
        AShell _virtualView;

        IMauiContext _mauiContext;

        Dictionary<AShellContent, NavigationViewItem> _contentToMenuItemMap;

        Dictionary<AShellGroup, NavigationViewItem> _groupToMenuItemMap;
        readonly Dictionary<AShellItem, ImageSource?> _iconSources = new();
        readonly Dictionary<string, object?> _originalColors = new();
        readonly HashSet<string> _originalColorKeys = new();
        AShellContent? _presentedContent;
        Page? _presentedPage;
        bool _disposed;

        static readonly string[] ColorKeys =
        {
            "NavigationViewItemForegroundSelected",
            "NavigationViewItemForegroundSelectedPointerOver",
            "NavigationViewSelectionIndicatorForeground",
            "NavigationViewItemForeground",
        };

        public NavigationView PlatformView => _platformView;

        public AShellView(AShell aShell, IMauiContext mauiContext)
        {
            _virtualView = aShell;
            _mauiContext = mauiContext;
                
            _defaultResources = new XamlControlsResources();

            _platformView = new NavigationView();

            _platformView.Resources.MergedDictionaries.Add(_defaultResources);

            _contentToMenuItemMap = new Dictionary<AShellContent, NavigationViewItem>();
            _groupToMenuItemMap = new Dictionary<AShellGroup, NavigationViewItem>();

            _platformView.ItemInvoked += OnPlatformViewItemInvoked;
            foreach (var key in ColorKeys)
            {
                if (_platformView.Resources.Keys.Any(candidate => Equals(candidate, key)))
                {
                    _originalColorKeys.Add(key);
                    _originalColors[key] = _platformView.Resources[key];
                }
            }
        }



        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _platformView.ItemInvoked -= OnPlatformViewItemInvoked;
            _platformView.SelectedItem = null;
            _platformView.Content = null;
            _platformView.MenuItems.Clear();
            _contentToMenuItemMap.Clear();
            _groupToMenuItemMap.Clear();
            _iconSources.Clear();
            _presentedContent = null;
            _presentedPage = null;
        }

        public void UpdateItems()
        {
            if (_disposed) return;
            var desiredContents = new HashSet<AShellContent>();
            var desiredGroups = new HashSet<AShellGroup>();
            PlatformView.MenuItems.Clear();
            foreach (var groupItem in _groupToMenuItemMap.Values) groupItem.MenuItems.Clear();

            foreach (var item in _virtualView.Items)
            {
                if (item is AShellContent content)
                {
                    desiredContents.Add(content);
                    var menuItem = GetOrCreateContentItem(content);
                    PlatformView.MenuItems.Add(menuItem);
                }
                else if (item is AShellGroup group)
                {
                    desiredGroups.Add(group);
                    var groupItem = GetOrCreateGroupItem(group);
                    groupItem.MenuItems.Clear();

                    foreach (var child in group.Items)
                    {
                        desiredContents.Add(child);
                        var childItem = GetOrCreateContentItem(child);
                        groupItem.MenuItems.Add(childItem);
                    }

                    PlatformView.MenuItems.Add(groupItem);
                }
            }
            foreach (var content in _contentToMenuItemMap.Keys.Where(x => !desiredContents.Contains(x)).ToArray())
            {
                _contentToMenuItemMap.Remove(content);
                _iconSources.Remove(content);
            }
            foreach (var group in _groupToMenuItemMap.Keys.Where(x => !desiredGroups.Contains(x)).ToArray())
            {
                _groupToMenuItemMap.Remove(group);
                _iconSources.Remove(group);
            }
        }

        NavigationViewItem GetOrCreateContentItem(AShellContent content)
        {
            if (!_contentToMenuItemMap.TryGetValue(content, out var item))
            {
                item = new NavigationViewItem { Tag = content };
                _contentToMenuItemMap[content] = item;
            }
            UpdateMenuItem(item, content);
            return item;
        }

        NavigationViewItem GetOrCreateGroupItem(AShellGroup group)
        {
            if (!_groupToMenuItemMap.TryGetValue(group, out var item))
            {
                item = new NavigationViewItem { Tag = group };
                _groupToMenuItemMap[group] = item;
            }
            UpdateMenuItem(item, group);
            return item;
        }

        private void UpdateMenuItem(NavigationViewItem menuItem, AShellItem item)
        {
            menuItem.Content = item.Title;

            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                menuItem, item.AutomationId ?? item.Title);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                menuItem, item.Title);

            if (_iconSources.TryGetValue(item, out var previous)
                && ReferenceEquals(previous, item.Icon)) return;
            _iconSources[item] = item.Icon;
            menuItem.Icon = null;
            if (item.Icon is FileImageSource fileIcon)
            {
                var fileName = System.IO.Path.GetFileNameWithoutExtension(fileIcon.File);
                var extension = System.IO.Path.GetExtension(fileIcon.File);
                var packagedFileName = string.Equals(extension, ".svg", StringComparison.OrdinalIgnoreCase)
                    ? $"{fileName}.scale-400.png"
                    : fileIcon.File;

                // MAUI's Windows asset pipeline converts SVG images to scaled PNGs at package root.
                menuItem.Icon = new BitmapIcon
                {
                    UriSource = new Uri($"ms-appx:///{packagedFileName}")
                };
            }
            else if (item.Icon is FontImageSource fontIcon)
            {
                // Use FontIcon (an IconElement) instead of FontIconSource
                menuItem.Icon = new FontIcon
                {
                    Glyph = fontIcon.Glyph,
                    FontFamily = string.IsNullOrEmpty(fontIcon.FontFamily)
                        ? null
                        : new Microsoft.UI.Xaml.Media.FontFamily(fontIcon.FontFamily)
                };
            }

        }

        public void UpdateColors()
        {
            var selected = _virtualView.GetEffectiveSelectedItemColor();
            if (selected is not null)
            {
                var brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(selected.ToWindowsColor());
                PlatformView.Resources["NavigationViewItemForegroundSelected"] = brush;
                PlatformView.Resources["NavigationViewItemForegroundSelectedPointerOver"] = brush;
                PlatformView.Resources["NavigationViewSelectionIndicatorForeground"] = brush;
            }
            else
            {
                RestoreColor("NavigationViewItemForegroundSelected");
                RestoreColor("NavigationViewItemForegroundSelectedPointerOver");
                RestoreColor("NavigationViewSelectionIndicatorForeground");
            }

            var unselected = _virtualView.GetEffectiveUnselectedItemColor();
            if (unselected is not null)
            {
                PlatformView.Resources["NavigationViewItemForeground"] =
                    new Microsoft.UI.Xaml.Media.SolidColorBrush(unselected.ToWindowsColor());
            }
            else RestoreColor("NavigationViewItemForeground");
        }

        void RestoreColor(string key)
        {
            if (_originalColorKeys.Contains(key)) PlatformView.Resources[key] = _originalColors[key];
            else PlatformView.Resources.Remove(key);
        }

        public void UpdateCurrentItem()
        {
            var current = _virtualView.CurrentItem;
            if (current is null || !_contentToMenuItemMap.TryGetValue(current, out var menuItem))
            {
                PlatformView.SelectedItem = null;
                PlatformView.Content = null;
                _presentedContent = null;
                _presentedPage = null;
                return;
            }

            // NavigationView.SelectedItem only accepts a top-level item.
            // Group children are nested NavigationViewItems, and assigning one
            // here causes WinUI to throw "invalid vector subscript".
            if (_virtualView.Items.Contains(current))
            {
                if (!ReferenceEquals(PlatformView.SelectedItem, menuItem))
                {
                    PlatformView.SelectedItem = menuItem;
                }
            }

            // For a group child, keep WinUI's selection during ItemInvoked to avoid reentry.

            var page = ((IAShellContentController)current).page;
            if (!ReferenceEquals(_presentedContent, current) || !ReferenceEquals(_presentedPage, page))
            {
                PlatformView.Content = page.ToPlatform(_mauiContext);
                _presentedContent = current;
                _presentedPage = page;
            }
        }

        private void OnPlatformViewItemInvoked(NavigationView sender,
                                 NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                
            }
            else if (args.InvokedItemContainer != null)
            {
                var selectedItem = _contentToMenuItemMap.
                    FirstOrDefault(
                    a => ReferenceEquals(a.Value, args.InvokedItemContainer
                    )).Key as AShellContent;

                // 点中的是分组标题本身:选中组内第一个子项
                if (selectedItem is null)
                {
                    selectedItem = _groupToMenuItemMap.
                        FirstOrDefault(
                        a => ReferenceEquals(a.Value, args.InvokedItemContainer
                        )).Key?.FirstLeaf();
                }

                if (selectedItem is null)
                {
                    return;
                }

                if (!ReferenceEquals(_virtualView.CurrentItem, selectedItem))
                {
                    _virtualView.CurrentItem = selectedItem;
                }

            }
        }
    }
}
