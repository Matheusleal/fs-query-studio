namespace FSharpQueryStudio.Ui

open System.ComponentModel
open System.Threading.Tasks
open FSharpQueryStudio.Protocol

/// One column under a `WorkspaceTableViewModel` in the Workspace panel's
/// navigation tree — see Section 10's `SchemaColumn`. `Label` is what the
/// XAML actually displays; it exists so `MainWindow.axaml` doesn't need a
/// value converter or a compound `TextBlock` to show "Name: DataType".
type WorkspaceColumnViewModel(column: SchemaColumn) =
    member _.Name = column.Name
    member _.DataType = column.DataType
    member _.Label = $"{column.Name}: {column.DataType}"

/// One table under a `WorkspaceConnectionViewModel` — see Section 10's
/// `SchemaTable`.
type WorkspaceTableViewModel(table: SchemaTable) =
    member _.Name = table.Name
    member _.Columns = table.Columns |> List.map WorkspaceColumnViewModel

/// One connection in the Workspace panel's navigation tree, wrapping a
/// `ConnectionDefinition` with the real object navigation (Connection ->
/// Tables -> Columns) that the workspace panel's flat `ListBox` used to
/// have none of. `loadSchema` is the composition root's hook into
/// `connection/schema` (`ConnectionService.schema`), reached via
/// `ShellViewModel.LoadConnectionSchemaAsync` — same no-op-when-unconfigured
/// shape as every other hook on `ShellViewModel`, except here the "no
/// hook configured" case is handled by the caller building this instance,
/// not by this type itself.
///
/// `LoadSchemaAsync` is idempotent on success (an already-loaded
/// connection's schema is never re-fetched just because the user
/// collapses and re-expands it), but a failed load is *not* sticky —
/// calling it again after an error retries the load, since the failure
/// (bad credentials, unreachable server, missing file) may no longer
/// apply.
///
/// `LoadError`/`Tables` are exposed only in already-unwrapped shapes
/// (`HasError`/`ErrorMessage`, plain `WorkspaceTableViewModel list`) —
/// binding a raw `string option` straight to a XAML `TextBlock.Text`
/// falls back to Avalonia calling `.ToString()` on the boxed
/// `FSharpOption<string>`, rendering the literal text "Some(...)"/"None"
/// instead of unwrapping it (the same bug already fixed once this session
/// in `ResultViewModel.DisplayText`).
type WorkspaceConnectionViewModel(definition: ConnectionDefinition, loadSchema: System.Guid -> Task<Result<SchemaTable list, string>>) as self =
    let mutable definition = definition
    let mutable tables: WorkspaceTableViewModel list = []
    let mutable isLoading = false
    let mutable hasLoaded = false
    let mutable errorMessage: string option = None
    let propertyChanged = Event<PropertyChangedEventHandler, PropertyChangedEventArgs>()

    let raise name =
        propertyChanged.Trigger(self, PropertyChangedEventArgs(name: string))

    interface INotifyPropertyChanged with
        [<CLIEvent>]
        member _.PropertyChanged = propertyChanged.Publish

    member _.Definition = definition
    member _.Id = definition.Id
    member _.Kind = definition.Kind
    member _.Name = definition.Name

    /// `RestJson` connectors always return an empty table list from
    /// `DiscoverSchemaAsync` (no fixed schema) — this lets the XAML skip
    /// even attempting a load for those connections.
    member _.HasSchema = definition.Kind <> ConnectorKind.RestJson

    member _.Tables = tables
    member _.IsLoading = isLoading
    member _.HasError = errorMessage.IsSome
    member _.ErrorMessage = errorMessage |> Option.defaultValue ""

    /// True only once a load has completed successfully and genuinely
    /// found nothing — distinct from "never loaded" and from "errored".
    member _.HasNoSchema = hasLoaded && errorMessage.IsNone && List.isEmpty tables

    member this.LoadSchemaAsync() : Task<unit> =
        task {
            if this.HasSchema && not hasLoaded && not isLoading then
                isLoading <- true
                raise "IsLoading"

                let! result = loadSchema definition.Id

                match result with
                | Ok schemaTables ->
                    tables <- schemaTables |> List.map WorkspaceTableViewModel
                    hasLoaded <- true
                    errorMessage <- None
                | Result.Error message -> errorMessage <- Some message

                isLoading <- false
                raise "Tables"
                raise "IsLoading"
                raise "HasError"
                raise "HasNoSchema"
        }

    /// Reconciles this instance to a freshly-fetched `definition` (e.g.
    /// after `ShellViewModel.Connections` refreshes) without discarding
    /// its already-loaded schema/expand state.
    member _.UpdateDefinition(newDefinition: ConnectionDefinition) =
        definition <- newDefinition
        raise "Name"
        raise "Kind"
