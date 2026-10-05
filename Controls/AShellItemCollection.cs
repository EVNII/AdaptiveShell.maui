using System.Collections.ObjectModel;

namespace AdaptiveShell.Controls
{
    internal sealed class AShellItemCollection<T> : ObservableCollection<T> where T : AShellItem
    {
        readonly Element _owner;

        public AShellItemCollection(Element owner)
        {
            _owner = owner;
        }

        protected override void InsertItem(int index, T item)
        {
            CheckReentrancy();
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(index, Count);
            ValidateItem(item);
            _owner.AddLogicalChild(item);
            base.InsertItem(index, item);
        }

        protected override void RemoveItem(int index)
        {
            var item = this[index];
            CheckReentrancy();
            _owner.RemoveLogicalChild(item);
            base.RemoveItem(index);
        }

        protected override void SetItem(int index, T item)
        {
            var oldItem = this[index];
            CheckReentrancy();
            if (ReferenceEquals(oldItem, item))
            {
                return;
            }
            ValidateItem(item);
            _owner.RemoveLogicalChild(oldItem);
            _owner.AddLogicalChild(item);
            base.SetItem(index, item);
        }

        protected override void ClearItems()
        {
            CheckReentrancy();
            foreach (var item in this.ToArray())
            {
                _owner.RemoveLogicalChild(item);
            }
            base.ClearItems();
        }

        void ValidateItem(T item)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (Contains(item) || item.Parent is not null)
            {
                throw new InvalidOperationException("Remove the navigation item from its current collection before adding it to another location.");
            }
        }
    }
}
