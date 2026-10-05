using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace AdaptiveShell.Controls
{
    [ContentProperty(nameof(Items))]
    public class AShell : Page, IAShellController
    {
        readonly HashSet<AShellItem> _observedItems = new();
        readonly HashSet<AShellGroup> _observedGroups = new();
        bool _itemsUpdatePending;
        bool _normalizeSelectionPending;

        internal bool IsUpdatingItems { get; private set; }

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

                    items.CollectionChanged += shell.OnItemsCollectionChanged;

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

        internal Color? GetEffectiveSelectedItemColor(bool useDarkDefault = false) =>
            SelectedItemColor
            ?? (useDarkDefault ? FindAppResourceColor("PrimaryDark") : null)
            ?? FindAppResourceColor("Primary");

        internal Color? GetEffectiveUnselectedItemColor() =>
            UnselectedItemColor;

        void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshItems(normalizeSelection: true);

        void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName is nameof(AShellItem.Title) or nameof(AShellItem.Icon)
                    or nameof(AShellItem.AutomationId) or nameof(AShellContent.ContentTemplate)
                    or nameof(AShellContent._page) or nameof(AShellGroup.LandingTemplate))
            {
                RefreshItems(normalizeSelection: false);
            }
        }

        void RefreshItems(bool normalizeSelection)
        {
            _normalizeSelectionPending |= normalizeSelection;
            if (IsUpdatingItems)
            {
                _itemsUpdatePending = true;
                return;
            }

            do
            {
                _itemsUpdatePending = false;
                bool updateSelection = _normalizeSelectionPending;
                _normalizeSelectionPending = false;
                IsUpdatingItems = true;
                try
                {
                    UpdateSubscriptions();
                    var contents = Items.SelectMany(item => item is AShellGroup group
                        ? group.Items.AsEnumerable()
                        : item is AShellContent content ? new[] { content } : Enumerable.Empty<AShellContent>());
                    if (updateSelection && (CurrentItem is null || !contents.Contains(CurrentItem)))
                    {
                        CurrentItem = contents.FirstOrDefault();
                    }

                    // The Items instance stays stable. Explicitly notify the mapper only after
                    // logical ownership and selection have been brought into agreement.
                    OnPropertyChanged(nameof(Items));
                }
                finally
                {
                    IsUpdatingItems = false;
                }
            }
            while (_itemsUpdatePending);
        }

        void UpdateSubscriptions()
        {
            var groups = Items.OfType<AShellGroup>().ToHashSet();
            var items = Items.Concat(groups.SelectMany(group => group.Items)).ToHashSet();

            foreach (var removed in _observedGroups.Except(groups).ToArray())
            {
                removed.Items.CollectionChanged -= OnItemsCollectionChanged;
                _observedGroups.Remove(removed);
            }
            foreach (var added in groups.Except(_observedGroups).ToArray())
            {
                added.Items.CollectionChanged += OnItemsCollectionChanged;
                _observedGroups.Add(added);
            }
            foreach (var removed in _observedItems.Except(items).ToArray())
            {
                removed.PropertyChanged -= OnItemPropertyChanged;
                _observedItems.Remove(removed);
            }
            foreach (var added in items.Except(_observedItems).ToArray())
            {
                added.PropertyChanged += OnItemPropertyChanged;
                _observedItems.Add(added);
            }
        }

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
