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
            match part.ToUpperInvariant() with
            | "YEAR" -> Some $"YEAR({operand})"
            | "MONTH" -> Some $"MONTH({operand})"
            | "DAY" -> Some $"DAY({operand})"
            | "HOUR" -> Some $"DATEPART(HOUR, {operand})"
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
