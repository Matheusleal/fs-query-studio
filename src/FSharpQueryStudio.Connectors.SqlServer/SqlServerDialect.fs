namespace FSharpQueryStudio.Connectors

open System

type SqlServerDialect private () =
    static let instance = SqlServerDialect() :> ISqlDialect

    static member Instance: ISqlDialect = instance

    interface ISqlDialect with
        member _.Name = "SqlServer"

        member _.QuoteIdentifier(name: string) : string =
            if SqlDialectCommon.shouldQuote name then
                $"[{name}]"
            else
                name

        member _.FormatLimitOffset(take: int option, skip: int option, hasOrderBy: bool) : string =
            let orderPrefix = if not hasOrderBy then " ORDER BY (SELECT 1)" else ""

            match take, skip with
            | Some t, Some s -> $"{orderPrefix} OFFSET {s} ROWS FETCH NEXT {t} ROWS ONLY"
            | Some t, None -> $"{orderPrefix} OFFSET 0 ROWS FETCH NEXT {t} ROWS ONLY"
            | None, Some s -> $"{orderPrefix} OFFSET {s} ROWS"
            | None, None -> ""

        member _.FormatApplyJoin(isOuter: bool, tableSql: string) : string =
            if isOuter then
                $"OUTER APPLY {tableSql}"
            else
                $"CROSS APPLY {tableSql}"

        member _.SupportsRecursiveCteKeyword: bool = false

        member _.DualTable: string option = None

        member _.TranslateDatePart(part: string, operand: string) : string option =
            match part.ToLowerInvariant() with
            | "year" -> Some $"YEAR({operand})"
            | "month" -> Some $"MONTH({operand})"
            | "day" -> Some $"DAY({operand})"
            | "date" -> Some $"CAST({operand} AS DATE)"
            | "hour" -> Some $"DATEPART(HOUR, {operand})"
            | "minute" -> Some $"DATEPART(MINUTE, {operand})"
            | "second" -> Some $"DATEPART(SECOND, {operand})"
            | _ -> None

        member _.TranslateDateTrunc(operand: string) : string option = Some $"CAST({operand} AS DATE)"

        member _.TranslateJsonExtract(colSql: string, path: string) : string option =
            Some $"JSON_VALUE({colSql}, '{path}')"

        member _.FormatExplain(connector: IQueryableConnector, sql: string, parameters: (string * obj) list) : string =
            let res =
                connector.ExecuteQuery $"SET SHOWPLAN_TEXT ON; {sql}; SET SHOWPLAN_TEXT OFF;" parameters

            let lines =
                res.Rows
                |> Seq.map (fun row -> if row.Length > 0 then string row.[0] else "")
                |> Seq.filter (not << String.IsNullOrWhiteSpace)
                |> String.concat "\n"

            $"-- SQL Server SHOWPLAN:\n{lines}"

        member _.FormatSqlTarget
            (topGroup: string, subSchemaOpt: string option, rawTable: string, origName: string, _: bool)
            : string =
            match subSchemaOpt with
            | None ->
                if origName.IndexOf('.') < 0 then
                    rawTable
                elif topGroup = "public" || topGroup = "dbo" || topGroup = "main" then
                    origName
                else
                    $"{topGroup}.dbo.{rawTable}"
            | Some s -> $"{topGroup}.{s}.{rawTable}"

        member _.SqlProviderType: string = "Common.DatabaseProviderTypes.MSSQLSERVER"

        member _.FormatOuterApplyJoin(tableSql: string, _: string option) : string = $"OUTER APPLY {tableSql}"

        member _.FormatSelect
            (distinct, selectCols, fromSource, whereSql, groupBySql, havingSql, orderSql, take, skip)
            : string =
            let distinctKeyword = if distinct then "DISTINCT " else ""

            match skip, take with
            | Some s, Some t ->
                let ord =
                    if orderSql = "" then
                        " ORDER BY (SELECT NULL)"
                    else
                        orderSql

                $"SELECT {distinctKeyword}{selectCols} FROM {fromSource}{whereSql}{groupBySql}{havingSql}{ord} OFFSET {s} ROWS FETCH NEXT {t} ROWS ONLY"
            | Some s, None ->
                let ord =
                    if orderSql = "" then
                        " ORDER BY (SELECT NULL)"
                    else
                        orderSql

                $"SELECT {distinctKeyword}{selectCols} FROM {fromSource}{whereSql}{groupBySql}{havingSql}{ord} OFFSET {s} ROWS"
            | None, Some t ->
                if orderSql <> "" then
                    $"SELECT {distinctKeyword}{selectCols} FROM {fromSource}{whereSql}{groupBySql}{havingSql}{orderSql} OFFSET 0 ROWS FETCH NEXT {t} ROWS ONLY"
                else
                    $"SELECT {distinctKeyword}TOP ({t}) {selectCols} FROM {fromSource}{whereSql}{groupBySql}{havingSql}"
            | None, None ->
                $"SELECT {distinctKeyword}{selectCols} FROM {fromSource}{whereSql}{groupBySql}{havingSql}{orderSql}"

        member _.PostProcessSql(finalSql: string) : string = finalSql

        member _.FormatBooleanLiteral(value: bool) : string = if value then "1" else "0"

        member _.FormatIsEmpty(col: string) : string = $"({col} IS NULL OR {col} = '')"

        member _.FormatIsNotEmpty(col: string) : string = $"({col} IS NOT NULL AND {col} <> '')"

        member _.DefaultWindowOrder: string option = Some "ORDER BY (SELECT NULL)"

        member _.TranslateStringLength(operand: string) : string = $"LEN({operand})"

        member _.FormatConcat(parts: string list) : string =
            let joined = String.concat " + " parts
            $"({joined})"

        member _.TranslateDateFormat(operand: string, format: string) : string option =
            Some $"FORMAT({operand}, '{format}')"

        member _.TranslateCastToString(operand: string) : string = $"CAST({operand} AS NVARCHAR(MAX))"

        member _.TranslateSubstring(operand: string, startExpr: string, lengthExpr: string option) : string =
            match lengthExpr with
            | Some l -> $"SUBSTRING({operand}, ({startExpr}) + 1, {l})"
            | None -> $"SUBSTRING({operand}, ({startExpr}) + 1, LEN({operand}))"

        member _.TranslateCeiling(operand: string) : string = $"CEILING({operand})"

        member _.TranslateIndexOf(strSql: string, subSql: string) : string = $"(CHARINDEX({subSql}, {strSql}) - 1)"
