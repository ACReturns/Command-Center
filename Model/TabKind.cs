namespace CommandCenter.Model
{
    // What a top-level tab actually shows. BuildSection covers GMS/CMS/Live and every extra tab
    // created via "+ Add Tab" - ServerStatus, OpTool and Settings are each a single, permanent,
    // never-duplicated tab. ActiveClients is different from all of those: MainViewModel creates
    // and removes it at runtime (see EnsureActiveClientsTab/OnActiveClientsTrackingEmpty) rather
    // than it ever living in AppSettings.Tabs, so it never shows up in Settings and is never
    // persisted - see ViewModel/ActiveClientsViewModel.cs.
    //
    // settings.json stores this enum as its underlying int (no JsonStringEnumConverter), so new
    // members must only ever be APPENDED - inserting one mid-list would silently re-map every
    // existing install's saved tabs to the wrong kind on next load.
    public enum TabKind
    {
        BuildSection,
        ServerStatus,
        Settings,
        ActiveClients,
        OpTool,
        MaintenanceVerification
    }
}
