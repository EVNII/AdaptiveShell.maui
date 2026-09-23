using System.Collections.ObjectModel;

namespace AdaptiveShell.Controls
{
    internal class AShellItemCollection<T> : ObservableCollection<T> where T : Element
    {
        readonly Element _owner;

        public AShellItemCollection(Element owner)
        {
            _owner = owner;
        }

        protected override void InsertItem(int index, T item)
        {
            base.InsertItem(index, item);
            _owner.AddLogicalChild(item);
        }

        protected override void RemoveItem(int index)
        {
            var item = this[index];
            base.RemoveItem(index);
            _owner.RemoveLogicalChild(item);
        }

        protected override void SetItem(int index, T item)
        {
            var oldItem = this[index];
            base.SetItem(index, item);
            _owner.RemoveLogicalChild(oldItem);
            _owner.AddLogicalChild(item);
        }

        protected override void ClearItems()
        {
            foreach (var item in this)
            {
                _owner.RemoveLogicalChild(item);
            }
            base.ClearItems();
        }
    }
}
