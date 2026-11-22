namespace FSharpQueryStudio.Connectors

open System
open System.Collections.Generic
open System.Linq
open System.Linq.Expressions
open FSharpQueryStudio.Protocol

[<AutoOpen>]
module SqlComputationExtensions =
    open System.Reflection
    open Microsoft.FSharp.Linq
    open Microsoft.FSharp.Linq.RuntimeHelpers
    open Microsoft.FSharp.Quotations
    open Microsoft.FSharp.Quotations.Patterns
    open Microsoft.FSharp.Quotations.DerivedPatterns

    /// Marker function declaring a correlated subquery existence check in F# queries.
    let exists (subquery: #seq<'T>) : bool = true

    /// Marker function declaring a correlated subquery non-existence check in F# queries.
    let notExists (subquery: #seq<'T>) : bool = false

    /// Marker function declaring an explicit correlated CROSS APPLY in F# queries.
    let crossApply (subquery: #seq<'T>) : #seq<'T> = subquery

    /// Marker function declaring an explicit correlated CROSS JOIN LATERAL in F# queries.
    let lateral (subquery: #seq<'T>) : #seq<'T> = subquery

    /// Checks whether a column or value is NOT NULL (translates to `col IS NOT NULL` in SQL pushdown).
    let isNotNull (value: 'T) : bool = not (isNull (box value))

    /// Checks whether a string or value is NOT NULL and NOT empty (`""`) (translates to `(col IS NOT NULL AND col <> '')` in SQL pushdown).
    let isNotEmpty (value: 'T) : bool =
        match box value with
        | null -> false
        | :? string as s -> not (String.IsNullOrEmpty(s))
        | other -> not (String.IsNullOrEmpty(string other))

    /// Checks whether a string or value is NOT NULL and NOT empty (`""`) (translates to `(col IS NOT NULL AND col <> '')` in SQL pushdown).
    let isNotNullOrEmpty (value: 'T) : bool = isNotEmpty value

    /// Strongly-typed F# computation expression builder providing dedicated keywords
    /// for subqueries, EXISTS, NOT EXISTS, CROSS APPLY / LATERAL, HAVING, CTEs, and Set Operations.
    type FSqlQueryBuilder() =
        inherit QueryBuilder()

        [<CustomOperation("whereExists", MaintainsVariableSpace = true)>]
        member inline _.WhereExists
            (source: QuerySource<'T, 'Q>, [<ProjectionParameter>] subquery: 'T -> #seq<'U>)
            : QuerySource<'T, 'Q> =
            failwith "whereExists marker"

        [<CustomOperation("whereExists", MaintainsVariableSpace = true)>]
        member inline _.WhereExistsPred
            (source: QuerySource<'T, 'Q>, [<ProjectionParameter>] predicate: 'T -> bool)
            : QuerySource<'T, 'Q> =
            failwith "whereExists marker"

        [<CustomOperation("whereNotExists", MaintainsVariableSpace = true)>]
        member inline _.WhereNotExists
            (source: QuerySource<'T, 'Q>, [<ProjectionParameter>] subquery: 'T -> #seq<'U>)
            : QuerySource<'T, 'Q> =
            failwith "whereNotExists marker"

        [<CustomOperation("whereNotExists", MaintainsVariableSpace = true)>]
        member inline _.WhereNotExistsPred
            (source: QuerySource<'T, 'Q>, [<ProjectionParameter>] predicate: 'T -> bool)
            : QuerySource<'T, 'Q> =
            failwith "whereNotExists marker"

        [<CustomOperation("having", MaintainsVariableSpace = true)>]
        member inline _.Having
            (source: QuerySource<'T, 'Q>, [<ProjectionParameter>] predicate: 'T -> bool)
            : QuerySource<'T, 'Q> =
            failwith "having marker"

        [<CustomOperation("union")>]
        member inline _.Union
            (source: QuerySource<'T, 'Q>, other: System.Collections.Generic.IEnumerable<'T>)
            : QuerySource<'T, 'Q> =
            failwith "union marker"

        [<CustomOperation("unionAll")>]
        member inline _.UnionAll
            (source: QuerySource<'T, 'Q>, other: System.Collections.Generic.IEnumerable<'T>)
            : QuerySource<'T, 'Q> =
            failwith "unionAll marker"

        [<CustomOperation("intersect")>]
        member inline _.Intersect
            (source: QuerySource<'T, 'Q>, other: System.Collections.Generic.IEnumerable<'T>)
            : QuerySource<'T, 'Q> =
            failwith "intersect marker"

        [<CustomOperation("except")>]
        member inline _.Except
            (source: QuerySource<'T, 'Q>, other: System.Collections.Generic.IEnumerable<'T>)
            : QuerySource<'T, 'Q> =
            failwith "except marker"

        member _.union
            (lhs: System.Linq.IQueryable<'T>)
            (rhs: System.Linq.IQueryable<'T>)
            : System.Linq.IQueryable<'T> =
            match box lhs with
            | :? SqlPushdownQueryable<'T> as s -> s.Union(rhs) :> System.Linq.IQueryable<'T>
            | _ -> System.Linq.Queryable.Union(lhs, rhs)

        member _.unionAll
            (lhs: System.Linq.IQueryable<'T>)
            (rhs: System.Linq.IQueryable<'T>)
            : System.Linq.IQueryable<'T> =
            match box lhs with
            | :? SqlPushdownQueryable<'T> as s -> s.UnionAll(rhs) :> System.Linq.IQueryable<'T>
            | _ -> System.Linq.Queryable.Concat(lhs, rhs)

        member _.intersect
            (lhs: System.Linq.IQueryable<'T>)
            (rhs: System.Linq.IQueryable<'T>)
            : System.Linq.IQueryable<'T> =
            match box lhs with
            | :? SqlPushdownQueryable<'T> as s -> s.Intersect(rhs) :> System.Linq.IQueryable<'T>
            | _ -> System.Linq.Queryable.Intersect(lhs, rhs)

        member _.except
            (lhs: System.Linq.IQueryable<'T>)
            (rhs: System.Linq.IQueryable<'T>)
            : System.Linq.IQueryable<'T> =
            match box lhs with
            | :? SqlPushdownQueryable<'T> as s -> s.Except(rhs) :> System.Linq.IQueryable<'T>
            | _ -> System.Linq.Queryable.Except(lhs, rhs)

        [<CustomOperation("withCte", MaintainsVariableSpace = true)>]
        member inline _.WithCte
            (source: QuerySource<'T, 'Q>, name: string, cteQuery: System.Collections.Generic.IEnumerable<'U>)
            : QuerySource<'T, 'Q> =
            failwith "withCte marker"

        [<CustomOperation("withCte", MaintainsVariableSpace = true)>]
        member inline _.WithCte
            (source: QuerySource<'T, 'Q>, cteQuery: System.Collections.Generic.IEnumerable<'U>)
            : QuerySource<'T, 'Q> =
            failwith "withCte marker"

        [<CustomOperation("withCte", MaintainsVariableSpace = true)>]
        member inline _.WithCteSql
            (source: QuerySource<'T, 'Q>, name: string, subquerySql: string)
            : QuerySource<'T, 'Q> =
            failwith "withCte marker"

        [<CustomOperation("withCte", MaintainsVariableSpace = true)>]
        member inline _.WithCteSql(source: QuerySource<'T, 'Q>, subquerySql: string) : QuerySource<'T, 'Q> =
            failwith "withCte marker"

        [<CustomOperation("withCteSql", MaintainsVariableSpace = true)>]
        member inline _.WithCteSqlAlt
            (source: QuerySource<'T, 'Q>, name: string, subquerySql: string)
            : QuerySource<'T, 'Q> =
            failwith "withCteSql marker"

        [<CustomOperation("withCteSql", MaintainsVariableSpace = true)>]
        member inline _.WithCteSqlAlt(source: QuerySource<'T, 'Q>, subquerySql: string) : QuerySource<'T, 'Q> =
            failwith "withCteSql marker"

        [<CustomOperation("withRecursiveCte", MaintainsVariableSpace = true)>]
        member inline _.WithRecursiveCte
            (source: QuerySource<'T, 'Q>, name: string, cteQuery: System.Collections.Generic.IEnumerable<'U>)
            : QuerySource<'T, 'Q> =
            failwith "withRecursiveCte marker"

        [<CustomOperation("withRecursiveCte", MaintainsVariableSpace = true)>]
        member inline _.WithRecursiveCte
            (source: QuerySource<'T, 'Q>, cteQuery: System.Collections.Generic.IEnumerable<'U>)
            : QuerySource<'T, 'Q> =
            failwith "withRecursiveCte marker"

        [<CustomOperation("withRecursiveCte", MaintainsVariableSpace = true)>]
        member inline _.WithRecursiveCte
            (source: QuerySource<'T, 'Q>, name: string, subquerySql: string)
            : QuerySource<'T, 'Q> =
            failwith "withRecursiveCte marker"

        [<CustomOperation("withRecursiveCte", MaintainsVariableSpace = true)>]
        member inline _.WithRecursiveCte(source: QuerySource<'T, 'Q>, subquerySql: string) : QuerySource<'T, 'Q> =
            failwith "withRecursiveCte marker"

        [<CustomOperation("withRecursiveCteSql", MaintainsVariableSpace = true)>]
        member inline _.WithRecursiveCteSql
            (source: QuerySource<'T, 'Q>, name: string, subquerySql: string)
            : QuerySource<'T, 'Q> =
            failwith "withRecursiveCteSql marker"

        [<CustomOperation("withRecursiveCteSql", MaintainsVariableSpace = true)>]
        member inline _.WithRecursiveCteSql(source: QuerySource<'T, 'Q>, subquerySql: string) : QuerySource<'T, 'Q> =
            failwith "withRecursiveCteSql marker"

        [<CustomOperation("fullOuterJoin", IsLikeGroupJoin = true)>]
        member inline _.FullOuterJoin
            (
                source: QuerySource<'T, 'Q>,
                provider: QuerySource<'U, 'Q>,
                outerKey: 'T -> 'Key,
                innerKey: 'U -> 'Key,
                resultSelector: 'T -> seq<'U> -> 'Result
            ) : QuerySource<'Result, 'Q> =
            failwith "fullOuterJoin marker"

        [<CustomOperation("rightOuterJoin", IsLikeJoin = true)>]
        member inline _.RightOuterJoin
            (
                source: QuerySource<'T, 'Q>,
                provider: QuerySource<'U, 'Q>,
                outerKey: 'T -> 'Key,
                innerKey: 'U -> 'Key,
                resultSelector: 'T -> 'U -> 'Result
            ) : QuerySource<'Result, 'Q> =
            failwith "rightOuterJoin marker"

        [<CustomOperation("rightOuterJoinGroup", IsLikeGroupJoin = true)>]
        member inline _.RightOuterJoinGroup
            (
                source: QuerySource<'T, 'Q>,
                provider: QuerySource<'U, 'Q>,
                outerKey: 'T -> 'Key,
                innerKey: 'U -> 'Key,
                resultSelector: 'T -> seq<'U> -> 'Result
            ) : QuerySource<'Result, 'Q> =
            failwith "rightOuterJoin marker"

        [<CustomOperation("fullOuterJoinRaw", MaintainsVariableSpace = true)>]
        member inline _.FullOuterJoinRaw
            (source: QuerySource<'T, 'Q>, tableOrQuery: string, alias: string, onClause: string)
            : QuerySource<'T, 'Q> =
            failwith "fullOuterJoin marker"

        [<CustomOperation("rightOuterJoinRaw", MaintainsVariableSpace = true)>]
        member inline _.RightOuterJoinRaw
            (source: QuerySource<'T, 'Q>, tableOrQuery: string, alias: string, onClause: string)
            : QuerySource<'T, 'Q> =
            failwith "rightOuterJoin marker"

        [<CustomOperation("crossJoin", MaintainsVariableSpace = true)>]
        member inline _.CrossJoin
            (source: QuerySource<'T, 'Q>, tableOrQuery: string, alias: string)
            : QuerySource<'T, 'Q> =
            failwith "crossJoin marker"

        /// Declares a window specification (OVER clause) with optional partition criteria and ordering.
        member inline _.window(?partitionBy: 'P, ?sortBy: 'S, ?sortByDescending: 'SD) : WindowDefinition =
            { Dummy = "window" }

        /// Ranking window function: ROW_NUMBER() OVER (...)
        member inline _.rowNumber(?over: WindowDefinition) : int64 = 0L

        /// Ranking window function: RANK() OVER (...)
        member inline _.rank(?over: WindowDefinition) : int64 = 0L

        /// Ranking window function: DENSE_RANK() OVER (...)
        member inline _.denseRank(?over: WindowDefinition) : int64 = 0L

        /// Ranking window function: NTILE(n) OVER (...)
        member inline _.ntile(tiles: int, ?over: WindowDefinition) : int64 = 0L

        /// Value window function: LAG(expr, offset, default) OVER (...)
        member inline _.lag(expr: 'T, ?offset: int, ?defaultValue: 'T, ?over: WindowDefinition) : 'T = expr

        /// Value window function: LEAD(expr, offset, default) OVER (...)
        member inline _.lead(expr: 'T, ?offset: int, ?defaultValue: 'T, ?over: WindowDefinition) : 'T = expr

        /// Value window function: FIRST_VALUE(expr) OVER (...)
        member inline _.firstValue(expr: 'T, ?over: WindowDefinition) : 'T = expr

        /// Value window function: LAST_VALUE(expr) OVER (...)
        member inline _.lastValue(expr: 'T, ?over: WindowDefinition) : 'T = expr

        /// Aggregate window function: SUM(expr) OVER (...)
        member inline _.sum(expr: 'T, ?over: WindowDefinition) : 'T = expr

        /// Aggregate window function: AVG(expr) OVER (...)
        member inline _.avg(expr: 'T, ?over: WindowDefinition) : 'T = expr

        /// Aggregate window function: MIN(expr) OVER (...)
        member inline _.min(expr: 'T, ?over: WindowDefinition) : 'T = expr

        /// Aggregate window function: MAX(expr) OVER (...)
        member inline _.max(expr: 'T, ?over: WindowDefinition) : 'T = expr

        /// Aggregate window function: COUNT(*) OVER (...) or COUNT(expr) OVER (...)
        member inline _.count(?over: WindowDefinition) : int64 = 0L
        member inline _.count(expr: 'T, ?over: WindowDefinition) : int64 = 0L

        /// Semi-structured JSON navigation pushdown (JSON_VALUE / ->> / json_extract).
        member inline _.jsonValue(column: obj, path: string) : string = ""

        /// Checks whether a column or value is NOT NULL (translates to `col IS NOT NULL` in SQL pushdown).
        member inline _.isNotNull(value: 'T) : bool = isNotNull value

        /// Checks whether a string or value is NOT NULL and NOT empty (`""`) (translates to `(col IS NOT NULL AND col <> '')` in SQL pushdown).
        member inline _.isNotEmpty(value: 'T) : bool = isNotEmpty value

        /// Checks whether a string or value is NOT NULL and NOT empty (`""`) (translates to `(col IS NOT NULL AND col <> '')` in SQL pushdown).
        member inline _.isNotNullOrEmpty(value: 'T) : bool = isNotNullOrEmpty value

        /// Declares a correlated subquery existence check in F# queries.
        member inline _.exists(subquery: #seq<'T>) : bool = true

        /// Declares a correlated subquery non-existence check in F# queries.
        member inline _.notExists(subquery: #seq<'T>) : bool = false

        /// Declares a Common Table Expression (CTE) in the current query.
        member inline _.withCte(name: string, cteQuery: #seq<'U>) : unit = ()
        member inline _.withCte(cteQuery: #seq<'U>) : unit = ()
        member inline _.withCte(subquerySql: string) : unit = ()
        member inline _.withCteSql(name: string, subquerySql: string) : unit = ()
        member inline _.withCteSql(subquerySql: string) : unit = ()
        member inline _.withRecursiveCte(name: string, cteQuery: #seq<'U>) : unit = ()
        member inline _.withRecursiveCte(cteQuery: #seq<'U>) : unit = ()
        member inline _.withRecursiveCte(subquerySql: string) : unit = ()
        member inline _.withRecursiveCteSql(name: string, subquerySql: string) : unit = ()
        member inline _.withRecursiveCteSql(subquerySql: string) : unit = ()

        /// Creates a recursive Common Table Expression with an anchor and recursive query.
        member inline _.recursive(name: string, anchor: #seq<'T>, recursive: #seq<'T> -> #seq<'T>) : #seq<'T> = anchor

        /// Returns a structured query execution plan report for diagnostics.
        member inline _.explain(query: obj) : ExecutionPlanReport = SqlPushdownQuery.explainQuery query

        member private this.BaseRun(typed) = base.Run(typed)

        member this.Run(q: Expr<QuerySource<'T, System.Linq.IQueryable>>) : System.Linq.IQueryable<'T> =
            let whereMethod = typeof<QueryBuilder>.GetMethod("Where")

            let countMethod =
                typeof<System.Linq.Enumerable>.GetMethods()
                |> Seq.find (fun m -> m.Name = "Count" && m.GetParameters().Length = 1)

            let leftOuterJoinMethod =
                typeof<QueryBuilder>.GetMethods()
                |> Seq.find (fun m -> m.Name = "LeftOuterJoin" && m.GetParameters().Length = 5)

            let joinMethod =
                typeof<QueryBuilder>.GetMethods()
                |> Seq.find (fun m -> m.Name = "Join" && m.GetParameters().Length = 5)

            let queryBuilderRunMethod =
                typeof<QueryBuilder>.GetMethods()
                |> Seq.find (fun m -> m.Name = "Run" && m.IsGenericMethod && m.GetParameters().Length = 1)

            let ctes = ResizeArray<string * obj * bool>()
            let explicitJoins = ResizeArray<string * string * string * string>()
            let fullOuterJoinTables = ResizeArray<string>()
            let rightOuterJoinTables = ResizeArray<string>()
            let mutable fromCteName: string option = None

            let rec tryFindRootSource (e: Expr) : obj option =
                match e with
                | Call(_, mi, firstArg :: _) when
                    mi.DeclaringType = typeof<QueryBuilder>
                    || mi.DeclaringType = typeof<FSqlQueryBuilder>
                    ->
                    tryFindRootSource firstArg
                    |> Option.orElseWith (fun () ->
                        try
                            let v = LeafExpressionConverter.EvaluateQuotation firstArg

                            if isNull v then
                                None
                            else
                                let t = v.GetType()
                                let srcProp = t.GetProperty("Source")

                                if not (isNull srcProp) then
                                    Some(srcProp.GetValue(v))
                                else
                                    Some v
                        with _ ->
                            None)
                | _ ->
                    try
                        let v = LeafExpressionConverter.EvaluateQuotation e

                        if isNull v then
                            None
                        else
                            let t = v.GetType()
                            let srcProp = t.GetProperty("Source")

                            if not (isNull srcProp) then
                                Some(srcProp.GetValue(v))
                            else
                                Some v
                    with _ ->
                        None

            let isSourceMatch (sourceObj: obj) (targetCte: obj) =
                if isNull sourceObj || isNull targetCte then
                    false
                elif obj.ReferenceEquals(sourceObj, targetCte) then
                    true
                else
                    match sourceObj, targetCte with
                    | (:? IQueryable as sqIq), (:? IQueryable as cteIq) ->
                        obj.ReferenceEquals(sqIq.Expression, cteIq.Expression)
                        || (match
                                SqlPushdownQuery.tryFindPushdownSource sqIq.Expression,
                                SqlPushdownQuery.tryFindPushdownSource cteIq.Expression
                            with
                            | Some(s1, _), Some(s2, _) -> obj.ReferenceEquals(s1, s2)
                            | _ -> false)
                    | _ -> false

            let rec peelCustomOps (expr: Expr) : Expr =
                match expr with
                | Call(instanceOpt, mi, [ innerSource; nameArg; cteArg ]) when
                    mi.Name = "WithCte"
                    || mi.Name = "WithCteSql"
                    || mi.Name = "WithCteSqlAlt"
                    || mi.Name = "WithRecursiveCte"
                    || mi.Name = "WithRecursiveCteSql"
                    ->
                    let name = LeafExpressionConverter.EvaluateQuotation(nameArg) :?> string

                    let cteVal =
                        match cteArg with
                        | Lambda(_, body) -> LeafExpressionConverter.EvaluateQuotation(body)
                        | _ -> LeafExpressionConverter.EvaluateQuotation(cteArg)

                    let isRec = mi.Name.StartsWith("WithRecursive")
                    ctes.Add((name, cteVal, isRec))

                    match tryFindRootSource innerSource with
                    | Some rSrc when isSourceMatch rSrc cteVal -> fromCteName <- Some name
                    | _ -> ()

                    peelCustomOps innerSource
                | Call(instanceOpt, mi, [ innerSource; cteArg ]) when
                    mi.Name = "WithCte"
                    || mi.Name = "WithCteSql"
                    || mi.Name = "WithCteSqlAlt"
                    || mi.Name = "WithRecursiveCte"
                    || mi.Name = "WithRecursiveCteSql"
                    ->
                    let autoName = $"cte_{ctes.Count + 1}"

                    let cteVal =
                        match cteArg with
                        | Lambda(_, body) -> LeafExpressionConverter.EvaluateQuotation(body)
                        | _ -> LeafExpressionConverter.EvaluateQuotation(cteArg)

                    let isRec = mi.Name.StartsWith("WithRecursive")
                    ctes.Add((autoName, cteVal, isRec))

                    match tryFindRootSource innerSource with
                    | Some rSrc when isSourceMatch rSrc cteVal -> fromCteName <- Some autoName
                    | _ -> ()

                    peelCustomOps innerSource
                | Call(instanceOpt, mi, [ innerSource; tableArg; aliasArg; onArg ]) when
                    mi.Name = "FullOuterJoinRaw" || mi.Name = "RightOuterJoinRaw"
                    ->
                    let tbl = LeafExpressionConverter.EvaluateQuotation(tableArg) :?> string
                    let alias = LeafExpressionConverter.EvaluateQuotation(aliasArg) :?> string
                    let onCl = LeafExpressionConverter.EvaluateQuotation(onArg) :?> string

                    let jt =
                        if mi.Name.StartsWith("Full") then
                            "FULL OUTER JOIN"
                        else
                            "RIGHT OUTER JOIN"

                    explicitJoins.Add((jt, tbl, alias, onCl))
                    peelCustomOps innerSource
                | Call(instanceOpt, mi, [ innerSource; tableArg; aliasArg ]) when mi.Name = "CrossJoin" ->
                    let tbl = LeafExpressionConverter.EvaluateQuotation(tableArg) :?> string
                    let alias = LeafExpressionConverter.EvaluateQuotation(aliasArg) :?> string
                    explicitJoins.Add(("CROSS JOIN", tbl, alias, ""))
                    peelCustomOps innerSource
                | ExprShape.ShapeVar v -> Expr.Var v
                | ExprShape.ShapeLambda(v, b) -> Expr.Lambda(v, peelCustomOps b)
                | ExprShape.ShapeCombination(shape, args) ->
                    ExprShape.RebuildShapeCombination(shape, List.map peelCustomOps args)

            let rec rewrite (expr: Expr) : Expr =
                match expr with
                | Call(instanceOpt, mi, [ innerArg ]) when
                    mi.Name = "Run" && mi.DeclaringType = typeof<FSqlQueryBuilder>
                    ->
                    let elemType = mi.GetGenericArguments().[0]
                    let genericRun = queryBuilderRunMethod.MakeGenericMethod(elemType)
                    let newInner = rewrite innerArg

                    match instanceOpt with
                    | Some inst -> Expr.Call(inst, genericRun, [ newInner ])
                    | None -> Expr.Call(genericRun, [ newInner ])
                | Call(instanceOpt, mi, [ source; provider; outerKey; innerKey; resultSelector ]) when
                    (mi.Name = "FullOuterJoin"
                     || mi.Name = "RightOuterJoin"
                     || mi.Name = "RightOuterJoinGroup")
                    && mi.GetGenericArguments().Length = 5
                    ->
                    let isRight = mi.Name = "RightOuterJoin" || mi.Name = "RightOuterJoinGroup"

                    let targetList =
                        if isRight then
                            rightOuterJoinTables
                        else
                            fullOuterJoinTables

                    let genArgs = mi.GetGenericArguments()

                    let provObj =
                        try
                            let v = LeafExpressionConverter.EvaluateQuotation(provider)
                            let t = v.GetType()
                            let srcProp = t.GetProperty("Source")
                            if not (isNull srcProp) then srcProp.GetValue(v) else v
                        with _ ->
                            null

                    if not (isNull provObj) then
                        match provObj with
                        | :? IQueryable as iq ->
                            match SqlPushdownQuery.tryFindPushdownSource iq.Expression with
                            | Some(_, Some p) -> targetList.Add(p.BaseSql)
                            | Some(sIq, None) ->
                                try
                                    let pProp = sIq.GetType().GetProperty("Plan")

                                    if not (isNull pProp) then
                                        let p = pProp.GetValue(sIq) :?> PushdownQueryPlan
                                        targetList.Add(p.BaseSql)
                                with _ ->
                                    ()
                            | None -> ()
                        | _ -> ()

                    let rec tryFindTable (e: Expr) : string option =
                        match e with
                        | PropertyGet(_, pi, _) -> Some pi.Name
                        | Call(_, _, args) -> args |> List.tryPick tryFindTable
                        | Coerce(inner, _) -> tryFindTable inner
                        | _ -> None

                    match tryFindTable provider with
                    | Some tbl -> targetList.Add(tbl)
                    | None -> ()

                    if targetList.Count = 0 then
                        targetList.Add("*")

                    let newSource = rewrite source
                    let newProvider = rewrite provider
                    let newResultSelector = rewrite resultSelector

                    let targetMethod =
                        if mi.Name = "RightOuterJoin" then
                            joinMethod.MakeGenericMethod(genArgs)
                        else
                            leftOuterJoinMethod.MakeGenericMethod(genArgs)

                    match instanceOpt with
                    | Some inst ->
                        Expr.Call(inst, targetMethod, [ newSource; newProvider; outerKey; innerKey; newResultSelector ])
                    | None -> Expr.Call(targetMethod, [ newSource; newProvider; outerKey; innerKey; newResultSelector ])
                | Call(Some instance, mi, [ source; Lambda(var, body) ]) when mi.Name = "WhereExists" ->
                    let srcType = mi.GetGenericArguments().[0]
                    let qType = mi.GetGenericArguments().[1]
                    let genericWhere = whereMethod.MakeGenericMethod(srcType, qType)

                    let subElemType =
                        if body.Type.IsGenericType then
                            body.Type.GetGenericArguments().[0]
                        else
                            typeof<obj>

                    let genericCount = countMethod.MakeGenericMethod(subElemType)
                    let countCall = Expr.Call(genericCount, [ rewrite body ])
                    let gtExpr = <@@ (%%countCall: int) > 0 @@>
                    let newSource = rewrite source
                    Expr.Call(instance, genericWhere, [ newSource; Expr.Lambda(var, gtExpr) ])
                | Call(Some instance, mi, [ source; Lambda(var, body) ]) when mi.Name = "WhereExistsPred" ->
                    let srcType = mi.GetGenericArguments().[0]
                    let qType = mi.GetGenericArguments().[1]
                    let genericWhere = whereMethod.MakeGenericMethod(srcType, qType)
                    let newSource = rewrite source
                    let newBody = rewrite body
                    Expr.Call(instance, genericWhere, [ newSource; Expr.Lambda(var, newBody) ])
                | Call(Some instance, mi, [ source; Lambda(var, body) ]) when mi.Name = "WhereNotExists" ->
                    let srcType = mi.GetGenericArguments().[0]
                    let qType = mi.GetGenericArguments().[1]
                    let genericWhere = whereMethod.MakeGenericMethod(srcType, qType)

                    let subElemType =
                        if body.Type.IsGenericType then
                            body.Type.GetGenericArguments().[0]
                        else
                            typeof<obj>

                    let genericCount = countMethod.MakeGenericMethod(subElemType)
                    let countCall = Expr.Call(genericCount, [ rewrite body ])
                    let eqExpr = <@@ (%%countCall: int) = 0 @@>
                    let newSource = rewrite source
                    Expr.Call(instance, genericWhere, [ newSource; Expr.Lambda(var, eqExpr) ])
                | Call(Some instance, mi, [ source; Lambda(var, body) ]) when mi.Name = "WhereNotExistsPred" ->
                    let srcType = mi.GetGenericArguments().[0]
                    let qType = mi.GetGenericArguments().[1]
                    let genericWhere = whereMethod.MakeGenericMethod(srcType, qType)
                    let newSource = rewrite source
                    let newBody = rewrite body
                    let notExpr = <@@ not %%(Expr.Cast<bool>(newBody)) @@>
                    Expr.Call(instance, genericWhere, [ newSource; Expr.Lambda(var, notExpr) ])
                | Call(Some instance, mi, [ source; Lambda(var, body) ]) when mi.Name = "Having" ->
                    let srcType = mi.GetGenericArguments().[0]
                    let qType = mi.GetGenericArguments().[1]
                    let genericWhere = whereMethod.MakeGenericMethod(srcType, qType)
                    let newSource = rewrite source
                    let newBody = rewrite body
                    Expr.Call(instance, genericWhere, [ newSource; Expr.Lambda(var, newBody) ])
                | ExprShape.ShapeVar v -> Expr.Var v
                | ExprShape.ShapeLambda(v, b) -> Expr.Lambda(v, rewrite b)
                | ExprShape.ShapeCombination(shape, args) ->
                    ExprShape.RebuildShapeCombination(shape, List.map rewrite args)

            let attachToPlan (plan: PushdownQueryPlan) =
                for (name, cteVal, isRec) in ctes do
                    if not (plan.Ctes |> Seq.exists (fun c -> c.Name = name)) then
                        match cteVal with
                        | :? string as rawSql ->
                            plan.Ctes.Add(
                                {
                                    Name = name
                                    SubquerySql = rawSql
                                    IsRecursive = isRec
                                    SourceQuery = None
                                }
                            )
                        | _ ->
                            let (subSql, subParams) = SqlPushdownQuery.extractSqlWithParameters cteVal
                            let mutable remappedSql = subSql

                            for (pName, pVal) in subParams do
                                let uniqueName = $"@p{plan.Parameters.Count}"
                                remappedSql <- remappedSql.Replace(pName, uniqueName)
                                plan.Parameters.Add(uniqueName, pVal)

                            plan.Ctes.Add(
                                {
                                    Name = name
                                    SubquerySql = remappedSql
                                    IsRecursive = isRec
                                    SourceQuery = Some cteVal
                                }
                            )

                for (jt, tbl, alias, onCl) in explicitJoins do
                    plan.Joins.Add(
                        {
                            JoinType = jt
                            SourceTable = tbl
                            Alias = alias
                            OnClause = onCl
                            RowType = None
                        }
                    )

                plan.FullOuterJoinTables.AddRange(fullOuterJoinTables)

                if fullOuterJoinTables.Count > 0 then
                    for j in plan.Joins do
                        if
                            fullOuterJoinTables.Contains("*")
                            || (fullOuterJoinTables
                                |> Seq.exists (fun tbl ->
                                    String.Equals(tbl, j.SourceTable, StringComparison.OrdinalIgnoreCase)))
                        then
                            j.JoinType <- "FULL OUTER JOIN"

                plan.RightOuterJoinTables.AddRange(rightOuterJoinTables)

                if rightOuterJoinTables.Count > 0 then
                    for j in plan.Joins do
                        if
                            rightOuterJoinTables.Contains("*")
                            || (rightOuterJoinTables
                                |> Seq.exists (fun tbl ->
                                    String.Equals(tbl, j.SourceTable, StringComparison.OrdinalIgnoreCase)))
                        then
                            j.JoinType <- "RIGHT OUTER JOIN"

                match fromCteName with
                | Some cName ->
                    plan.BaseSql <- cName
                    plan.WhereClauses.Clear()
                | None -> ()

            let attachToQueryable (queryable: System.Linq.IQueryable<'T>) : System.Linq.IQueryable<'T> =
                let planOpt =
                    match box queryable with
                    | :? SqlPushdownQueryable<'T> as spq -> Some spq.Plan
                    | _ ->
                        match SqlPushdownQuery.tryFindPushdownSource queryable.Expression with
                        | Some(_, Some p) -> Some p
                        | _ -> None

                match planOpt with
                | Some plan ->
                    attachToPlan plan

                    for (name, cteVal, _) in ctes do
                        let isBaseFromThisCte =
                            match box queryable with
                            | :? ISqlPushdownQueryable as spq -> obj.ReferenceEquals(spq, cteVal)
                            | _ ->
                                match ExpressionPushdownTranslator.tryFindPushdownSource queryable.Expression with
                                | Some(sourceIq, _) -> obj.ReferenceEquals(sourceIq, cteVal)
                                | None -> false

                        if isBaseFromThisCte then
                            plan.BaseSql <- name
                            plan.WhereClauses.Clear()

                    queryable
                | None -> queryable

            let toSpq (q: obj) : SqlPushdownQueryable<'T> option =
                match q with
                | :? SqlPushdownQueryable<'T> as s -> Some s
                | :? IQueryable as iq when
                    not (isNull iq.Provider)
                    && iq.Provider.GetType().Name.StartsWith("SqlPushdownQueryProvider")
                    ->
                    match ExpressionPushdownTranslator.tryFindPushdownSource iq.Expression with
                    | Some(sourceIq, seedPlan) ->
                        let prov = sourceIq.Provider :?> SqlPushdownQueryProvider
                        let p = prov.ExtractPlan(iq.Expression, ?seedPlan = seedPlan)

                        match seedPlan with
                        | Some sp ->
                            for cte in sp.Ctes do
                                if not (p.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                                    p.Ctes.Add(cte)
                        | None -> ()

                        Some(
                            SqlPushdownQueryable<'T>(
                                prov,
                                Expression.Constant(null, typeof<IQueryable<'T>>),
                                prov.Connector,
                                prov.BaseSql,
                                prov.BaseParameters,
                                p
                            )
                        )
                    | None -> None
                | _ -> None

            let doUnion (lhs: System.Linq.IQueryable<'T>) (rhs: obj) : System.Linq.IQueryable<'T> =
                match toSpq lhs, toSpq rhs with
                | Some spq, Some rspq -> spq.Union(rspq) :> System.Linq.IQueryable<'T>
                | _ ->
                    match rhs with
                    | :? System.Linq.IQueryable<'T> as rhsIq -> System.Linq.Queryable.Union(lhs, rhsIq)
                    | :? System.Collections.Generic.IEnumerable<'T> as rhsSeq ->
                        System.Linq.Queryable.Union(lhs, rhsSeq.AsQueryable())
                    | _ -> lhs

            let doUnionAll (lhs: System.Linq.IQueryable<'T>) (rhs: obj) : System.Linq.IQueryable<'T> =
                match toSpq lhs, toSpq rhs with
                | Some spq, Some rspq -> spq.UnionAll(rspq) :> System.Linq.IQueryable<'T>
                | _ ->
                    match rhs with
                    | :? System.Linq.IQueryable<'T> as rhsIq -> System.Linq.Queryable.Concat(lhs, rhsIq)
                    | :? System.Collections.Generic.IEnumerable<'T> as rhsSeq ->
                        System.Linq.Queryable.Concat(lhs, rhsSeq.AsQueryable())
                    | _ -> lhs

            let doIntersect (lhs: System.Linq.IQueryable<'T>) (rhs: obj) : System.Linq.IQueryable<'T> =
                match toSpq lhs, toSpq rhs with
                | Some spq, Some rspq -> spq.Intersect(rspq) :> System.Linq.IQueryable<'T>
                | _ ->
                    match rhs with
                    | :? System.Linq.IQueryable<'T> as rhsIq -> System.Linq.Queryable.Intersect(lhs, rhsIq)
                    | :? System.Collections.Generic.IEnumerable<'T> as rhsSeq ->
                        System.Linq.Queryable.Intersect(lhs, rhsSeq.AsQueryable())
                    | _ -> lhs

            let doExcept (lhs: System.Linq.IQueryable<'T>) (rhs: obj) : System.Linq.IQueryable<'T> =
                match toSpq lhs, toSpq rhs with
                | Some spq, Some rspq -> spq.Except(rspq) :> System.Linq.IQueryable<'T>
                | _ ->
                    match rhs with
                    | :? System.Linq.IQueryable<'T> as rhsIq -> System.Linq.Queryable.Except(lhs, rhsIq)
                    | :? System.Collections.Generic.IEnumerable<'T> as rhsSeq ->
                        System.Linq.Queryable.Except(lhs, rhsSeq.AsQueryable())
                    | _ -> lhs

            let rec handleSetOps (expr: Expr) : System.Linq.IQueryable<'T> =
                match expr with
                | Call(inst, mi, [ lhsSource; rhsArg ]) when mi.Name = "Union" ->
                    let lhsQ = handleSetOps lhsSource

                    let rhsVal =
                        match rhsArg with
                        | Lambda(_, body) -> LeafExpressionConverter.EvaluateQuotation(body)
                        | _ -> LeafExpressionConverter.EvaluateQuotation(rhsArg)

                    doUnion lhsQ rhsVal
                | Call(inst, mi, [ lhsSource; rhsArg ]) when mi.Name = "UnionAll" ->
                    let lhsQ = handleSetOps lhsSource

                    let rhsVal =
                        match rhsArg with
                        | Lambda(_, body) -> LeafExpressionConverter.EvaluateQuotation(body)
                        | _ -> LeafExpressionConverter.EvaluateQuotation(rhsArg)

                    doUnionAll lhsQ rhsVal
                | Call(inst, mi, [ lhsSource; rhsArg ]) when mi.Name = "Intersect" ->
                    let lhsQ = handleSetOps lhsSource

                    let rhsVal =
                        match rhsArg with
                        | Lambda(_, body) -> LeafExpressionConverter.EvaluateQuotation(body)
                        | _ -> LeafExpressionConverter.EvaluateQuotation(rhsArg)

                    doIntersect lhsQ rhsVal
                | Call(inst, mi, [ lhsSource; rhsArg ]) when mi.Name = "Except" ->
                    let lhsQ = handleSetOps lhsSource

                    let rhsVal =
                        match rhsArg with
                        | Lambda(_, body) -> LeafExpressionConverter.EvaluateQuotation(body)
                        | _ -> LeafExpressionConverter.EvaluateQuotation(rhsArg)

                    doExcept lhsQ rhsVal
                | other ->
                    let baseRes =
                        let rec tryGetDirectSource e =
                            match e with
                            | Call(_, forMi, [ Call(_, srcMi, [ srcExpr ]); Lambda(var, body) ]) when
                                forMi.Name = "For" && srcMi.Name = "Source"
                                ->
                                let isId =
                                    match body with
                                    | Call(_, yieldMi, [ Var v ]) when
                                        (yieldMi.Name = "Yield" || yieldMi.Name = "Return") && v.Name = var.Name
                                        ->
                                        true
                                    | Let(vInner, Var vOuter, Call(_, yieldMi, [ Var vFinal ])) when
                                        (yieldMi.Name = "Yield" || yieldMi.Name = "Return")
                                        && vOuter.Name = var.Name
                                        && vFinal.Name = vInner.Name
                                        ->
                                        true
                                    | _ -> false

                                if isId then
                                    try
                                        let evaled = LeafExpressionConverter.EvaluateQuotation(srcExpr)

                                        match evaled with
                                        | :? System.Linq.IQueryable<'T> as iq -> Some iq
                                        | _ -> None
                                    with _ ->
                                        None
                                else
                                    None
                            | _ -> None

                        match tryGetDirectSource other with
                        | Some directIq -> directIq
                        | None ->
                            let rewritten = rewrite other
                            let typedExpr = Expr.Cast<QuerySource<'T, System.Linq.IQueryable>>(rewritten)
                            this.BaseRun(typedExpr)

                    baseRes

            let rec collectAllCtesFromExpr (e: Expr) : CtePlan list =
                match e with
                | Call(_, mi, [ arg ]) when mi.Name = "Source" ->
                    try
                        let v = LeafExpressionConverter.EvaluateQuotation(arg)

                        match v with
                        | :? ISqlPushdownQueryable as spq -> spq.Plan.Ctes |> Seq.toList
                        | :? IQueryable as iq ->
                            match ExpressionPushdownTranslator.tryFindPushdownSource iq.Expression with
                            | Some(_, Some p) -> p.Ctes |> Seq.toList
                            | _ -> []
                        | _ -> []
                    with _ ->
                        []
                | PropertyGet(None, pi, []) when typeof<IQueryable>.IsAssignableFrom(pi.PropertyType) ->
                    try
                        let v = pi.GetValue(null)

                        match v with
                        | :? ISqlPushdownQueryable as spq -> spq.Plan.Ctes |> Seq.toList
                        | _ -> []
                    with _ ->
                        []
                | Value(v, t) when not (isNull v) && typeof<IQueryable>.IsAssignableFrom(t) ->
                    match v with
                    | :? ISqlPushdownQueryable as spq -> spq.Plan.Ctes |> Seq.toList
                    | _ -> []
                | ExprShape.ShapeCombination(_, args) -> args |> List.collect collectAllCtesFromExpr
                | ExprShape.ShapeLambda(_, b) -> collectAllCtesFromExpr b
                | ExprShape.ShapeVar _ -> []
                | _ -> []

            let allDiscoveredCtes = collectAllCtesFromExpr q

            for cte in allDiscoveredCtes do
                if not (ctes |> Seq.exists (fun (n, _, _) -> n = cte.Name)) then
                    ctes.Add((cte.Name, box (defaultArg cte.SourceQuery (box cte.SubquerySql)), cte.IsRecursive))

            let cleaned = peelCustomOps q
            let res = handleSetOps cleaned
            attachToQueryable res

    /// Primary computation expression for F# Query Language (fsql) providing
    /// descriptive DQL operations (whereExists, whereNotExists, having, crossApply, lateral, etc.).
    let fsql = FSqlQueryBuilder()

    /// Alias for backwards compatibility.
    let fquery = fsql

    /// Extracts the formatted, indented raw SQL query for any queryable, plan, or expression.
    let toRawQuery (query: obj) : string = SqlPushdownQuery.toRawQuery query

    /// Extracts the raw SQL query for any queryable, plan, or expression.
    let toRawSql (query: obj) : string = SqlPushdownQuery.toRawSql query

    /// Returns a structured query execution plan report for diagnostics.
    let explainQuery (query: obj) : ExecutionPlanReport = SqlPushdownQuery.explainQuery query

    /// Creates a recursive Common Table Expression with an anchor and recursive query.
    let recursive (name: string) (anchor: #seq<'T>) (recursiveFunc: #seq<'T> -> #seq<'T>) : #seq<'T> = anchor

    /// Semi-structured JSON navigation pushdown (JSON_VALUE / ->> / json_extract).
    let jsonValue (column: obj) (path: string) : string = ""

    /// Combines two queryables using SQL UNION (distinct results).
    let union (lhs: System.Linq.IQueryable<'T>) (rhs: System.Linq.IQueryable<'T>) : System.Linq.IQueryable<'T> =
        match box lhs with
        | :? SqlPushdownQueryable<'T> as s -> s.Union(rhs) :> System.Linq.IQueryable<'T>
        | _ -> System.Linq.Queryable.Union(lhs, rhs)

    /// Combines two queryables using SQL UNION ALL (all rows preserved).
    let unionAll (lhs: System.Linq.IQueryable<'T>) (rhs: System.Linq.IQueryable<'T>) : System.Linq.IQueryable<'T> =
        match box lhs with
        | :? SqlPushdownQueryable<'T> as s -> s.UnionAll(rhs) :> System.Linq.IQueryable<'T>
        | _ -> System.Linq.Queryable.Concat(lhs, rhs)

    /// Combines two queryables using SQL INTERSECT.
    let intersect (lhs: System.Linq.IQueryable<'T>) (rhs: System.Linq.IQueryable<'T>) : System.Linq.IQueryable<'T> =
        match box lhs with
        | :? SqlPushdownQueryable<'T> as s -> s.Intersect(rhs) :> System.Linq.IQueryable<'T>
        | _ -> System.Linq.Queryable.Intersect(lhs, rhs)

    /// Combines two queryables using SQL EXCEPT.
    let except (lhs: System.Linq.IQueryable<'T>) (rhs: System.Linq.IQueryable<'T>) : System.Linq.IQueryable<'T> =
        match box lhs with
        | :? SqlPushdownQueryable<'T> as s -> s.Except(rhs) :> System.Linq.IQueryable<'T>
        | _ -> System.Linq.Queryable.Except(lhs, rhs)

[<AutoOpen>]
module FSqlExtensions =

    let private applySpqUnion
        (spq: SqlPushdownQueryable<'T>)
        (other: System.Linq.IQueryable<'T>)
        : SqlPushdownQueryable<'T> =
        spq.Union(other)

    let private applySpqUnionAll
        (spq: SqlPushdownQueryable<'T>)
        (other: System.Linq.IQueryable<'T>)
        : SqlPushdownQueryable<'T> =
        spq.UnionAll(other)

    let private applySpqIntersect
        (spq: SqlPushdownQueryable<'T>)
        (other: System.Linq.IQueryable<'T>)
        : SqlPushdownQueryable<'T> =
        spq.Intersect(other)

    let private applySpqExcept
        (spq: SqlPushdownQueryable<'T>)
        (other: System.Linq.IQueryable<'T>)
        : SqlPushdownQueryable<'T> =
        spq.Except(other)

    let private applySpqWithCte
        (spq: SqlPushdownQueryable<'T>)
        (cteQuery: System.Linq.IQueryable<'U>)
        (isRec: bool option)
        : SqlPushdownQueryable<'T> =
        match isRec with
        | Some r -> spq.WithCte(cteQuery, isRecursive = r)
        | None -> spq.WithCte(cteQuery)

    let private applySpqWithCteNamed
        (spq: SqlPushdownQueryable<'T>)
        (name: string)
        (cteQuery: System.Linq.IQueryable<'U>)
        (isRec: bool option)
        : SqlPushdownQueryable<'T> =
        match isRec with
        | Some r -> spq.WithCte(name, cteQuery, isRecursive = r)
        | None -> spq.WithCte(name, cteQuery)

    type System.Linq.IQueryable<'T> with
        member this.Union(other: System.Linq.IQueryable<'T>) : System.Linq.IQueryable<'T> =
            match box this with
            | :? SqlPushdownQueryable<'T> as s1 -> applySpqUnion s1 other :> System.Linq.IQueryable<'T>
            | _ ->
                match ExpressionPushdownTranslator.tryFindPushdownSource this.Expression with
                | Some(sourceIq, seedPlan) ->
                    let prov = sourceIq.Provider :?> SqlPushdownQueryProvider
                    let fullPlan = prov.ExtractPlan(this.Expression, ?seedPlan = seedPlan)

                    match seedPlan with
                    | Some sp ->
                        for cte in sp.Ctes do
                            if not (fullPlan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                                fullPlan.Ctes.Add(cte)
                    | None -> ()

                    let spq =
                        SqlPushdownQueryable<'T>(
                            prov,
                            Expression.Constant(null, typeof<IQueryable<'T>>),
                            prov.Connector,
                            prov.BaseSql,
                            prov.BaseParameters,
                            fullPlan
                        )

                    spq.Union(other) :> System.Linq.IQueryable<'T>
                | None -> System.Linq.Queryable.Union(this, other)

        member this.UnionAll(other: System.Linq.IQueryable<'T>) : System.Linq.IQueryable<'T> =
            match box this with
            | :? SqlPushdownQueryable<'T> as s1 -> applySpqUnionAll s1 other :> System.Linq.IQueryable<'T>
            | _ ->
                match ExpressionPushdownTranslator.tryFindPushdownSource this.Expression with
                | Some(sourceIq, seedPlan) ->
                    let prov = sourceIq.Provider :?> SqlPushdownQueryProvider
                    let fullPlan = prov.ExtractPlan(this.Expression, ?seedPlan = seedPlan)

                    match seedPlan with
                    | Some sp ->
                        for cte in sp.Ctes do
                            if not (fullPlan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                                fullPlan.Ctes.Add(cte)
                    | None -> ()

                    let spq =
                        SqlPushdownQueryable<'T>(
                            prov,
                            Expression.Constant(null, typeof<IQueryable<'T>>),
                            prov.Connector,
                            prov.BaseSql,
                            prov.BaseParameters,
                            fullPlan
                        )

                    spq.UnionAll(other) :> System.Linq.IQueryable<'T>
                | None -> System.Linq.Queryable.Concat(this, other)

        member this.Intersect(other: System.Linq.IQueryable<'T>) : System.Linq.IQueryable<'T> =
            match box this with
            | :? SqlPushdownQueryable<'T> as s1 -> applySpqIntersect s1 other :> System.Linq.IQueryable<'T>
            | _ ->
                match ExpressionPushdownTranslator.tryFindPushdownSource this.Expression with
                | Some(sourceIq, seedPlan) ->
                    let prov = sourceIq.Provider :?> SqlPushdownQueryProvider
                    let fullPlan = prov.ExtractPlan(this.Expression, ?seedPlan = seedPlan)

                    match seedPlan with
                    | Some sp ->
                        for cte in sp.Ctes do
                            if not (fullPlan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                                fullPlan.Ctes.Add(cte)
                    | None -> ()

                    let spq =
                        SqlPushdownQueryable<'T>(
                            prov,
                            Expression.Constant(null, typeof<IQueryable<'T>>),
                            prov.Connector,
                            prov.BaseSql,
                            prov.BaseParameters,
                            fullPlan
                        )

                    spq.Intersect(other) :> System.Linq.IQueryable<'T>
                | None -> System.Linq.Queryable.Intersect(this, other)

        member this.Except(other: System.Linq.IQueryable<'T>) : System.Linq.IQueryable<'T> =
            match box this with
            | :? SqlPushdownQueryable<'T> as s1 -> applySpqExcept s1 other :> System.Linq.IQueryable<'T>
            | _ ->
                match ExpressionPushdownTranslator.tryFindPushdownSource this.Expression with
                | Some(sourceIq, seedPlan) ->
                    let prov = sourceIq.Provider :?> SqlPushdownQueryProvider
                    let fullPlan = prov.ExtractPlan(this.Expression, ?seedPlan = seedPlan)

                    match seedPlan with
                    | Some sp ->
                        for cte in sp.Ctes do
                            if not (fullPlan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                                fullPlan.Ctes.Add(cte)
                    | None -> ()

                    let spq =
                        SqlPushdownQueryable<'T>(
                            prov,
                            Expression.Constant(null, typeof<IQueryable<'T>>),
                            prov.Connector,
                            prov.BaseSql,
                            prov.BaseParameters,
                            fullPlan
                        )

                    spq.Except(other) :> System.Linq.IQueryable<'T>
                | None -> System.Linq.Queryable.Except(this, other)

        member this.WithCte(cteQuery: System.Linq.IQueryable<'U>, ?isRecursive: bool) : System.Linq.IQueryable<'T> =
            match box this with
            | :? SqlPushdownQueryable<'T> as spq ->
                applySpqWithCte spq cteQuery isRecursive :> System.Linq.IQueryable<'T>
            | _ ->
                match SqlPushdownQuery.tryFindPushdownSource this.Expression with
                | Some(sourceIq, Some plan) ->
                    match box sourceIq with
                    | :? ISqlPushdownQueryable as spq ->
                        let autoName = $"cte_{plan.Ctes.Count + 1}"
                        let newPlan = plan.Clone()
                        let (subSql, subParams) = SqlPushdownQuery.extractSqlWithParameters cteQuery
                        let mutable remappedSql = subSql

                        for (pName, pVal) in subParams do
                            let uniqueName = $"@p{newPlan.Parameters.Count}"
                            remappedSql <- remappedSql.Replace(pName, uniqueName)
                            newPlan.Parameters.Add(uniqueName, pVal)

                        newPlan.Ctes.Add(
                            {
                                Name = autoName
                                SubquerySql = remappedSql
                                IsRecursive = defaultArg isRecursive false
                                SourceQuery = Some(box cteQuery)
                            }
                        )

                        SqlPushdownQueryable<'T>(sourceIq.Provider, this.Expression, spq.Connector, "", [], newPlan)
                        :> System.Linq.IQueryable<'T>
                    | _ -> this
                | _ -> this

        member this.WithCte
            (name: string, cteQuery: System.Linq.IQueryable<'U>, ?isRecursive: bool)
            : System.Linq.IQueryable<'T> =
            match box this with
            | :? SqlPushdownQueryable<'T> as spq ->
                applySpqWithCteNamed spq name cteQuery isRecursive :> System.Linq.IQueryable<'T>
            | _ ->
                match SqlPushdownQuery.tryFindPushdownSource this.Expression with
                | Some(sourceIq, Some plan) ->
                    match box sourceIq with
                    | :? ISqlPushdownQueryable as spq ->
                        let newPlan = plan.Clone()
                        let (subSql, subParams) = SqlPushdownQuery.extractSqlWithParameters cteQuery
                        let mutable remappedSql = subSql

                        for (pName, pVal) in subParams do
                            let uniqueName = $"@p{newPlan.Parameters.Count}"
                            remappedSql <- remappedSql.Replace(pName, uniqueName)
                            newPlan.Parameters.Add(uniqueName, pVal)

                        newPlan.Ctes.Add(
                            {
                                Name = name
                                SubquerySql = remappedSql
                                IsRecursive = defaultArg isRecursive false
                                SourceQuery = Some(box cteQuery)
                            }
                        )

                        SqlPushdownQueryable<'T>(sourceIq.Provider, this.Expression, spq.Connector, "", [], newPlan)
                        :> System.Linq.IQueryable<'T>
                    | _ -> this
                | _ -> this
