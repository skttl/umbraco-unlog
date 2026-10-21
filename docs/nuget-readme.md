# Umbraco.Community.Unlog

Unlog cleans old entries from Umbraco's own Serilog files. It can keep lower-severity events for less time while preserving errors longer. It also scans files left by previous machine names, which matters when an Azure App Service changes workers.

## Install

Add `Umbraco.Community.Unlog` to your Umbraco site. Choose a package version matching your Umbraco major version:

| Umbraco | Unlog version | Umbraco dependency |
| --- | --- | --- |
| 17 | `17.x.x` | `[17.0.0,18.0.0)` |
| 18 | `18.x.x` | `[18.0.0,19.0.0)` |

```sh
dotnet add package Umbraco.Community.Unlog --version 17.0.1
```

NuGet may report a dependency warning if the application directly overrides the Umbraco range.

## Configure retention

Add rules to your site's `appsettings.json`:

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

Each rule applies to the named level and every lower level. When rules overlap, the shortest applicable retention wins. In this example, Information and lower are kept for 14 days, Warning for 30 days, and Error and Fatal for 90 days. `All` explicitly covers every level; omit a level only if you do not want to set a retention for it. `Days` must be a positive whole number. The job first attempts to run 15 minutes after startup and repeats every 24 hours unless you change `Delay` or `Period`. A one-minute delay can be useful for local testing once you have confirmed Umbraco starts in time; the 15-minute default allows more time for slower environments. Umbraco may defer cleanup until its startup and scheduling checks allow it. Use the Health Check's Run now action for immediate testing. Restart the application after changing either schedule setting; retention rules are read again on each run.

No rules means no deletion. The package does not change Serilog's own file retention settings. If Serilog removes a file earlier than a rule allows, Unlog cannot restore it.

## Run and inspect

Open Umbraco's Health Checks to see the configured rules and configuration errors. The Unlog check offers a run-now action for the current instance. Routine runs use an Umbraco recurring background job on each instance, so separate local log directories are cleaned independently. If instances share a log directory, a directory lock prevents simultaneous cleanup, but later runs may scan the same files again. Keep retention rules identical on instances that share a directory, since an instance with shorter retention can delete entries first. The package records a summary of removed entries and empty files in the application log.

![Unlog Health Check showing retention rules and the Run now action](https://raw.githubusercontent.com/skttl/umbraco-unlog/main/docs/images/health-check.png)

Unlog reads each event's `@t` timestamp. It scans Umbraco file logs across machine names in the configured log directory, but skips today's files and files that are open or locked. It retries those files on a later run and writes a log entry about skipped files. If it cannot parse an entry, it leaves the entire file unchanged and logs an error. A file that becomes empty is deleted.

Unlog only works with Umbraco's `UmbracoFile` Serilog sink. It does not modify events sent to Application Insights, Elmah, or other sinks. If Umbraco file logging is disabled, there are no files to clean.

## Icon credit

Package icon: ["File" by Mohamed Salah Hajji](https://thenounproject.com/icon/file-5074819/), supplied under a royalty-free license by the project owner.
