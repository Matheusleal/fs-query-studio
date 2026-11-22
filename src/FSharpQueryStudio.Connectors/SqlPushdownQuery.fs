namespace FSharpQueryStudio.Connectors

open System
open System.Collections
open System.Collections.Generic
open System.Collections.Concurrent
open System.Data
open System.Globalization
open System.Linq
open System.Linq.Expressions
open System.Reflection
open Microsoft.FSharp.Reflection
open FSharpQueryStudio.Protocol
open Serilog


module LinqInMemoryFallback =
    let rec evaluate (expr: Expression) : obj =
        match expr with
        | null -> null
        | :? ConstantExpression as c -> c.Value
        | :? MethodCallExpression as mc when mc.Method.DeclaringType = typeof<Queryable> ->
            let evaluatedArgs =
                [|
                    for a in mc.Arguments do
                        match a with
                        | :? UnaryExpression as u when u.NodeType = ExpressionType.Quote ->
                            match u.Operand with
                            | :? LambdaExpression as l -> box (l.Compile())
                            | other -> evaluate other
                        | :? LambdaExpression as l -> box (l.Compile())
                        | other -> evaluate other
                |]

            let methodGenericArgs = mc.Method.GetGenericArguments()
            let enumMethods = typeof<Enumerable>.GetMethods()

            let targetMethod =
                enumMethods
                |> Array.find (fun m ->
                    if
                        m.Name <> mc.Method.Name
                        || not m.IsGenericMethod
                        || m.GetGenericArguments().Length <> methodGenericArgs.Length
                        || m.GetParameters().Length <> evaluatedArgs.Length
                    then
                        false
                    else
                        try
                            let candidate = m.MakeGenericMethod(methodGenericArgs)
                            let pars = candidate.GetParameters()

                            Array.zip pars evaluatedArgs
                            |> Array.forall (fun (p, a) ->
                                if isNull a then
                                    true
                                else
                                    let argType = a.GetType()

                                    if p.ParameterType.IsAssignableFrom(argType) then
                                        true
                                    elif
                                        typeof<Delegate>.IsAssignableFrom(argType)
                                        && typeof<Delegate>.IsAssignableFrom(p.ParameterType)
                                    then
                                        let aInvoke = argType.GetMethod("Invoke")
                                        let pInvoke = p.ParameterType.GetMethod("Invoke")

                                        if not (isNull aInvoke) && not (isNull pInvoke) then
                                            aInvoke.GetParameters().Length = pInvoke.GetParameters().Length
                                        else
                                            true
                                    else
                                        false)
                        with _ ->
                            false)

            let specialized = targetMethod.MakeGenericMethod(methodGenericArgs)
            specialized.Invoke(null, evaluatedArgs)
        | _ ->
            let lambda = Expression.Lambda(expr)
            let fn = lambda.Compile()
            fn.DynamicInvoke()

