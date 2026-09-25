using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using AdaptiveShell.Controls;
using Microsoft.Maui.Platform;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

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
        }



        public void Dispose()
        {
            _platformView.ItemInvoked -= OnPlatformViewItemInvoked;
            return;
        }

        public void UpdateItems()
        {
            _contentToMenuItemMap.Clear();
            _groupToMenuItemMap.Clear();
            PlatformView.MenuItems.Clear();

            foreach (var item in _virtualView.Items)
            {
                if (item is AShellContent content)
                {
                    var menuItem = CreateMenuItem(content);
                    PlatformView.MenuItems.Add(menuItem);
                    _contentToMenuItemMap[content] = menuItem;
                }
                else if (item is AShellGroup group)
                {
                    var groupItem = CreateMenuItem(group);

                    foreach (var child in group.Items)
                    {
                        var childItem = CreateMenuItem(child);
                        groupItem.MenuItems.Add(childItem);
                        _contentToMenuItemMap[child] = childItem;
                    }

                    PlatformView.MenuItems.Add(groupItem);
                    _groupToMenuItemMap[group] = groupItem;
                }
            }
        }

        private static NavigationViewItem CreateMenuItem(AShellItem item)
        {
            var menuItem = new NavigationViewItem
            {
                Content = item.Title,
                Tag = item
            };

            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                menuItem, item.AutomationId ?? item.Title);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                menuItem, item.Title);

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

            return menuItem;
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

            var unselected = _virtualView.GetEffectiveUnselectedItemColor();
            if (unselected is not null)
            {
                PlatformView.Resources["NavigationViewItemForeground"] =
                    new Microsoft.UI.Xaml.Media.SolidColorBrush(unselected.ToWindowsColor());
            }
        }

        public void UpdateCurrentItem()
        {
            if (_virtualView.CurrentItem is null)
            {
                PlatformView.SelectedItem = null;
                PlatformView.Content = null;
                return;
            }

            // NavigationView.SelectedItem only accepts a top-level item.
            // Group children are nested NavigationViewItems, and assigning one
            // here causes WinUI to throw "invalid vector subscript".
            if (_virtualView.Items.Contains(_virtualView.CurrentItem))
            {
                var menuItem = _contentToMenuItemMap[_virtualView.CurrentItem];

                if (!ReferenceEquals(PlatformView.SelectedItem, menuItem))
                {
                    PlatformView.SelectedItem = menuItem;
                }
            }

            PlatformView.Content = ((IAShellContentController)_virtualView.CurrentItem).page.ToPlatform(_mauiContext);
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
