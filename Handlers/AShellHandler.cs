using AdaptiveShell.Controls;
using Microsoft.Maui.Handlers;

namespace AdaptiveShell.Handlers
{
    public partial class AShellHandler
    {
        public static IPropertyMapper<AShell, AShellHandler> PropertyMapper =
            new PropertyMapper<AShell, AShellHandler>(ViewHandler.ViewMapper)
            {
                [nameof(AShell.Items)] = MapItems,
                [nameof(AShell.CurrentItem)] = MapCurrentItem,
                [nameof(AShell.SelectedItemColor)] = MapColors,
                [nameof(AShell.UnselectedItemColor)] = MapColors
            };

        public AShellHandler() : base(PropertyMapper)
        {
        }
    }
}
