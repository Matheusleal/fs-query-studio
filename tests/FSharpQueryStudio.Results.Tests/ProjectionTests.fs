module ProjectionTests

open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Results

let private envOf (value: obj) =
    let env, _, _ = Projection.projectWithCursor value 0L
    env

let private retainedOf (value: obj) =
    let _, _, retained = Projection.projectWithCursor value 0L
    retained

[<Fact>]
let ``a ConnectorQueryResult tail value projects to a Table with the connector's own columns`` () =
    let query: ConnectorQueryResult =
        { Columns = [ "id"; "name" ]
          Rows = seq { yield [| box 1; box "Ada" |]; yield [| box 2; box "Grace" |] } }

    let env = envOf (box query)

    Assert.Equal(ResultKind.Table, env.Kind)
    Assert.Equal<string list>([ "id"; "name" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal(2, env.Rows.Length)
    Assert.Equal<obj array>([| box 1; box "Ada" |], env.Rows.[0])
    Assert.False(env.IsTruncated)

[<Fact>]
let ``a ConnectorQueryResult tail value is fully retained for export, even past the preview limit`` () =
    let rowCount = Projection.PreviewRowLimit + 10
    let query: ConnectorQueryResult =
        { Columns = [ "n" ]; Rows = seq { for i in 1..rowCount -> [| box i |] } }

    let env, cursor, retained = Projection.projectWithCursor (box query) 0L

    Assert.True(env.IsTruncated)
    Assert.Equal(Projection.PreviewRowLimit, env.Rows.Length)
    Assert.True(cursor.IsNone)                       // materialized, not streamed
    Assert.True(retained.IsSome)
    Assert.Equal(rowCount, retained.Value.Rows.Length)
    Assert.Equal<string list>([ "n" ], retained.Value.Columns)
