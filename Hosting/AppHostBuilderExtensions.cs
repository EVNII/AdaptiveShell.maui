using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace AdaptiveShell.Hosting
{
    public static partial class AppHostBuilderExtensions
    {
        public static MauiAppBuilder UseAdaptiveShell(this MauiAppBuilder builder)
        {
            builder.ConfigureMauiHandlers(
                handlers =>
                {
                    handlers.AddHandler(typeof(Controls.AShell), typeof(Handlers.AShellHandler));
                }
                );
            return builder;
        }
    }
}
