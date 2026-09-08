module ProjectionTests

open Xunit
open System.Collections.Generic
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

type private Person = { Id: int; Name: string }

[<Fact>]
let ``a record sequence infers columns from reflected field names in declaration order`` () =
    let people = [ { Id = 1; Name = "Ada" }; { Id = 2; Name = "Grace" } ]
    let env = envOf (box people)
    Assert.Equal(ResultKind.Table, env.Kind)
    Assert.Equal<string list>([ "Id"; "Name" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; box "Ada" |], env.Rows.[0])
    Assert.Equal<obj array>([| box 2; box "Grace" |], env.Rows.[1])
    Assert.True((retainedOf (box people)).IsSome)

[<Fact>]
let ``a tuple sequence maps each element to a positional Item column`` () =
    let pairs = [ (1, "Ada"); (2, "Grace") ]
    let env = envOf (box pairs)
    Assert.Equal<string list>([ "Item1"; "Item2" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; box "Ada" |], env.Rows.[0])
    Assert.Equal<obj array>([| box 2; box "Grace" |], env.Rows.[1])
    Assert.True((retainedOf (box pairs)).IsSome)

[<Fact>]
let ``a Map sequence infers the union of keys in first-seen order`` () =
    let rows =
        [ Map.ofList [ ("id", box 1); ("name", box "Ada") ]
          Map.ofList [ ("id", box 2); ("extra", box "x") ] ]
    let env = envOf (box rows)
    Assert.Equal<string list>([ "id"; "name"; "extra" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; box "Ada"; null |], env.Rows.[0])
    Assert.Equal<obj array>([| box 2; null; box "x" |], env.Rows.[1])

[<Fact>]
let ``a Map of string,int sequence keeps sorted keys but first-seen column order`` () =
    let rows =
        [ Map.ofList [ ("id", 1); ("count", 5) ]
          Map.ofList [ ("id", 2); ("extra", 10) ] ]
    let env = envOf (box rows)
    Assert.Equal<string list>([ "count"; "id"; "extra" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 5; box 1; null |], env.Rows.[0])
    Assert.Equal<obj array>([| null; box 2; box 10 |], env.Rows.[1])

[<Fact>]
let ``a Dictionary<string,obj> sequence infers union-of-keys columns`` () =
    let d1 = Dictionary<string, obj>()
    d1.Add("id", box 1); d1.Add("name", box "Alice")
    let d2 = Dictionary<string, obj>()
    d2.Add("id", box 2); d2.Add("age", box 30)
    let env = envOf (box [ d1; d2 ])
    Assert.Equal<string list>([ "id"; "name"; "age" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; box "Alice"; null |], env.Rows.[0])
    Assert.Equal<obj array>([| box 2; null; box 30 |], env.Rows.[1])

[<Fact>]
let ``a nested collection consumes row 0 as headers`` () =
    let grid = [ [| "id"; "name" |]; [| "1"; "Ada" |]; [| "2"; "Grace" |] ]
    let env = envOf (box grid)
    Assert.Equal<string list>([ "id"; "name" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal(2, env.Rows.Length)
    Assert.Equal<obj array>([| box "1"; box "Ada" |], env.Rows.[0])

[<Fact>]
let ``ragged nested rows pad short rows with null and truncate long rows`` () =
    let grid: obj array list =
        [ [| box "a"; box "b"; box "c" |]; [| box 1 |]; [| box 2; box 3; box 4; box 5 |] ]
    let env = envOf (box grid)
    Assert.Equal<string list>([ "a"; "b"; "c" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; null; null |], env.Rows.[0])
    Assert.Equal<obj array>([| box 2; box 3; box 4 |], env.Rows.[1])

[<Fact>]
let ``a sequence of strings renders as one Value column, not exploded into characters`` () =
    let env = envOf (box [ "Ada"; "Grace" ])
    Assert.Equal<string list>([ "Value" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal(2, env.Rows.Length)
    Assert.Equal<obj array>([| box "Ada" |], env.Rows.[0])

[<Fact>]
let ``a scalar sequence within the preview limit is one Value column, no cursor, not retained`` () =
    let env, cursor, retained = Projection.projectWithCursor (box [ 1; 2; 3 ]) 0L
    Assert.Equal<string list>([ "Value" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal(3, env.Rows.Length)
    Assert.Equal<obj array>([| box 1 |], env.Rows.[0])
    Assert.True(cursor.IsNone)
    Assert.True(retained.IsNone)

[<Fact>]
let ``a scalar sequence past the preview limit keeps a streaming cursor and is not retained`` () =
    let env, cursor, retained = Projection.projectWithCursor (box [ 1 .. Projection.PreviewRowLimit + 10 ]) 0L
    Assert.True(env.IsTruncated)
    Assert.Equal(Projection.PreviewRowLimit, env.Rows.Length)
    Assert.True(cursor.IsSome)
    Assert.True(retained.IsNone)

[<Fact>]
let ``an empty sequence is an empty Table`` () =
    let env, _, retained = Projection.projectWithCursor (box (List.empty<Person>)) 0L
    Assert.Equal(ResultKind.Table, env.Kind)
    Assert.Empty(env.Schema)
    Assert.Empty(env.Rows)
    Assert.True(retained.IsSome)

[<Fact>]
let ``a single record is a one-row Table and is retained`` () =
    let env, _, retained = Projection.projectWithCursor (box { Id = 1; Name = "Ada" }) 0L
    Assert.Equal(ResultKind.Table, env.Kind)
    Assert.Equal<string list>([ "Id"; "Name" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; box "Ada" |], env.Rows.[0])
    Assert.True(retained.IsSome)

[<Fact>]
let ``a bare int is a Scalar, not a Table`` () =
    let env = envOf (box 42)
    Assert.Equal(ResultKind.Scalar, env.Kind)
    Assert.Equal(Some "42", env.DisplayText)

[<Fact>]
let ``a bare string is Text`` () =
    let env = envOf (box "hello")
    Assert.Equal(ResultKind.Text, env.Kind)
    Assert.Equal(Some "hello", env.DisplayText)

[<Fact>]
let ``null is NoValue`` () =
    let env = envOf null
    Assert.Equal(ResultKind.NoValue, env.Kind)

[<Fact>]
let ``a mixed-shape sequence falls back to a Text envelope instead of throwing`` () =
    let mixed: obj list = [ box (1, "Ada"); box { Id = 2; Name = "Grace" } ]
    let env, cursor, retained = Projection.projectWithCursor (box mixed) 0L
    Assert.Equal(ResultKind.Text, env.Kind)
    Assert.True(cursor.IsNone)
    Assert.True(retained.IsNone)

[<Fact>]
let ``an infinite scalar sequence streams a bounded preview instead of hanging`` () =
    let infinite = Seq.initInfinite id
    let env, cursor, retained = Projection.projectWithCursor (box infinite) 0L
    Assert.Equal(ResultKind.Table, env.Kind)
    Assert.Equal<string list>([ "Value" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal(Projection.PreviewRowLimit, env.Rows.Length)
    Assert.True(env.IsTruncated)
    Assert.True(cursor.IsSome)
    Assert.True(retained.IsNone)
