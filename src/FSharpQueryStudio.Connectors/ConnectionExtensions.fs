namespace FSharpQueryStudio.Connectors

open System
open System.Linq
open FSharpQueryStudio.Protocol

[<AutoOpen>]
module QueryInspectionExtensions =
    type ResultKind = FSharpQueryStudio.Protocol.ResultKind

    do
        ResultKind.RegisterRawSqlExtractor(SqlPushdownQuery.extractRawSql, SqlPushdownQuery.extractSqlWithParameters)

    /// Displays the raw generated SQL query for a queryable or SQL expression without executing it against the database.
    let toRawSql (query: obj) : string = ResultKind.toRawSql query

    /// Displays the formatted, indented raw generated SQL query for a queryable or SQL expression without executing it against the database.
    let toRawQuery (query: obj) : string = SqlPushdownQuery.toRawQuery query

    /// Returns the raw generated SQL query and parameter bindings without executing it against the database.
    let toRawSqlWithParameters (query: obj) : string * (string * obj) list = ResultKind.toRawSqlWithParameters query

[<AutoOpen>]
module ConnectorQueryResultExtensions =
    type ConnectorQueryResult with
        member this.As<'T>() : 'T seq =
            let mapper = Connections.getMapper<'T> this.Columns
            seq {
                for row in this.Rows do
                    yield mapper row
            }

        member this.AsQueryable<'T>() : IQueryable<'T> =
            this.As<'T>().AsQueryable()

[<AutoOpen>]
module SequencePivotExtensions =
    /// Module with pivot / cross-tabulation functions for in-memory sequences.
    module Seq =
        /// Performs an in-memory pivot / cross-tabulation of any sequence of records.
        let pivot (rowKeySelector: 'T -> 'RowKey) (colKeySelector: 'T -> 'ColKey) (valSelector: 'T -> 'Value) (aggregator: 'Value seq -> 'Result) (source: 'T seq) : ('RowKey * Map<'ColKey, 'Result>) seq =
            source
            |> Seq.groupBy rowKeySelector
            |> Seq.map (fun (rowKey, rowItems) ->
                let colMap =
                    rowItems
                    |> Seq.groupBy colKeySelector
                    |> Seq.map (fun (colKey, colItems) ->
                        let vals = colItems |> Seq.map valSelector
                        (colKey, aggregator vals)
                    )
                    |> Map.ofSeq
                (rowKey, colMap)
            )
