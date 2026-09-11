namespace CommandCenter.Model
{
    // What a top-level tab actually shows. BuildSection covers GMS/CMS/Live and every extra tab
    // created via "+ Add Tab" - ServerStatus and Settings are each a single, permanent, never-
    // duplicated tab. ActiveClients is different from all three: MainViewModel creates and removes
    // it at runtime (see EnsureActiveClientsTab/OnActiveClientsTrackingEmpty) rather than it ever
    // living in AppSettings.Tabs, so it never shows up in Settings and is never persisted - see
    // ViewModel/ActiveClientsViewModel.cs.
    public enum TabKind
    {
        BuildSection,
        ServerStatus,
        Settings,
        ActiveClients
    }
}
