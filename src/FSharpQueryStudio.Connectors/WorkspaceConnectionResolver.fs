namespace FSharpQueryStudio.Connectors

open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

/// Builds a live `IConnector` from a registered `ConnectionDefinition`
/// by delegating validation and instantiation to the central `ConnectorRegistry`.
/// When `workspaceDirectory` is provided, relative local file paths declared by a
/// connector's `File` form fields are resolved against the workspace project root
/// (the folder containing `.fsqs`), never against the worker's transient working directory.
type WorkspaceConnectionResolver(connections: ConnectionDefinition list, secretStore: ISecretStore, ?workspaceDirectory: string) =
    do BuiltinConnectors.registerDefaults ()

    let baseDirectory =
        workspaceDirectory
        |> Option.map ConnectorDescriptor.projectRootOf
        |> Option.filter (fun d -> not (System.String.IsNullOrWhiteSpace d))

    let build (definition: ConnectionDefinition) : IConnector =
        let kindId = ConnectorRegistry.KindIdOf definition.Kind
        match ConnectorRegistry.Instance.TryGet kindId with
        | Some _ ->
            ConnectorRegistry.Instance.Resolve(definition, secretStore, baseDirectory)
        | None ->
            failwith $"No connector descriptor registered for kind '{kindId}' (connection '{definition.Name}')."

    /// The project root used to resolve relative file paths, if any.
    member _.BaseDirectory = baseDirectory

    interface IConnectionResolver with
        member _.Resolve(name: string) : IConnector =
            match connections |> List.tryFindBack (fun c -> System.String.Equals(c.Name, name, System.StringComparison.OrdinalIgnoreCase)) with
            | Some definition -> build definition
            | None -> failwith $"No connection named '{name}' is registered."

    interface IWorkspaceAwareResolver with
        member _.BaseDirectory = baseDirectory
