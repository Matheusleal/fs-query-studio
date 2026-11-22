namespace FSharpQueryStudio.Connectors

open System

type OracleDialect private () =
    static let instance = OracleDialect() :> ISqlDialect

    static member Instance: ISqlDialect = instance

    interface ISqlDialect with
        member _.Name = "Oracle"

        member _.QuoteIdentifier(name: string) : string =
            if SqlDialectCommon.shouldQuote name then
                $"\"{name}\""
            else
                name

        member _.FormatLimitOffset(take: int option, skip: int option, _: bool) : string =
            match take, skip with
            | Some t, Some s -> $" OFFSET {s} ROWS FETCH NEXT {t} ROWS ONLY"
            | Some t, None -> $" FETCH NEXT {t} ROWS ONLY"
            | None, Some s -> $" OFFSET {s} ROWS"
            | None, None -> ""

        member _.FormatApplyJoin(isOuter: bool, tableSql: string) : string =
            if isOuter then
                $"OUTER APPLY {tableSql}"
            else
                $"CROSS APPLY {tableSql}"

        member _.SupportsRecursiveCteKeyword: bool = false

        member _.DualTable: string option = Some "DUAL"

        member _.TranslateDatePart(part: string, operand: string) : string option =
            match part.ToLowerInvariant() with
            | "year" -> Some $"EXTRACT(YEAR FROM {operand})"
            | "month" -> Some $"EXTRACT(MONTH FROM {operand})"
            | "day" -> Some $"EXTRACT(DAY FROM {operand})"
            | "date" -> Some $"TRUNC({operand})"
            | "hour" -> Some $"TO_NUMBER(TO_CHAR({operand}, 'HH24'))"
            | "minute" -> Some $"TO_NUMBER(TO_CHAR({operand}, 'MI'))"
            | "second" -> Some $"TO_NUMBER(TO_CHAR({operand}, 'SS'))"
            | _ -> None

        member _.TranslateDateTrunc(operand: string) : string option = Some $"TRUNC({operand})"

        member _.TranslateJsonExtract(colSql: string, path: string) : string option =
            Some $"JSON_VALUE({colSql}, '{path}')"

        member _.FormatExplain(connector: IQueryableConnector, sql: string, parameters: (string * obj) list) : string =
            connector.ExecuteQuery $"EXPLAIN PLAN FOR {sql}" parameters |> ignore

            let res =
                connector.ExecuteQuery "SELECT PLAN_TABLE_OUTPUT FROM TABLE(DBMS_XPLAN.DISPLAY())" []

            let lines =
                res.Rows
                |> Seq.map (fun row -> if row.Length > 0 then string row.[0] else "")
                |> Seq.filter (not << String.IsNullOrWhiteSpace)
                |> String.concat "\n"

            $"-- Oracle Execution Plan (DBMS_XPLAN):\n{lines}"

        member _.FormatSqlTarget
            (_: string, subSchemaOpt: string option, rawTable: string, origName: string, _: bool)
            : string =
            match subSchemaOpt with
            | Some subSchema ->
                if String.Equals(subSchema, "SYSTEM", StringComparison.OrdinalIgnoreCase) then
                    rawTable
                else
                    $"{subSchema}.{rawTable}"
            | None -> origName

        member _.SqlProviderType: string = "Common.DatabaseProviderTypes.POSTGRESQL"

        member _.FormatOuterApplyJoin(tableSql: string, _: string option) : string = $"OUTER APPLY {tableSql}"

        member _.FormatSelect
            (distinct, selectCols, fromSource, whereSql, groupBySql, havingSql, orderSql, take, skip)
            : string =
            let distinctKeyword = if distinct then "DISTINCT " else ""
            let ord = if orderSql = "" then "" else orderSql

            match skip, take with
            | Some s, Some t ->
                $"SELECT {distinctKeyword}{selectCols} FROM {fromSource}{whereSql}{groupBySql}{havingSql}{ord} OFFSET {s} ROWS FETCH NEXT {t} ROWS ONLY"
            | Some s, None ->
                $"SELECT {distinctKeyword}{selectCols} FROM {fromSource}{whereSql}{groupBySql}{havingSql}{ord} OFFSET {s} ROWS"
            | None, Some t ->
                $"SELECT {distinctKeyword}{selectCols} FROM {fromSource}{whereSql}{groupBySql}{havingSql}{ord} OFFSET 0 ROWS FETCH NEXT {t} ROWS ONLY"
            | None, None ->
                $"SELECT {distinctKeyword}{selectCols} FROM {fromSource}{whereSql}{groupBySql}{havingSql}{orderSql}"

        member _.PostProcessSql(finalSql: string) : string =
            System.Text.RegularExpressions.Regex.Replace(finalSql, @"\b_([a-zA-Z0-9_]+)\b", "$1")

        member _.FormatBooleanLiteral(value: bool) : string = if value then "1" else "0"

        member _.FormatIsEmpty(col: string) : string = $"({col} IS NULL OR LENGTH({col}) = 0)"

        member _.FormatIsNotEmpty(col: string) : string =
            $"({col} IS NOT NULL AND LENGTH({col}) > 0)"

        member _.DefaultWindowOrder: string option = Some "ORDER BY NULL"

        member _.TranslateStringLength(operand: string) : string = $"LENGTH({operand})"

        member _.FormatConcat(parts: string list) : string =
            let joined = String.concat " || " parts
            $"({joined})"

        member _.TranslateDateFormat(operand: string, format: string) : string option =
            let oraFmt =
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

            Some $"TO_CHAR({operand}, '{oraFmt}')"

        member _.TranslateCastToString(operand: string) : string = $"TO_CHAR({operand})"

        member _.TranslateSubstring(operand: string, startExpr: string, lengthExpr: string option) : string =
            match lengthExpr with
            | Some l -> $"SUBSTR({operand}, ({startExpr}) + 1, {l})"
            | None -> $"SUBSTR({operand}, ({startExpr}) + 1)"

        member _.TranslateCeiling(operand: string) : string = $"CEIL({operand})"

        member _.TranslateIndexOf(strSql: string, subSql: string) : string = $"(INSTR({strSql}, {subSql}) - 1)"