/// LINQ Query Provider that inspects expression trees and executes parameterized SQL pushdown queries.
type SqlPushdownQueryProvider(connector: IQueryableConnector, baseSql: string, baseParameters: (string * obj) list) =
    static do
        try
            let t =
                typeof<PushdownQueryPlan>.Assembly.GetType("FSharpQueryStudio.Connectors.SqlPushdownQuery")

            if not (isNull t) then
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(t.TypeHandle)
        with _ ->
            ()

    let dialect = SqlDialect.fromConnectorToDialect (connector :> IConnector)

    member _.Connector = connector
    member _.BaseSql = baseSql
    member _.BaseParameters = baseParameters
    member _.Dialect = dialect

    member this.ExtractPlan(expression: Expression, ?seedPlan: PushdownQueryPlan) : PushdownQueryPlan =
        match seedPlan with
        | Some sp ->
            let cloned = sp.Clone()
            ExpressionPushdownTranslator.mergeExpressionIntoPlan expression cloned dialect
            cloned
        | None ->
            let foundSeed =
                match ExpressionPushdownTranslator.tryFindPushdownSource expression with
                | Some(_, Some p) -> Some p
                | _ -> None

            match foundSeed with
            | Some sp ->
                let cloned = sp.Clone()
                ExpressionPushdownTranslator.mergeExpressionIntoPlan expression cloned dialect
                cloned
            | None -> ExpressionPushdownTranslator.extractPlanFromExpression expression baseSql baseParameters dialect

    member this.ToSqlWithParameters
        (expression: Expression, ?seedPlan: PushdownQueryPlan)
        : string * (string * obj) list =
        let plan = this.ExtractPlan(expression, ?seedPlan = seedPlan)
        let sql = plan.BuildSql(dialect)
        let allParams = (plan.BaseParameters @ (plan.Parameters |> List.ofSeq))
        sql, allParams

    member this.ToRawSql(expression: Expression, ?seedPlan: PushdownQueryPlan) : string =
        let sql, allParams = this.ToSqlWithParameters(expression, ?seedPlan = seedPlan)
        SqlPushdownFormatting.formatSqlAndParameters sql allParams

    member this.ExecutePlanOrStream<'T>(expression: Expression, ?seedPlan: PushdownQueryPlan) : 'T seq =
        let isJoin = ExpressionPushdownTranslator.hasJoin expression

        let canPushdown =
            if isJoin then
                ExpressionPushdownTranslator.canPushdownAllJoins expression connector
            else
                true

        if isJoin && not canPushdown then
            let res = LinqInMemoryFallback.evaluate expression
            res :?> IEnumerable<'T>
        else
            let plan =
                match seedPlan with
                | Some sp ->
                    let cloned = sp.Clone()
                    ExpressionPushdownTranslator.mergeExpressionIntoPlan expression cloned dialect
                    cloned
                | None
                | _ -> ExpressionPushdownTranslator.extractPlanFromExpression expression baseSql baseParameters dialect

            let sql = plan.BuildSql(dialect)
            let allParams = (plan.BaseParameters @ (plan.Parameters |> List.ofSeq))

            let rows: 'T seq =
                match connector with
                | :? IStreamingQueryableConnector as streaming ->
                    streaming.ExecuteReaderStreamWithColumns<'T>
                        sql
                        allParams
                        CompiledRecordMapper.getCompiledRecordMapper<'T>
                | _ ->
                    let res = connector.ExecuteQuery sql allParams

                    seq {
                        for row in res.Rows do
                            let targetType = typeof<'T>

                            if FSharpType.IsRecord(targetType) then
                                let fields = FSharpType.GetRecordFields(targetType)
                                let ctor = FSharpValue.PreComputeRecordConstructor(targetType)
                                let args = Array.zeroCreate fields.Length

                                for i in 0 .. fields.Length - 1 do
                                    let v = if i < row.Length then row.[i] else null
                                    args.[i] <- RecordReaders.ConvertValue v fields.[i].PropertyType

                                yield unbox<'T>(ctor args)
                            else if row.Length > 0 then
                                yield unbox<'T>(RecordReaders.ConvertValue row.[0] targetType)
                    }

            if plan.FallbackPredicates.Count > 0 then
                rows
                |> Seq.filter (fun item -> plan.FallbackPredicates |> Seq.forall (fun p -> p (box item)))
            else
                rows

    member this.ExecuteStream<'T>(expression: Expression) : 'T seq =
        this.ExecutePlanOrStream<'T>(expression)

    member this.ExecuteScalarWithPlan<'TResult>(expression: Expression, plan: PushdownQueryPlan) : 'TResult =
        let initialProjection = plan.Projection

        if
            not (isNull expression)
            && (match expression with
                | :? ConstantExpression as c -> isNull c.Value
                | _ -> false)
        then
            ()
        elif not (isNull expression) then
            ExpressionPushdownTranslator.mergeExpressionIntoPlan expression plan dialect

        if initialProjection.IsSome then
            match initialProjection.Value.Trim().ToUpperInvariant() with
            | p when
                p.StartsWith("COUNT(")
                || p.StartsWith("SUM(")
                || p.StartsWith("MIN(")
                || p.StartsWith("MAX(")
                || p.StartsWith("AVG(")
                ->
                match plan.Projection with
                | Some newP when
                    not (
                        newP.Trim().ToUpperInvariant().StartsWith("COUNT(")
                        || newP.Trim().ToUpperInvariant().StartsWith("SUM(")
                        || newP.Trim().ToUpperInvariant().StartsWith("MIN(")
                        || newP.Trim().ToUpperInvariant().StartsWith("MAX(")
                        || newP.Trim().ToUpperInvariant().StartsWith("AVG(")
                    )
                    ->
                    plan.Projection <- initialProjection
                | _ -> ()
            | _ -> ()

        let sql = plan.BuildSql(dialect)
        let allParams = (plan.BaseParameters @ (plan.Parameters |> List.ofSeq))
        let res = connector.ExecuteQuery sql allParams
        let firstRow = res.Rows |> Seq.tryHead

        match firstRow with
        | Some row when row.Length > 0 && not (isNull row.[0]) && not (Convert.IsDBNull(row.[0])) ->
            let targetType = typeof<'TResult>

            if FSharpType.IsRecord(targetType) then
                let fields = FSharpType.GetRecordFields(targetType)
                let ctor = FSharpValue.PreComputeRecordConstructor(targetType)
                let args = Array.zeroCreate fields.Length

                for i in 0 .. fields.Length - 1 do
                    let v = if i < row.Length then row.[i] else null
                    args.[i] <- RecordReaders.ConvertValue v fields.[i].PropertyType

                unbox<'TResult>(ctor args)
            else
                unbox<'TResult>(RecordReaders.ConvertValue row.[0] targetType)
        | _ -> Unchecked.defaultof<'TResult>

    member this.ExecuteScalar<'TResult>(expression: Expression, ?seedPlan: PushdownQueryPlan) : 'TResult =
        let plan =
            match seedPlan with
            | Some sp ->
                let cloned = sp.Clone()
                ExpressionPushdownTranslator.mergeExpressionIntoPlan expression cloned dialect
                cloned
            | None
            | _ -> ExpressionPushdownTranslator.extractPlanFromExpression expression baseSql baseParameters dialect

        this.ExecuteScalarWithPlan<'TResult>(null, plan)

    interface IQueryProvider with
        member this.CreateQuery(expression: Expression) : IQueryable =
            let elemType =
                let t = expression.Type

                if t.IsGenericType && typeof<IEnumerable>.IsAssignableFrom(t) then
                    t.GetGenericArguments().[0]
                else
                    t

            let seedPlan =
                match ExpressionPushdownTranslator.tryFindPushdownSource expression with
                | Some(_, Some p) -> Some p
                | _ -> None

            let plan =
                match seedPlan with
                | Some sp -> sp.Clone()
                | None -> PushdownQueryPlan.Create(baseSql, baseParameters, baseRowType = elemType, dialect = dialect)

            plan.BaseRowType <- Some elemType

            Activator.CreateInstance(
                typedefof<SqlPushdownQueryable<_>>.MakeGenericType(elemType),
                this,
                expression,
                connector,
                baseSql,
                baseParameters,
                plan
            )
            :?> IQueryable

        member this.CreateQuery<'TElement>(expression: Expression) : IQueryable<'TElement> =
            let seedPlan =
                match ExpressionPushdownTranslator.tryFindPushdownSource expression with
                | Some(_, Some p) -> Some p
                | _ -> None

            let plan =
                match seedPlan with
                | Some sp -> sp.Clone()
                | None ->
                    PushdownQueryPlan.Create(
                        baseSql,
                        baseParameters,
                        baseRowType = typeof<'TElement>,
                        dialect = dialect
                    )

            plan.BaseRowType <- Some typeof<'TElement>

            SqlPushdownQueryable<'TElement>(this, expression, connector, baseSql, baseParameters, plan)
            :> IQueryable<'TElement>

        member this.Execute(expression: Expression) : obj =
            let seedPlan =
                match ExpressionPushdownTranslator.tryFindPushdownSource expression with
                | Some(_, Some p) -> Some p
                | _ -> None

            this.ExecuteScalar<obj>(expression, ?seedPlan = seedPlan)

        member this.Execute<'TResult>(expression: Expression) : 'TResult =
            let targetType = typeof<'TResult>

            if typeof<IEnumerable>.IsAssignableFrom(targetType) && targetType <> typeof<string> then
                let elemType =
                    if targetType.IsGenericType then
                        targetType.GetGenericArguments().[0]
                    elif targetType.IsArray then
                        targetType.GetElementType()
                    else
                        typeof<obj>

                let method =
                    typeof<SqlPushdownQueryProvider>.GetMethod("ExecuteStream").MakeGenericMethod([| elemType |])

                method.Invoke(this, [| expression |]) :?> 'TResult
            else
                let seedPlan =
                    match ExpressionPushdownTranslator.tryFindPushdownSource expression with
                    | Some(_, Some p) -> Some p
                    | _ -> None

                this.ExecuteScalar<'TResult>(expression, ?seedPlan = seedPlan)

