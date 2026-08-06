namespace FSharpQueryStudio.Connectors

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open Serilog
open FSharpQueryStudio.Protocol

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

    /// Wraps `rows` so the row count and elapsed time (Section 15's
    /// connector-call logging: kind, elapsed time, row count — never
    /// parameter values) are logged once the caller finishes enumerating
    /// them, not eagerly on the call itself. Eagerly counting would force
    /// full materialization of what may be a reader-backed sequence,
    /// defeating C.5/C.13's reader-based paging; a caller that never
    /// fully drains `rows` (e.g. it truncates a huge result) simply never
    /// triggers the log event, which is the correct tradeoff here.
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
        | :? CsvConnector as c -> c.ReadRows()
        | other -> unsupported name "Connections.csv" other

    /// Reads a named Excel connection's rows, bounded to `maxRows`.
    let excel (name: string) (maxRows: int option) : ConnectorQueryResult =
        match resolve name with
        | :? ExcelConnector as c -> c.ReadRows(?maxRows = maxRows)
        | other -> unsupported name "Connections.excel" other

    /// Sends a configured request through a named REST/JSON connection.
    let rest (name: string) (options: RestRequestOptions) (ct: CancellationToken) : Task<JsonElement> =
        match resolve name with
        | :? RestConnector as c -> c.SendAsync options ct
        | other -> unsupported name "Connections.rest" other
