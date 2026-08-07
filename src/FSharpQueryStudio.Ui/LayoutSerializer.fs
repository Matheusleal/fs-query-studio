namespace FSharpQueryStudio.Ui

open System
open System.Text.Json.Serialization.Metadata
open Dock.Model.Core
open Dock.Model.Controls
open Dock.Serializer.SystemTextJson

/// Builds the `DockSerializer` layout persistence (`App.axaml.fs`, Task
/// 12) actually needs to round-trip a real, live-built dock tree without
/// throwing.
///
/// The installed v12.1.0 `Dock.Serializer.SystemTextJson` package
/// configures `ReferenceHandler.Preserve` internally, but its own
/// `JsonConverterList<T>.Write` serializes each list element via a
/// *nested* `JsonSerializer.Serialize(writer, item, options)` call
/// (confirmed by reading the package's real source on GitHub) — and a
/// nested `Serialize` call starts its own independent reference-tracking
/// scope, so `Preserve`'s cycle protection does not span a list
/// boundary. Every `IDockable.Owner` (a `[DataMember]`, not
/// `[IgnoreDataMember]`, back-reference to the parent — confirmed via
/// reflection over `Dock.Model.Mvvm.Core.DockableBase`) points back up
/// through exactly one such boundary, so serializing any real,
/// non-trivial dock tree throws `JsonException: A possible object cycle
/// was detected` — reproduced empirically against a real `DockFactory`
/// layout built the same way `MainWindow`'s constructor builds one, not
/// merely assumed from the stack trace.
///
/// `Owner` is also fully redundant on the wire: it's entirely derived
/// from parent/child containment, and `IFactory.InitLayout` already
/// reconstructs it after every deserialize — the same call `MainWindow`'s
/// constructor already makes on a freshly-built layout. So the fix here
/// is to omit `Owner` from the JSON via a `JsonTypeInfo` modifier
/// attached to the package's own polymorphic type resolver, rather than
/// hand-rolling a parallel resolver that would have to reimplement its
/// `$type`-discriminator handling for every `IDockable` from scratch.
/// `DockModelPolymorphicTypeResolver` itself is `internal` to
/// `Dock.Serializer.SystemTextJson`, but it derives from the public
/// `System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver`
/// (confirmed via reflection), which exposes the public `Modifiers` list
/// this needs — reachable via `Activator.CreateInstance` even though the
/// concrete type name can't appear in source.
module LayoutSerializer =

    let private resolverTypeName = "Dock.Serializer.SystemTextJson.DockModelPolymorphicTypeResolver"

    let private findResolverType () : Type =
        let asm =
            AppDomain.CurrentDomain.GetAssemblies()
            |> Array.find (fun a -> a.GetName().Name = "Dock.Serializer.SystemTextJson")

        asm.GetType(resolverTypeName)

    /// A `DockSerializer` that can actually serialize a real dock tree
    /// (see the module doc comment above). Used for both directions:
    /// omitting `Owner` from the write side has no effect on read, since
    /// nothing ever set it from JSON either way — `IFactory.InitLayout`
    /// is what reconstructs it, on both a freshly-built and a restored
    /// layout alike.
    let create () : DockSerializer =
        let resolverType = findResolverType ()

        let resolver =
            Activator.CreateInstance(resolverType) :?> DefaultJsonTypeInfoResolver

        resolver.Modifiers.Add(fun typeInfo ->
            for prop in typeInfo.Properties do
                if prop.Name = "Owner" then
                    prop.Get <- null)

        DockSerializer(resolver :> IJsonTypeInfoResolver)

    /// The same nested-`Serialize`-per-list-boundary defect behind the
    /// cycle above (see the module doc comment) has a second symptom:
    /// `ActiveDockable`/`DefaultDockable`/`FocusedDockable` are written
    /// *inline* within their owning `IDock`'s own serialize call (so
    /// `Preserve` correctly tags the first occurrence there), but that
    /// same instance also gets written a second time - as an unrelated,
    /// full duplicate copy, not a `$ref` - when its owner's
    /// `VisibleDockables` list is written moments later via the nested
    /// per-item nested call `JsonConverterList<T>.Write` makes. After
    /// deserialize, `dock.ActiveDockable` and `dock.VisibleDockables.[i]`
    /// that are supposed to be the *same* dockable are two structurally
    /// identical but reference-distinct objects - confirmed empirically
    /// (`root.ActiveDockable` fails a reference-equality check against
    /// `root.VisibleDockables.[0]` after a real round-trip, and the same
    /// failure recurs at every `IDock` level, not just the root). A
    /// fresh, non-restored layout never has this duplication.
    ///
    /// Repairs it by walking the tree and, for each `IDock`, replacing
    /// `ActiveDockable`/`DefaultDockable`/`FocusedDockable` with the
    /// matching entry from that same dock's own `VisibleDockables` list
    /// whenever it isn't already one of them by reference. Matching is
    /// done *locally* per container (by `Id` when the duplicate's `Id` is
    /// non-null and found among the children, falling back to "the only
    /// child" when there's exactly one) rather than via a single
    /// tree-wide `Id`-keyed map: several containers in a typical layout
    /// (this app's `ProportionalDock` rows/columns, for instance) never
    /// get an `Id` assigned at all, so a global map keyed by `Id` would
    /// silently collide different anonymous nodes onto the same key.
    /// Call this on a freshly deserialized tree before `InitLayout`.
    ///
    /// The tree walk itself descends through `HiddenDockables` and the
    /// four `*PinnedDockables` lists too, not just `VisibleDockables` -
    /// matching `DockFactory.FindById`'s reach, so a `ToolDock` nested
    /// inside a hidden or pinned branch still gets normalized. `current`/
    /// `dock.VisibleDockables` are both null-checked: an `IDock` with no
    /// `VisibleDockables` at all is reachable from a hand-edited or
    /// truncated `ui-state.json` (App.axaml.fs's `try`/`with` around the
    /// whole restore already turns that into "fall back to the default
    /// layout" rather than a crash, but this guard keeps `normalize`
    /// itself well-behaved instead of relying solely on that outer net).
    let normalize (root: IDockable) : unit =
        let childLists (dock: IDock) : System.Collections.Generic.IList<IDockable> list =
            [ dock.VisibleDockables ]
            @ (match dock with
               | :? IRootDock as r ->
                   [ r.HiddenDockables
                     r.LeftPinnedDockables
                     r.RightPinnedDockables
                     r.TopPinnedDockables
                     r.BottomPinnedDockables ]
               | _ -> [])
            |> List.filter (fun l -> not (isNull (box l)))

        let resolveDuplicate (dock: IDock) (current: IDockable) : IDockable option =
            if isNull (box current) || isNull (box dock.VisibleDockables) then
                None
            elif dock.VisibleDockables |> Seq.exists (fun d -> obj.ReferenceEquals(d, current)) then
                None // already one of this dock's own children - not a duplicate
            else
                match
                    dock.VisibleDockables
                    |> Seq.tryFind (fun d -> not (isNull current.Id) && d.Id = current.Id)
                with
                | Some byId -> Some byId
                | None -> if dock.VisibleDockables.Count = 1 then Some dock.VisibleDockables.[0] else None

        let repoint (dock: IDock) (get: unit -> IDockable) (set: IDockable -> unit) =
            match resolveDuplicate dock (get ()) with
            | Some canonical -> set canonical
            | None -> ()

        let rec walk (node: IDockable) =
            match node with
            | :? IDock as dock ->
                repoint dock (fun () -> dock.ActiveDockable) (fun v -> dock.ActiveDockable <- v)
                repoint dock (fun () -> dock.DefaultDockable) (fun v -> dock.DefaultDockable <- v)
                repoint dock (fun () -> dock.FocusedDockable) (fun v -> dock.FocusedDockable <- v)

                for l in childLists dock do
                    for child in l do
                        walk child
            | _ -> ()

        walk root
