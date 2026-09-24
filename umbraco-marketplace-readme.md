# Unlog for Umbraco

Keep the useful log events and clear the noise on your own schedule. Unlog removes old entries from Umbraco's Serilog files by severity, including files left behind when an Azure worker's machine name changes.

Install `Umbraco.Community.Unlog` from NuGet. Use a `17.x.x` package for Umbraco 17 or an `18.x.x` package for Umbraco 18. The package depends on the matching Umbraco major version.

## A simple retention policy

Add this to `appsettings.json`:

```json
{
  "Umbraco": {
    "Community": {
      "Unlog": {
        "Delay": "00:15:00",
        "Period": "1.00:00:00",
        "Rules": [
          { "Level": "Information", "Days": 14 },
          { "Level": "Warning", "Days": 30 },
          { "Level": "All", "Days": 90 }
        ]
      }
    }
  }
}
```

Rules apply to their level and lower levels. The shortest applicable retention wins. Here, Information and lower last 14 days, Warning lasts 30 days, and Error and Fatal last 90 days. `All` is the explicit catch-all rule. Without any rules, Unlog deletes nothing.

The first run starts 15 minutes after application startup; later runs happen every 24 hours by default. Both timings are configurable and take effect after an application restart. The Health Check shows the rules and can start a run on the current instance.

Unlog touches only Umbraco's own `UmbracoFile` log files. It skips today's files and files in use, preserves a file if any event cannot be parsed, and deletes a file once no entries remain. Other log destinations, including Application Insights and Elmah, are outside its scope. It logs what each cleanup removed and reports problems in the application log.

The package includes an `appsettings.json` schema for configuration help in supporting editors. For more detail, see the [NuGet user guide](https://github.com/skttl/umbraco-unlog/blob/main/docs/nuget-readme.md).

Icon: ["File" by Mohamed Salah Hajji](https://thenounproject.com/icon/file-5074819/), supplied under a royalty-free license by the project owner.