/// Strongly-typed IQueryable that captures LINQ expressions and provides pushdown filtering, paging, and sorting.
and SqlPushdownQueryable<'T>
    (
        provider: IQueryProvider,
        expression: Expression,
        connector: IQueryableConnector,
        baseSql: string,
        baseParameters: (string * obj) list,
        plan: PushdownQueryPlan
    ) =
    let dialect =
        match box provider with
        | :? SqlPushdownQueryProvider as spp -> spp.Dialect
        | _ -> defaultArg plan.Dialect (SqlDialect.fromConnectorToDialect (connector :> IConnector))

    new(connector: IQueryableConnector, baseSql: string, ?baseParameters: (string * obj) list) =
        let p = defaultArg baseParameters []
        let provider = SqlPushdownQueryProvider(connector, baseSql, p)

        let plan =
            PushdownQueryPlan.Create(baseSql, p, baseRowType = typeof<'T>, dialect = provider.Dialect)

        SqlPushdownQueryable<'T>(
            provider,
            Expression.Constant(null, typeof<IQueryable<'T>>),
            connector,
            baseSql,
            p,
            plan
        )

    member _.Plan = plan
    member _.Connector = connector
    member _.BaseSql = baseSql
    member _.BaseParameters = baseParameters
    member _.Dialect = dialect

    /// Appends a raw parameterized WHERE clause directly to the pushdown query.
    member this.Where(filterSql: string, ?parameters: (string * obj) list) : SqlPushdownQueryable<'T> =
        let p = defaultArg parameters []
        let newPlan = plan.Clone()
        newPlan.WhereClauses.Add($"({filterSql})")

        for (k, v) in p do
            newPlan.Parameters.Add(k, v)

        SqlPushdownQueryable<'T>(
            provider,
            Expression.Constant(null, typeof<IQueryable<'T>>),
            connector,
            baseSql,
            baseParameters,
            newPlan
        )

    /// Configures 1-based pagination directly pushed down to database LIMIT/OFFSET or SQL Server OFFSET/FETCH.
    member this.Page(page: int, pageSize: int) : SqlPushdownQueryable<'T> =
        let validPage = if page < 1 then 1 else page
        let validSize = if pageSize < 1 then 10 else pageSize
        let skip = (validPage - 1) * validSize
        let newPlan = plan.Clone()
        newPlan.Skip <- Some skip
        newPlan.Take <- Some validSize

        SqlPushdownQueryable<'T>(
            provider,
            Expression.Constant(null, typeof<IQueryable<'T>>),
            connector,
            baseSql,
            baseParameters,
            newPlan
        )

    /// Appends one or more GROUP BY columns directly (e.g. "Category", "_t0.ProductId").
    member this.GroupBy(columns: string) : SqlPushdownQueryable<'T> =
        let newPlan = plan.Clone()

        let parts =
            columns.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)

        for p in parts do
            if not (newPlan.GroupBy.Contains p) then
                newPlan.GroupBy.Add p

        SqlPushdownQueryable<'T>(
            provider,
            Expression.Constant(null, typeof<IQueryable<'T>>),
            connector,
            baseSql,
            baseParameters,
            newPlan
        )

    /// Appends a raw HAVING clause for filtering aggregated results.
    member this.Having(havingSql: string, ?parameters: (string * obj) list) : SqlPushdownQueryable<'T> =
        let p = defaultArg parameters []
        let newPlan = plan.Clone()
        newPlan.Having.Add($"({havingSql})")

        for (k, v) in p do
            newPlan.Parameters.Add(k, v)

        SqlPushdownQueryable<'T>(
            provider,
            Expression.Constant(null, typeof<IQueryable<'T>>),
            connector,
            baseSql,
            baseParameters,
            newPlan
        )

    /// Appends a CROSS APPLY join (transpiled to CROSS JOIN LATERAL on PostgreSQL, CROSS JOIN on SQLite).
    member this.CrossApply(subquerySql: string, alias: string) : SqlPushdownQueryable<'T> =
        let newPlan = plan.Clone()

        newPlan.Joins.Add(
            {
                JoinType = "CROSS APPLY"
                SourceTable = subquerySql
                Alias = alias
                OnClause = ""
                RowType = None
            }
        )

        SqlPushdownQueryable<'T>(
            provider,
            Expression.Constant(null, typeof<IQueryable<'T>>),
            connector,
            baseSql,
            baseParameters,
            newPlan
        )

    /// Appends an OUTER APPLY join (transpiled to LEFT JOIN LATERAL ... ON true on PostgreSQL, LEFT JOIN on SQLite).
    member this.OuterApply(subquerySql: string, alias: string, ?onClause: string) : SqlPushdownQueryable<'T> =
        let newPlan = plan.Clone()

        newPlan.Joins.Add(
            {
                JoinType = "OUTER APPLY"
                SourceTable = subquerySql
                Alias = alias
                OnClause = defaultArg onClause "1=1"
                RowType = None
            }
        )

        SqlPushdownQueryable<'T>(
            provider,
            Expression.Constant(null, typeof<IQueryable<'T>>),
            connector,
            baseSql,
            baseParameters,
            newPlan
        )

    /// Appends a standard CROSS JOIN clause without an ON predicate.
    member this.CrossJoin(tableOrSubquery: string, alias: string) : SqlPushdownQueryable<'T> =
        let newPlan = plan.Clone()

        newPlan.Joins.Add(
            {
                JoinType = "CROSS JOIN"
                SourceTable = tableOrSubquery
                Alias = alias
                OnClause = ""
                RowType = None
            }
        )

        SqlPushdownQueryable<'T>(
            provider,
            Expression.Constant(null, typeof<IQueryable<'T>>),
            connector,
            baseSql,
            baseParameters,
            newPlan
        )

    /// Appends a FULL OUTER JOIN clause with an ON predicate.
    member this.FullOuterJoin(tableOrSubquery: string, alias: string, onClause: string) : SqlPushdownQueryable<'T> =
        let newPlan = plan.Clone()

        newPlan.Joins.Add(
            {
                JoinType = "FULL OUTER JOIN"
                SourceTable = tableOrSubquery
                Alias = alias
                OnClause = onClause
                RowType = None
            }
        )

        SqlPushdownQueryable<'T>(
            provider,
            Expression.Constant(null, typeof<IQueryable<'T>>),
            connector,
            baseSql,
            baseParameters,
            newPlan
        )

    /// Appends a RIGHT OUTER JOIN clause with an ON predicate.
    member this.RightOuterJoin(tableOrSubquery: string, alias: string, onClause: string) : SqlPushdownQueryable<'T> =
        let newPlan = plan.Clone()

        newPlan.Joins.Add(
            {
                JoinType = "RIGHT OUTER JOIN"
                SourceTable = tableOrSubquery
                Alias = alias
                OnClause = onClause
                RowType = None
            }
        )

        SqlPushdownQueryable<'T>(
            provider,
            Expression.Constant(null, typeof<IQueryable<'T>>),
            connector,
            baseSql,
            baseParameters,
            newPlan
        )

    /// Overrides the SELECT projection columns directly (e.g. "Id, Name, COUNT(*) OVER() AS Total").
    member this.SelectProjection(columnsSql: string) : SqlPushdownQueryable<'T> =
        let newPlan = plan.Clone()
        newPlan.Projection <- Some columnsSql

        SqlPushdownQueryable<'T>(
            provider,
            Expression.Constant(null, typeof<IQueryable<'T>>),
            connector,
            baseSql,
            baseParameters,
            newPlan
        )

    /// Appends a WHERE EXISTS subquery clause.
    member this.WhereExists(subquerySql: string, ?parameters: (string * obj) list) : SqlPushdownQueryable<'T> =
        let p = defaultArg parameters []
        let newPlan = plan.Clone()
        newPlan.WhereClauses.Add($"EXISTS ({subquerySql})")

        for (k, v) in p do
            newPlan.Parameters.Add(k, v)

        SqlPushdownQueryable<'T>(
            provider,
            Expression.Constant(null, typeof<IQueryable<'T>>),
            connector,
            baseSql,
            baseParameters,
            newPlan
        )

    /// Appends a WHERE NOT EXISTS subquery clause.
    member this.WhereNotExists(subquerySql: string, ?parameters: (string * obj) list) : SqlPushdownQueryable<'T> =
        let p = defaultArg parameters []
        let newPlan = plan.Clone()
        newPlan.WhereClauses.Add($"NOT EXISTS ({subquerySql})")

        for (k, v) in p do
            newPlan.Parameters.Add(k, v)

        SqlPushdownQueryable<'T>(
            provider,
            Expression.Constant(null, typeof<IQueryable<'T>>),
            connector,
            baseSql,
            baseParameters,
            newPlan
        )

    /// Appends a WHERE column IN (subquery) clause.
    member this.WhereIn
        (column: string, subquerySql: string, ?parameters: (string * obj) list)
        : SqlPushdownQueryable<'T> =
        let p = defaultArg parameters []
        let newPlan = plan.Clone()
        newPlan.WhereClauses.Add($"({column} IN ({subquerySql}))")

        for (k, v) in p do
            newPlan.Parameters.Add(k, v)

        SqlPushdownQueryable<'T>(
            provider,
            Expression.Constant(null, typeof<IQueryable<'T>>),
            connector,
            baseSql,
            baseParameters,
            newPlan
        )

    /// Appends a WHERE column IN (val1, val2, ...) clause with safe parameterized values.
    member this.WhereIn(column: string, values: 'V seq) : SqlPushdownQueryable<'T> =
        let valList = values |> Seq.map box |> List.ofSeq

        if valList.IsEmpty then
            let newPlan = plan.Clone()
            newPlan.WhereClauses.Add("1 = 0")

            SqlPushdownQueryable<'T>(
                provider,
                Expression.Constant(null, typeof<IQueryable<'T>>),
                connector,
                baseSql,
                baseParameters,
                newPlan
            )
        else
            let newPlan = plan.Clone()
            let paramNames = ResizeArray<string>()

            for i, v in valList |> List.indexed do
                let pName = $"@in_p{newPlan.Parameters.Count}_{i}"
                paramNames.Add(pName)
                newPlan.Parameters.Add(pName, v)

            let inList = String.concat ", " paramNames
            newPlan.WhereClauses.Add($"({column} IN ({inList}))")

            SqlPushdownQueryable<'T>(
                provider,
                Expression.Constant(null, typeof<IQueryable<'T>>),
                connector,
                baseSql,
                baseParameters,
                newPlan
            )

    /// Appends a WHERE column NOT IN (subquery) clause.
    member this.WhereNotIn
        (column: string, subquerySql: string, ?parameters: (string * obj) list)
        : SqlPushdownQueryable<'T> =
        let p = defaultArg parameters []
        let newPlan = plan.Clone()
        newPlan.WhereClauses.Add($"({column} NOT IN ({subquerySql}))")

        for (k, v) in p do
            newPlan.Parameters.Add(k, v)

        SqlPushdownQueryable<'T>(
            provider,
            Expression.Constant(null, typeof<IQueryable<'T>>),
            connector,
            baseSql,
            baseParameters,
            newPlan
        )

    /// Appends a WHERE column NOT IN (val1, val2, ...) clause with safe parameterized values.
    member this.WhereNotIn(column: string, values: 'V seq) : SqlPushdownQueryable<'T> =
        let valList = values |> Seq.map box |> List.ofSeq

        if valList.IsEmpty then
            this
        else
            let newPlan = plan.Clone()
            let paramNames = ResizeArray<string>()

            for i, v in valList |> List.indexed do
                let pName = $"@notin_p{newPlan.Parameters.Count}_{i}"
                paramNames.Add(pName)
                newPlan.Parameters.Add(pName, v)

            let inList = String.concat ", " paramNames
            newPlan.WhereClauses.Add($"({column} NOT IN ({inList}))")

            SqlPushdownQueryable<'T>(
                provider,
                Expression.Constant(null, typeof<IQueryable<'T>>),
                connector,
                baseSql,
                baseParameters,
                newPlan
            )

    /// Direct scalar Count pushdown.
    member this.Count() : int =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()
        let dPrefix = if newPlan.IsDistinct then "DISTINCT " else ""
        newPlan.Projection <- Some $"COUNT({dPrefix}*)"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<int>(pExpr, newPlan)

    /// Direct scalar Count of a specific column (counting non-nulls).
    member this.Count(columnName: string) : int =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()

        let qCol = SqlDialect.quoteIdentifier dialect columnName

        let dPrefix = if newPlan.IsDistinct then "DISTINCT " else ""
        newPlan.Projection <- Some $"COUNT({dPrefix}{qCol})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<int>(pExpr, newPlan)

    /// Direct scalar Count of a strongly-typed column selector lambda (e.g. fun p -> p.Id) or predicate (e.g. fun p -> p.Price > 10.0M).
    member this.Count(colSelector: Expression<Func<'T, 'U>>) : int =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()
        let dialect = SqlDialect.fromConnectorToDialect (connector :> IConnector)

        if colSelector.ReturnType = typeof<bool> then
            match
                ExpressionPushdownTranslator.translatePredicate
                    colSelector.Parameters.[0]
                    newPlan
                    dialect
                    colSelector.Body
            with
            | Some pSql -> newPlan.WhereClauses.Add(pSql)
            | None -> ()

            let dPrefix = if newPlan.IsDistinct then "DISTINCT " else ""
            newPlan.Projection <- Some $"COUNT({dPrefix}*)"
        else
            let col =
                ExpressionPushdownTranslator.extractColumnNameWithAlias newPlan colSelector.Body

            let dPrefix = if newPlan.IsDistinct then "DISTINCT " else ""
            newPlan.Projection <- Some $"COUNT({dPrefix}{col})"

        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<int>(pExpr, newPlan)

    /// Direct scalar COUNT(DISTINCT column) pushdown.
    member this.CountDistinct(columnName: string) : int =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()

        let qCol = SqlDialect.quoteIdentifier dialect columnName

        newPlan.Projection <- Some $"COUNT(DISTINCT {qCol})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<int>(pExpr, newPlan)

    /// Direct scalar COUNT(DISTINCT colSelector) pushdown.
    member this.CountDistinct(colSelector: Expression<Func<'T, 'U>>) : int =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()

        let col =
            ExpressionPushdownTranslator.extractColumnNameWithAlias newPlan colSelector.Body

        newPlan.Projection <- Some $"COUNT(DISTINCT {col})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<int>(pExpr, newPlan)

    /// Direct scalar Sum pushdown on a column.
    member this.Sum<'TResult>(columnName: string) : 'TResult =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()

        let qCol = SqlDialect.quoteIdentifier dialect columnName

        newPlan.Projection <- Some $"SUM({qCol})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<'TResult>(pExpr, newPlan)

    /// Direct scalar Sum pushdown on a strongly-typed column selector.
    member this.Sum<'TResult>(colSelector: Expression<Func<'T, 'TResult>>) : 'TResult =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()

        let col =
            ExpressionPushdownTranslator.extractColumnNameWithAlias newPlan colSelector.Body

        newPlan.Projection <- Some $"SUM({col})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<'TResult>(pExpr, newPlan)

    /// Direct scalar Min pushdown on a column.
    member this.Min<'TResult>(columnName: string) : 'TResult =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()

        let qCol = SqlDialect.quoteIdentifier dialect columnName

        newPlan.Projection <- Some $"MIN({qCol})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<'TResult>(pExpr, newPlan)

    /// Direct scalar Min pushdown on a strongly-typed column selector.
    member this.Min<'TResult>(colSelector: Expression<Func<'T, 'TResult>>) : 'TResult =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()

        let col =
            ExpressionPushdownTranslator.extractColumnNameWithAlias newPlan colSelector.Body

        newPlan.Projection <- Some $"MIN({col})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<'TResult>(pExpr, newPlan)

    /// Direct scalar Max pushdown on a column.
    member this.Max<'TResult>(columnName: string) : 'TResult =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()

        let qCol = SqlDialect.quoteIdentifier dialect columnName

        newPlan.Projection <- Some $"MAX({qCol})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<'TResult>(pExpr, newPlan)

    /// Direct scalar Max pushdown on a strongly-typed column selector.
    member this.Max<'TResult>(colSelector: Expression<Func<'T, 'TResult>>) : 'TResult =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()

        let col =
            ExpressionPushdownTranslator.extractColumnNameWithAlias newPlan colSelector.Body

        newPlan.Projection <- Some $"MAX({col})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<'TResult>(pExpr, newPlan)

    /// Direct scalar Average pushdown on a column.
    member this.Average<'TResult>(columnName: string) : 'TResult =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()

        let qCol = SqlDialect.quoteIdentifier dialect columnName

        newPlan.Projection <- Some $"AVG({qCol})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<'TResult>(pExpr, newPlan)

    /// Direct scalar Average pushdown on a strongly-typed column selector.
    member this.Average<'TResult>(colSelector: Expression<Func<'T, 'TResult>>) : 'TResult =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()

        let col =
            ExpressionPushdownTranslator.extractColumnNameWithAlias newPlan colSelector.Body

        newPlan.Projection <- Some $"AVG({col})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<'TResult>(pExpr, newPlan)

    /// Extracts the raw SQL query generated for this queryable without executing it against the database.
    member this.ToRawSql() : string =
        let p = provider :?> SqlPushdownQueryProvider
        let expr = (this :> IQueryable<'T>).Expression
        p.ToRawSql(expr, plan)

    /// Extracts the formatted, indented raw SQL query generated for this queryable without executing it against the database.
    member this.ToRawQuery() : string =
        let p = provider :?> SqlPushdownQueryProvider
        let expr = (this :> IQueryable<'T>).Expression
        let sql, allParams = p.ToSqlWithParameters(expr, plan)
        let formatted = SqlPushdownFormatting.formatSql sql
        SqlPushdownFormatting.formatSqlAndParameters formatted allParams

    /// Extracts the raw SQL query and parameter bindings for this queryable without executing it against the database.
    member this.ToSqlWithParameters() : string * (string * obj) list =
        let p = provider :?> SqlPushdownQueryProvider
        let expr = (this :> IQueryable<'T>).Expression
        p.ToSqlWithParameters(expr, plan)

    member private this.ApplySetOperation(op: SetOperation, otherPlan: PushdownQueryPlan) : SqlPushdownQueryable<'T> =
        let p = (this :> IQueryable).Provider :?> SqlPushdownQueryProvider

        let fullThisPlan =
            p.ExtractPlan((this :> IQueryable<'T>).Expression, seedPlan = plan)

        let newPlan = fullThisPlan.Clone()
        let otherForSub = otherPlan.Clone()
        otherForSub.Ctes.Clear()

        let subSql = otherForSub.BuildSql dialect

        let allOtherParams =
            otherForSub.BaseParameters @ (otherForSub.Parameters |> List.ofSeq)

        let pOffset = newPlan.Parameters.Count

        let remappedSubSql =
            System.Text.RegularExpressions.Regex.Replace(
                subSql,
                @"@p(\d+)",
                fun m ->
                    let idx = int m.Groups.[1].Value
                    $"@p{pOffset + idx}"
            )

        for (_, pVal) in allOtherParams do
            newPlan.Parameters.Add($"@p{newPlan.Parameters.Count}", pVal)

        for cte in otherPlan.Ctes do
            if not (newPlan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                let remappedCteSql =
                    System.Text.RegularExpressions.Regex.Replace(
                        cte.SubquerySql,
                        @"@p(\d+)",
                        fun m ->
                            let idx = int m.Groups.[1].Value
                            $"@p{pOffset + idx}"
                    )

                newPlan.Ctes.Add(
                    { cte with
                        SubquerySql = remappedCteSql
                    }
                )

        newPlan.SetOperations.Add(
            {
                Operation = op
                TargetSql = remappedSubSql
            }
        )

        SqlPushdownQueryable<'T>(
            provider,
            Expression.Constant(null, typeof<IQueryable<'T>>),
            connector,
            baseSql,
            baseParameters,
            newPlan
        )

    /// Combines this query with another query using SQL UNION (distinct results).
    member this.Union(other: SqlPushdownQueryable<'T>) : SqlPushdownQueryable<'T> =
        let op = (other :> IQueryable).Provider :?> SqlPushdownQueryProvider

        let fullOtherPlan =
            op.ExtractPlan((other :> IQueryable<'T>).Expression, seedPlan = other.Plan)

        this.ApplySetOperation(SetOperation.Union, fullOtherPlan)

    /// Combines this query with another query using SQL UNION (distinct results).
    member this.Union(other: System.Linq.IQueryable<'T>) : SqlPushdownQueryable<'T> =
        match box other with
        | :? SqlPushdownQueryable<'T> as s -> this.Union(s)
        | _ ->
            match ExpressionPushdownTranslator.tryFindPushdownSource other.Expression with
            | Some(sourceIq, seedPlan) ->
                let prov = sourceIq.Provider :?> SqlPushdownQueryProvider
                let fullOtherPlan = prov.ExtractPlan(other.Expression, ?seedPlan = seedPlan)

                match seedPlan with
                | Some sp ->
                    for cte in sp.Ctes do
                        if not (fullOtherPlan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                            fullOtherPlan.Ctes.Add(cte)
                | None -> ()

                this.ApplySetOperation(SetOperation.Union, fullOtherPlan)
            | None -> failwith "Unsupported queryable for pushdown UNION"

    /// Combines this query with another query using SQL UNION ALL (all rows preserved).
    member this.UnionAll(other: SqlPushdownQueryable<'T>) : SqlPushdownQueryable<'T> =
        let op = (other :> IQueryable).Provider :?> SqlPushdownQueryProvider

        let fullOtherPlan =
            op.ExtractPlan((other :> IQueryable<'T>).Expression, seedPlan = other.Plan)

        this.ApplySetOperation(SetOperation.UnionAll, fullOtherPlan)

    /// Combines this query with another query using SQL UNION ALL (all rows preserved).
    member this.UnionAll(other: System.Linq.IQueryable<'T>) : SqlPushdownQueryable<'T> =
        match box other with
        | :? SqlPushdownQueryable<'T> as s -> this.UnionAll(s)
        | _ ->
            match ExpressionPushdownTranslator.tryFindPushdownSource other.Expression with
            | Some(sourceIq, seedPlan) ->
                let prov = sourceIq.Provider :?> SqlPushdownQueryProvider
                let fullOtherPlan = prov.ExtractPlan(other.Expression, ?seedPlan = seedPlan)

                match seedPlan with
                | Some sp ->
                    for cte in sp.Ctes do
                        if not (fullOtherPlan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                            fullOtherPlan.Ctes.Add(cte)
                | None -> ()

                this.ApplySetOperation(SetOperation.UnionAll, fullOtherPlan)
            | None -> failwith "Unsupported queryable for pushdown UNION ALL"

    /// Combines this query with another query using SQL INTERSECT.
    member this.Intersect(other: SqlPushdownQueryable<'T>) : SqlPushdownQueryable<'T> =
        let op = (other :> IQueryable).Provider :?> SqlPushdownQueryProvider

        let fullOtherPlan =
            op.ExtractPlan((other :> IQueryable<'T>).Expression, seedPlan = other.Plan)

        this.ApplySetOperation(SetOperation.Intersect, fullOtherPlan)

    /// Combines this query with another query using SQL INTERSECT.
    member this.Intersect(other: System.Linq.IQueryable<'T>) : SqlPushdownQueryable<'T> =
        match box other with
        | :? SqlPushdownQueryable<'T> as s -> this.Intersect(s)
        | _ ->
            match ExpressionPushdownTranslator.tryFindPushdownSource other.Expression with
            | Some(sourceIq, seedPlan) ->
                let prov = sourceIq.Provider :?> SqlPushdownQueryProvider
                let fullOtherPlan = prov.ExtractPlan(other.Expression, ?seedPlan = seedPlan)

                match seedPlan with
                | Some sp ->
                    for cte in sp.Ctes do
                        if not (fullOtherPlan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                            fullOtherPlan.Ctes.Add(cte)
                | None -> ()

                this.ApplySetOperation(SetOperation.Intersect, fullOtherPlan)
            | None -> failwith "Unsupported queryable for pushdown INTERSECT"

    /// Combines this query with another query using SQL EXCEPT.
    member this.Except(other: SqlPushdownQueryable<'T>) : SqlPushdownQueryable<'T> =
        let op = (other :> IQueryable).Provider :?> SqlPushdownQueryProvider

        let fullOtherPlan =
            op.ExtractPlan((other :> IQueryable<'T>).Expression, seedPlan = other.Plan)

        this.ApplySetOperation(SetOperation.Except, fullOtherPlan)

    /// Combines this query with another query using SQL EXCEPT.
    member this.Except(other: System.Linq.IQueryable<'T>) : SqlPushdownQueryable<'T> =
        match box other with
        | :? SqlPushdownQueryable<'T> as s -> this.Except(s)
        | _ ->
            match ExpressionPushdownTranslator.tryFindPushdownSource other.Expression with
            | Some(sourceIq, seedPlan) ->
                let prov = sourceIq.Provider :?> SqlPushdownQueryProvider
                let fullOtherPlan = prov.ExtractPlan(other.Expression, ?seedPlan = seedPlan)

                match seedPlan with
                | Some sp ->
                    for cte in sp.Ctes do
                        if not (fullOtherPlan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                            fullOtherPlan.Ctes.Add(cte)
                | None -> ()

                this.ApplySetOperation(SetOperation.Except, fullOtherPlan)
            | None -> failwith "Unsupported queryable for pushdown EXCEPT"

    /// Combines this query with another query using SQL UNION ALL.
    member this.Concat(other: SqlPushdownQueryable<'T>) : SqlPushdownQueryable<'T> = this.UnionAll(other)

    /// Combines this query with another query using SQL UNION ALL.
    member this.Concat(other: System.Linq.IQueryable<'T>) : SqlPushdownQueryable<'T> = this.UnionAll(other)

    /// Prepends a Common Table Expression (WITH cte AS (...)) to this query.
    member this.WithCte
        (name: string, cteQuery: System.Linq.IQueryable<'U>, ?isRecursive: bool)
        : SqlPushdownQueryable<'T> =
        let p = (this :> IQueryable).Provider :?> SqlPushdownQueryProvider

        let fullThisPlan =
            p.ExtractPlan((this :> IQueryable<'T>).Expression, seedPlan = plan)

        let newPlan = fullThisPlan.Clone()

        match (cteQuery :> IQueryable).Provider with
        | :? SqlPushdownQueryProvider as cp ->
            let seedPlan =
                match box cteQuery with
                | :? ISqlPushdownQueryable as spq -> spq.Plan
                | _ -> PushdownQueryPlan.Create("", [])

            let fullCtePlan = cp.ExtractPlan(cteQuery.Expression, seedPlan = seedPlan)

            let mutable subSql = fullCtePlan.BuildSql dialect

            for (pName, pVal) in fullCtePlan.Parameters do
                let uniqueName = $"@p{newPlan.Parameters.Count}"
                subSql <- subSql.Replace(pName, uniqueName)
                newPlan.Parameters.Add(uniqueName, pVal)

            newPlan.Ctes.Add(
                {
                    Name = name
                    SubquerySql = subSql
                    IsRecursive = defaultArg isRecursive false
                    SourceQuery = Some(box cteQuery)
                }
            )
        | _ -> ()

        SqlPushdownQueryable<'T>(
            provider,
            Expression.Constant(null, typeof<IQueryable<'T>>),
            connector,
            baseSql,
            baseParameters,
            newPlan
        )

    /// Prepends a Common Table Expression (WITH cte AS (...)) to this query with an auto-generated name.
    member this.WithCte(cteQuery: System.Linq.IQueryable<'U>, ?isRecursive: bool) : SqlPushdownQueryable<'T> =
        let autoName = $"cte_{plan.Ctes.Count + 1}"
        this.WithCte(autoName, cteQuery, ?isRecursive = isRecursive)

    /// Prepends a Common Table Expression (WITH cte AS (...)) to this query.
    member this.WithCte
        (name: string, cteQuery: SqlPushdownQueryable<'U>, ?isRecursive: bool)
        : SqlPushdownQueryable<'T> =
        this.WithCte(name, (cteQuery :> System.Linq.IQueryable<'U>), ?isRecursive = isRecursive)

    /// Prepends a Common Table Expression (WITH cte AS (...)) to this query with an auto-generated name.
    member this.WithCte(cteQuery: SqlPushdownQueryable<'U>, ?isRecursive: bool) : SqlPushdownQueryable<'T> =
        this.WithCte((cteQuery :> System.Linq.IQueryable<'U>), ?isRecursive = isRecursive)

    /// Prepends a Recursive Common Table Expression (WITH RECURSIVE cte AS (...)) to this query.
    member this.WithRecursiveCte(name: string, cteQuery: System.Linq.IQueryable<'U>) : SqlPushdownQueryable<'T> =
        this.WithCte(name, cteQuery, isRecursive = true)

    /// Prepends a Recursive Common Table Expression (WITH RECURSIVE cte AS (...)) to this query with an auto-generated name.
    member this.WithRecursiveCte(cteQuery: System.Linq.IQueryable<'U>) : SqlPushdownQueryable<'T> =
        this.WithCte(cteQuery, isRecursive = true)

    /// Prepends a Recursive Common Table Expression (WITH RECURSIVE cte AS (...)) to this query.
    member this.WithRecursiveCte(name: string, cteQuery: SqlPushdownQueryable<'U>) : SqlPushdownQueryable<'T> =
        this.WithCte(name, (cteQuery :> System.Linq.IQueryable<'U>), isRecursive = true)

    /// Prepends a Recursive Common Table Expression (WITH RECURSIVE cte AS (...)) to this query with an auto-generated name.
    member this.WithRecursiveCte(cteQuery: SqlPushdownQueryable<'U>) : SqlPushdownQueryable<'T> =
        this.WithCte((cteQuery :> System.Linq.IQueryable<'U>), isRecursive = true)

    /// Prepends a Common Table Expression (WITH cte AS (...)) with raw SQL and an auto-generated name.
    member this.WithCte
        (subquerySql: string, ?parameters: (string * obj) list, ?isRecursive: bool)
        : SqlPushdownQueryable<'T> =
        let autoName = $"cte_{plan.Ctes.Count + 1}"
        this.WithCte(autoName, subquerySql, ?parameters = parameters, ?isRecursive = isRecursive)

    /// Prepends a Common Table Expression (WITH cte AS (...)) with raw SQL.
    member this.WithCte
        (name: string, subquerySql: string, ?parameters: (string * obj) list, ?isRecursive: bool)
        : SqlPushdownQueryable<'T> =
        let newPlan = plan.Clone()
        let p = defaultArg parameters []
        let mutable remappedSql = subquerySql

        for (k, v) in p do
            let uniqueName = $"@p{newPlan.Parameters.Count}"
            remappedSql <- remappedSql.Replace(k, uniqueName)
            newPlan.Parameters.Add(uniqueName, v)

        newPlan.Ctes.Add(
            {
                Name = name
                SubquerySql = remappedSql
                IsRecursive = defaultArg isRecursive false
                SourceQuery = None
            }
        )

        SqlPushdownQueryable<'T>(
            provider,
            Expression.Constant(null, typeof<IQueryable<'T>>),
            connector,
            baseSql,
            baseParameters,
            newPlan
        )

    /// Returns a structured query execution plan report for diagnostics.
    member this.Explain() : ExecutionPlanReport =
        let dialect = defaultArg plan.Dialect SqlDialect.Sqlite
        let formatted = plan.BuildSql(dialect)
        let parms = plan.BaseParameters @ (plan.Parameters |> List.ofSeq)

        let planText =
            try
                dialect.Dialect.FormatExplain(connector, formatted, parms)
            with ex ->
                $"-- EXPLAIN diagnostic report:\n--   Engine: {dialect}\n--   Status: {ex.Message}"

        {
            Dialect = dialect
            PlanText = planText
            FormattedSql = formatted
            Parameters = parms
        }

    interface ISqlPushdownQueryable with
        member _.Plan = plan
        member _.Connector = connector
        member this.Explain() = this.Explain()

    interface IQueryable<'T> with
        member _.Provider = provider

        member this.Expression =
            if
                isNull expression
                || (match expression with
                    | :? ConstantExpression as c -> isNull c.Value
                    | _ -> false)
            then
                Expression.Constant(this, typeof<IQueryable<'T>>) :> Expression
            else
                expression

        member _.ElementType = typeof<'T>

    interface IOrderedQueryable<'T>

    interface IEnumerable<'T> with
        member this.GetEnumerator() : IEnumerator<'T> =
            let p = provider :?> SqlPushdownQueryProvider
            let expr = (this :> IQueryable<'T>).Expression
            let seqResult = p.ExecutePlanOrStream<'T>(expr, plan)
            seqResult.GetEnumerator()

    interface IEnumerable with
        member this.GetEnumerator() =
            (this :> IEnumerable<'T>).GetEnumerator() :> IEnumerator

module SqlPushdownQuery =
    open SqlPushdownFormatting
    open System.Reflection

    let rec tryFindPushdownSource (expr: Expression) : (IQueryable * PushdownQueryPlan option) option =
        match expr with
        | null -> None
        | :? ConstantExpression as c when not (isNull c.Value) ->
            match c.Value with
            | :? IQueryable as iq when
                not (isNull iq.Provider)
                && iq.Provider.GetType() = typeof<SqlPushdownQueryProvider>
                ->
                let t = c.Value.GetType()
                let planProp = t.GetProperty("Plan")

                let plan =
                    if isNull planProp then
                        None
                    else
                        Some(planProp.GetValue(c.Value) :?> PushdownQueryPlan)

                Some(iq, plan)
            | other when not (isNull other) ->
                let fields =
                    other.GetType().GetFields(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance)

                fields
                |> Seq.tryPick (fun f ->
                    try
                        match f.GetValue(other) with
                        | :? IQueryable as iq when
                            not (isNull iq.Provider)
                            && iq.Provider.GetType() = typeof<SqlPushdownQueryProvider>
                            ->
                            let planProp = iq.GetType().GetProperty("Plan")

                            let plan =
                                if isNull planProp then
                                    None
                                else
                                    Some(planProp.GetValue(iq) :?> PushdownQueryPlan)

                            Some(iq, plan)
                        | _ -> None
                    with _ ->
                        None)
            | _ -> None
        | :? MethodCallExpression as m ->
            m.Arguments
            |> Seq.tryPick tryFindPushdownSource
            |> Option.orElseWith (fun () -> tryFindPushdownSource m.Object)
        | :? LambdaExpression as l -> tryFindPushdownSource l.Body
        | :? MemberExpression as m ->
            tryFindPushdownSource m.Expression
            |> Option.orElseWith (fun () ->
                try
                    if isNull m.Expression && m.Member :? PropertyInfo then
                        let p = m.Member :?> PropertyInfo

                        match p.GetValue(null) with
                        | :? IQueryable as iq when
                            not (isNull iq.Provider)
                            && iq.Provider.GetType() = typeof<SqlPushdownQueryProvider>
                            ->
                            let planProp = iq.GetType().GetProperty("Plan")

                            let plan =
                                if isNull planProp then
                                    None
                                else
                                    Some(planProp.GetValue(iq) :?> PushdownQueryPlan)

                            Some(iq, plan)
                        | _ -> None
                    else
                        None
                with _ ->
                    None)
        | :? InvocationExpression as inv ->
            tryFindPushdownSource inv.Expression
            |> Option.orElseWith (fun () -> inv.Arguments |> Seq.tryPick tryFindPushdownSource)
        | :? UnaryExpression as u -> tryFindPushdownSource u.Operand
        | :? BinaryExpression as b ->
            tryFindPushdownSource b.Left
            |> Option.orElseWith (fun () -> tryFindPushdownSource b.Right)
        | _ -> None

    let extractSqlWithParameters (query: obj) : string * (string * obj) list =
        match query with
        | null -> ("", [])
        | :? string as s -> (s, [])
        | :? PushdownQueryPlan as plan ->
            let dialect = defaultArg plan.Dialect SqlDialect.Sqlite
            let sql = plan.BuildSql(dialect)
            let parms = plan.BaseParameters @ (plan.Parameters |> List.ofSeq)
            (sql, parms)
        | :? IQueryable as iq ->
            if
                not (isNull iq.Provider)
                && iq.Provider.GetType() = typeof<SqlPushdownQueryProvider>
            then
                let provider = iq.Provider :?> SqlPushdownQueryProvider
                let planProp = iq.GetType().GetProperty("Plan")

                let seedPlan =
                    if isNull planProp then
                        None
                    else
                        Some(planProp.GetValue(iq) :?> PushdownQueryPlan)

                provider.ToSqlWithParameters(iq.Expression, ?seedPlan = seedPlan)
            else
                match tryFindPushdownSource iq.Expression with
                | Some(sourceIq, seedPlan) ->
                    let provider = sourceIq.Provider :?> SqlPushdownQueryProvider
                    provider.ToSqlWithParameters(iq.Expression, ?seedPlan = seedPlan)
                | None
                | _ -> (string iq, [])
        | other ->
            let t = other.GetType()
            let toRawSqlMethod = t.GetMethod("ToRawSql", Type.EmptyTypes)

            if not (isNull toRawSqlMethod) then
                let res = toRawSqlMethod.Invoke(other, [||]) :?> string
                (res, [])
            else
                let toRawQueryMethod = t.GetMethod("ToRawQuery", Type.EmptyTypes)

                if not (isNull toRawQueryMethod) then
                    let res = toRawQueryMethod.Invoke(other, [||]) :?> string
                    (res, [])
                else
                    (string other, [])

    let extractRawSql (query: obj) : string =
        let sql, parms = extractSqlWithParameters query
        formatSqlAndParameters sql parms

    let toRawSql (query: obj) : string = extractRawSql query

    let toRawQuery (query: obj) : string =
        let sql, parms = extractSqlWithParameters query
        let formatted = SqlPushdownFormatting.formatSql sql
        formatSqlAndParameters formatted parms

    let explainQuery (query: obj) : ExecutionPlanReport =
        let sql, parms = extractSqlWithParameters query
        let formatted = SqlPushdownFormatting.formatSql sql

        let dialectOpt =
            match query with
            | :? IQueryable as iq ->
                match tryFindPushdownSource iq.Expression with
                | Some(_, Some p) -> p.Dialect
                | _ -> None
            | _ -> None

        let connectorOpt =
            match query with
            | :? IQueryable as iq ->
                let t = iq.GetType()
                let p = t.GetProperty("Connector")

                if not (isNull p) then
                    try
                        Some(p.GetValue(iq) :?> IQueryableConnector)
                    with _ ->
                        None
                else
                    None
            | _ -> None

        let dialect =
            match dialectOpt with
            | Some d -> d
            | None ->
                match connectorOpt with
                | Some c -> SqlDialect.fromConnectorToDialect (c :> IConnector)
                | None -> SqlDialect.Sqlite

        let planText =
            match connectorOpt with
            | Some conn ->
                try
                    dialect.Dialect.FormatExplain(conn, sql, parms)
                with ex ->
                    $"-- EXPLAIN diagnostic report:\n--   Engine: {dialect}\n--   Status: {ex.Message}"
            | None -> $"-- Diagnostic execution plan estimate:\n--   Engine: {dialect}\n--   Target SQL:\n{formatted}"

        {
            Dialect = dialect
            PlanText = planText
            FormattedSql = formatted
            Parameters = parms
        }

    do ResultKind.RegisterRawSqlExtractor(extractRawSql, extractSqlWithParameters)
