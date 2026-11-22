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
            match part.ToUpperInvariant() with
            | "YEAR" -> Some $"EXTRACT(YEAR FROM {operand})"
            | "MONTH" -> Some $"EXTRACT(MONTH FROM {operand})"
            | "DAY" -> Some $"EXTRACT(DAY FROM {operand})"
            | "HOUR" -> Some $"EXTRACT(HOUR FROM {operand})"
            | _ -> None

        member _.TranslateDateTrunc(operand: string) : string option = Some $"CAST({operand} AS DATE)"

        member _.TranslateJsonExtract(colSql: string, path: string) : string option =
            let cleanPath = path.TrimStart('$', '.')
            Some $"{colSql} ->> '{cleanPath}'"

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
