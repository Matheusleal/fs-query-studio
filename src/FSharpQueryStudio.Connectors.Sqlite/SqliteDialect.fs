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
            match part.ToUpperInvariant() with
            | "YEAR" -> Some $"CAST(strftime('%%Y', {operand}) AS INTEGER)"
            | "MONTH" -> Some $"CAST(strftime('%%m', {operand}) AS INTEGER)"
            | "DAY" -> Some $"CAST(strftime('%%d', {operand}) AS INTEGER)"
            | "HOUR" -> Some $"CAST(strftime('%%H', {operand}) AS INTEGER)"
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
