namespace FSharpQueryStudio.Connectors

open System
open System.Collections.Generic

type OrderDirection =
    | Ascending
    | Descending

type OrderClause = {
    ColumnName: string
    Direction: OrderDirection
}

type JoinPlan = {
    JoinType: string
    SourceTable: string
    Alias: string
    OnClause: string
    RowType: Type option
}

module SqlPushdownFormatting =
    let formatSqlAndParameters (sql: string) (parameters: (string * obj) list) : string =
        if List.isEmpty parameters then
            sql
        else
            let paramLines =
                parameters
                |> List.map (fun (name, value) ->
                    let formattedVal =
                        if isNull value || Convert.IsDBNull(value) then "NULL"
                        else
                            match value with
                            | :? string as s -> sprintf "'%s'" s
                            | :? DateTime as dt -> sprintf "'%s'" (dt.ToString("yyyy-MM-dd HH:mm:ss"))
                            | :? bool as b -> if b then "1" else "0"
                            | other -> string other
                    sprintf "--   %s = %s" name formattedVal)
                |> String.concat "\n"
            $"-- Parameters:\n{paramLines}\n{sql}"

/// Represents the planned SQL pushdown query and any in-memory fallback predicates.
type PushdownQueryPlan = {
    BaseSql: string
    BaseParameters: (string * obj) list
    BaseAlias: string
    Joins: ResizeArray<JoinPlan>
    WhereClauses: ResizeArray<string>
    Parameters: ResizeArray<string * obj>
    OrderBy: ResizeArray<OrderClause>
    GroupBy: ResizeArray<string>
    Having: ResizeArray<string>
    mutable IsDistinct: bool
    mutable BaseRowType: Type option
    mutable Skip: int option
    mutable Take: int option
    mutable Projection: string option
    FallbackPredicates: ResizeArray<obj -> bool>
    OuterAliases: Dictionary<string, string>
} with
    static member Create(baseSql: string, baseParameters: (string * obj) list, ?baseAlias: string, ?baseRowType: Type) =
        {
            BaseSql = baseSql
            BaseParameters = baseParameters
            BaseAlias = defaultArg baseAlias "_t0"
            Joins = ResizeArray<JoinPlan>()
            WhereClauses = ResizeArray<string>()
            Parameters = ResizeArray<string * obj>()
            OrderBy = ResizeArray<OrderClause>()
            GroupBy = ResizeArray<string>()
            Having = ResizeArray<string>()
            IsDistinct = false
            BaseRowType = baseRowType
            Skip = None
            Take = None
            Projection = None
            FallbackPredicates = ResizeArray<obj -> bool>()
            OuterAliases = Dictionary<string, string>()
        }

    member this.Clone() =
        let p = PushdownQueryPlan.Create(this.BaseSql, this.BaseParameters, this.BaseAlias, ?baseRowType = this.BaseRowType)
        p.Joins.AddRange(this.Joins)
        p.WhereClauses.AddRange(this.WhereClauses)
        p.Parameters.AddRange(this.Parameters)
        p.OrderBy.AddRange(this.OrderBy)
        p.GroupBy.AddRange(this.GroupBy)
        p.Having.AddRange(this.Having)
        p.IsDistinct <- this.IsDistinct
        p.Skip <- this.Skip
        p.Take <- this.Take
        p.Projection <- this.Projection
        p.FallbackPredicates.AddRange(this.FallbackPredicates)
        for kv in this.OuterAliases do
            p.OuterAliases.[kv.Key] <- kv.Value
        p

    member this.BuildRawSql(dialect: SqlDialect) : string =
        let sql = this.BuildSql(dialect)
        let allParams = (this.BaseParameters @ (this.Parameters |> List.ofSeq))
        SqlPushdownFormatting.formatSqlAndParameters sql allParams

    member this.BuildSql(dialect: SqlDialect) : string =
        let isTableOnly =
            let trimmed = this.BaseSql.Trim()
            not (trimmed.StartsWith("select", StringComparison.OrdinalIgnoreCase))
            && not (trimmed.StartsWith("with", StringComparison.OrdinalIgnoreCase))
            && not (trimmed.Contains(" from ", StringComparison.OrdinalIgnoreCase))

        let joinsSql =
            this.Joins
            |> Seq.map (fun j ->
                let tbl =
                    let t = j.SourceTable.Trim()
                    if t.StartsWith("select", StringComparison.OrdinalIgnoreCase) || t.StartsWith("(", StringComparison.Ordinal) || t.Contains(" ") then
                        if t.StartsWith("(", StringComparison.Ordinal) then $"{t} AS {j.Alias}"
                        else $"({t}) AS {j.Alias}"
                    else $"{t} {j.Alias}"
                match j.JoinType.Trim().ToUpperInvariant() with
                | "CROSS APPLY" ->
                    match dialect with
                    | SqlDialect.SqlServer -> $"CROSS APPLY {tbl}"
                    | SqlDialect.PostgreSql -> $"CROSS JOIN LATERAL {tbl}"
                    | SqlDialect.Sqlite -> $"CROSS JOIN {tbl}"
                | "OUTER APPLY" ->
                    match dialect with
                    | SqlDialect.SqlServer -> $"OUTER APPLY {tbl}"
                    | SqlDialect.PostgreSql -> $"LEFT JOIN LATERAL {tbl} ON true"
                    | SqlDialect.Sqlite ->
                        let onPart = if String.IsNullOrWhiteSpace j.OnClause then "1=1" else j.OnClause
                        $"LEFT JOIN {tbl} ON {onPart}"
                | "CROSS JOIN" ->
                    $"CROSS JOIN {tbl}"
                | _ ->
                    let onPart = if String.IsNullOrWhiteSpace j.OnClause then "1=1" else j.OnClause
                    $"{j.JoinType} {tbl} ON {onPart}"
            )
            |> String.concat " "
        let requiresBaseAlias =
            this.BaseAlias <> "_t0"
            || (this.WhereClauses |> Seq.exists (fun w -> w.Contains(this.BaseAlias)))
            || (this.Having |> Seq.exists (fun h -> h.Contains(this.BaseAlias)))
            || (match this.Projection with Some p -> p.Contains(this.BaseAlias) | None -> false)
            || (this.OrderBy |> Seq.exists (fun o -> o.ColumnName.Contains(this.BaseAlias)))
            || (this.Joins |> Seq.exists (fun j -> j.SourceTable.Contains(this.BaseAlias)))

        let fromSource =
            if this.Joins.Count > 0 then
                let baseFrom =
                    if isTableOnly then $"{this.BaseSql.Trim()} {this.BaseAlias}"
                    else $"({this.BaseSql}) AS {this.BaseAlias}"
                $"{baseFrom} {joinsSql}"
            else
                if isTableOnly then
                    if requiresBaseAlias then $"{this.BaseSql.Trim()} {this.BaseAlias}"
                    else this.BaseSql.Trim()
                else
                    if requiresBaseAlias then $"({this.BaseSql}) AS {this.BaseAlias}"
                    else $"({this.BaseSql}) AS _src"

        let whereSql =
            if this.WhereClauses.Count > 0 then
                " WHERE " + String.concat " AND " this.WhereClauses
            else
                ""

        let groupBySql =
            if this.GroupBy.Count > 0 then
                " GROUP BY " + String.concat ", " this.GroupBy
            else
                ""

        let havingSql =
            if this.Having.Count > 0 then
                " HAVING " + String.concat " AND " this.Having
            else
                ""

        let selectCols =
            match this.Projection with
            | Some proj -> proj
            | None ->
                if this.Joins.Count > 0 then
                    let joinedCols = this.Joins |> Seq.map (fun j -> $"{j.Alias}.*") |> String.concat ", "
                    $"{this.BaseAlias}.*, {joinedCols}"
                else
                    "*"

        let orderSql =
            if this.OrderBy.Count > 0 then
                let isScalarAggregateOnly =
                    this.GroupBy.Count = 0 &&
                    match this.Projection with
                    | Some proj ->
                        let p = proj.Trim().ToUpperInvariant()
                        p.StartsWith("COUNT(") || p.StartsWith("SUM(") || p.StartsWith("MIN(") || p.StartsWith("MAX(") || p.StartsWith("AVG(")
                    | None -> false

                if isScalarAggregateOnly then
                    ""
                else
                    let parts =
                        this.OrderBy
                        |> Seq.map (fun o ->
                            let dir = if o.Direction = Ascending then "ASC" else "DESC"
                            $"{o.ColumnName} {dir}"
                        )
                    " ORDER BY " + String.concat ", " parts
            else
                ""

        let distinctKeyword = if this.IsDistinct then "DISTINCT " else ""

        match dialect with
        | SqlDialect.SqlServer ->
            match this.Skip, this.Take with
            | Some skip, Some take ->
                let ord = if orderSql = "" then " ORDER BY (SELECT NULL)" else orderSql
                $"SELECT {distinctKeyword}{selectCols} FROM {fromSource}{whereSql}{groupBySql}{havingSql}{ord} OFFSET {skip} ROWS FETCH NEXT {take} ROWS ONLY"
            | Some skip, None ->
                let ord = if orderSql = "" then " ORDER BY (SELECT NULL)" else orderSql
                $"SELECT {distinctKeyword}{selectCols} FROM {fromSource}{whereSql}{groupBySql}{havingSql}{ord} OFFSET {skip} ROWS"
            | None, Some take ->
                if orderSql <> "" then
                    $"SELECT {distinctKeyword}{selectCols} FROM {fromSource}{whereSql}{groupBySql}{havingSql}{orderSql} OFFSET 0 ROWS FETCH NEXT {take} ROWS ONLY"
                else
                    $"SELECT {distinctKeyword}TOP ({take}) {selectCols} FROM {fromSource}{whereSql}{groupBySql}{havingSql}"
            | None, None ->
                $"SELECT {distinctKeyword}{selectCols} FROM {fromSource}{whereSql}{groupBySql}{havingSql}{orderSql}"
        | _ ->
            let limitSql =
                match this.Take, this.Skip with
                | Some take, Some skip -> $" LIMIT {take} OFFSET {skip}"
                | Some take, None -> $" LIMIT {take}"
                | None, Some skip -> $" OFFSET {skip}"
                | None, None -> ""

            $"SELECT {distinctKeyword}{selectCols} FROM {fromSource}{whereSql}{groupBySql}{havingSql}{orderSql}{limitSql}"

