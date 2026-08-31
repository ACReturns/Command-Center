namespace CommandCenter.Model
{
    // One file or subfolder shown in a build section's Documents list (see DocumentsService and
    // BuildSectionViewModel.Documents). FullPath is what OpenDocumentCommand hands to
    // Process.Start - works for both a file (opens in its default app) and a folder (opens in
    // Explorer) since both just go through ShellExecute.
    public record DocumentEntry(string Name, string FullPath, bool IsDirectory);
}
