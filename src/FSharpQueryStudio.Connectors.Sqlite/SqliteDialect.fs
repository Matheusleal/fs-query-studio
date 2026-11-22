namespace FSharpQueryStudio.Connectors

open System

type SqliteDialect private () =
    static let instance = SqliteDialect() :> ISqlDialect

    static member Instance: ISqlDialect = instance

    interface ISqlDialect with
        member _.Name = "Sqlite"

        member _.QuoteIdentifier(name: string) : string =
            if SqlDialectCommon.shouldQuote name then
                $"\"{name}\""
            else
                name

        member _.FormatLimitOffset(take: int option, skip: int option, _: bool) : string =
            match take, skip with
            | Some t, Some s -> $" LIMIT {t} OFFSET {s}"
            | Some t, None -> $" LIMIT {t}"
            | None, Some s -> $" LIMIT -1 OFFSET {s}"
            | None, None -> ""

        member _.FormatApplyJoin(_: bool, tableSql: string) : string = $"CROSS JOIN {tableSql}"

        member _.SupportsRecursiveCteKeyword: bool = true

        member _.DualTable: string option = None

        member _.TranslateDatePart(part: string, operand: string) : string option =
            match part.ToLowerInvariant() with
            | "year" -> Some $"CAST(strftime('%%Y', {operand}) AS INTEGER)"
            | "month" -> Some $"CAST(strftime('%%m', {operand}) AS INTEGER)"
            | "day" -> Some $"CAST(strftime('%%d', {operand}) AS INTEGER)"
            | "hour" -> Some $"CAST(strftime('%%H', {operand}) AS INTEGER)"
            | "minute" -> Some $"CAST(strftime('%%M', {operand}) AS INTEGER)"
            | "second" -> Some $"CAST(strftime('%%S', {operand}) AS INTEGER)"
            | "date" -> Some $"date({operand})"
            | _ -> None

        member _.TranslateDateTrunc(operand: string) : string option = Some $"date({operand})"

        member _.TranslateJsonExtract(colSql: string, path: string) : string option =
            Some $"json_extract({colSql}, '{path}')"

        member _.FormatExplain(connector: IQueryableConnector, sql: string, parameters: (string * obj) list) : string =
            let res = connector.ExecuteQuery $"EXPLAIN QUERY PLAN {sql}" parameters

            let lines =
                res.Rows
                |> Seq.map (fun row ->
                    if row.Length >= 4 && not (isNull row.[3]) then
                        string row.[3]
                    elif row.Length > 0 then
                        string row.[0]
                    else
                        "")
                |> Seq.filter (not << String.IsNullOrWhiteSpace)
                |> String.concat "\n"

            if String.IsNullOrWhiteSpace lines then
                "-- SQLite EXPLAIN: Plan generated successfully."
            else
                $"-- SQLite EXPLAIN QUERY PLAN:\n{lines}"

        member _.FormatSqlTarget(_: string, _: string option, rawTable: string, _: string, _: bool) : string = rawTable

        member _.SqlProviderType: string = "Common.DatabaseProviderTypes.SQLITE"

        member _.FormatOuterApplyJoin(tableSql: string, onClause: string option) : string =
            let onPart = defaultArg onClause "1=1"
            $"LEFT JOIN {tableSql} ON {onPart}"

        member _.FormatSelect
            (distinct, selectCols, fromSource, whereSql, groupBySql, havingSql, orderSql, take, skip)
            : string =
            let distinctKeyword = if distinct then "DISTINCT " else ""

            let limitSql =
                match take, skip with
                | Some t, Some s -> $" LIMIT {t} OFFSET {s}"
                | Some t, None -> $" LIMIT {t}"
                | None, Some s -> $" OFFSET {s}"
                | None, None -> ""

            $"SELECT {distinctKeyword}{selectCols} FROM {fromSource}{whereSql}{groupBySql}{havingSql}{orderSql}{limitSql}"

        member _.PostProcessSql(finalSql: string) : string = finalSql

        member _.FormatBooleanLiteral(value: bool) : string = if value then "1" else "0"

        member _.FormatIsEmpty(col: string) : string = $"({col} IS NULL OR {col} = '')"

        member _.FormatIsNotEmpty(col: string) : string = $"({col} IS NOT NULL AND {col} <> '')"

        member _.DefaultWindowOrder: string option = None

        member _.TranslateStringLength(operand: string) : string = $"LENGTH({operand})"

        member _.FormatConcat(parts: string list) : string =
            let joined = String.concat " || " parts
            $"({joined})"

        member _.TranslateDateFormat(operand: string, format: string) : string option =
            let sqliteFmt =
                format
                    .Replace("yyyy", "%Y")
                    .Replace("YYYY", "%Y")
                    .Replace("yy", "%Y")
                    .Replace("MM", "%m")
                    .Replace("dd", "%d")
                    .Replace("DD", "%d")
                    .Replace("HH", "%H")
                    .Replace("hh", "%H")
                    .Replace("mm", "%M")
                    .Replace("ss", "%S")
                    .Replace("SS", "%S")

            if sqliteFmt.Contains("%") then
                Some $"strftime('{sqliteFmt}', {operand})"
            else
                Some $"CAST({operand} AS TEXT)"

        member _.TranslateCastToString(operand: string) : string = $"CAST({operand} AS TEXT)"

        member _.TranslateSubstring(operand: string, startExpr: string, lengthExpr: string option) : string =
            match lengthExpr with
            | Some l -> $"SUBSTR({operand}, ({startExpr}) + 1, {l})"
            | None -> $"SUBSTR({operand}, ({startExpr}) + 1)"

        member _.TranslateCeiling(operand: string) : string = $"CEIL({operand})"

        member _.TranslateIndexOf(strSql: string, subSql: string) : string = $"(INSTR({strSql}, {subSql}) - 1)"
