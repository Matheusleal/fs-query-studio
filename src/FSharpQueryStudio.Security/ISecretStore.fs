namespace FSharpQueryStudio.Security

/// Platform-neutral secret storage abstraction over the OS credential
/// store — see Sections 13-14. `ConnectionDefinition.SecretReference`
/// values are opaque keys into this store; the store is the only place
/// an actual credential value ever lives, never `workspace.json` or a
/// log line.
type ISecretStore =
    abstract Save: reference: string -> secret: string -> unit
    abstract TryRead: reference: string -> string option
    abstract Delete: reference: string -> unit
