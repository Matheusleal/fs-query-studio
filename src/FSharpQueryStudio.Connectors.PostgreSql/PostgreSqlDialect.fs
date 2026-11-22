namespace FSharpQueryStudio.Connectors

open System

type PostgreSqlDialect private () =
    static let instance = PostgreSqlDialect() :> ISqlDialect

    static member Instance: ISqlDialect = instance

    interface ISqlDialect with
        member _.Name = "PostgreSql"

        member _.QuoteIdentifier(name: string) : string =
            if SqlDialectCommon.shouldQuote name then
                $"\"{name}\""
            else
                name

        member _.FormatLimitOffset(take: int option, skip: int option, _: bool) : string =
            match take, skip with
            | Some t, Some s -> $" LIMIT {t} OFFSET {s}"
            | Some t, None -> $" LIMIT {t}"
            | None, Some s -> $" OFFSET {s}"
            | None, None -> ""

        member _.FormatApplyJoin(isOuter: bool, tableSql: string) : string =
            if isOuter then
                $"LEFT JOIN LATERAL {tableSql} ON true"
            else
                $"CROSS JOIN LATERAL {tableSql}"

        member _.SupportsRecursiveCteKeyword: bool = true

        member _.DualTable: string option = None

        member _.TranslateDatePart(part: string, operand: string) : string option =
            match part.ToLowerInvariant() with
            | "year" -> Some $"EXTRACT(YEAR FROM {operand})"
            | "month" -> Some $"EXTRACT(MONTH FROM {operand})"
            | "day" -> Some $"EXTRACT(DAY FROM {operand})"
            | "date" -> Some $"CAST({operand} AS DATE)"
            | "hour" -> Some $"EXTRACT(HOUR FROM {operand})"
            | "minute" -> Some $"EXTRACT(MINUTE FROM {operand})"
            | "second" -> Some $"EXTRACT(SECOND FROM {operand})"
            | _ -> None

        member _.TranslateDateTrunc(operand: string) : string option = Some $"CAST({operand} AS DATE)"

        member _.TranslateJsonExtract(colSql: string, path: string) : string option =
            let trimmed =
                if path.StartsWith("$.") then path.Substring(2)
                elif path.StartsWith("$") then path.Substring(1)
                else path

            if trimmed.Contains(".") then
                let parts = trimmed.Split('.') |> String.concat "', '"
                Some $"({colSql} #>> '{{{parts}}}')"
            else
                Some $"({colSql} ->> '{trimmed}')"

        member _.FormatExplain(connector: IQueryableConnector, sql: string, parameters: (string * obj) list) : string =
            let res =
                connector.ExecuteQuery $"EXPLAIN (ANALYZE false, FORMAT TEXT) {sql}" parameters

            let lines =
                res.Rows
                |> Seq.map (fun row -> if row.Length > 0 then string row.[0] else "")
                |> Seq.filter (not << String.IsNullOrWhiteSpace)
                |> String.concat "\n"

            $"-- PostgreSQL EXPLAIN:\n{lines}"

        member _.FormatSqlTarget
            (_: string, subSchemaOpt: string option, rawTable: string, origName: string, isDefaultGroup: bool)
            : string =
            match subSchemaOpt with
            | None -> if isDefaultGroup then rawTable else origName
            | Some s ->
                if isDefaultGroup && s = "public" then
                    rawTable
                else
                    $"{s}.{rawTable}"

        member _.SqlProviderType: string = "Common.DatabaseProviderTypes.POSTGRESQL"

        member _.FormatOuterApplyJoin(tableSql: string, onClause: string option) : string =
            let onPart =
                match onClause with
                | Some c when c <> "1=1" && not (String.IsNullOrWhiteSpace c) -> c
                | _ -> "true"

            $"LEFT JOIN LATERAL {tableSql} ON {onPart}"

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

        member _.FormatBooleanLiteral(value: bool) : string = if value then "TRUE" else "FALSE"

        member _.FormatIsEmpty(col: string) : string = $"({col} IS NULL OR {col} = '')"

        member _.FormatIsNotEmpty(col: string) : string = $"({col} IS NOT NULL AND {col} <> '')"

        member _.DefaultWindowOrder: string option = None

        member _.TranslateStringLength(operand: string) : string = $"LENGTH({operand})"

        member _.FormatConcat(parts: string list) : string =
            let joined = String.concat " || " parts
            $"({joined})"

        member _.TranslateDateFormat(operand: string, format: string) : string option =
            let pgFmt =
                format
                    .Replace("yyyy", "YYYY")
                    .Replace("YYYY", "YYYY")
                    .Replace("yy", "YY")
                    .Replace("MM", "MM")
                    .Replace("dd", "DD")
                    .Replace("DD", "DD")
                    .Replace("HH", "HH24")
                    .Replace("hh", "HH12")
                    .Replace("mm", "MI")
                    .Replace("ss", "SS")
                    .Replace("SS", "SS")

            Some $"to_char({operand}, '{pgFmt}')"

        member _.TranslateCastToString(operand: string) : string = $"CAST({operand} AS TEXT)"

        member _.TranslateSubstring(operand: string, startExpr: string, lengthExpr: string option) : string =
            match lengthExpr with
            | Some l -> $"SUBSTR({operand}, ({startExpr}) + 1, {l})"
            | None -> $"SUBSTR({operand}, ({startExpr}) + 1)"

        member _.TranslateCeiling(operand: string) : string = $"CEIL({operand})"

        member _.TranslateIndexOf(strSql: string, subSql: string) : string = $"(POSITION({subSql} IN {strSql}) - 1)"
