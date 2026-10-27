namespace FSharpQueryStudio.Connectors

open System
open System.Globalization
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

    let private convertValue (raw: obj) (targetType: Type) : obj =
        if isNull raw || Convert.IsDBNull(raw) then
            null
        elif targetType.IsInstanceOfType(raw) then
            raw
        elif raw :? string then
            let rawStr = (raw :?> string).Trim().Trim('"', '\'')
            if targetType = typeof<string> then
                box rawStr
            elif String.IsNullOrEmpty rawStr then
                null
            elif targetType = typeof<decimal> then
                let clean = rawStr
                let parsed =
                    if clean.Contains(",") && not (clean.Contains(".")) then
                        let normalized = clean.Replace(",", ".")
                        match Decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                        | true, v -> Some v | false, _ -> None
                    elif clean.Contains(".") && not (clean.Contains(",")) then
                        match Decimal.TryParse(clean, NumberStyles.AllowDecimalPoint ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                        | true, v -> Some v | false, _ -> None
                    elif clean.Contains(",") && clean.Contains(".") then
                        let lastComma = clean.LastIndexOf(',')
                        let lastDot = clean.LastIndexOf('.')
                        let normalized =
                            if lastDot > lastComma then clean.Replace(",", "")
                            else clean.Replace(".", "").Replace(",", ".")
                        match Decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                        | true, v -> Some v | false, _ -> None
                    else
                        match Decimal.TryParse(clean, NumberStyles.Integer ||| NumberStyles.AllowLeadingSign ||| NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) with
                        | true, v -> Some v
                        | false, _ ->
                            match Decimal.TryParse(clean, NumberStyles.Number, CultureInfo.CurrentCulture) with
                            | true, v -> Some v | false, _ -> None

                match parsed with
                | Some v -> box v
                | None -> Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)

            elif targetType = typeof<double> || targetType = typeof<float> then
                let clean = rawStr
                let parsed =
                    if clean.Contains(",") && not (clean.Contains(".")) then
                        let normalized = clean.Replace(",", ".")
                        match Double.TryParse(normalized, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                        | true, v -> Some v | false, _ -> None
                    elif clean.Contains(".") && not (clean.Contains(",")) then
                        match Double.TryParse(clean, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                        | true, v -> Some v | false, _ -> None
                    elif clean.Contains(",") && clean.Contains(".") then
                        let lastComma = clean.LastIndexOf(',')
                        let lastDot = clean.LastIndexOf('.')
                        let normalized =
                            if lastDot > lastComma then clean.Replace(",", "")
                            else clean.Replace(".", "").Replace(",", ".")
                        match Double.TryParse(normalized, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                        | true, v -> Some v | false, _ -> None
                    else
                        match Double.TryParse(clean, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                        | true, v -> Some v
                        | false, _ ->
                            match Double.TryParse(clean, NumberStyles.Float ||| NumberStyles.AllowThousands, CultureInfo.CurrentCulture) with
                            | true, v -> Some v | false, _ -> None

                match parsed with
                | Some v -> box v
                | None -> Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)

            elif targetType = typeof<single> then
                let clean = rawStr
                let parsed =
                    if clean.Contains(",") && not (clean.Contains(".")) then
                        let normalized = clean.Replace(",", ".")
                        match Single.TryParse(normalized, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                        | true, v -> Some v | false, _ -> None
                    elif clean.Contains(".") && not (clean.Contains(",")) then
                        match Single.TryParse(clean, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                        | true, v -> Some v | false, _ -> None
                    elif clean.Contains(",") && clean.Contains(".") then
                        let lastComma = clean.LastIndexOf(',')
                        let lastDot = clean.LastIndexOf('.')
                        let normalized =
                            if lastDot > lastComma then clean.Replace(",", "")
                            else clean.Replace(".", "").Replace(",", ".")
                        match Single.TryParse(normalized, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                        | true, v -> Some v | false, _ -> None
                    else
                        match Single.TryParse(clean, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                        | true, v -> Some v
                        | false, _ ->
                            match Single.TryParse(clean, NumberStyles.Float ||| NumberStyles.AllowThousands, CultureInfo.CurrentCulture) with
                            | true, v -> Some v | false, _ -> None

                match parsed with
                | Some v -> box v
                | None -> Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)
            elif targetType = typeof<int> then
                match Int32.TryParse(rawStr, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                | true, v -> box v
                | false, _ -> Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)
            elif targetType = typeof<int64> then
                match Int64.TryParse(rawStr, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                | true, v -> box v
                | false, _ -> Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)
            elif targetType = typeof<int16> then
                match Int16.TryParse(rawStr, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                | true, v -> box v
                | false, _ -> Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)
            elif targetType = typeof<bool> then
                match Boolean.TryParse(rawStr) with
                | true, v -> box v
                | false, _ ->
                    let l = rawStr.ToLowerInvariant()
                    if l = "1" || l = "true" || l = "t" || l = "yes" || l = "y" then box true
                    elif l = "0" || l = "false" || l = "f" || l = "no" || l = "n" then box false
                    else Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)
            elif targetType = typeof<DateTime> then
                match DateTime.TryParse(rawStr, CultureInfo.InvariantCulture, DateTimeStyles.None) with
                | true, v -> box v
                | false, _ ->
                    match DateTime.TryParse(rawStr, CultureInfo.CurrentCulture, DateTimeStyles.None) with
                    | true, v -> box v
                    | false, _ -> Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)
            elif targetType = typeof<Guid> then
                match Guid.TryParse(rawStr) with
                | true, v -> box v
                | false, _ -> Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)
            else
                Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)
        else
            if targetType = typeof<decimal> then
                match raw with
                | :? double as d -> box (Convert.ToDecimal(d, CultureInfo.InvariantCulture))
                | :? single as f -> box (Convert.ToDecimal(f, CultureInfo.InvariantCulture))
                | :? int as i -> box (decimal i)
                | :? int64 as i64 -> box (decimal i64)
                | _ -> Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture)
            else
                Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture)

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
                                        let converted = convertValue raw innerType
                                        if isNull converted then
                                            args.[i] <- FSharpValue.MakeUnion(noneCase, [||])
                                        else
                                            args.[i] <- FSharpValue.MakeUnion(someCase, [| converted |])
                                else
                                    if isNullVal then
                                        args.[i] <- null
                                    else
                                        args.[i] <- convertValue raw propType
                            ctor args
                    else
                        fun (row: obj array) ->
                            if row.Length > 0 && not (isNull row.[0]) && not (Convert.IsDBNull(row.[0])) then
                                convertValue row.[0] targetType
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

    /// <summary>
    /// Resolves the raw ADO.NET connection string for a named SQL connection
    /// (e.g. for use with SQLProvider: SqlDataProvider&lt;...&gt;).
    /// </summary>
    /// <param name="name">The registered connection name.</param>
    /// <returns>The raw connection string with any stored credentials resolved.</returns>
    let getConnectionString (name: string) : string =
        match resolve name with
        | :? IQueryableConnector as c -> c.ConnectionString
        | other -> unsupported name "Connections.getConnectionString" other

    /// <summary>
    /// Runs a parameterized SQL query against a registered database (PostgreSQL, SQL Server, SQLite, MySQL).
    /// Parameters are bound via native ADO.NET parameter APIs, protecting against SQL injection.
    /// Returns a tabular result that renders automatically in the interactive grid.
    /// </summary>
    /// <param name="name">The registered database connection name.</param>
    /// <param name="sqlText">The SQL query string with parameter placeholders (e.g. @status or $1).</param>
    /// <param name="parameters">List of parameter name and value tuples: [ "paramName", box value ].</param>
    /// <returns>A ConnectorQueryResult displayed in the interactive grid.</returns>
    /// <example>
    /// <code>
    /// Connections.sql "SampleStore" "SELECT * FROM Products WHERE UnitPrice > @min" [ "min", box 50.0M ]
    /// </code>
    /// </example>
    let sql (name: string) (sqlText: string) (parameters: (string * obj) list) : ConnectorQueryResult =
        match resolve name with
        | :? IQueryableConnector as c ->
            let stopwatch = Stopwatch.StartNew()
            let result = c.ExecuteQuery sqlText parameters
            { result with Rows = logOnceDrained c.Kind stopwatch result.Rows }
        | other -> unsupported name "Connections.sql" other

    /// <summary>
    /// Reads all rows from a registered CSV file connection as a tabular result.
    /// </summary>
    /// <param name="name">The registered CSV connection name.</param>
    /// <returns>A ConnectorQueryResult displayed in the interactive grid.</returns>
    let csv (name: string) : ConnectorQueryResult =
        match resolve name with
        | :? CsvConnector as c ->
            let stopwatch = Stopwatch.StartNew()
            let result = c.ReadRows()
            { result with Rows = logOnceDrained (c :> IConnector).Kind stopwatch result.Rows }
        | other -> unsupported name "Connections.csv" other

    /// <summary>
    /// Reads rows from a registered Excel (.xlsx) connection, optionally bounded to maxRows.
    /// </summary>
    /// <param name="name">The registered Excel connection name.</param>
    /// <param name="maxRows">Optional upper bound on the number of rows to read.</param>
    /// <returns>A ConnectorQueryResult displayed in the interactive grid.</returns>
    let excel (name: string) (maxRows: int option) : ConnectorQueryResult =
        match resolve name with
        | :? ExcelConnector as c ->
            let stopwatch = Stopwatch.StartNew()
            let result = c.ReadRows(?maxRows = maxRows)
            { result with Rows = logOnceDrained (c :> IConnector).Kind stopwatch result.Rows }
        | other -> unsupported name "Connections.excel" other

    /// <summary>
    /// Sends an HTTP request through a registered REST/JSON connection.
    /// </summary>
    /// <param name="name">The registered REST connection name.</param>
    /// <param name="options">Request options specifying HTTP method, headers, query parameters, and body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A Task returning the parsed JsonElement response.</returns>
    let rest (name: string) (options: RestRequestOptions) (ct: CancellationToken) : Task<JsonElement> =
        match resolve name with
        | :? RestConnector as c -> c.SendAsync options ct
        | other -> unsupported name "Connections.rest" other

    /// <summary>
    /// Executes a SQL query and maps the resulting rows directly to a sequence of strongly-typed F# records or primitive types.
    /// </summary>
    /// <typeparam name="'T">The target F# record type whose field names match query columns.</typeparam>
    /// <param name="name">The registered database connection name.</param>
    /// <param name="sqlText">The parameterized SQL query.</param>
    /// <param name="parameters">List of parameter tuples: [ "paramName", box value ].</param>
    /// <returns>A sequence of strongly-typed records of type 'T.</returns>
    let query<'T> (name: string) (sqlText: string) (parameters: (string * obj) list) : 'T seq =
        let r = sql name sqlText parameters
        let mapper = getMapper<'T> r.Columns
        seq {
            for row in r.Rows do
                yield mapper row
        }

    /// <summary>
    /// Reads CSV rows and maps each record directly to a sequence of strongly-typed F# records.
    /// </summary>
    /// <typeparam name="'T">The target F# record type whose field names match CSV headers.</typeparam>
    /// <param name="name">The registered CSV connection name.</param>
    /// <returns>A sequence of strongly-typed records of type 'T.</returns>
    let queryCsv<'T> (name: string) : 'T seq =
        let r = csv name
        let mapper = getMapper<'T> r.Columns
        seq {
            for row in r.Rows do
                yield mapper row
        }

    /// <summary>
    /// Reads Excel rows and maps each row directly to a sequence of strongly-typed F# records.
    /// </summary>
    /// <typeparam name="'T">The target F# record type whose field names match column headers.</typeparam>
    /// <param name="name">The registered Excel connection name.</param>
    /// <param name="maxRows">Optional maximum number of rows to read.</param>
    /// <returns>A sequence of strongly-typed records of type 'T.</returns>
    let queryExcel<'T> (name: string) (maxRows: int option) : 'T seq =
        let r = excel name maxRows
        let mapper = getMapper<'T> r.Columns
        seq {
            for row in r.Rows do
                yield mapper row
        }

    /// <summary>
    /// Reads rows from a specific Excel sheet and maps them to strongly-typed F# records.
    /// </summary>
    /// <typeparam name="'T">The target F# record type.</typeparam>
    /// <param name="name">The registered Excel connection name.</param>
    /// <param name="sheetName">Optional sheet name (defaults to the first worksheet).</param>
    /// <param name="maxRows">Optional maximum number of rows.</param>
    /// <returns>A sequence of strongly-typed records of type 'T.</returns>
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

    /// <summary>
    /// Sends a request to a REST/JSON connection and deserializes JSON into strongly-typed records.
    /// </summary>
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

    /// <summary>
    /// Sends a request with default options or reads from a REST/JSON connection into strongly-typed records.
    /// </summary>
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

            let tables =
                try
                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds(5.0))
                    (connector.DiscoverSchemaAsync(cts.Token)).GetAwaiter().GetResult()
                with ex ->
                    Log.Warning("FS Query Studio: Schema discovery timed out or failed during entity generation for '{Name}': {Error}", name, ex.Message)
                    []
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
                if schema = "public" || schema = "dbo" || schema = "main" then
                    for t in tbls do
                        let rawTableName =
                            let dotIdx = t.Name.IndexOf(".")
                            if dotIdx > 0 then t.Name.Substring(dotIdx + 1) else t.Name
                        let propName = escapeIdent rawTableName
                        let colList = t.Columns |> List.map (fun c -> c.Name) |> String.concat ", "
                        let sqlQuery = $"SELECT {colList} FROM {t.Name}"
                        sb.AppendLine($"        member _.{propName} : System.Linq.IQueryable<{escapeIdent schema}.{propName}> = conn.Queryable<{escapeIdent schema}.{propName}>(\"{sqlQuery}\")") |> ignore
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
        member _.Name = name
        member _.Items<'T>(?options: RestRequestOptions) : 'T seq = queryJsonWithOptions<'T> name options
        member this.Queryable<'T>(?options: RestRequestOptions) : IQueryable<'T> = (this.Items<'T>(?options = options)).AsQueryable()
        member _.SendAsync(options: RestRequestOptions, ?ct: CancellationToken) : Task<JsonElement> =
            let token = defaultArg ct CancellationToken.None
            connector.SendAsync options token

    /// <summary>
    /// Retrieves a first-class database connection object exposing Query, Queryable, Execute, Schema and Tables.
    /// </summary>
    /// <param name="name">The registered database connection name.</param>
    /// <returns>A DatabaseConnection instance.</returns>
    let get (name: string) : DatabaseConnection =
        match resolve name with
        | :? IQueryableConnector as qc -> DatabaseConnection(name, qc)
        | other -> unsupported name "Connections.get" other

    /// <summary>
    /// Connects to a registered database connection (PostgreSQL, SQL Server, MySQL, SQLite).
    /// Returns a DatabaseConnection supporting `.asEntities`, `.Execute`, `.Query<'T>`, and `.Queryable<'T>`.
    /// </summary>
    /// <param name="name">The registered database connection name (with contextual autocomplete).</param>
    /// <returns>A DatabaseConnection object ready for LINQ or raw SQL.</returns>
    /// <example>
    /// <code>
    /// let conn = Connections.connect "SampleStore"
    /// let db = conn.asEntities
    /// query { for p in db.Products do select (p.Name, p.UnitPrice) }
    /// </code>
    /// </example>
    let connect (name: string) : DatabaseConnection = get name

    /// <summary>
    /// Connects to a registered CSV file connection and returns an active CsvConnection.
    /// Supports `.asEntities`, `.Rows<'T>()`, `.Queryable<'T>()`, and `.Raw()`.
    /// </summary>
    /// <param name="name">The registered CSV connection name (with contextual autocomplete).</param>
    /// <returns>A CsvConnection instance.</returns>
    /// <example>
    /// <code>
    /// let sales = (Connections.connectCsv "SalesFeed").asEntities
    /// query { for r in sales.Rows do select r }
    /// </code>
    /// </example>
    let connectCsv (name: string) : CsvConnection =
        match resolve name with
        | :? CsvConnector as c -> CsvConnection(name, c)
        | other -> unsupported name "Connections.connectCsv" other

    /// <summary>
    /// Connects to a registered Excel (.xlsx) file connection and returns an active ExcelConnection.
    /// Supports `.asEntities`, `.Rows<'T>()`, `.Sheet<'T>()`, `.Queryable<'T>()`, and `.Raw()`.
    /// </summary>
    /// <param name="name">The registered Excel connection name (with contextual autocomplete).</param>
    /// <returns>An ExcelConnection instance.</returns>
    let connectExcel (name: string) : ExcelConnection =
        match resolve name with
        | :? ExcelConnector as c -> ExcelConnection(name, c)
        | other -> unsupported name "Connections.connectExcel" other

    /// <summary>
    /// Connects to a registered REST/JSON API connection and returns an active RestConnection.
    /// Supports `.asEntities`, `.Items<'T>()`, `.Queryable<'T>()`, and `.SendAsync()`.
    /// </summary>
    /// <param name="name">The registered REST connection name (with contextual autocomplete).</param>
    /// <returns>A RestConnection instance.</returns>
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
