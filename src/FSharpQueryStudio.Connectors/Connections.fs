namespace FSharpQueryStudio.Connectors

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open Serilog
open FSharpQueryStudio.Protocol

open System.Collections.Concurrent
open System.Linq
open Microsoft.FSharp.Reflection

/// Resolves a named, already-configured connection to its live
/// connector instance — Section 13's workspace connection registry,
/// abstracted so `Connections` (and its tests) don't depend on how that
/// registry is actually built (`workspace.json` plus the secret store).
type IConnectionResolver =
    abstract Resolve: name: string -> IConnector

/// The controlled surface `.fsx` scripts use to reach configured
/// connections by name — Section 10:
///
///     let rows = Connections.sql "Sales" "select * from Orders where CreatedAt >= @from" [ "from", box startDate ]
///
/// Every function here reaches its connector's own bound-parameter API;
/// none of them ever build a command by concatenating a caller-supplied
/// value into command text. That is what structurally rules out
/// injection through this surface, regardless of what a script passes
/// as a parameter value — a script that wants string-built SQL would
/// have to bypass `Connections` entirely and drive a connector's own
/// ADO.NET types directly, which the controlled API surface doesn't
/// expose.
///
/// `configure` is called once by the worker before running a script
/// (Section 9: one evaluation in flight per worker), the same
/// single-writer assumption `Handlers.evaluationInFlight` already relies
/// on elsewhere.
module Connections =
    let mutable private resolver: IConnectionResolver option = None

    let configure (newResolver: IConnectionResolver) = resolver <- Some newResolver

    let private resolve (name: string) : IConnector =
        match resolver with
        | None -> failwith "Connections has not been configured for this session."
        | Some r -> r.Resolve name

    let private unsupported (name: string) (operation: string) (connector: IConnector) =
        failwith $"Connection '{name}' (kind {connector.Kind}) does not support {operation}."

    let private mapperCache = ConcurrentDictionary<Type * string, obj array -> obj>()

    let getMapper<'T> (columns: string list) : (obj array -> 'T) =
        let targetType = typeof<'T>
        let key = (targetType, String.concat "," columns)
        let rawMapper: obj array -> obj =
            mapperCache.GetOrAdd(
                key,
                Func<Type * string, obj array -> obj>(fun _ ->
                    if targetType = typeof<obj array> then
                        (fun row -> box row)
                    elif FSharpType.IsRecord(targetType) then
                        let fields = FSharpType.GetRecordFields(targetType)
                        let colIndices =
                            fields
                            |> Array.mapi (fun defaultIdx f ->
                                let byName =
                                    columns
                                    |> List.tryFindIndex (fun c -> String.Equals(c, f.Name, StringComparison.OrdinalIgnoreCase))
                                match byName with
                                | Some idx -> idx
                                | None -> if defaultIdx < columns.Length then defaultIdx else 0
                            )
                        let ctor = FSharpValue.PreComputeRecordConstructor(targetType)
                        fun (row: obj array) ->
                            let args: obj array = Array.zeroCreate fields.Length
                            for i in 0 .. fields.Length - 1 do
                                let idx = colIndices.[i]
                                let raw = if idx < row.Length then row.[idx] else null
                                let isNullVal = isNull raw || Convert.IsDBNull(raw)
                                let propType = fields.[i].PropertyType
                                let isOption = propType.IsGenericType && propType.GetGenericTypeDefinition() = typedefof<option<_>>

                                if isOption then
                                    let cases = FSharpType.GetUnionCases(propType)
                                    let noneCase = cases |> Array.find (fun c -> c.Name = "None")
                                    let someCase = cases |> Array.find (fun c -> c.Name = "Some")
                                    if isNullVal then
                                        args.[i] <- FSharpValue.MakeUnion(noneCase, [||])
                                    else
                                        let innerType = propType.GetGenericArguments().[0]
                                        let converted =
                                            if innerType.IsInstanceOfType(raw) then raw
                                            else Convert.ChangeType(raw, innerType)
                                        args.[i] <- FSharpValue.MakeUnion(someCase, [| converted |])
                                else
                                    if isNullVal then
                                        args.[i] <- null
                                    elif propType.IsInstanceOfType(raw) then
                                        args.[i] <- raw
                                    else
                                        args.[i] <- Convert.ChangeType(raw, propType)
                            ctor args
                    else
                        fun (row: obj array) ->
                            if row.Length > 0 && not (isNull row.[0]) && not (Convert.IsDBNull(row.[0])) then
                                Convert.ChangeType(row.[0], targetType)
                            else
                                null
                )
            )
        fun (row: obj array) -> unbox<'T> (rawMapper row)

    /// Wraps `rows` so the row count and elapsed time (Section 15's
    /// connector-call logging: kind, elapsed time, row count — never
    /// parameter values) are logged once the caller finishes enumerating
    /// them, not eagerly on the call itself.
    let private logOnceDrained (kind: ConnectorKind) (stopwatch: Stopwatch) (rows: obj array seq) : obj array seq =
        seq {
            let mutable rowCount = 0

            for row in rows do
                rowCount <- rowCount + 1
                yield row

            stopwatch.Stop()

            Log.Information(
                "Connector call {Kind} returned {RowCount} rows in {ElapsedMs}ms",
                kind,
                rowCount,
                stopwatch.ElapsedMilliseconds
            )
        }

    /// Resolves the raw ADO.NET connection string for a named SQL connection
    /// (e.g. for use with SQLProvider: SqlDataProvider<...>).
    let getConnectionString (name: string) : string =
        match resolve name with
        | :? IQueryableConnector as c -> c.ConnectionString
        | other -> unsupported name "Connections.getConnectionString" other

    /// Runs a parameterized SQL query against a named SQLite/SQL Server/
    /// PostgreSQL connection. `parameters` are always bound through the
    /// underlying connector's own ADO.NET parameter API — never
    /// concatenated into `sqlText`, and never logged (Section 15).
    let sql (name: string) (sqlText: string) (parameters: (string * obj) list) : ConnectorQueryResult =
        match resolve name with
        | :? IQueryableConnector as c ->
            let stopwatch = Stopwatch.StartNew()
            let result = c.ExecuteQuery sqlText parameters
            { result with Rows = logOnceDrained c.Kind stopwatch result.Rows }
        | other -> unsupported name "Connections.sql" other

    /// Reads a named CSV connection's rows.
    let csv (name: string) : ConnectorQueryResult =
        match resolve name with
        | :? CsvConnector as c ->
            let stopwatch = Stopwatch.StartNew()
            let result = c.ReadRows()
            { result with Rows = logOnceDrained (c :> IConnector).Kind stopwatch result.Rows }
        | other -> unsupported name "Connections.csv" other

    /// Reads a named Excel connection's rows, bounded to `maxRows`.
    let excel (name: string) (maxRows: int option) : ConnectorQueryResult =
        match resolve name with
        | :? ExcelConnector as c ->
            let stopwatch = Stopwatch.StartNew()
            let result = c.ReadRows(?maxRows = maxRows)
            { result with Rows = logOnceDrained (c :> IConnector).Kind stopwatch result.Rows }
        | other -> unsupported name "Connections.excel" other

    /// Sends a configured request through a named REST/JSON connection.
    let rest (name: string) (options: RestRequestOptions) (ct: CancellationToken) : Task<JsonElement> =
        match resolve name with
        | :? RestConnector as c -> c.SendAsync options ct
        | other -> unsupported name "Connections.rest" other

    /// Runs a query and streams strongly-typed records/primitives with high performance.
    let query<'T> (name: string) (sqlText: string) (parameters: (string * obj) list) : 'T seq =
        let r = sql name sqlText parameters
        let mapper = getMapper<'T> r.Columns
        seq {
            for row in r.Rows do
                yield mapper row
        }

    /// Reads CSV rows into strongly-typed records/primitives with high performance.
    let queryCsv<'T> (name: string) : 'T seq =
        let r = csv name
        let mapper = getMapper<'T> r.Columns
        seq {
            for row in r.Rows do
                yield mapper row
        }

    /// Reads Excel rows into strongly-typed records/primitives with high performance.
    let queryExcel<'T> (name: string) (maxRows: int option) : 'T seq =
        let r = excel name maxRows
        let mapper = getMapper<'T> r.Columns
        seq {
            for row in r.Rows do
                yield mapper row
        }

[<AutoOpen>]
module ConnectorQueryResultExtensions =
    type ConnectorQueryResult with
        member this.As<'T>() : 'T seq =
            let mapper = Connections.getMapper<'T> this.Columns
            seq {
                for row in this.Rows do
                    yield mapper row
            }

        member this.AsQueryable<'T>() : IQueryable<'T> =
            this.As<'T>().AsQueryable()
