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
            match part.ToUpperInvariant() with
            | "YEAR" -> Some $"YEAR({operand})"
            | "MONTH" -> Some $"MONTH({operand})"
            | "DAY" -> Some $"DAY({operand})"
            | "HOUR" -> Some $"HOUR({operand})"
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
