namespace FSharpQueryStudio.Connectors

open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

/// Builds a live `IConnector` from a registered `ConnectionDefinition`
/// by delegating validation and instantiation to the central `ConnectorRegistry`.
type WorkspaceConnectionResolver(connections: ConnectionDefinition list, secretStore: ISecretStore) =
    do BuiltinConnectors.registerDefaults ()

    let build (definition: ConnectionDefinition) : IConnector =
        let kindId = ConnectorRegistry.KindIdOf definition.Kind
        match ConnectorRegistry.Instance.TryGet kindId with
        | Some _ ->
            ConnectorRegistry.Instance.Resolve(definition, secretStore)
        | None ->
            failwith $"No connector descriptor registered for kind '{kindId}' (connection '{definition.Name}')."

    interface IConnectionResolver with
        member _.Resolve(name: string) : IConnector =
            match connections |> List.tryFindBack (fun c -> System.String.Equals(c.Name, name, System.StringComparison.OrdinalIgnoreCase)) with
            | Some definition -> build definition
            | None -> failwith $"No connection named '{name}' is registered."
