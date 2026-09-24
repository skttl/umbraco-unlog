using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Community.Unlog.Engine;
using Umbraco.Extensions;

namespace Umbraco.Community.Unlog.Integration;

public sealed class UnlogComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.Configure<UnlogOptions>(builder.Config.GetSection("Umbraco:Community:Unlog"));
        builder.Services.AddSingleton<CleanupEngine>();
        builder.Services.AddSingleton<UnlogRunner>();
        builder.Services.AddRecurringBackgroundJob<UnlogJob>();
    }
}
