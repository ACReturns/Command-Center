namespace CommandCenter.Model
{
    // DisplayName is what shows in the dropdown; LaunchArgument is passed to the client
    // executable verbatim as its command-line argument at launch time.
    public record LaunchServerOption(string DisplayName, string LaunchArgument);
}
