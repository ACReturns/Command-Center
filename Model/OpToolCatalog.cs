using System;
using System.Collections.Generic;

namespace CommandCenter.Model
{
    // One QA OpTool web panel the OpTool tab can open. AccessLevel is informational only (shown
    // next to the label in the picker) - Command Center never logs in on anyone's behalf, the
    // OpTool's own login page decides what the signed-in account can actually do.
    public sealed record OpToolEndpoint(string Label, string Url, int AccessLevel)
    {
        public Uri Uri => new(Url);
        public string AccessLabel => $"Level {AccessLevel}";
    }

    // Fixed list of OpTool endpoints, straight from the internal QA OpTool Guide. Deliberately
    // hardcoded (same idea as LaunchServerCatalog's built-ins) - if an endpoint moves, update it
    // here and rebuild. No credentials live anywhere in Command Center: the OpTool tab is a plain
    // embedded browser pointed at these URLs, and you sign in by hand on the real login page.
    public static class OpToolCatalog
    {
        public static IReadOnlyList<OpToolEndpoint> Endpoints { get; } = new List<OpToolEndpoint>
        {
            new("Test 1", "http://35.162.209.242:81/Login.aspx", 10),
            new("Test 2", "http://35.162.209.242:82/Login.aspx", 10),
            new("Test 3", "http://35.162.209.242:83/Login.aspx", 10),
            new("Test 4", "http://35.162.209.242:84/Login.aspx", 10),
            new("Test 5", "http://35.162.209.242:85/Login.aspx", 10),
            new("Test 6", "http://35.162.209.242:86/Login.aspx", 10),
            new("NA Staging", "https://optool-na-staging-ms.nexon.net", 1),
            new("EU Staging", "https://optool-eu-staging-ms.nexon.net", 1),
        };
    }
}
