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
        {
            Columns = [ "id"; "name" ]
            Rows =
                seq {
                    yield [| box 1; box "Ada" |]
                    yield [| box 2; box "Grace" |]
                }
        }

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
        {
            Columns = [ "n" ]
            Rows = seq { for i in 1..rowCount -> [| box i |] }
        }

    let env, cursor, retained = Projection.projectWithCursor (box query) 0L

    Assert.True(env.IsTruncated)
    Assert.Equal(Projection.PreviewRowLimit, env.Rows.Length)
    Assert.True(cursor.IsNone) // materialized, not streamed
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
    // The schema carries the reflected property type names, not "obj".
    Assert.Equal<string list>([ "Int32"; "String" ], env.Schema |> List.map (fun c -> c.DisplayType))
    Assert.Equal<obj array>([| box 1; box "Ada" |], env.Rows.[0])
    Assert.Equal<obj array>([| box 2; box "Grace" |], env.Rows.[1])
    Assert.True((retainedOf (box people)).IsSome)

[<Fact>]
let ``a record sequence past the preview limit truncates the preview but retains every row`` () =
    let rowCount = Projection.PreviewRowLimit + 25
    let people = [ for i in 1..rowCount -> { Id = i; Name = sprintf "n%d" i } ]
    let env, cursor, retained = Projection.projectWithCursor (box people) 0L
    Assert.True(env.IsTruncated)
    Assert.Equal(Projection.PreviewRowLimit, env.Rows.Length)
    Assert.True(cursor.IsNone)
    Assert.True(retained.IsSome)
    Assert.Equal(rowCount, retained.Value.Rows.Length)

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
        [
            Map.ofList [ ("id", box 1); ("name", box "Ada") ]
            Map.ofList [ ("id", box 2); ("extra", box "x") ]
        ]

    let env = envOf (box rows)
    Assert.Equal<string list>([ "id"; "name"; "extra" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; box "Ada"; null |], env.Rows.[0])
    Assert.Equal<obj array>([| box 2; null; box "x" |], env.Rows.[1])
    Assert.True((retainedOf (box rows)).IsSome)

[<Fact>]
let ``a Map of string,int sequence keeps sorted keys but first-seen column order`` () =
    let rows =
        [
            Map.ofList [ ("id", 1); ("count", 5) ]
            Map.ofList [ ("id", 2); ("extra", 10) ]
        ]

    let env = envOf (box rows)
    Assert.Equal<string list>([ "count"; "id"; "extra" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 5; box 1; null |], env.Rows.[0])
    Assert.Equal<obj array>([| null; box 2; box 10 |], env.Rows.[1])

[<Fact>]
let ``a Dictionary<string,obj> sequence infers union-of-keys columns`` () =
    let d1 = Dictionary<string, obj>()
    d1.Add("id", box 1)
    d1.Add("name", box "Alice")
    let d2 = Dictionary<string, obj>()
    d2.Add("id", box 2)
    d2.Add("age", box 30)
    let env = envOf (box [ d1; d2 ])
    Assert.Equal<string list>([ "id"; "name"; "age" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; box "Alice"; null |], env.Rows.[0])
    Assert.Equal<obj array>([| box 2; null; box 30 |], env.Rows.[1])

