namespace Umbraco.Community.Unlog.Engine;

public sealed class UnlogOptions
{
    public TimeSpan Delay { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan Period { get; set; } = TimeSpan.FromDays(1);

    public List<RetentionRule> Rules { get; set; } = [];
}

public sealed class RetentionRule
{
    public string Level { get; set; } = string.Empty;

    public int Days { get; set; }
}
