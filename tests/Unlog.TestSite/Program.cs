
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Umbraco:Community:Unlog:Delay"] = "00:01:00",
    ["Umbraco:Community:Unlog:Period"] = "01:00:00",
    ["Umbraco:Community:Unlog:Rules:0:Level"] = "Information",
    ["Umbraco:Community:Unlog:Rules:0:Days"] = "14",
    ["Umbraco:Community:Unlog:Rules:1:Level"] = "Warning",
    ["Umbraco:Community:Unlog:Rules:1:Days"] = "30",
    ["Umbraco:Community:Unlog:Rules:2:Level"] = "All",
    ["Umbraco:Community:Unlog:Rules:2:Days"] = "90"
});

#if DEBUG
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
#endif

builder.CreateUmbracoBuilder()
    .AddBackOffice()
    .AddWebsite()
    .AddComposers()
    .Build();

WebApplication app = builder.Build();


await app.BootUmbracoAsync();


app.UseUmbraco()
    .WithMiddleware(u =>
    {
        u.UseBackOffice();
        u.UseWebsite();
    })
    .WithEndpoints(u =>
    {
        u.UseBackOfficeEndpoints();
        u.UseWebsiteEndpoints();
    });

await app.RunAsync();
