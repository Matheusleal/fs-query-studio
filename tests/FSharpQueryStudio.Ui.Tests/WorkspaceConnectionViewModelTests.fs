module FSharpQueryStudio.Ui.Tests.WorkspaceConnectionViewModelTests

open System
open System.ComponentModel
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Ui

let private testConnection (kind: ConnectorKind) (name: string) : ConnectionDefinition =
    { Id = Guid.NewGuid()
      Name = name
      Kind = kind
      Configuration = JsonDocument.Parse("""{"FilePath":"C:\\data.db"}""").RootElement
      SecretReference = None
      IsTrusted = false }

[<Fact>]
let ``WorkspaceColumnViewModel exposes Name, DataType and a combined Label`` () =
    let column = ({ Name = "Total"; DataType = "real" }: SchemaColumn)
    let vm = WorkspaceColumnViewModel(column)

    Assert.Equal("Total", vm.Name)
    Assert.Equal("real", vm.DataType)
    Assert.Equal("Total: real", vm.Label)

[<Fact>]
let ``WorkspaceTableViewModel exposes Name and maps every column`` () =
    let table: SchemaTable =
        { Name = "Orders"
          Columns =
            [ ({ Name = "Id"; DataType = "integer" }: SchemaColumn)
              ({ Name = "Total"; DataType = "real" }: SchemaColumn) ] }

    let vm = WorkspaceTableViewModel(table)

    Assert.Equal("Orders", vm.Name)
    Assert.Equal<string list>([ "Id"; "Total" ], vm.Columns |> List.map (fun c -> c.Name))

[<Fact>]
let ``a freshly-constructed WorkspaceConnectionViewModel mirrors the definition and has no tables yet`` () =
    let connection = testConnection Sqlite "Sales"
    let vm = WorkspaceConnectionViewModel(connection, fun _ -> Task.FromResult(Ok []))

    Assert.Equal(connection.Id, vm.Id)
    Assert.Equal(connection.Kind, vm.Kind)
    Assert.Equal("Sales", vm.Name)
    Assert.Empty(vm.Tables)
    Assert.False(vm.IsLoading)
    Assert.False(vm.HasError)

[<Fact>]
let ``HasSchema is false for a RestJson connection, since DiscoverSchemaAsync always returns no tables for it`` () =
    let connection = testConnection RestJson "Api"
    let vm = WorkspaceConnectionViewModel(connection, fun _ -> Task.FromResult(Ok []))

    Assert.False(vm.HasSchema)

[<Fact>]
let ``HasSchema is true for a Sqlite connection`` () =
    let connection = testConnection Sqlite "Sales"
    let vm = WorkspaceConnectionViewModel(connection, fun _ -> Task.FromResult(Ok []))

    Assert.True(vm.HasSchema)

[<Fact>]
let ``LoadSchemaAsync populates Tables from a successful load`` () =
    task {
        let connection = testConnection Sqlite "Sales"

        let tables: SchemaTable list =
            [ { Name = "Orders"
                Columns = [ ({ Name = "Id"; DataType = "integer" }: SchemaColumn) ] } ]

        let vm = WorkspaceConnectionViewModel(connection, fun _ -> Task.FromResult(Ok tables))

        do! vm.LoadSchemaAsync()

        Assert.False(vm.IsLoading)
        Assert.False(vm.HasError)
        Assert.Equal<string list>([ "Orders" ], vm.Tables |> List.map (fun t -> t.Name))
    }

[<Fact>]
let ``LoadSchemaAsync only calls the loader once across repeated calls after a successful load`` () =
    task {
        let connection = testConnection Sqlite "Sales"
        let mutable callCount = 0

        let vm =
            WorkspaceConnectionViewModel(
                connection,
                fun _ ->
                    callCount <- callCount + 1
                    Task.FromResult(Ok [])
            )

        do! vm.LoadSchemaAsync()
        do! vm.LoadSchemaAsync()

        Assert.Equal(1, callCount)
    }

[<Fact>]
let ``LoadSchemaAsync surfaces a structured error without throwing, and does not mark the connection as loaded`` () =
    task {
        let connection = testConnection Sqlite "Sales"
        let mutable callCount = 0

        let vm =
            WorkspaceConnectionViewModel(
                connection,
                fun _ ->
                    callCount <- callCount + 1
                    Task.FromResult(Result.Error "file not found")
            )

        do! vm.LoadSchemaAsync()

        Assert.True(vm.HasError)
        Assert.Equal("file not found", vm.ErrorMessage)
        Assert.False(vm.IsLoading)
        Assert.Empty(vm.Tables)

        do! vm.LoadSchemaAsync()

        Assert.Equal(2, callCount) // A failed load can be retried on the next expand.
    }

[<Fact>]
let ``LoadSchemaAsync is a no-op for a RestJson connection, since it has no schema to discover`` () =
    task {
        let connection = testConnection RestJson "Api"
        let mutable callCount = 0

        let vm =
            WorkspaceConnectionViewModel(
                connection,
                fun _ ->
                    callCount <- callCount + 1
                    Task.FromResult(Ok [])
            )

        do! vm.LoadSchemaAsync()

        Assert.Equal(0, callCount)
    }

[<Fact>]
let ``PropertyChanged fires for Tables, IsLoading and HasError after LoadSchemaAsync`` () =
    task {
        let connection = testConnection Sqlite "Sales"
        let vm = WorkspaceConnectionViewModel(connection, fun _ -> Task.FromResult(Ok []))

        let raisedFor = ResizeArray<string>()
        (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor.Add(args.PropertyName))

        do! vm.LoadSchemaAsync()

        Assert.Contains("Tables", raisedFor)
        Assert.Contains("IsLoading", raisedFor)
        Assert.Contains("HasError", raisedFor)
    }

[<Fact>]
let ``UpdateDefinition replaces the underlying definition and raises PropertyChanged for Name`` () =
    let connection = testConnection Sqlite "Sales"
    let vm = WorkspaceConnectionViewModel(connection, fun _ -> Task.FromResult(Ok []))

    let raisedFor = ResizeArray<string>()
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor.Add(args.PropertyName))

    let renamed = { connection with Name = "Sales (renamed)" }
    vm.UpdateDefinition(renamed)

    Assert.Equal("Sales (renamed)", vm.Name)
    Assert.Contains("Name", raisedFor)
