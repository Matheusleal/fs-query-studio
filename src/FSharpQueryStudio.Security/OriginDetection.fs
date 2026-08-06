namespace FSharpQueryStudio.Security

open FSharpQueryStudio.Protocol

/// How a workspace came to be opened — the input `OriginDetection`
/// classifies into a `ScriptOrigin` (Section 13).
type WorkspaceOpenSource =
    /// A brand-new workspace directory created by this app.
    | CreatedFresh
    /// An existing directory opened from outside any workspace this app
    /// created — a plain folder path, or one under `downloadsDir`.
    | ExternalPath of path: string
    /// Opened from a source explicitly marked as shared (e.g. a network
    /// share or a workspace carrying a shared-origin marker file).
    | MarkedShared

/// Section 13's origin metadata, derived from how a workspace was
/// opened rather than inferred from its contents.
module OriginDetection =

    let detectOrigin (downloadsDir: string) (source: WorkspaceOpenSource) : ScriptOrigin =
        match source with
        | CreatedFresh -> ScriptOrigin.Local
        | MarkedShared -> ScriptOrigin.SharedWorkspace
        | ExternalPath path ->
            let normalizedPath = path.Replace('/', '\\').TrimEnd('\\')
            let normalizedDownloads = downloadsDir.Replace('/', '\\').TrimEnd('\\')

            if normalizedPath.StartsWith(normalizedDownloads + "\\", System.StringComparison.OrdinalIgnoreCase)
               || normalizedPath.Equals(normalizedDownloads, System.StringComparison.OrdinalIgnoreCase) then
                ScriptOrigin.Downloaded
            else
                ScriptOrigin.Imported
