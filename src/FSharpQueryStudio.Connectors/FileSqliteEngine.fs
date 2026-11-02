namespace FSharpQueryStudio.Connectors

open System
open System.IO
open System.Data
open System.Collections.Concurrent
open Microsoft.Data.Sqlite
open FSharpQueryStudio.Protocol
open Serilog

/// Manages an embedded SQLite staging database for tabular file formats (CSV, Excel)
/// to enable relational joins, B-tree indexes, and SQL pushdown across workspace files.
type FileSqliteEngine(dbPath: string) =
    let connStr = SqliteConnectionStringBuilder(DataSource = dbPath, Pooling = false).ToString()
    let connector = SqliteConnector(dbPath)
    let stagingLock = obj()
    let stagedTables = ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase)

    let isMissingOrSentinel (s: string) : bool =
        if String.IsNullOrWhiteSpace s then true
        else
            let t = s.Trim().Trim('"', '\'')
            String.IsNullOrEmpty t
            || t = "-"
            || t = "--"
            || t = "?"
            || String.Equals(t, "n/a", StringComparison.OrdinalIgnoreCase)
            || String.Equals(t, "na", StringComparison.OrdinalIgnoreCase)
            || String.Equals(t, "null", StringComparison.OrdinalIgnoreCase)
            || String.Equals(t, "none", StringComparison.OrdinalIgnoreCase)
            || String.Equals(t, "nan", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("#", StringComparison.Ordinal)

    do
        let dir = Path.GetDirectoryName(dbPath)
        if not (String.IsNullOrWhiteSpace dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        // Ensure database and tracking table exist
        use conn = new SqliteConnection(connStr)
        conn.Open()
        use pragmaCmd = conn.CreateCommand()
        pragmaCmd.CommandText <- "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA cache_size = -64000; PRAGMA temp_store = MEMORY;"
        pragmaCmd.ExecuteNonQuery() |> ignore

        use tableCmd = conn.CreateCommand()
        tableCmd.CommandText <- """
        CREATE TABLE IF NOT EXISTS _fqs_file_cache (
            table_name TEXT PRIMARY KEY,
            file_path TEXT NOT NULL,
            last_write_time_utc INTEGER NOT NULL,
            file_size INTEGER NOT NULL,
            staged_at_utc TEXT NOT NULL
        );
        """
        tableCmd.ExecuteNonQuery() |> ignore

    /// The underlying SqliteConnector pointing to this staging database.
    member _.Connector : SqliteConnector = connector

    /// The absolute path of the staging database file.
    member _.DatabasePath : string = dbPath

    /// Executes raw SQL directly on the staging database.
    member _.ExecuteQuery(sqlText: string, ?parameters: (string * obj) list) : ConnectorQueryResult =
        let p = defaultArg parameters []
        connector.ExecuteQuery sqlText p

    static member private InferAffinity(samples: obj seq, ?columnTypeOverride: string) : string =
        match columnTypeOverride with
        | Some t when not (String.IsNullOrWhiteSpace t) ->
            let cleanT = t.Trim().ToLowerInvariant()
            if cleanT.StartsWith("int") || cleanT.StartsWith("long") then "INTEGER"
            elif cleanT.StartsWith("dec") || cleanT.StartsWith("doub") || cleanT.StartsWith("float") || cleanT.StartsWith("num") then "NUMERIC"
            elif cleanT.StartsWith("bool") then "INTEGER"
            elif cleanT.StartsWith("date") || cleanT.StartsWith("time") then "TEXT"
            else "TEXT"
        | _ ->
            let nonNulls =
                samples
                |> Seq.filter (fun v -> not (isNull v) && not (Convert.IsDBNull(v)))
                |> Seq.map (fun v ->
                    match v with
                    | :? string as s -> s.Trim().Trim('"', '\'')
                    | other -> Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture).Trim()
                )
                |> Seq.filter (fun s -> not (String.IsNullOrEmpty s) && not (s = "N/A" || s = "null" || s = "-"))
                |> Seq.truncate 500
                |> Seq.toList

            if List.isEmpty nonNulls then
                "TEXT"
            elif nonNulls |> List.forall (fun s ->
                not (s.Contains('.') || s.Contains(','))
                && (match Int64.TryParse(s, System.Globalization.NumberStyles.Integer ||| System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture) with
                    | true, _ -> true
                    | false, _ -> false)) then
                "INTEGER"
            elif nonNulls |> List.forall (fun s ->
                let clean = s.Replace(",", ".")
                match Double.TryParse(clean, System.Globalization.NumberStyles.Float ||| System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture) with
                | true, d -> Double.IsFinite d
                | false, _ -> false) then
                "NUMERIC"
            else
                "TEXT"

    /// Checks if a file has already been staged and is up-to-date with disk.
    member this.IsStagedAndCurrent(tableName: string, filePath: string) : bool =
        if not (File.Exists filePath) then false
        else
            try
                let fi = FileInfo(filePath)
                let mtime = fi.LastWriteTimeUtc.Ticks
                let size = fi.Length

                use conn = new SqliteConnection(connStr)
                conn.Open()
                use cmd = conn.CreateCommand()
                cmd.CommandText <- "SELECT last_write_time_utc, file_size FROM _fqs_file_cache WHERE table_name = @t;"
                cmd.Parameters.AddWithValue("@t", tableName) |> ignore
                use reader = cmd.ExecuteReader()
                if reader.Read() then
                    let cachedMtime = reader.GetInt64(0)
                    let cachedSize = reader.GetInt64(1)
                    if cachedMtime = mtime && cachedSize = size then
                        use checkCmd = conn.CreateCommand()
                        checkCmd.CommandText <- "SELECT count(*) FROM sqlite_master WHERE type='table' AND name = @t;"
                        checkCmd.Parameters.AddWithValue("@t", tableName) |> ignore
                        let count = Convert.ToInt64(checkCmd.ExecuteScalar())
                        count > 0L
                    else
                        false
                else
                    false
            with ex ->
                Log.Warning("FS Query Studio: FileSqliteEngine cache check failed for {Table}: {Error}", tableName, ex.Message)
                false

    /// Ensures that the specified CSV or Excel file is staged into the SQLite database as `tableName`.
    /// If the table is already staged and the file on disk has not changed, this returns immediately (0ms).
    /// Supports optional explicit column type overrides via `columnTypes`.
    member this.EnsureStaged(tableName: string, filePath: string, readRows: unit -> ConnectorQueryResult, ?columnTypes: Map<string, string>) : string =
        let cleanTableName =
            let clean = CodeGenerationHelpers.cleanIdentifier tableName
            if String.IsNullOrWhiteSpace clean then "StagedFile" else clean

        if stagedTables.ContainsKey cleanTableName && this.IsStagedAndCurrent(cleanTableName, filePath) then
            cleanTableName
        else
            lock stagingLock (fun () ->
                if this.IsStagedAndCurrent(cleanTableName, filePath) then
                    stagedTables.[cleanTableName] <- true
                    cleanTableName
                else
                    let sw = System.Diagnostics.Stopwatch.StartNew()
                    let data = readRows()
                    let rawColumns = data.Columns
                    let sanitizedColumns = CodeGenerationHelpers.sanitizeColumns rawColumns

                    let fi = if File.Exists filePath then FileInfo(filePath) else null
                    let mtime = if not (isNull fi) then fi.LastWriteTimeUtc.Ticks else 0L
                    let size = if not (isNull fi) then fi.Length else 0L

                    use conn = new SqliteConnection(connStr)
                    conn.Open()

                    use pragmaCmd = conn.CreateCommand()
                    pragmaCmd.CommandText <- """
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous = NORMAL;
                    PRAGMA cache_size = -64000;
                    PRAGMA temp_store = MEMORY;
                    """
                    pragmaCmd.ExecuteNonQuery() |> ignore

                    use tx = conn.BeginTransaction()

                    // Drop table if exists
                    use dropCmd = conn.CreateCommand()
                    dropCmd.Transaction <- tx
                    dropCmd.CommandText <- $"DROP TABLE IF EXISTS \"{cleanTableName}\";"
                    dropCmd.ExecuteNonQuery() |> ignore

                    let effectiveCols =
                        if List.isEmpty sanitizedColumns then [ "Value" ] else sanitizedColumns

                    let sampleRows = data.Rows |> Seq.truncate 500 |> Seq.toList
                    let typesMap = defaultArg columnTypes Map.empty
                    let affinities =
                        effectiveCols
                        |> List.mapi (fun i colName ->
                            let overrideType =
                                match typesMap.TryFind colName with
                                | Some t -> Some t
                                | None ->
                                    if i < rawColumns.Length then typesMap.TryFind rawColumns.[i] else None
                            FileSqliteEngine.InferAffinity(sampleRows |> Seq.map (fun r -> if i < r.Length then r.[i] else null), ?columnTypeOverride = overrideType)
                        )

                    // Create table with explicit type affinities
                    let colDefs =
                        List.zip effectiveCols affinities
                        |> List.map (fun (c, aff) -> $"\"{c}\" {aff}")
                        |> String.concat ", "
                    use createCmd = conn.CreateCommand()
                    createCmd.Transaction <- tx
                    createCmd.CommandText <- $"CREATE TABLE \"{cleanTableName}\" ({colDefs});"
                    createCmd.ExecuteNonQuery() |> ignore

                    // Fast parameterized row insertion
                    let placeholders =
                        [ for i in 0 .. effectiveCols.Length - 1 -> $"@p{i}" ]
                        |> String.concat ", "
                    let insertSql = $"INSERT INTO \"{cleanTableName}\" VALUES ({placeholders});"

                    use insertCmd = conn.CreateCommand()
                    insertCmd.Transaction <- tx
                    insertCmd.CommandText <- insertSql
                    let paramArray = [|
                        for i in 0 .. effectiveCols.Length - 1 do
                            let p = insertCmd.CreateParameter()
                            p.ParameterName <- $"@p{i}"
                            insertCmd.Parameters.Add(p) |> ignore
                            yield p
                    |]

                    let mutable rowCount = 0
                    for row in data.Rows do
                        rowCount <- rowCount + 1
                        for i in 0 .. effectiveCols.Length - 1 do
                            let p = paramArray.[i]
                            if i < row.Length && not (isNull row.[i]) && not (Convert.IsDBNull(row.[i])) then
                                let raw = row.[i]
                                let aff = affinities.[i]
                                match raw with
                                | :? string as s ->
                                    let clean = s.Trim().Trim('"', '\'')
                                    if isMissingOrSentinel clean then
                                        p.Value <- DBNull.Value
                                    elif aff = "INTEGER" then
                                        match Int64.TryParse(clean, System.Globalization.NumberStyles.Integer ||| System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture) with
                                        | true, v -> p.Value <- box v
                                        | false, _ ->
                                            match Double.TryParse(clean.Replace(",", "."), System.Globalization.NumberStyles.Float ||| System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture) with
                                            | true, d when Double.IsFinite d ->
                                                if d % 1.0 = 0.0 then p.Value <- box (int64 d)
                                                else p.Value <- box d // Do not truncate decimals!
                                            | _ -> p.Value <- DBNull.Value
                                    elif aff = "NUMERIC" then
                                        match Double.TryParse(clean.Replace(",", "."), System.Globalization.NumberStyles.Float ||| System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture) with
                                        | true, v when Double.IsFinite v -> p.Value <- box v
                                        | _ -> p.Value <- DBNull.Value
                                    else
                                        p.Value <- box clean
                                | :? double as d ->
                                    if Double.IsFinite d then
                                        if aff = "INTEGER" && d % 1.0 = 0.0 && d >= float Int64.MinValue && d <= float Int64.MaxValue then
                                            p.Value <- box (int64 d)
                                        else
                                            p.Value <- box d
                                    else p.Value <- DBNull.Value
                                | :? decimal as dec ->
                                    if aff = "INTEGER" && dec % 1.0M = 0.0M && dec >= decimal Int64.MinValue && dec <= decimal Int64.MaxValue then
                                        p.Value <- box (int64 dec)
                                    else
                                        p.Value <- box (double dec)
                                | :? single as f ->
                                    if Single.IsFinite f then p.Value <- box (double f)
                                    else p.Value <- DBNull.Value
                                | _ ->
                                    p.Value <- raw
                            else
                                p.Value <- DBNull.Value
                        insertCmd.ExecuteNonQuery() |> ignore

                    // Auto-index key, code, and relation columns for instant join performance
                    let keyCols =
                        effectiveCols
                        |> List.filter (fun col ->
                            col.Equals("Id", StringComparison.OrdinalIgnoreCase)
                            || col.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
                            || col.EndsWith("_id", StringComparison.OrdinalIgnoreCase)
                            || col.EndsWith("Code", StringComparison.OrdinalIgnoreCase)
                            || col.EndsWith("_code", StringComparison.OrdinalIgnoreCase)
                            || col.EndsWith("Key", StringComparison.OrdinalIgnoreCase)
                            || col.EndsWith("_key", StringComparison.OrdinalIgnoreCase)
                            || col.Equals("Region", StringComparison.OrdinalIgnoreCase)
                            || col.Equals("Category", StringComparison.OrdinalIgnoreCase)
                            || col.Equals("Sku", StringComparison.OrdinalIgnoreCase)
                        )

                    for col in keyCols do
                        try
                            use idxCmd = conn.CreateCommand()
                            idxCmd.Transaction <- tx
                            idxCmd.CommandText <- $"CREATE INDEX IF NOT EXISTS \"ix_{cleanTableName}_{col}\" ON \"{cleanTableName}\" (\"{col}\");"
                            idxCmd.ExecuteNonQuery() |> ignore
                        with ex ->
                            Log.Warning("FS Query Studio: Could not create index on {Col} for {Table}: {Error}", col, cleanTableName, ex.Message)

                    // Composite index on pairs of key columns for multi-property joins
                    if keyCols.Length >= 2 then
                        try
                            let c1 = keyCols.[0]
                            let c2 = keyCols.[1]
                            use compIdxCmd = conn.CreateCommand()
                            compIdxCmd.Transaction <- tx
                            compIdxCmd.CommandText <- $"CREATE INDEX IF NOT EXISTS \"ix_{cleanTableName}_{c1}_{c2}\" ON \"{cleanTableName}\" (\"{c1}\", \"{c2}\");"
                            compIdxCmd.ExecuteNonQuery() |> ignore
                        with ex ->
                            Log.Warning("FS Query Studio: Could not create composite index on ({C1}, {C2}) for {Table}: {Error}", keyCols.[0], keyCols.[1], cleanTableName, ex.Message)

                    // Update _fqs_file_cache
                    use ensureCacheCmd = conn.CreateCommand()
                    ensureCacheCmd.Transaction <- tx
                    ensureCacheCmd.CommandText <- """
                    CREATE TABLE IF NOT EXISTS _fqs_file_cache (
                        table_name TEXT PRIMARY KEY,
                        file_path TEXT NOT NULL,
                        last_write_time_utc INTEGER NOT NULL,
                        file_size INTEGER NOT NULL,
                        staged_at_utc TEXT NOT NULL
                    );
                    """
                    ensureCacheCmd.ExecuteNonQuery() |> ignore

                    use cacheCmd = conn.CreateCommand()
                    cacheCmd.Transaction <- tx
                    cacheCmd.CommandText <- """
                    INSERT OR REPLACE INTO _fqs_file_cache (table_name, file_path, last_write_time_utc, file_size, staged_at_utc)
                    VALUES (@tbl, @path, @mtime, @size, @stagedAt);
                    """
                    cacheCmd.Parameters.AddWithValue("@tbl", cleanTableName) |> ignore
                    cacheCmd.Parameters.AddWithValue("@path", filePath) |> ignore
                    cacheCmd.Parameters.AddWithValue("@mtime", mtime) |> ignore
                    cacheCmd.Parameters.AddWithValue("@size", size) |> ignore
                    cacheCmd.Parameters.AddWithValue("@stagedAt", DateTime.UtcNow.ToString("o")) |> ignore
                    cacheCmd.ExecuteNonQuery() |> ignore

                    tx.Commit()
                    sw.Stop()

                    Log.Information(
                        "FileSqliteEngine: Staged {RowCount} rows into table '{Table}' from '{FilePath}' in {ElapsedMs}ms",
                        rowCount,
                        cleanTableName,
                        filePath,
                        sw.ElapsedMilliseconds
                    )
                    stagedTables.[cleanTableName] <- true
                    cleanTableName
            )

