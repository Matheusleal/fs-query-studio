namespace FSharpQueryStudio.Ui

open System.Runtime.Serialization
open Dock.Model.Controls
open Dock.Model.Mvvm.Controls

/// A `Tool` whose content Dock's own default rendering can find — see
/// `ScriptDocument`'s doc comment for the underlying bug: Dock's real
/// default template resolves a tool's content via
/// `Dock.Model.Controls.IToolContent`, not a plain `Tool.Context`.
///
/// `Content` is a plain settable property, not a constructor parameter,
/// so this type keeps the parameterless constructor
/// `Dock.Serializer.SystemTextJson` needs to deserialize a persisted
/// layout. Unlike `ScriptDocument` (whose document tabs are structurally
/// excluded from the wire format — see `DockFactory.SerializeLayout`), a
/// tool like "Results" *is* part of the normal serialized layout, so it
/// has to survive a real round-trip.
///
/// `[<IgnoreDataMember>]` keeps the live `ResultsView`/`NativeWebView`
/// sitting in `Content` off the wire entirely — the same reason
/// `DockableBase.Context` already carries that attribute, and for the
/// identical failure mode `DockFactory.SerializeLayout` already guards
/// against for `Context` (a live Avalonia control can't be
/// JSON-serialized). `DockFactory.RestoreToolPanes` repairs `Content`
/// back onto the restored instance after a layout restore, the same way
/// it already repairs `OriginalOwner`.
type ContentTool() =
    inherit Tool()

    [<IgnoreDataMember>]
    member val Content: obj = null with get, set

    interface IToolContent with
        member this.Content
            with get () = this.Content
            and set value = this.Content <- value
