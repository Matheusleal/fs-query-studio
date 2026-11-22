namespace FSharpQueryStudio.Connectors

open System

type MySqlDialect private () =
    static let instance = MySqlDialect() :> ISqlDialect

    static member Instance: ISqlDialect = instance

    interface ISqlDialect with
        member _.Name = "MySql"

        member _.QuoteIdentifier(name: string) : string =
            if SqlDialectCommon.shouldQuote name then
                $"`{name}`"
            else
                name

        member _.FormatLimitOffset(take: int option, skip: int option, _: bool) : string =
            match take, skip with
            | Some t, Some s -> $" LIMIT {t} OFFSET {s}"
            | Some t, None -> $" LIMIT {t}"
            | None, Some s -> $" LIMIT 18446744073709551615 OFFSET {s}"
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
            | "year" -> Some $"YEAR({operand})"
            | "month" -> Some $"MONTH({operand})"
            | "day" -> Some $"DAY({operand})"
            | "date" -> Some $"DATE({operand})"
            | "hour" -> Some $"HOUR({operand})"
            | "minute" -> Some $"MINUTE({operand})"
            | "second" -> Some $"SECOND({operand})"
            | _ -> None

        member _.TranslateDateTrunc(operand: string) : string option = Some $"DATE({operand})"

        member _.TranslateJsonExtract(colSql: string, path: string) : string option =
            Some $"JSON_UNQUOTE(JSON_EXTRACT({colSql}, '{path}'))"

        member _.FormatExplain(connector: IQueryableConnector, sql: string, parameters: (string * obj) list) : string =
            let res = connector.ExecuteQuery $"EXPLAIN {sql}" parameters

            let lines =
                res.Rows
                |> Seq.map (fun row ->
                    res.Columns
                    |> List.mapi (fun i col -> $"{col}: {row.[i]}")
                    |> String.concat ", ")
                |> String.concat "\n"

            $"-- MySql EXPLAIN:\n{lines}"

        member _.FormatSqlTarget
            (topGroup: string, subSchemaOpt: string option, rawTable: string, origName: string, _: bool)
            : string =
            match subSchemaOpt with
            | None -> origName
            | Some subSchema -> $"{topGroup}.{subSchema}.{rawTable}"

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
                | None, Some s -> $" LIMIT 18446744073709551615 OFFSET {s}"
                | None, None -> ""

            $"SELECT {distinctKeyword}{selectCols} FROM {fromSource}{whereSql}{groupBySql}{havingSql}{orderSql}{limitSql}"

        member _.PostProcessSql(finalSql: string) : string = finalSql

        member _.FormatBooleanLiteral(value: bool) : string = if value then "1" else "0"

        member _.FormatIsEmpty(col: string) : string = $"({col} IS NULL OR {col} = '')"

        member _.FormatIsNotEmpty(col: string) : string = $"({col} IS NOT NULL AND {col} <> '')"

        member _.DefaultWindowOrder: string option = None

        member _.TranslateStringLength(operand: string) : string = $"LENGTH({operand})"

        member _.FormatConcat(parts: string list) : string =
            let joined = String.concat ", " parts
            $"CONCAT({joined})"

        member _.TranslateDateFormat(operand: string, format: string) : string option =
            let myFmt =
                format
                    .Replace("yyyy", "%Y")
                    .Replace("YYYY", "%Y")
                    .Replace("yy", "%y")
                    .Replace("MM", "%m")
                    .Replace("dd", "%d")
                    .Replace("DD", "%d")
                    .Replace("HH", "%H")
                    .Replace("hh", "%h")
                    .Replace("mm", "%i")
                    .Replace("ss", "%s")
                    .Replace("SS", "%s")

            Some $"DATE_FORMAT({operand}, '{myFmt}')"

        member _.TranslateCastToString(operand: string) : string = $"CAST({operand} AS CHAR)"

        member _.TranslateSubstring(operand: string, startExpr: string, lengthExpr: string option) : string =
            match lengthExpr with
            | Some l -> $"SUBSTR({operand}, ({startExpr}) + 1, {l})"
            | None -> $"SUBSTR({operand}, ({startExpr}) + 1)"

        member _.TranslateCeiling(operand: string) : string = $"CEILING({operand})"

        member _.TranslateIndexOf(strSql: string, subSql: string) : string = $"(INSTR({strSql}, {subSql}) - 1)"