[<Fact>]
let ``large sequence of FSharpMap rows projects accurately and efficiently with cached accessors`` () =
    let maps =
        [
            for i in 1..200 -> Map.ofList [ ("id", box i); ("name", box $"Item {i}"); ("active", box (i % 2 = 0)) ]
        ]

    let env = envOf (box maps)
    Assert.Equal<string list>([ "active"; "id"; "name" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal(200, env.Rows.Length)
    Assert.Equal<obj array>([| box false; box 1; box "Item 1" |], env.Rows.[0])
    Assert.Equal<obj array>([| box true; box 200; box "Item 200" |], env.Rows.[199])

[<Fact>]
let ``a nested collection consumes row 0 as headers`` () =
    let grid = [ [| "id"; "name" |]; [| "1"; "Ada" |]; [| "2"; "Grace" |] ]
    let env = envOf (box grid)
    Assert.Equal<string list>([ "id"; "name" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal(2, env.Rows.Length)
    Assert.Equal<obj array>([| box "1"; box "Ada" |], env.Rows.[0])
    Assert.True((retainedOf (box grid)).IsSome)

[<Fact>]
let ``ragged nested rows pad short rows with null and truncate long rows`` () =
    let grid: obj array list =
        [
            [| box "a"; box "b"; box "c" |]
            [| box 1 |]
            [| box 2; box 3; box 4; box 5 |]
        ]

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
    let env, cursor, retained =
        Projection.projectWithCursor (box [ 1 .. Projection.PreviewRowLimit + 10 ]) 0L

    Assert.True(env.IsTruncated)
    Assert.Equal(Projection.PreviewRowLimit, env.Rows.Length)
    Assert.True(cursor.IsSome)
    Assert.True(retained.IsNone)

[<Fact>]
let ``an empty sequence is an empty Table that retains nothing`` () =
    let env, cursor, retained =
        Projection.projectWithCursor (box (List.empty<Person>)) 0L

    Assert.Equal(ResultKind.Table, env.Kind)
    Assert.Empty(env.Schema)
    Assert.Empty(env.Rows)
    Assert.True(cursor.IsNone)
    // A cell ending in `[]` must not blank the notebook's exportable grid.
    Assert.True(retained.IsNone)

[<Fact>]
let ``a single record is a one-row Table and is retained`` () =
    let env, _, retained =
        Projection.projectWithCursor (box { Id = 1; Name = "Ada" }) 0L

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

[<Fact>]
let ``extractAnonymousRecordBlocks extracts property names from single line record`` () =
    let code =
        """let item = {| Order = 1001; Date = "2026-01-01"; Status = "Completed" |}"""

    let blocks = Projection.extractAnonymousRecordBlocks code
    Assert.Single(blocks) |> ignore
    Assert.Equal<string list>([ "Order"; "Date"; "Status" ], blocks.[0])

[<Fact>]
let ``extractAnonymousRecordBlocks extracts property names from query select clause in user declared order`` () =
    let code =
        """
query {
    for c in Connections.csv do
    leftOuterJoin o in Connections.orders on (c.Id = o.CustomerId) into ords
    for ord in ords.DefaultIfEmpty() do
    leftOuterJoin r in Connections.returns on (ord.Id = r.OrderId) into rets
    for r in rets.DefaultIfEmpty() do
    select {|
        Order = ord.Id
        Date = ord.Date
        Status = ord.Status
        Ccy = ord.Ccy
        Item = ord.Item
        Qtt = ord.Qtt
        Total = ord.Total
        Carrier = c.Name
        Returned = r.Status
    |}
}
"""

    let blocks = Projection.extractAnonymousRecordBlocks code
    Assert.Single(blocks) |> ignore

    let expected =
        [
            "Order"
            "Date"
            "Status"
            "Ccy"
            "Item"
            "Qtt"
            "Total"
            "Carrier"
            "Returned"
        ]

    Assert.Equal<string list>(expected, blocks.[0])

[<Fact>]
let ``extractAnonymousRecordBlocks handles backticks and comments inside anonymous record`` () =
    let code =
        """
select {|
    // Header id
    ``Order ID`` = ord.Id
    (* Order timestamp *)
    Date = ord.Date
    Status = if ord.Total > 0M then "Paid" else "Unpaid"
|}
"""

    let blocks = Projection.extractAnonymousRecordBlocks code
    Assert.Single(blocks) |> ignore
    Assert.Equal<string list>([ "Order ID"; "Date"; "Status" ], blocks.[0])

[<Fact>]
let ``extractAnonymousRecordBlocks ignores nested anonymous record properties in outer block`` () =
    let code =
        """
{|
    Order = 1
    Customer = {| Name = "Alice"; City = "Paris" |}
    Total = 50.0M
|}
"""

    let blocks = Projection.extractAnonymousRecordBlocks code
    Assert.Equal(2, blocks.Length)
    Assert.Equal<string list>([ "Name"; "City" ], blocks.[0])
    Assert.Equal<string list>([ "Order"; "Customer"; "Total" ], blocks.[1])

[<Fact>]
let ``projectWithCursorDetailed renders anonymous record columns in declared select order instead of alphabetical`` () =
    let item =
        {|
            Order = 1001
            Date = "2026-01-01"
            Status = "Completed"
        |}

    let code =
        """
select {|
    Order = 1001
    Date = "2026-01-01"
    Status = "Completed"
|}
"""
    // In F#, reflected fields on {| Order = ...; Date = ...; Status = ... |} are alphabetically: Date, Order, Status
    let envDefault, _, _ = Projection.projectWithCursorDetailed (box item) 0L None None
    Assert.Equal<string list>([ "Date"; "Order"; "Status" ], envDefault.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box "2026-01-01"; box 1001; box "Completed" |], envDefault.Rows.[0])

    // With source code containing the select clause, the order is transformed to Order, Date, Status
    let envOrdered, _, retained =
        Projection.projectWithCursorDetailed (box item) 0L (Some code) None

    Assert.Equal<string list>([ "Order"; "Date"; "Status" ], envOrdered.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1001; box "2026-01-01"; box "Completed" |], envOrdered.Rows.[0])
    Assert.True(retained.IsSome)
    Assert.Equal<string list>([ "Order"; "Date"; "Status" ], retained.Value.Columns)
    Assert.Equal<obj array>([| box 1001; box "2026-01-01"; box "Completed" |], retained.Value.Rows.[0])

[<Fact>]
let ``projectWithCursorDetailed renders a sequence of anonymous records in declared order matching 9 fields from user request``
    ()
    =
    let items =
        [
            {|
                Carrier = "DHL"
                Ccy = "USD"
                Date = "2026-01-01"
                Item = "Laptop"
                Order = 101
                Qtt = 2
                Returned = "No"
                Status = "Delivered"
                Total = 1500M
            |}
        ]

    let code =
        """
query {
    for c in Connections.csv do
    select {|
        Order = ord.Id
        Date = ord.Date
        Status = ord.Status
        Ccy = ord.Ccy
        Item = ord.Item
        Qtt = ord.Qtt
        Total = ord.Total
        Carrier = c.Name
        Returned = r.Status
    |}
}
"""

    let env, _, retained =
        Projection.projectWithCursorDetailed (box items) 0L (Some code) None

    let expectedCols =
        [
            "Order"
            "Date"
            "Status"
            "Ccy"
            "Item"
            "Qtt"
            "Total"
            "Carrier"
            "Returned"
        ]

    Assert.Equal<string list>(expectedCols, env.Schema |> List.map (fun c -> c.Name))

    let expectedRow: obj array =
        [|
            box 101
            box "2026-01-01"
            box "Delivered"
            box "USD"
            box "Laptop"
            box 2
            box 1500M
            box "DHL"
            box "No"
        |]

    Assert.Equal<obj array>(expectedRow, env.Rows.[0])
    Assert.True(retained.IsSome)
    Assert.Equal<string list>(expectedCols, retained.Value.Columns)
    Assert.Equal<obj array>(expectedRow, retained.Value.Rows.[0])

[<Fact>]
let ``registerSourceCode preserves column ordering across notebook cells without passing source to second cell`` () =
    let cell1Code =
        """
let myQuery =
    select {|
        Zed = 1
        Alpha = 2
        Middle = 3
    |}
"""

    Projection.registerSourceCode cell1Code

    let item = {| Zed = 10; Alpha = 20; Middle = 30 |}
    // Evaluating item with no source code (simulating Cell 2 with just `myQuery`)
    let env, _, _ = Projection.projectWithCursorDetailed (box item) 0L None None
    Assert.Equal<string list>([ "Zed"; "Alpha"; "Middle" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 10; box 20; box 30 |], env.Rows.[0])

type private ComplexRecord =
    {
        DVal: double
        SVal: single
        OptInt: int option
        OptDouble: double option
        OptNull: string option
        ObjVal: obj
    }

[<Fact>]
let ``sanitizeCell handles non-finite floats, DBNull, and options in records`` () =
    let recVal =
        {
            DVal = System.Double.NaN
            SVal = System.Single.PositiveInfinity
            OptInt = Some 42
            OptDouble = Some System.Double.NegativeInfinity
            OptNull = None
            ObjVal = box System.DBNull.Value
        }

    let env, _, _ = Projection.projectWithCursor (box [ recVal ]) 0L
    Assert.Equal(ResultKind.Table, env.Kind)
    let row = env.Rows.[0]
    Assert.Null(row.[0]) // NaN -> null
    Assert.Null(row.[1]) // +Infinity -> null
    Assert.Equal(box 42, row.[2]) // Some 42 -> 42
    Assert.Null(row.[3]) // Some -Infinity -> null
    Assert.Null(row.[4]) // None -> null
    Assert.Null(row.[5]) // DBNull -> null

[<Fact>]
let ``tuple sequence with 3 elements maps to Item1, Item2, Item3`` () =
    let triples: (int * string * bool) seq =
        seq {
            yield (1, "A", true)
            yield (2, "B", false)
        }

    let env = envOf (box triples)
    Assert.Equal(ResultKind.Table, env.Kind)
    Assert.Equal<string list>([ "Item1"; "Item2"; "Item3" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; box "A"; box true |], env.Rows.[0])
    Assert.Equal<obj array>([| box 2; box "B"; box false |], env.Rows.[1])

[<Fact>]
let ``tuple sequence with mixed types falls back to Text envelope`` () =
    let mixed: obj seq =
        seq {
            yield box (1, "A")
            yield box (2, "B", true)
        }

    let env, _, _ = Projection.projectWithCursor (box mixed) 0L
    Assert.Equal(ResultKind.Text, env.Kind)

[<Fact>]
let ``IDictionary sequence such as Hashtable projects to Table`` () =
    let ht1 = System.Collections.Hashtable()
    ht1.["Id"] <- box 1
    ht1.["Name"] <- box "Alice"
    let ht2 = System.Collections.Hashtable()
    ht2.["Id"] <- box 2
    ht2.["Role"] <- box "Admin"
    let env = envOf (box [ ht1; ht2 ])
    Assert.Equal(ResultKind.Table, env.Kind)
    Assert.Equal(2, env.Rows.Length)
    Assert.Contains(env.Schema, fun c -> c.Name = "Id")

[<Fact>]
let ``sequence of sequences consumes first row as headers and data rows as content`` () =
    let rawSeq: obj seq seq =
        seq {
            yield
                seq {
                    yield box "ColA"
                    yield box "ColB"
                }

            yield
                seq {
                    yield box 10
                    yield box 20
                }

            yield
                seq {
                    yield box 30
                    yield box 40
                }
        }

    let env = envOf (box rawSeq)
    Assert.Equal(ResultKind.Table, env.Kind)
    Assert.Equal<string list>([ "ColA"; "ColB" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal(2, env.Rows.Length)
    Assert.Equal<obj array>([| box 10; box 20 |], env.Rows.[0])
    Assert.Equal<obj array>([| box 30; box 40 |], env.Rows.[1])

[<Fact>]
let ``sequence of ConnectorQueryResult falls back to Text envelope with expected failure message`` () =
    let q1: ConnectorQueryResult =
        {
            Columns = [ "x" ]
            Rows = seq { yield [| box 1 |] }
        }

    let q2: ConnectorQueryResult =
        {
            Columns = [ "y" ]
            Rows = seq { yield [| box 2 |] }
        }

    let env, _, _ = Projection.projectWithCursor (box [ q1; q2 ]) 0L
    Assert.Equal(ResultKind.Text, env.Kind)
    Assert.True(env.DisplayText.IsSome)

[<Fact>]
let ``record sequence with mixed types falls back to Text envelope`` () =
    let mixedRecords: obj seq =
        seq {
            yield box { Id = 1; Name = "A" }
            yield box {| Extra = 42 |}
        }

    let env, _, _ = Projection.projectWithCursor (box mixedRecords) 0L
    Assert.Equal(ResultKind.Text, env.Kind)

[<Fact>]
let ``record sequence containing null elements ignores null and continues reading`` () =
    let records: Person seq =
        seq {
            yield { Id = 1; Name = "A" }
            yield Unchecked.defaultof<Person>
            yield { Id = 2; Name = "B" }
        }

    let env = envOf (box records)
    Assert.Equal(ResultKind.Table, env.Kind)
    Assert.Equal(2, env.Rows.Length)
    Assert.Equal<obj array>([| box 1; box "A" |], env.Rows.[0])
    Assert.Equal<obj array>([| box 2; box "B" |], env.Rows.[1])

[<Fact>]
let ``unwrapException and projectError unwrap TargetInvocationException and AggregateException with causes`` () =
    let baseEx = System.InvalidOperationException("Root failure")
    let tie = System.Reflection.TargetInvocationException("Invocation error", baseEx)
    let agg = System.AggregateException("Aggregate error", tie)
    let unwrapped = Projection.unwrapException agg
    Assert.Same(baseEx :> obj, unwrapped :> obj)

    let innerEx = System.ArgumentException("Invalid argument")
    let outerEx = System.InvalidOperationException("Outer message", innerEx)
    let errEnv = Projection.projectError outerEx 15L
    Assert.Equal(ResultKind.Error, errEnv.Kind)
    Assert.Equal(15L, errEnv.ElapsedMs)
    Assert.Contains("Outer message", errEnv.DisplayText.Value)
    Assert.Contains("Cause: Invalid argument", errEnv.DisplayText.Value)

[<Fact>]
let ``projectErrorWithDetails handles Operation could not be completed and details`` () =
    let exBlocked = System.Exception("Operation could not be completed")

    let envBlocked =
        Projection.projectErrorWithDetails exBlocked (Some "Compiler error details") 10L

    Assert.Equal("Compiler error details", envBlocked.DisplayText.Value)

    let exOther = System.Exception("Other error")
    let envOther = Projection.projectErrorWithDetails exOther (Some "More context") 10L
    Assert.Equal("Other error\n\nDetails:\nMore context", envOther.DisplayText.Value)

    let envNoDetails = Projection.projectErrorWithDetails exOther None 10L
    Assert.Equal("Other error", envNoDetails.DisplayText.Value)

[<Fact>]
let ``projectErrorWithDetails includes docker compose tips when database connection fails on standard ports`` () =
    let exPg = System.Exception("Connection refused to 127.0.0.1:5432")
    let envPg = Projection.projectErrorWithDetails exPg None 10L
    Assert.Contains("docker compose -f docker-compose.fsqs.yml up -d fsqs-postgres", envPg.DisplayText.Value)

    let exMs = System.Exception("Could not connect to localhost:1433")
    let envMs = Projection.projectErrorWithDetails exMs None 10L
    Assert.Contains("docker compose -f docker-compose.fsqs.yml up -d fsqs-sqlserver", envMs.DisplayText.Value)

[<Fact>]
let ``projectErrorWithDetails includes docker compose tips for mysql, mariadb and oracle`` () =
    let exMy = System.Exception("Connection to 127.0.0.1:3306 failed")
    let envMy = Projection.projectErrorWithDetails exMy None 10L
    Assert.Contains("docker compose -f docker-compose.fsqs.yml up -d fsqs-mysql", envMy.DisplayText.Value)

    let exMaria = System.Exception("Connection to 127.0.0.1:3307 failed")
    let envMaria = Projection.projectErrorWithDetails exMaria None 10L
    Assert.Contains("docker compose -f docker-compose.fsqs.yml up -d fsqs-mariadb", envMaria.DisplayText.Value)

    let exOra = System.Exception("ORA-12541: TNS:no listener at 127.0.0.1:1521")
    let envOra = Projection.projectErrorWithDetails exOra None 10L
    Assert.Contains("docker compose -f docker-compose.fsqs.yml up -d fsqs-oracle", envOra.DisplayText.Value)

[<Fact>]
let ``projectWorkerError creates Error envelope from WorkerErrorInfo`` () =
    let info =
        {
            Code = "worker/crash"
            Message = "Process terminated unexpectedly"
        }

    let env = Projection.projectWorkerError info
    Assert.Equal(ResultKind.Error, env.Kind)
    Assert.Equal(Some "Process terminated unexpectedly", env.DisplayText)

[<Fact>]
let ``toOutputLines formats stdout and stderr streams correctly`` () =
    Assert.Empty(Projection.toOutputLines "" "")
    let lines = Projection.toOutputLines "out1\nout2\n" "err1\r\n"
    Assert.Equal(3, lines.Length)
    Assert.Equal(OutputStream.Stdout, lines.[0].Stream)
    Assert.Equal("out1", lines.[0].Text)
    Assert.Equal(OutputStream.Stdout, lines.[1].Stream)
    Assert.Equal("out2", lines.[1].Text)
    Assert.Equal(OutputStream.Stderr, lines.[2].Stream)
    Assert.Equal("err1", lines.[2].Text)

[<Fact>]
let ``extractAnonymousRecordBlocks handles nested block comments, triple quoted strings, verbatim strings, and expressions``
    ()
    =
    let code =
        "let x = {|\n"
        + "    (* outer (* nested block comment *) outer *)\n"
        + "    PropA = \"\"\" triple \"quoted\" string \"\"\"\n"
        + "    PropB = @\" verbatim \"\"quote\"\" inside \"\n"
        + "    PropC = \" escaped \\\"string\\\" \"\n"
        + "    PropD = (1 + 2)\n"
        + "    PropE = [ 3; 4 ]\n"
        + "    PropF = { Name = \"test\" }\n"
        + "    PropG = if true then 1 else 0\n"
        + "|}"

    let blocks = Projection.extractAnonymousRecordBlocks code
    Assert.Single(blocks) |> ignore
    let expected = [ "PropA"; "PropB"; "PropC"; "PropD"; "PropE"; "PropF"; "PropG" ]
    Assert.Equal<string list>(expected, blocks.[0])

    Assert.Empty(Projection.extractAnonymousRecordBlocks "")
    Assert.Empty(Projection.extractAnonymousRecordBlocks "   \n\t  ")

[<Fact>]
let ``tryFindMatchingPropertyOrder matches partial target fields`` () =
    let blocks = [ [ "A"; "B"; "C"; "D" ] ]
    let target = Set.ofList [ "C"; "A" ]
    let matched = Projection.tryFindMatchingPropertyOrder blocks target
    Assert.True(matched.IsSome)
    Assert.Equal<string list>([ "A"; "C" ], matched.Value)

    Assert.True(Projection.tryFindMatchingPropertyOrder [] target |> Option.isNone)
    Assert.True(Projection.tryFindMatchingPropertyOrder blocks Set.empty |> Option.isNone)

[<Fact>]
let ``projectWithCursor handles case-insensitive preferred column reordering`` () =
    let item = {| foo = 1; bar = 2 |}

    let env, _, _ =
        Projection.projectWithCursorDetailed (box item) 0L None (Some [ "BAR"; "FOO" ])

    Assert.Equal<string list>([ "bar"; "foo" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 2; box 1 |], env.Rows.[0])

type private Unprintable() =
    override _.ToString() = failwith "cannot stringify"

[<Fact>]
let ``unprintable value falls back to unprintable value placeholder in Text envelope`` () =
    let env = Projection.project (Unprintable()) 0L
    Assert.Equal(ResultKind.Text, env.Kind)
    Assert.True(env.DisplayText.IsSome)

type private FailingQuerySequence() =
    interface System.Collections.IEnumerable with
        member _.GetEnumerator() =
            raise (System.InvalidOperationException("Database connection timeout during execution"))

[<Fact>]
let ``projectWithCursor surfaces database and evaluation exceptions during enumeration as Error envelope`` () =
    let failingSeq = FailingQuerySequence()
    let env, cursor, retained = Projection.projectWithCursor failingSeq 0L
    Assert.Equal(ResultKind.Error, env.Kind)
    Assert.True(cursor.IsNone)
    Assert.True(retained.IsNone)
    Assert.True(env.DisplayText.IsSome)
    Assert.Contains("Database connection timeout during execution", env.DisplayText.Value)

[<Fact>]
let ``projectWithCursor surfaces mid-stream record sequence exceptions as Error envelope instead of silent truncation``
    ()
    =
    let failingRecords =
        seq {
            yield { Id = 1; Name = "Row1" }
            raise (System.InvalidOperationException("ORA-00936: missing expression in database"))
            yield { Id = 2; Name = "Row2" }
        }

    let env, cursor, retained = Projection.projectWithCursor failingRecords 0L
    Assert.Equal(ResultKind.Error, env.Kind)
    Assert.True(cursor.IsNone)
    Assert.True(retained.IsNone)
    Assert.True(env.DisplayText.IsSome)
    Assert.Contains("ORA-00936", env.DisplayText.Value)

type private RecordWithFloatOption = { Id: int; Value: double option }

[<Fact>]
let ``extractAnonymousRecordBlocks handles backtick identifiers not followed by equals scenario 1`` () =
    let src = "{| ``notAssigned`` ; ``assigned`` = 42 |}"
    Projection.registerSourceCode src
    let blocks = Projection.extractAnonymousRecordBlocks src
    Assert.Single(blocks) |> ignore
    Assert.Equal<string list>([ "assigned" ], blocks.[0])

[<Fact>]
let ``extractAnonymousRecordBlocks handles normal identifiers not followed by equals scenario 2`` () =
    let src = "{| notAssigned ; assigned = 100 |}"
    let blocks = Projection.extractAnonymousRecordBlocks src
    Assert.Single(blocks) |> ignore
    Assert.Equal<string list>([ "assigned" ], blocks.[0])

[<Fact>]
let ``tryGetOrExtractRecordOrder returns None when fields do not match source blocks scenario 1`` () =
    let src = "{| a = 1; b = 2 |}"
    let item = {| x = 10; y = 20 |}
    let order = Projection.tryGetOrExtractRecordOrder (item.GetType()) (Some src)
    Assert.True(order.IsNone)

[<Fact>]
let ``registerSourceCode handles whitespace, empty, and invalid blocks safely scenario 2`` () =
    Projection.registerSourceCode ""
    Projection.registerSourceCode "   "
    Projection.registerSourceCode null
    Projection.registerSourceCode "let x = 1"
    Assert.True(true)

[<Fact>]
let ``sanitizeCell handles float option with NaN and Infinity scenario 1`` () =
    let r1 = { Id = 1; Value = Some System.Double.NaN }
    let r2 = { Id = 2; Value = Some System.Double.PositiveInfinity }
    let env = envOf (box [ r1; r2 ])
    Assert.Equal(ResultKind.Table, env.Kind)
    Assert.Null(env.Rows.[0].[1])
    Assert.Null(env.Rows.[1].[1])

[<Fact>]
let ``sanitizeCell handles regular float option values scenario 2`` () =
    let r = { Id = 1; Value = Some 42.5 }
    let env = envOf (box [ r ])
    Assert.Equal(ResultKind.Table, env.Kind)
    Assert.Equal(box 42.5, env.Rows.[0].[1])
