using Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;

namespace AdaptiveShell.Controls
{
    [ContentProperty(nameof(Items))]
    public class AShell : Page, IAShellController
    {
        static readonly BindablePropertyKey ItemsPropertyKey =
            BindableProperty.CreateReadOnly(
                nameof(Items),
                typeof(ObservableCollection<AShellItem>),
                typeof(AShell),
                null,
                defaultValueCreator: (bo) =>
                {
                    var shell = (AShell)bo;
                    var items = new AShellItemCollection<AShellItem>(shell);

                    items.CollectionChanged += (sender, e) =>
                    {
                        if(e.Action is NotifyCollectionChangedAction.Add)
                        {
                            if(shell.CurrentItem is null && e.NewItems != null && e.NewItems.Count > 0)
                            {
                                shell.CurrentItem = FirstLeaf((AShellItem)e.NewItems[0]!);
                            }
                        }
                    };

                    return items;
                }
            );


        public static readonly BindableProperty ItemsProperty = ItemsPropertyKey.BindableProperty;

        public static readonly BindableProperty CurrentItemProperty = 
            BindableProperty.Create(
                nameof(CurrentItem),
                typeof(AShellContent),
                typeof(AShell),
                null);

        public static readonly BindableProperty SelectedItemColorProperty =
            BindableProperty.Create(
                nameof(SelectedItemColor),
                typeof(Color),
                typeof(AShell),
                null);

        public static readonly BindableProperty UnselectedItemColorProperty =
            BindableProperty.Create(
                nameof(UnselectedItemColor),
                typeof(Color),
                typeof(AShell),
                null);

        public ObservableCollection<AShellItem> Items
        {
            get { return (ObservableCollection<AShellItem>)GetValue(ItemsProperty); }
        }

        public AShellContent? CurrentItem
        {
            get { return (AShellContent?)GetValue(CurrentItemProperty); }
            set { SetValue(CurrentItemProperty, value); }
        }

        public Color? SelectedItemColor
        {
            get { return (Color?)GetValue(SelectedItemColorProperty); }
            set { SetValue(SelectedItemColorProperty, value); }
        }

        public Color? UnselectedItemColor
        {
            get { return (Color?)GetValue(UnselectedItemColorProperty); }
            set { SetValue(UnselectedItemColorProperty, value); }
        }

        internal Color? GetEffectiveSelectedItemColor() =>
            SelectedItemColor ?? FindAppResourceColor("Primary");

        internal Color? GetEffectiveUnselectedItemColor() =>
            UnselectedItemColor;

        internal static AShellContent? FirstLeaf(AShellItem item) =>
            item switch
            {
                AShellContent content => content,
                AShellGroup group => group.FirstLeaf(),
                _ => null,
            };

        static Color? FindAppResourceColor(string key)
        {
            if (Microsoft.Maui.Controls.Application.Current?.Resources?.TryGetValue(key, out var value) == true
                && value is Color color)
            {
                return color;
            }

            return null;
        }
    }
}
