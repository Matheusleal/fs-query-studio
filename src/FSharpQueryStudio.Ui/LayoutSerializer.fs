namespace FSharpQueryStudio.Ui

open System
open System.Text.Json.Serialization.Metadata
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
