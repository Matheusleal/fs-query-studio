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

    /// Reads Excel rows from a specific sheet into strongly-typed records/primitives with high performance.
    let queryExcelSheet<'T> (name: string) (sheetName: string option) (maxRows: int option) : 'T seq =
        match resolve name with
        | :? ExcelConnector as c ->
            let stopwatch = Stopwatch.StartNew()
            let result = c.ReadRows(?sheetName = sheetName, ?maxRows = maxRows)
            let logged = { result with Rows = logOnceDrained (c :> IConnector).Kind stopwatch result.Rows }
            let mapper = getMapper<'T> logged.Columns
            seq {
                for row in logged.Rows do
                    yield mapper row
            }
        | other -> unsupported name "Connections.queryExcelSheet" other

    /// Sends a request or reads from a REST/JSON connection into strongly-typed records.
    let queryJsonWithOptions<'T> (name: string) (options: RestRequestOptions option) : 'T seq =
        match resolve name with
        | :? RestConnector as c ->
            let opts = defaultArg options c.BaseOptions
            let elem = (c.SendAsync opts CancellationToken.None).GetAwaiter().GetResult()
            let jsonText = elem.GetRawText()
            let items: 'T list =
                if elem.ValueKind = JsonValueKind.Array then
                    JsonSerializer.Deserialize<'T list>(jsonText, Serialization.options)
                elif elem.ValueKind = JsonValueKind.Object then
                    let arrayProp =
                        elem.EnumerateObject()
                        |> Seq.tryFind (fun prop -> prop.Value.ValueKind = JsonValueKind.Array)
                    match arrayProp with
                    | Some prop ->
                        JsonSerializer.Deserialize<'T list>(prop.Value.GetRawText(), Serialization.options)
                    | None ->
                        [ JsonSerializer.Deserialize<'T>(jsonText, Serialization.options) ]
                else
                    []
            items :> 'T seq
        | other -> unsupported name "Connections.queryJson" other

    /// Sends a request with default options or reads from a REST/JSON connection into strongly-typed records.
    let queryJson<'T> (name: string) : 'T seq =
        queryJsonWithOptions<'T> name None

    /// Represents an active database connection with first-class query and schema discovery members for autocomplete.
    type DatabaseConnection(name: string, connector: IQueryableConnector) =
        member _.Name = name
        member _.ConnectionString = connector.ConnectionString
        member _.Execute(sqlText: string, ?parameters: (string * obj) list) : ConnectorQueryResult =
            let p = defaultArg parameters []
            match resolver with
            | Some _ -> sql name sqlText p
            | None -> connector.ExecuteQuery sqlText p
        member _.Query<'T>(sqlText: string, ?parameters: (string * obj) list) : 'T seq =
            let p = defaultArg parameters []
            match resolver with
            | Some _ -> query<'T> name sqlText p
            | None ->
                let (r: ConnectorQueryResult) = connector.ExecuteQuery sqlText p
                let mapper = getMapper<'T> r.Columns
                seq {
                    for row in r.Rows do
                        yield mapper row
                }
        member this.Queryable<'T>(sqlText: string, ?parameters: (string * obj) list) : IQueryable<'T> =
            let p = defaultArg parameters []
            (this.Query<'T>(sqlText, p)).AsQueryable()
        member _.DiscoverSchemaAsync(?ct: CancellationToken) =
            let token = defaultArg ct CancellationToken.None
            connector.DiscoverSchemaAsync(token)
        member this.Tables : string list =
            this.DiscoverSchemaAsync().Result |> List.map (fun t -> t.Name)
        member this.Schema : ConnectorTable list =
            this.DiscoverSchemaAsync().Result

        /// Generates strongly-typed F# entity record types and a Database accessor class for all discovered tables and schemas.
        member this.GenerateEntities(?moduleName: string) : string =
            let modName =
                match moduleName with
                | Some m -> m
                | None ->
                    let clean = System.Text.RegularExpressions.Regex.Replace(name, @"[^a-zA-Z0-9_]", "_")
                    $"{clean}Db"

            let keywords =
                set [ "abstract"; "and"; "as"; "assert"; "base"; "begin"; "class"; "default"; "delegate";
                      "do"; "done"; "downcast"; "downto"; "elif"; "else"; "end"; "exception"; "extern";
                      "false"; "finally"; "fixed"; "for"; "fun"; "function"; "global"; "if"; "in";
                      "inherit"; "inline"; "interface"; "internal"; "lazy"; "let"; "match"; "member";
                      "module"; "mutable"; "namespace"; "new"; "not"; "null"; "of"; "open"; "or";
                      "override"; "private"; "public"; "rec"; "return"; "static"; "struct"; "then";
                      "to"; "true"; "try"; "type"; "upcast"; "use"; "val"; "void"; "when"; "while";
                      "with"; "yield"; "atomic"; "break"; "checked"; "component"; "const"; "constraint";
                      "constructor"; "continue"; "eager"; "event"; "external"; "functor"; "include";
                      "method"; "mixin"; "object"; "parallel"; "process"; "protected"; "pure"; "sealed";
                      "tailcall"; "trait"; "virtual"; "volatile" ]

            let escapeIdent (s: string) =
                if System.Text.RegularExpressions.Regex.IsMatch(s, @"^[a-zA-Z][a-zA-Z0-9_]*$") && not (Set.contains s keywords) then
                    s
                else
                    $"``{s}``"

            let mapType (t: string) =
                let lower = t.ToLowerInvariant()
                if lower.Contains("bigint") || lower.Contains("int8") || lower.Contains("bigserial") then "int64"
                elif lower.Contains("integer") || lower.Contains("int4") || lower.Contains("serial") || lower = "int" then "int"
                elif lower.Contains("smallint") || lower.Contains("int2") then "int16"
                elif lower.Contains("bool") || lower.Contains("bit") then "bool"
                elif lower.Contains("uuid") || lower.Contains("uniqueidentifier") then "System.Guid"
                elif lower.Contains("time") || lower.Contains("date") then "System.DateTime"
                elif lower.Contains("decimal") || lower.Contains("numeric") || lower.Contains("money") then "decimal"
                elif lower.Contains("double") || lower.Contains("float8") || lower = "float" then "double"
                elif lower.Contains("real") || lower.Contains("float4") then "single"
                elif lower.Contains("bytea") || lower.Contains("blob") || lower.Contains("binary") then "byte[]"
                elif lower.Contains("char") || lower.Contains("text") || lower.Contains("json") then "string"
                else "obj"

            let sb = System.Text.StringBuilder()
            sb.AppendLine($"module {escapeIdent modName} =") |> ignore
            sb.AppendLine() |> ignore

            let tables = this.Schema
            let schemaGroups =
                tables
                |> Seq.groupBy (fun t ->
                    let dotIdx = t.Name.IndexOf(".")
                    if dotIdx > 0 then t.Name.Substring(0, dotIdx) else "public")
                |> Seq.sortBy fst

            for (schema, tbls) in schemaGroups do
                sb.AppendLine($"    module {escapeIdent schema} =") |> ignore
                for t in tbls do
                    let rawTableName =
                        let dotIdx = t.Name.IndexOf(".")
                        if dotIdx > 0 then t.Name.Substring(dotIdx + 1) else t.Name
                    let typeName = escapeIdent rawTableName
                    sb.AppendLine($"        type {typeName} =") |> ignore
                    sb.AppendLine("            {") |> ignore
                    for col in t.Columns do
                        sb.AppendLine($"                {escapeIdent col.Name}: {mapType col.DataType}") |> ignore
                    sb.AppendLine("            }") |> ignore
                    sb.AppendLine() |> ignore

            sb.AppendLine("    type Database(conn: FSharpQueryStudio.Connectors.Connections.DatabaseConnection) =") |> ignore
            sb.AppendLine($"        new(connName: string) = Database(FSharpQueryStudio.Connectors.Connections.connect(connName))") |> ignore
            sb.AppendLine($"        new() = Database(\"{name}\")") |> ignore
            for (schema, tbls) in schemaGroups do
                sb.AppendLine($"        member _.{escapeIdent schema} =") |> ignore
                sb.AppendLine("            {|") |> ignore
                for t in tbls do
                    let rawTableName =
                        let dotIdx = t.Name.IndexOf(".")
                        if dotIdx > 0 then t.Name.Substring(dotIdx + 1) else t.Name
                    let propName = escapeIdent rawTableName
                    let colList = t.Columns |> List.map (fun c -> c.Name) |> String.concat ", "
                    let sqlQuery = $"SELECT {colList} FROM {t.Name}"
                    sb.AppendLine($"                {propName} = conn.Queryable<{escapeIdent schema}.{propName}>(\"{sqlQuery}\")") |> ignore
                sb.AppendLine("            |}") |> ignore
                sb.AppendLine() |> ignore
            sb.AppendLine("    let database () : Database = Database()") |> ignore
            sb.AppendLine("    let db () : Database = Database()") |> ignore
            sb.AppendLine() |> ignore

            sb.ToString()

        /// Generates a SQLProvider declaration snippet for this database connection.
        member this.GenerateSqlProvider(?typeAlias: string) : string =
            let alias = defaultArg typeAlias "Db"
            let vendor =
                match (connector :> IConnector).Kind with
                | ConnectorKind.PostgreSql -> "Common.DatabaseProviderTypes.POSTGRESQL"
                | ConnectorKind.SqlServer -> "Common.DatabaseProviderTypes.MSSQLSERVER"
                | ConnectorKind.Sqlite -> "Common.DatabaseProviderTypes.SQLITE"
                | _ -> "Common.DatabaseProviderTypes.POSTGRESQL"
            $"open FSharp.Data.Sql\n\n[<Literal>]\nlet private connStr = \"{connector.ConnectionString}\"\n\ntype {alias} = SqlDataProvider<\n    DatabaseVendor = {vendor},\n    ConnectionString = connStr >\n\nlet db = {alias}.GetDataContext()\n"

    /// Represents an active CSV connection with typed row queries and autocomplete.
    type CsvConnection(name: string, connector: CsvConnector) =
        member _.Name = name
        member _.Connector = connector
        member _.FilePath = connector.FilePath
        member _.Rows<'T>() : 'T seq = queryCsv<'T> name
        member this.Queryable<'T>() : IQueryable<'T> = (this.Rows<'T>()).AsQueryable()
        member _.Raw() : ConnectorQueryResult = csv name

    /// Represents an active Excel workbook connection with multi-sheet typed queries.
    type ExcelConnection(name: string, connector: ExcelConnector) =
        member _.Name = name
        member _.Connector = connector
        member _.FilePath = connector.FilePath
        member _.Rows<'T>(?maxRows: int) : 'T seq = queryExcel<'T> name maxRows
        member _.Sheet<'T>(sheetName: string, ?maxRows: int) : 'T seq = queryExcelSheet<'T> name (Some sheetName) maxRows
        member this.Queryable<'T>(?maxRows: int) : IQueryable<'T> = (this.Rows<'T>(?maxRows = maxRows)).AsQueryable()
        member this.SheetQueryable<'T>(sheetName: string, ?maxRows: int) : IQueryable<'T> = (this.Sheet<'T>(sheetName, ?maxRows = maxRows)).AsQueryable()
        member _.Raw(?maxRows: int) : ConnectorQueryResult = excel name maxRows

    /// Represents an active REST/JSON connection with typed queries.
    type RestConnection(name: string, connector: RestConnector) =
        member _.Items<'T>(?options: RestRequestOptions) : 'T seq = queryJsonWithOptions<'T> name options
        member this.Queryable<'T>(?options: RestRequestOptions) : IQueryable<'T> = (this.Items<'T>(?options = options)).AsQueryable()
        member _.SendAsync(options: RestRequestOptions, ?ct: CancellationToken) : Task<JsonElement> =
            let token = defaultArg ct CancellationToken.None
            connector.SendAsync options token

    /// Retrieves a first-class connection object exposing Query, Queryable, Execute, Schema and Tables.
    let get (name: string) : DatabaseConnection =
        match resolve name with
        | :? IQueryableConnector as qc -> DatabaseConnection(name, qc)
        | other -> unsupported name "Connections.get" other

    /// Alias for `Connections.get`.
    let connect (name: string) : DatabaseConnection = get name

    /// Retrieves a first-class CSV connection object.
    let connectCsv (name: string) : CsvConnection =
        match resolve name with
        | :? CsvConnector as c -> CsvConnection(name, c)
        | other -> unsupported name "Connections.connectCsv" other

    /// Retrieves a first-class Excel connection object.
    let connectExcel (name: string) : ExcelConnection =
        match resolve name with
        | :? ExcelConnector as c -> ExcelConnection(name, c)
        | other -> unsupported name "Connections.connectExcel" other

    /// Retrieves a first-class REST/JSON connection object.
    let connectRest (name: string) : RestConnection =
        match resolve name with
        | :? RestConnector as c -> RestConnection(name, c)
        | other -> unsupported name "Connections.connectRest" other


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
