namespace FSharpQueryStudio.Connectors

open System
open System.Collections.Concurrent
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

/// Contract for the central connector discovery and resolution registry.
type IConnectorRegistry =
    abstract Register: descriptor: IConnectorDescriptor -> unit
    abstract TryGet: kindId: string -> IConnectorDescriptor option
    abstract AllDescriptors: unit -> IConnectorDescriptor list
    abstract Resolve: definition: ConnectionDefinition * secretStore: ISecretStore -> IConnector

/// Thread-safe registry holding registered connector descriptors and factories.
type ConnectorRegistry() =
    let descriptors = ConcurrentDictionary<string, IConnectorDescriptor>(StringComparer.OrdinalIgnoreCase)

    /// Maps legacy `ConnectorKind` union to string kind identifier.
    static member KindIdOf(kind: ConnectorKind) : string =
        match kind with
        | SqlServer -> "SqlServer"
        | PostgreSql -> "PostgreSql"
        | Sqlite -> "Sqlite"
        | Csv -> "Csv"
        | Excel -> "Excel"
        | RestJson -> "RestJson"
        | Custom s -> s

    member _.Register(descriptor: IConnectorDescriptor) : unit =
        if obj.ReferenceEquals(descriptor, null) then nullArg (nameof descriptor)
        descriptors.[descriptor.KindId] <- descriptor

    member _.TryGet(kindId: string) : IConnectorDescriptor option =
        if String.IsNullOrWhiteSpace kindId then None
        else
            match descriptors.TryGetValue kindId with
            | true, desc -> Some desc
            | false, _ -> None

    member _.AllDescriptors() : IConnectorDescriptor list =
        descriptors.Values |> Seq.toList

    member this.Resolve(definition: ConnectionDefinition, secretStore: ISecretStore) : IConnector =
        let kindId = ConnectorRegistry.KindIdOf definition.Kind
        match this.TryGet kindId with
        | Some desc ->
            desc.Factory.Create(definition.Configuration, secretStore, definition.SecretReference)
        | None ->
            failwith $"No connector descriptor registered for kind '{kindId}' (connection '{definition.Name}')."

    interface IConnectorRegistry with
        member this.Register(descriptor: IConnectorDescriptor) = this.Register descriptor
        member this.TryGet(kindId: string) = this.TryGet kindId
        member this.AllDescriptors() = this.AllDescriptors()
        member this.Resolve(definition: ConnectionDefinition, secretStore: ISecretStore) = this.Resolve(definition, secretStore)

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ConnectorRegistry =
    let private defaultInstance = ConnectorRegistry()
    
    /// Process-wide default singleton registry.
    let Instance : ConnectorRegistry = defaultInstance
