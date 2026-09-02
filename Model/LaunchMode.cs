namespace CommandCenter.Model
{
    // How a TabServerEntry's Host/Port compose into the LaunchArgument string actually passed to
    // the client executable - see TabServerEntry.LaunchArgument. GameLaunching and IpPort are the
    // two command keywords the client exes actually understand (every built-in entry in
    // LaunchServerCatalog uses one or the other); Raw is the escape hatch for anything else - the
    // whole argument string is typed in directly, unstructured, and Host/Port are ignored.
    public enum LaunchMode
    {
        GameLaunching,
        IpPort,
        Raw
    }
}
