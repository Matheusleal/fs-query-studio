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
            match part.ToUpperInvariant() with
            | "YEAR" -> Some $"EXTRACT(YEAR FROM {operand})"
            | "MONTH" -> Some $"EXTRACT(MONTH FROM {operand})"
            | "DAY" -> Some $"EXTRACT(DAY FROM {operand})"
            | "HOUR" -> Some $"EXTRACT(HOUR FROM {operand})"
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
