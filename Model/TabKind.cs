namespace CommandCenter.Model
{
    // What a top-level tab actually shows. BuildSection covers GMS/CMS/Live and every extra tab
    // created via "+ Add Tab" - ServerStatus and Settings are each a single, permanent, never-
    // duplicated tab.
    public enum TabKind
    {
        BuildSection,
        ServerStatus,
        Settings
    }
}
