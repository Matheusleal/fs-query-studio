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
open System.Data
open System.Linq
open System.Linq.Expressions
open Microsoft.FSharp.Reflection

/// Resolves a named, configured connection to its live connector instance.
type IConnectionResolver =
    abstract Resolve: name: string -> IConnector

/// Optional interface implemented by resolvers that know their project root folder.
type IWorkspaceAwareResolver =
    abstract BaseDirectory: string option

/// Controlled surface used by .fsx scripts to reach configured connections by name.
/// All queries use bound parameters to prevent SQL injection.
module Connections =
    let mutable private resolver: IConnectionResolver option = None
    let mutable private engine: FileSqliteEngine option = None

    let setEngine (newEngine: FileSqliteEngine) =
        engine <- Some newEngine

    let getEngine () : FileSqliteEngine =
        match engine with
        | Some e when
            let dir = System.IO.Path.GetDirectoryName(e.DatabasePath)
            not (String.IsNullOrWhiteSpace dir) && System.IO.Directory.Exists dir -> e
        | _ ->
            let defaultPath =
                match resolver with
                | Some r ->
                    match box r with
                    | :? IWorkspaceAwareResolver as war when war.BaseDirectory.IsSome ->
                        System.IO.Path.Combine(war.BaseDirectory.Value, ".fsqs", "cache", "engine.db")
                    | _ ->
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fsqs_cache", "engine.db")
                | None ->
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fsqs_cache", "engine.db")
            let e = FileSqliteEngine(defaultPath)
            engine <- Some e
            e

    let configure (newResolver: IConnectionResolver) =
        resolver <- Some newResolver
        match box newResolver with
        | :? IWorkspaceAwareResolver as war when war.BaseDirectory.IsSome ->
            let dbPath = System.IO.Path.Combine(war.BaseDirectory.Value, ".fsqs", "cache", "engine.db")
            engine <- Some (FileSqliteEngine(dbPath))
        | _ ->
            engine <- None

    let private resolve (name: string) : IConnector =
        match resolver with
        | None -> failwith "Connections has not been configured for this session."
        | Some r -> r.Resolve name

    let private unsupported (name: string) (operation: string) (connector: IConnector) =
        failwith $"Connection '{name}' (kind {connector.Kind}) does not support {operation}."

    let private mapperCache = ConcurrentDictionary<Type * string, obj array -> obj>()

    let private isSentinelString (s: string) : bool = RecordReaders.IsSentinelString s

    let private tryParseNormalizedDecimal (clean: string) : decimal option =
        match RecordReaders.TryParseNormalizedDecimal clean with
        | true, v -> Some v
        | false, _ -> None

    let private tryParseNormalizedDouble (clean: string) : double option =
        match RecordReaders.TryParseNormalizedDouble clean with
        | true, v -> Some v
        | false, _ -> None

    let private convertValue (raw: obj) (targetType: Type) : obj =
        if isNull raw || Convert.IsDBNull(raw) then
            null
        elif targetType.IsInstanceOfType(raw) then
            match raw with
            | :? double as d when not (Double.IsFinite d) -> null
            | :? single as f when not (Single.IsFinite f) -> null
            | _ -> raw
        elif raw :? string then
            let rawStr = (raw :?> string).Trim().Trim('"', '\'')

            if targetType = typeof<string> then
                box rawStr
            elif isSentinelString rawStr then
                null
            elif targetType = typeof<decimal> then
                match tryParseNormalizedDecimal rawStr with
                | Some v -> box v
                | None ->
                    match tryParseNormalizedDouble rawStr with
                    | Some d when d >= float Decimal.MinValue && d <= float Decimal.MaxValue ->
                        try box (Convert.ToDecimal(d, CultureInfo.InvariantCulture)) with _ -> null
                    | _ -> null
            elif targetType = typeof<double> || targetType = typeof<float> then
                match tryParseNormalizedDouble rawStr with
                | Some v -> box v
                | None -> null
            elif targetType = typeof<single> then
                match tryParseNormalizedDouble rawStr with
                | Some v ->
                    let f = single v
                    if Single.IsFinite f then box f else null
                | None -> null
            elif targetType = typeof<int> then
                match Int32.TryParse(rawStr, NumberStyles.Integer ||| NumberStyles.AllowThousands ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                | true, v -> box v
                | false, _ ->
                    match tryParseNormalizedDouble rawStr with
                    | Some d ->
                        let clamped = Math.Clamp(Math.Round(d), float Int32.MinValue, float Int32.MaxValue)
                        box (int clamped)
                    | None -> null
            elif targetType = typeof<int64> then
                match Int64.TryParse(rawStr, NumberStyles.Integer ||| NumberStyles.AllowThousands ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                | true, v -> box v
                | false, _ ->
                    match tryParseNormalizedDouble rawStr with
                    | Some d ->
                        let clamped = Math.Clamp(Math.Round(d), float Int64.MinValue, float Int64.MaxValue)
                        box (int64 clamped)
                    | None -> null
            elif targetType = typeof<int16> then
                match Int16.TryParse(rawStr, NumberStyles.Integer ||| NumberStyles.AllowThousands ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                | true, v -> box v
                | false, _ ->
                    match tryParseNormalizedDouble rawStr with
                    | Some d ->
                        let clamped = Math.Clamp(Math.Round(d), float Int16.MinValue, float Int16.MaxValue)
                        box (int16 clamped)
                    | None -> null
            elif targetType = typeof<bool> then
                match Boolean.TryParse(rawStr) with
                | true, v -> box v
                | false, _ ->
                    let l = rawStr.ToLowerInvariant()
                    if l = "1" || l = "true" || l = "t" || l = "yes" || l = "y" || l = "sim" || l = "s" then box true
                    elif l = "0" || l = "false" || l = "f" || l = "no" || l = "n" || l = "nao" || l = "não" then box false
                    else null
            elif targetType = typeof<DateTime> then
                match DateTime.TryParse(rawStr, CultureInfo.InvariantCulture, DateTimeStyles.None) with
                | true, v -> box v
                | false, _ ->
                    match DateTime.TryParse(rawStr, CultureInfo.CurrentCulture, DateTimeStyles.None) with
                    | true, v -> box v
                    | false, _ ->
                        match tryParseNormalizedDouble rawStr with
                        | Some d when d > -657435.0 && d < 2958466.0 ->
                            try box (DateTime.FromOADate(d)) with _ -> null
                        | _ -> null
            elif targetType = typeof<DateOnly> then
                match DateOnly.TryParse(rawStr, CultureInfo.InvariantCulture, DateTimeStyles.None) with
                | true, v -> box v
                | false, _ ->
                    match DateTime.TryParse(rawStr, CultureInfo.InvariantCulture, DateTimeStyles.None) with
                    | true, dt -> box (DateOnly.FromDateTime(dt))
                    | false, _ -> null
            elif targetType = typeof<Guid> then
                match Guid.TryParse(rawStr) with
                | true, v -> box v
                | false, _ -> null
            else
                try Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)
                with _ -> null
        else
            try
                if targetType = typeof<string> then
                    box (Convert.ToString(raw, CultureInfo.InvariantCulture))
                elif targetType = typeof<decimal> then
                    match raw with
                    | :? double as d -> if Double.IsFinite d then box (Convert.ToDecimal(d, CultureInfo.InvariantCulture)) else null
                    | :? single as f -> if Single.IsFinite f then box (Convert.ToDecimal(f, CultureInfo.InvariantCulture)) else null
                    | :? int as i -> box (decimal i)
                    | :? int64 as i64 -> box (decimal i64)
                    | :? int16 as i16 -> box (decimal i16)
                    | _ -> Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture)
                elif targetType = typeof<double> || targetType = typeof<float> then
                    match raw with
                    | :? decimal as m -> box (double m)
                    | :? single as f -> if Single.IsFinite f then box (double f) else null
                    | :? int as i -> box (double i)
                    | :? int64 as i64 -> box (double i64)
                    | _ ->
                        let v = Convert.ToDouble(raw, CultureInfo.InvariantCulture)
                        if Double.IsFinite v then box v else null
                elif targetType = typeof<int> then
                    match raw with
                    | :? double as d ->
                        if Double.IsFinite d then
                            box (int (Math.Clamp(Math.Round(d), float Int32.MinValue, float Int32.MaxValue)))
                        else null
                    | :? decimal as m ->
                        box (int (Math.Clamp(Math.Round(double m), float Int32.MinValue, float Int32.MaxValue)))
                    | :? int64 as i64 ->
                        box (int (Math.Clamp(i64, int64 Int32.MinValue, int64 Int32.MaxValue)))
                    | _ -> Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture)
                elif targetType = typeof<int64> then
                    match raw with
                    | :? double as d ->
                        if Double.IsFinite d then
                            box (int64 (Math.Clamp(Math.Round(d), float Int64.MinValue, float Int64.MaxValue)))
                        else null
                    | :? decimal as m ->
                        box (int64 (Math.Clamp(Math.Round(double m), float Int64.MinValue, float Int64.MaxValue)))
                    | :? int as i -> box (int64 i)
                    | _ -> Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture)
                elif targetType = typeof<DateTime> then
                    match raw with
                    | :? DateOnly as d -> box (d.ToDateTime(TimeOnly.MinValue))
                    | :? DateTimeOffset as dto -> box dto.DateTime
                    | :? double as d when Double.IsFinite d && d > -657435.0 && d < 2958466.0 -> box (DateTime.FromOADate(d))
                    | _ -> Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture)
                elif targetType = typeof<DateOnly> then
                    match raw with
                    | :? DateTime as dt -> box (DateOnly.FromDateTime(dt))
                    | :? DateTimeOffset as dto -> box (DateOnly.FromDateTime(dto.DateTime))
                    | :? double as d when Double.IsFinite d && d > -657435.0 && d < 2958466.0 ->
                        box (DateOnly.FromDateTime(DateTime.FromOADate(d)))
                    | _ -> DateOnly.Parse(string raw, CultureInfo.InvariantCulture) |> box
                else
                    Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture)
            with _ ->
                null

    let getCompiledRecordMapper<'T> = CompiledRecordMapper.getCompiledRecordMapper<'T>

    type private FieldMapperSpec =
        { ColIndex: int
          PropType: Type
          IsOption: bool
          InnerType: Type
          NoneValue: obj
          SomeCtor: (obj array -> obj) option
          DefaultValue: obj }

    let getMapper<'T> (columns: string list) : (obj array -> 'T) =
        let targetType = typeof<'T>
        let key = (targetType, String.concat "," columns)
        let rawMapper: obj array -> obj =
            mapperCache.GetOrAdd(
                key,
                Func<Type * string, obj array -> obj>(fun _ ->
                    let flags = System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.NonPublic ||| System.Reflection.BindingFlags.Instance
                    if targetType = typeof<obj array> then
                        (fun row -> box row)
                    elif FSharpType.IsRecord(targetType, flags) then
                        let fields = FSharpType.GetRecordFields(targetType, flags)
                        let sanitizedColumns = CodeGenerationHelpers.sanitizeColumns columns
                        let specs =
                            fields
                            |> Array.mapi (fun defaultIdx f ->
                                let byName =
                                    columns
                                    |> List.tryFindIndex (fun c ->
                                        String.Equals(c, f.Name, StringComparison.OrdinalIgnoreCase)
                                        || String.Equals(CodeGenerationHelpers.cleanIdentifier c, f.Name, StringComparison.OrdinalIgnoreCase)
                                        || String.Equals(CodeGenerationHelpers.cleanIdentifier c, CodeGenerationHelpers.cleanIdentifier f.Name, StringComparison.OrdinalIgnoreCase))
                                    |> Option.orElseWith (fun () ->
                                        sanitizedColumns
                                        |> List.tryFindIndex (fun sc ->
                                            String.Equals(sc, f.Name, StringComparison.OrdinalIgnoreCase)
                                            || String.Equals(CodeGenerationHelpers.cleanIdentifier sc, CodeGenerationHelpers.cleanIdentifier f.Name, StringComparison.OrdinalIgnoreCase)))
                                let colIdx =
                                    match byName with
                                    | Some idx -> idx
                                    | None -> if defaultIdx < columns.Length then defaultIdx else -1

                                let propType = f.PropertyType
                                let isOption = propType.IsGenericType && propType.GetGenericTypeDefinition() = typedefof<option<_>>
                                if isOption then
                                    let cases = FSharpType.GetUnionCases(propType, flags)
                                    let noneCase = cases |> Array.find (fun c -> c.Name = "None")
                                    let someCase = cases |> Array.find (fun c -> c.Name = "Some")
                                    { ColIndex = colIdx
                                      PropType = propType
                                      IsOption = true
                                      InnerType = propType.GetGenericArguments().[0]
                                      NoneValue = FSharpValue.MakeUnion(noneCase, [||], flags)
                                      SomeCtor = Some(FSharpValue.PreComputeUnionConstructor(someCase, flags))
                                      DefaultValue = null }
                                else
                                    let defaultVal =
                                        if propType = typeof<string> then box ""
                                        elif propType = typeof<DateTime> then box DateTime.MinValue
                                        elif propType.IsValueType then Activator.CreateInstance(propType)
                                        else null
                                    { ColIndex = colIdx
                                      PropType = propType
                                      IsOption = false
                                      InnerType = propType
                                      NoneValue = null
                                      SomeCtor = None
                                      DefaultValue = defaultVal }
                            )
                        let ctor = FSharpValue.PreComputeRecordConstructor(targetType, flags)
                        let fieldCount = specs.Length
                        fun (row: obj array) ->
                            let args: obj array = Array.zeroCreate fieldCount
                            let rowLen = if isNull row then 0 else row.Length
                            for i = 0 to fieldCount - 1 do
                                let spec = specs.[i]
                                let idx = spec.ColIndex
                                let raw = if idx >= 0 && idx < rowLen then row.[idx] else null
                                let isNullVal = isNull raw || Convert.IsDBNull(raw)

                                if spec.IsOption then
                                    let isOptionNone =
                                        isNullVal
                                        || (match raw with
                                            | :? string as s -> isSentinelString s
                                            | _ -> false)

                                    if isOptionNone then
                                        args.[i] <- spec.NoneValue
                                    else
                                        let converted = convertValue raw spec.InnerType
                                        if isNull converted then
                                            args.[i] <- spec.NoneValue
                                        else
                                            args.[i] <- spec.SomeCtor.Value [| converted |]
                                else
                                    if isNullVal then
                                        args.[i] <- spec.DefaultValue
                                    else
                                        let converted = convertValue raw spec.PropType
                                        args.[i] <- if isNull converted then spec.DefaultValue else converted
                            ctor args
                    else
                        let defaultScalar =
                            if targetType = typeof<string> then box ""
                            elif targetType.IsValueType then Activator.CreateInstance(targetType)
                            else null
                        fun (row: obj array) ->
                            if not (isNull row) && row.Length > 0 && not (isNull row.[0]) && not (Convert.IsDBNull(row.[0])) then
                                let converted = convertValue row.[0] targetType
                                if isNull converted then defaultScalar else converted
                            else
                                defaultScalar
                )
            )
        fun (row: obj array) -> unbox<'T> (rawMapper row)

    /// Wraps rows to log row count and elapsed duration once enumeration finishes.
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

    let executeSqlRaw (name: string) (sqlText: string) (parameters: (string * obj) list) : ConnectorQueryResult =
        match resolve name with
        | :? IQueryableConnector as c ->
            let stopwatch = Stopwatch.StartNew()
            let result = c.ExecuteQuery sqlText parameters
            { result with Rows = logOnceDrained c.Kind stopwatch result.Rows }
        | other -> unsupported name "Connections.sql" other

    let readCsvRaw (name: string) : ConnectorQueryResult =
        match resolve name with
        | :? CsvConnector as c ->
            let stopwatch = Stopwatch.StartNew()
            let result = c.ReadRows()
            { result with Rows = logOnceDrained (c :> IConnector).Kind stopwatch result.Rows }
        | other -> unsupported name "Connections.csv" other

    let readExcelRaw (name: string) (maxRows: int option) : ConnectorQueryResult =
        match resolve name with
        | :? ExcelConnector as c ->
            let stopwatch = Stopwatch.StartNew()
            let result = c.ReadRows(?maxRows = maxRows)
            { result with Rows = logOnceDrained (c :> IConnector).Kind stopwatch result.Rows }
        | other -> unsupported name "Connections.excel" other

    let sendRestAsync (name: string) (options: RestRequestOptions) (ct: CancellationToken) : Task<JsonElement> =
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
        match resolve name with
        | :? IStreamingQueryableConnector as streaming ->
            let stopwatch = Stopwatch.StartNew()
            let rows = streaming.ExecuteReaderStreamWithColumns<'T> sqlText parameters getCompiledRecordMapper<'T>
            seq {
                let mutable count = 0
                for row in rows do
                    count <- count + 1
                    yield row
                stopwatch.Stop()
                Log.Information(
                    "Connector call {Kind} returned {RowCount} rows in {ElapsedMs}ms",
                    streaming.Kind,
                    count,
                    stopwatch.ElapsedMilliseconds
                )
            }
        | _ ->
            let r = executeSqlRaw name sqlText parameters
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
        seq {
            let r = readCsvRaw name
            let mapper = getMapper<'T> r.Columns
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
        seq {
            let r = readExcelRaw name maxRows
            let mapper = getMapper<'T> r.Columns
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
        seq {
            match resolve name with
            | :? ExcelConnector as c ->
                let stopwatch = Stopwatch.StartNew()
                let result = c.ReadRows(?sheetName = sheetName, ?maxRows = maxRows)
                let logged = { result with Rows = logOnceDrained (c :> IConnector).Kind stopwatch result.Rows }
                let mapper = getMapper<'T> logged.Columns
                for row in logged.Rows do
                    yield mapper row
            | other -> unsupported name "Connections.queryExcelSheet" other
        }

    let private jsonElementToBoxedValue (v: JsonElement) : obj =
        match v.ValueKind with
        | JsonValueKind.Null
        | JsonValueKind.Undefined -> null
        | JsonValueKind.String -> box (v.GetString())
        | JsonValueKind.True -> box true
        | JsonValueKind.False -> box false
        | JsonValueKind.Number ->
            let okI, i = v.TryGetInt32()
            if okI then box i
            else
                let okL, l = v.TryGetInt64()
                if okL then box l
                else
                    let okM, m = v.TryGetDecimal()
                    if okM then box m
                    else
                        let okD, d = v.TryGetDouble()
                        if okD then box d else box (v.GetRawText())
        | _ -> box (v.GetRawText())

    /// <summary>
    /// Sends a request to a REST/JSON connection and deserializes JSON into strongly-typed records.
    /// </summary>
    let queryJsonWithOptions<'T> (name: string) (options: RestRequestOptions option) : 'T seq =
        match resolve name with
        | :? RestConnector as c ->
            let stopwatch = Stopwatch.StartNew()
            let opts = defaultArg options c.BaseOptions
            let elem = (c.SendAsync opts CancellationToken.None).GetAwaiter().GetResult()
            let objectElements =
                if elem.ValueKind = JsonValueKind.Array then
                    elem.EnumerateArray()
                    |> Seq.filter (fun e -> e.ValueKind = JsonValueKind.Object)
                    |> Seq.toList
                elif elem.ValueKind = JsonValueKind.Object then
                    let arrayProp =
                        elem.EnumerateObject()
                        |> Seq.tryFind (fun prop -> prop.Value.ValueKind = JsonValueKind.Array)
                    match arrayProp with
                    | Some prop ->
                        prop.Value.EnumerateArray()
                        |> Seq.filter (fun e -> e.ValueKind = JsonValueKind.Object)
                        |> Seq.toList
                    | None -> [ elem ]
                else
                    []

            if FSharpType.IsRecord(typeof<'T>) && not (List.isEmpty objectElements) then
                let columns =
                    objectElements
                    |> Seq.collect (fun o -> o.EnumerateObject() |> Seq.map (fun p -> p.Name))
                    |> Seq.distinct
                    |> Seq.toList
                let rows =
                    objectElements
                    |> Seq.map (fun o ->
                        columns
                        |> List.map (fun col ->
                            match o.TryGetProperty(col) with
                            | true, v -> jsonElementToBoxedValue v
                            | false, _ -> null)
                        |> Array.ofList)
                let loggedRows = logOnceDrained (c :> IConnector).Kind stopwatch rows
                let mapper = getMapper<'T> columns
                seq {
                    for row in loggedRows do
                        yield mapper row
                }
            else
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
                stopwatch.Stop()
                items :> 'T seq
        | other -> unsupported name "Connections.queryJson" other

    /// <summary>
    /// Sends a request with default options or reads from a REST/JSON connection into strongly-typed records.
    /// </summary>
    let queryJson<'T> (name: string) : 'T seq =
        queryJsonWithOptions<'T> name None

    /// <summary>
    /// Executes a SQL query directly against the workspace's FileSqliteEngine,
    /// returning a ConnectorQueryResult (ResultKind.Table) across all staged files.
    /// </summary>
    let queryEngine (sqlText: string) : ConnectorQueryResult =
        (getEngine()).ExecuteQuery(sqlText, [])

    let queryEngineWith (sqlText: string) (parameters: (string * obj) list) : ConnectorQueryResult =
        (getEngine()).ExecuteQuery(sqlText, parameters)

    /// Represents an active database connection with first-class query and schema discovery members for autocomplete.
    type DatabaseConnection(name: string, connector: IQueryableConnector) =
        member _.Name = name
        member _.ConnectionString = connector.ConnectionString
        member _.Execute(sqlText: string, ?parameters: (string * obj) list) : ConnectorQueryResult =
            let p = defaultArg parameters []
            match resolver with
            | Some _ -> executeSqlRaw name sqlText p
            | None -> connector.ExecuteQuery sqlText p
        member this.rawQuery(sqlText: string, ?parameters: (string * obj) list) : ConnectorQueryResult =
            let p = defaultArg parameters []
            match resolver with
            | Some _ -> this.Execute(sqlText, p)
            | None ->
                let stopwatch = Stopwatch.StartNew()
                let r = this.Execute(sqlText, p)
                { r with Rows = logOnceDrained (connector :> IConnector).Kind stopwatch r.Rows }
        member this.rawQuery(sqlText: string) : ConnectorQueryResult =
            this.rawQuery(sqlText, [])
        member _.Query<'T>(sqlText: string, ?parameters: (string * obj) list) : 'T seq =
            let p = defaultArg parameters []
            match connector with
            | :? IStreamingQueryableConnector as streaming ->
                let stopwatch = Stopwatch.StartNew()
                let rows = streaming.ExecuteReaderStreamWithColumns<'T> sqlText p getCompiledRecordMapper<'T>
                seq {
                    let mutable count = 0
                    for row in rows do
                        count <- count + 1
                        yield row
                    stopwatch.Stop()
                    Log.Information(
                        "Connector call {Kind} returned {RowCount} rows in {ElapsedMs}ms",
                        (connector :> IConnector).Kind,
                        count,
                        stopwatch.ElapsedMilliseconds
                    )
                }
            | _ ->
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
            SqlPushdownQueryable<'T>(connector, sqlText, p) :> IQueryable<'T>

        member this.PushdownQueryable<'T>(sqlText: string, ?parameters: (string * obj) list) : SqlPushdownQueryable<'T> =
            let p = defaultArg parameters []
            SqlPushdownQueryable<'T>(connector, sqlText, p)

        member this.Table<'T>(tableName: string) : SqlPushdownQueryable<'T> =
            SqlPushdownQueryable<'T>(connector, tableName, [])

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
            sb.AppendLine($"        new(connName: string) = Database(FSharpQueryStudio.Connectors.Connections.getDatabaseConnection(connName))") |> ignore
            sb.AppendLine($"        new() = Database(\"{name}\")") |> ignore
            sb.AppendLine("        member _.rawQuery(sqlText: string, ?parameters: (string * obj) list) : FSharpQueryStudio.Connectors.ConnectorQueryResult = conn.rawQuery(sqlText, ?parameters = parameters)") |> ignore
            sb.AppendLine("        member _.rawQuery(sqlText: string) : FSharpQueryStudio.Connectors.ConnectorQueryResult = conn.rawQuery(sqlText)") |> ignore
            sb.AppendLine("        member _.Execute(sqlText: string, ?parameters: (string * obj) list) : FSharpQueryStudio.Connectors.ConnectorQueryResult = conn.Execute(sqlText, ?parameters = parameters)") |> ignore
            sb.AppendLine("        member _.Table<'T>(tableName: string) : FSharpQueryStudio.Connectors.SqlPushdownQueryable<'T> = conn.Table<'T>(tableName)") |> ignore
            sb.AppendLine("        member _.Connection = conn") |> ignore
            for (schema, tbls) in schemaGroups do
                if schema = "public" || schema = "dbo" || schema = "main" then
                    for t in tbls do
                        let rawTableName =
                            let dotIdx = t.Name.IndexOf(".")
                            if dotIdx > 0 then t.Name.Substring(dotIdx + 1) else t.Name
                        let propName = escapeIdent rawTableName
                        sb.AppendLine($"        member _.{propName} : FSharpQueryStudio.Connectors.SqlPushdownQueryable<{escapeIdent schema}.{propName}> = conn.Table<{escapeIdent schema}.{propName}>(\"{t.Name}\")") |> ignore
                sb.AppendLine($"        member _.{escapeIdent schema} =") |> ignore
                sb.AppendLine("            {|") |> ignore
                for t in tbls do
                    let rawTableName =
                        let dotIdx = t.Name.IndexOf(".")
                        if dotIdx > 0 then t.Name.Substring(dotIdx + 1) else t.Name
                    let propName = escapeIdent rawTableName
                    sb.AppendLine($"                {propName} = conn.Table<{escapeIdent schema}.{propName}>(\"{t.Name}\")") |> ignore
                sb.AppendLine("            |}") |> ignore
                sb.AppendLine() |> ignore
            sb.AppendLine("    let database () : Database = Database()") |> ignore
            sb.AppendLine("    let db () : Database = Database()") |> ignore
            sb.AppendLine() |> ignore

            sb.ToString()

        /// Generates a SQLProvider declaration snippet for this database connection.
        member this.GenerateSqlProvider(?typeAlias: string) : string =
            let alias = defaultArg typeAlias "Db"
            let kind = (connector :> IConnector).Kind
            let vendor =
                if kind = ConnectorKind.PostgreSql then "Common.DatabaseProviderTypes.POSTGRESQL"
                elif kind = ConnectorKind.SqlServer then "Common.DatabaseProviderTypes.MSSQLSERVER"
                elif kind = ConnectorKind.Sqlite then "Common.DatabaseProviderTypes.SQLITE"
                else "Common.DatabaseProviderTypes.POSTGRESQL"
            $"open FSharp.Data.Sql\n\n[<Literal>]\nlet private connStr = \"{connector.ConnectionString}\"\n\ntype {alias} = SqlDataProvider<\n    DatabaseVendor = {vendor},\n    ConnectionString = connStr >\n\nlet db = {alias}.GetDataContext()\n"

    /// Represents an active CSV connection with typed row queries and autocomplete.
    type CsvConnection(name: string, connector: CsvConnector) =
        member _.Name = name
        member _.Connector = connector
        member _.FilePath = connector.FilePath
        member _.Rows<'T>() : 'T seq = queryCsv<'T> name
        member this.Queryable<'T>() : IQueryable<'T> =
            let eng = getEngine ()
            let cleanTableName = eng.EnsureStaged(name, connector.FilePath, (fun () -> connector.ReadRows()), ?columnTypes = (if connector.Options.ColumnTypes.IsEmpty then None else Some connector.Options.ColumnTypes))
            SqlPushdownQueryable<'T>(eng.Connector, cleanTableName) :> IQueryable<'T>
        member this.PushdownQueryable<'T>() : SqlPushdownQueryable<'T> =
            let eng = getEngine ()
            let cleanTableName = eng.EnsureStaged(name, connector.FilePath, (fun () -> connector.ReadRows()), ?columnTypes = (if connector.Options.ColumnTypes.IsEmpty then None else Some connector.Options.ColumnTypes))
            SqlPushdownQueryable<'T>(eng.Connector, cleanTableName)
        member _.Raw() : ConnectorQueryResult = readCsvRaw name

    /// Represents an active Excel workbook connection with multi-sheet typed queries.
    type ExcelConnection(name: string, connector: ExcelConnector) =
        let resolveDefaultSheetName () =
            match connector.Options.SheetName with
            | Some s when not (String.IsNullOrWhiteSpace s) -> s
            | _ ->
                let tables =
                    try
                        (connector :> IConnector).DiscoverSchemaAsync(CancellationToken.None).GetAwaiter().GetResult()
                    with _ -> []
                match tables with
                | first :: _ -> first.Name
                | [] -> "Sheet1"
        member _.Name = name
        member _.Connector = connector
        member _.FilePath = connector.FilePath
        member _.Rows<'T>(?maxRows: int) : 'T seq = queryExcel<'T> name maxRows
        member _.Sheet<'T>(sheetName: string, ?maxRows: int) : 'T seq = queryExcelSheet<'T> name (Some sheetName) maxRows
        member this.Queryable<'T>(?maxRows: int) : IQueryable<'T> =
            this.PushdownQueryable<'T>(?maxRows = maxRows) :> IQueryable<'T>
        member this.PushdownQueryable<'T>(?maxRows: int) : SqlPushdownQueryable<'T> =
            let sheetName = resolveDefaultSheetName ()
            this.SheetPushdownQueryable<'T>(sheetName, ?maxRows = maxRows)
        member this.SheetQueryable<'T>(sheetName: string, ?maxRows: int) : IQueryable<'T> =
            this.SheetPushdownQueryable<'T>(sheetName, ?maxRows = maxRows) :> IQueryable<'T>
        member this.SheetPushdownQueryable<'T>(sheetName: string, ?maxRows: int) : SqlPushdownQueryable<'T> =
            let eng = getEngine ()
            let compoundKey = $"{name}_{sheetName}"
            let cleanTableName =
                eng.EnsureStaged(
                    compoundKey,
                    connector.FilePath,
                    (fun () -> connector.ReadRows(sheetName = sheetName, ?maxRows = maxRows)),
                    ?columnTypes = (if connector.Options.ColumnTypes.IsEmpty then None else Some connector.Options.ColumnTypes)
                )
            SqlPushdownQueryable<'T>(eng.Connector, cleanTableName)
        member _.Raw(?maxRows: int) : ConnectorQueryResult = readExcelRaw name maxRows

    /// Represents an active REST/JSON connection with typed queries.
    type RestConnection(name: string, connector: RestConnector) =
        member _.Name = name
        member _.Items<'T>(?options: RestRequestOptions) : 'T seq = queryJsonWithOptions<'T> name options
        member this.Queryable<'T>(?options: RestRequestOptions) : IQueryable<'T> = (this.Items<'T>(?options = options)).AsQueryable()
        member _.SendAsync(options: RestRequestOptions, ?ct: CancellationToken) : Task<JsonElement> =
            let token = defaultArg ct CancellationToken.None
            connector.SendAsync options token

    /// Category container for registered relational SQL database connections (e.g. Connections.sql.SampleStore).
    type SqlCategory() =
        member _.query(sqlText: string, ?parameters: (string * obj) list) : ConnectorQueryResult =
            let p = defaultArg parameters []
            (getEngine()).ExecuteQuery(sqlText, p)
        member _.rawQuery(sqlText: string, ?parameters: (string * obj) list) : ConnectorQueryResult =
            let p = defaultArg parameters []
            (getEngine()).ExecuteQuery(sqlText, p)
        /// Builds dynamic SQL for cross-tabulation / pivot query given known pivot column values.
        static member buildPivotSql(tableName: string, rowKeys: string list, pivotCol: string, valCol: string, pivotVals: string list, ?aggFunc: string) : string =
            let agg = defaultArg aggFunc "SUM"
            let rowKeysStr = String.concat ", " rowKeys
            let pivotCols =
                pivotVals
                |> List.map (fun v ->
                    let safeVal = v.Replace("'", "''")
                    let safeAlias = v.Replace("\"", "\"\"")
                    $"{agg}(CASE WHEN {pivotCol} = '{safeVal}' THEN {valCol} END) AS \"{safeAlias}\""
                )
                |> String.concat ",\n    "
            if List.isEmpty pivotVals then
                $"SELECT {rowKeysStr} FROM {tableName} GROUP BY {rowKeysStr};"
            else
                $"SELECT {rowKeysStr},\n    {pivotCols}\nFROM {tableName}\nGROUP BY {rowKeysStr};"
        member this.buildPivotSql(tableName: string, rowKeys: string list, pivotCol: string, valCol: string, pivotVals: string list, ?aggFunc: string) : string =
            SqlCategory.buildPivotSql(tableName, rowKeys, pivotCol, valCol, pivotVals, ?aggFunc = aggFunc)
        /// Performs a dynamic SQL cross-tabulation / pivot on the staging engine.
        /// Extracts distinct values of pivotCol and executes conditional aggregation grouped by rowKeys.
        member this.pivot(tableName: string, rowKeys: string list, pivotCol: string, valCol: string, ?aggFunc: string) : ConnectorQueryResult =
            let eng = getEngine ()
            let distinctSql = $"SELECT DISTINCT {pivotCol} AS val FROM {tableName} WHERE {pivotCol} IS NOT NULL ORDER BY 1;"
            let distinctRes = eng.ExecuteQuery(distinctSql, [])
            let pivotVals =
                distinctRes.Rows
                |> Seq.choose (fun row ->
                    if row.Length > 0 && not (isNull row.[0]) then
                        let s = string row.[0]
                        if not (String.IsNullOrWhiteSpace s) then Some s else None
                    else None
                )
                |> Seq.toList
            let pivotSql = this.buildPivotSql(tableName, rowKeys, pivotCol, valCol, pivotVals, ?aggFunc = aggFunc)
            eng.ExecuteQuery(pivotSql, [])

    /// Category container for registered delimited CSV file connections (e.g. Connections.csv.CustomersFeed).
    type CsvCategory() = class end

    /// Category container for registered Excel workbook connections (e.g. Connections.excel.Reports).
    type ExcelCategory() = class end

    /// Category container for registered REST/JSON API connections (e.g. Connections.rest.WeatherApi).
    type RestCategory() = class end

    /// Access registered SQL database connections with strongly-typed schemas and rawQuery.
    let sql = SqlCategory()

    /// Access registered CSV file connections with strongly-typed records and LINQ query support.
    let csv = CsvCategory()

    /// Access registered Excel spreadsheet connections with strongly-typed sheet records.
    let excel = ExcelCategory()

    /// Access registered REST/JSON endpoint connections with strongly-typed response items.
    let rest = RestCategory()

    let getDatabaseConnection (name: string) : DatabaseConnection =
        match resolve name with
        | :? IQueryableConnector as qc -> DatabaseConnection(name, qc)
        | other -> unsupported name "Connections.sql" other

    let getCsvConnection (name: string) : CsvConnection =
        match resolve name with
        | :? CsvConnector as c -> CsvConnection(name, c)
        | other -> unsupported name "Connections.csv" other

    let getExcelConnection (name: string) : ExcelConnection =
        match resolve name with
        | :? ExcelConnector as c -> ExcelConnection(name, c)
        | other -> unsupported name "Connections.excel" other

    let getRestConnection (name: string) : RestConnection =
        match resolve name with
        | :? RestConnector as c -> RestConnection(name, c)
        | other -> unsupported name "Connections.rest" other

    /// Result of an asynchronous data sources pre-staging / sync operation.
    type DataSourcesSyncSummary =
        { StagedCount: int
          SkippedCount: int
          Total: int
          ElapsedMs: int64 }

    /// Asynchronously synchronizes and pre-stages all CSV and Excel connections for a workspace into the persistent SQLite disk cache with progress reporting.
    let syncDataSourcesAsyncWithProgress
        (workspaceDir: string)
        (connections: ConnectionDefinition list)
        (secretStore: FSharpQueryStudio.Security.ISecretStore)
        (onProgress: (StagingProgressEvent -> unit) option) : Task<DataSourcesSyncSummary> =
        task {
            let sw = Stopwatch.StartNew()
            let baseDir = ConnectorDescriptor.projectRootOf workspaceDir
            let cacheDir = System.IO.Path.Combine(workspaceDir, ".fsqs", "cache")
            if not (System.IO.Directory.Exists cacheDir) then
                System.IO.Directory.CreateDirectory cacheDir |> ignore
            let dbPath = System.IO.Path.Combine(cacheDir, "engine.db")
            let eng =
                match engine with
                | Some e when String.Equals(System.IO.Path.GetFullPath(e.DatabasePath), System.IO.Path.GetFullPath(dbPath), StringComparison.OrdinalIgnoreCase) -> e
                | _ ->
                    let e = FileSqliteEngine(dbPath)
                    engine <- Some e
                    e

            let mutable stagedCount = 0
            let mutable skippedCount = 0
            let fileConns =
                connections
                |> List.filter (fun c -> c.Kind.Id = "Csv" || c.Kind.Id = "Excel")

            let total = fileConns.Length

            for i in 0 .. fileConns.Length - 1 do
                let connDef = fileConns.[i]
                let currentIdx = i + 1
                try
                    let conn: IConnector = ConnectorRegistry.Instance.Resolve(connDef, secretStore, Some baseDir)
                    match connDef.Kind.Id with
                    | "Csv" ->
                        let csvConn = conn :?> CsvConnector
                        let filePath = csvConn.FilePath
                        let fullPath = if System.IO.Path.IsPathRooted filePath then filePath else System.IO.Path.Combine(workspaceDir, filePath)
                        if System.IO.File.Exists fullPath then
                            if eng.IsStagedAndCurrent(connDef.Name, fullPath) then
                                skippedCount <- skippedCount + 1
                                match onProgress with
                                | Some cb ->
                                    cb {
                                        TableName = connDef.Name
                                        FilePath = fullPath
                                        Phase = "Skipped"
                                        RowCount = None
                                        CurrentIndex = Some currentIdx
                                        TotalCount = Some total
                                        ElapsedMs = Some 0L
                                        Message = Some $"'{connDef.Name}' is up-to-date in cache"
                                    }
                                | None -> ()
                            else
                                eng.EnsureStaged(
                                    connDef.Name,
                                    fullPath,
                                    (fun () -> csvConn.ReadRows()),
                                    ?columnTypes = (if csvConn.Options.ColumnTypes.IsEmpty then None else Some csvConn.Options.ColumnTypes),
                                    currentIndex = currentIdx,
                                    totalCount = total,
                                    ?onProgress = onProgress
                                ) |> ignore
                                stagedCount <- stagedCount + 1
                    | "Excel" ->
                        let excelConn = conn :?> ExcelConnector
                        let filePath = excelConn.FilePath
                        let fullPath = if System.IO.Path.IsPathRooted filePath then filePath else System.IO.Path.Combine(workspaceDir, filePath)
                        if System.IO.File.Exists fullPath then
                            // Remove any legacy unprefixed table staged under the workbook connection name
                            eng.DropStaleTableIfExists(connDef.Name, fullPath)

                            let tables =
                                try
                                    (excelConn :> IConnector).DiscoverSchemaAsync(CancellationToken.None).GetAwaiter().GetResult()
                                with ex ->
                                    Log.Warning("FS Query Studio: Could not discover worksheets for Excel connection '{Name}': {Error}", connDef.Name, ex.Message)
                                    []

                            for tbl in tables do
                                let sheetName = tbl.Name
                                let compoundKey = $"{connDef.Name}_{sheetName}"

                                if eng.IsStagedAndCurrent(compoundKey, fullPath) then
                                    skippedCount <- skippedCount + 1
                                    match onProgress with
                                    | Some cb ->
                                        cb {
                                            TableName = compoundKey
                                            FilePath = fullPath
                                            Phase = "Skipped"
                                            RowCount = None
                                            CurrentIndex = Some currentIdx
                                            TotalCount = Some total
                                            ElapsedMs = Some 0L
                                            Message = Some $"'{compoundKey}' is up-to-date in cache"
                                        }
                                    | None -> ()
                                else
                                    eng.EnsureStaged(
                                        compoundKey,
                                        fullPath,
                                        (fun () -> excelConn.ReadRows(sheetName = sheetName)),
                                        ?columnTypes = (if excelConn.Options.ColumnTypes.IsEmpty then None else Some excelConn.Options.ColumnTypes),
                                        currentIndex = currentIdx,
                                        totalCount = total,
                                        ?onProgress = onProgress
                                    ) |> ignore
                                    stagedCount <- stagedCount + 1
                    | _ -> ()
                with ex ->
                    Log.Warning("FS Query Studio: Background staging failed for connection '{Name}': {Error}", connDef.Name, ex.Message)

            sw.Stop()
            return
                { StagedCount = stagedCount
                  SkippedCount = skippedCount
                  Total = fileConns.Length
                  ElapsedMs = sw.ElapsedMilliseconds }
        }

    /// Asynchronously synchronizes and pre-stages all CSV and Excel connections for a workspace into the persistent SQLite disk cache.
    let syncDataSourcesAsync (workspaceDir: string) (connections: ConnectionDefinition list) (secretStore: FSharpQueryStudio.Security.ISecretStore) : Task<DataSourcesSyncSummary> =
        syncDataSourcesAsyncWithProgress workspaceDir connections secretStore None

    /// Synchronizes and pre-stages all CSV and Excel connections without progress callback.
    let syncDataSources = syncDataSourcesAsync

    /// Triggers fire-and-forget background pre-staging of tabular files into the persistent disk cache.
    let triggerBackgroundSync (workspaceDir: string) (connections: ConnectionDefinition list) (secretStore: FSharpQueryStudio.Security.ISecretStore) : unit =
        Task.Run(fun () ->
            try
                (syncDataSourcesAsync workspaceDir connections secretStore).GetAwaiter().GetResult() |> ignore
            with ex ->
                Log.Warning("FS Query Studio: Fire-and-forget background sync failed: {Error}", ex.Message)
        ) |> ignore

    /// Displays the raw generated SQL query for a queryable or SQL expression without executing it against the database.
    let toRawSql (query: obj) : string = ResultKind.toRawSql query

    /// Returns the raw generated SQL query and parameter bindings without executing it against the database.
    let toRawSqlWithParameters (query: obj) : string * (string * obj) list = ResultKind.toRawSqlWithParameters query

[<AutoOpen>]
module QueryInspectionExtensions =
    type ResultKind = FSharpQueryStudio.Protocol.ResultKind

    do
        ResultKind.RegisterRawSqlExtractor(SqlPushdownQuery.extractRawSql, SqlPushdownQuery.extractSqlWithParameters)

    /// Displays the raw generated SQL query for a queryable or SQL expression without executing it against the database.
    let toRawSql (query: obj) : string = ResultKind.toRawSql query

    /// Returns the raw generated SQL query and parameter bindings without executing it against the database.
    let toRawSqlWithParameters (query: obj) : string * (string * obj) list = ResultKind.toRawSqlWithParameters query

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

[<AutoOpen>]
module SequencePivotExtensions =
    /// Module with pivot / cross-tabulation functions for in-memory sequences.
    module Seq =
        /// Performs an in-memory pivot / cross-tabulation of any sequence of records.
        let pivot (rowKeySelector: 'T -> 'RowKey) (colKeySelector: 'T -> 'ColKey) (valSelector: 'T -> 'Value) (aggregator: 'Value seq -> 'Result) (source: 'T seq) : ('RowKey * Map<'ColKey, 'Result>) seq =
            source
            |> Seq.groupBy rowKeySelector
            |> Seq.map (fun (rowKey, rowItems) ->
                let colMap =
                    rowItems
                    |> Seq.groupBy colKeySelector
                    |> Seq.map (fun (colKey, colItems) ->
                        let vals = colItems |> Seq.map valSelector
                        (colKey, aggregator vals)
                    )
                    |> Map.ofSeq
                (rowKey, colMap)
            )
