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

type ParameterMemberReplacer(param: ParameterExpression, memberMap: Map<string, Expression>) =
    inherit ExpressionVisitor()
    override this.VisitMember(m: MemberExpression) =
        if m.Expression = (param :> Expression) && memberMap.ContainsKey m.Member.Name then
            memberMap.[m.Member.Name]
        else
            base.VisitMember(m)

module ExpressionPushdownTranslator =
    let rec collectBlockAssignments (expr: Expression) (acc: (string * Expression) list) : Expression * (string * Expression) list =
        match expr with
        | :? BlockExpression as be when be.Expressions.Count > 0 ->
            let mutable currentAcc = acc
            for i in 0 .. be.Expressions.Count - 2 do
                match be.Expressions.[i] with
                | :? BinaryExpression as b when b.NodeType = ExpressionType.Assign ->
                    match b.Left with
                    | :? ParameterExpression as p when not (isNull p.Name) ->
                        currentAcc <- (p.Name, b.Right) :: currentAcc
                    | _ -> ()
                | _ -> ()
            let last = be.Expressions.[be.Expressions.Count - 1]
            match last with
            | :? BlockExpression ->
                collectBlockAssignments last currentAcc
            | _ ->
                last, currentAcc
        | other ->
            other, acc

    let unwrapBlock (expr: Expression) : Expression =
        let targetExpr, assignments = collectBlockAssignments expr []
        if List.isEmpty assignments then targetExpr
        else
            let varMap = assignments |> Map.ofList
            let replacer =
                { new ExpressionVisitor() with
                    override this.VisitParameter(p: ParameterExpression) =
                        if not (isNull p) && not (isNull p.Name) && varMap.ContainsKey p.Name then
                            varMap.[p.Name]
                        else
                            base.VisitParameter(p)
                }
            replacer.Visit(targetExpr)

    let extractMemberMap (body: Expression) : Map<string, Expression> =
        let unwrapped = unwrapBlock body
        match unwrapped with
        | :? NewExpression as ne ->
            let ctor = ne.Constructor
            let paramInfos = if isNull ctor then [||] else ctor.GetParameters()
            let mutable m = Map.empty
            for i in 0 .. ne.Arguments.Count - 1 do
                let arg = ne.Arguments.[i]
                let itemKey = sprintf "Item%d" (i + 1)
                m <- m.Add(itemKey, arg)
                if not (isNull ne.Members) && i < ne.Members.Count then
                    m <- m.Add(ne.Members.[i].Name, arg)
                elif i < paramInfos.Length then
                    m <- m.Add(paramInfos.[i].Name, arg)
            m
        | :? MemberInitExpression as mie ->
            let mutable m = Map.empty
            for b in mie.Bindings do
                match b with
                | :? MemberAssignment as ma ->
                    m <- m.Add(ma.Member.Name, ma.Expression)
                | _ -> ()
            m
        | _ ->
            Map.empty

    let rec dependsOnParameter (param: ParameterExpression) (expr: Expression) : bool =
        match expr with
        | null -> false
        | :? ParameterExpression as p -> p = param
        | :? MemberExpression as m -> dependsOnParameter param m.Expression
        | :? BinaryExpression as b -> dependsOnParameter param b.Left || dependsOnParameter param b.Right
        | :? UnaryExpression as u -> dependsOnParameter param u.Operand
        | :? MethodCallExpression as mc ->
            dependsOnParameter param mc.Object || (mc.Arguments |> Seq.exists (dependsOnParameter param))
        | _ -> false

    let evaluateClientValue (expr: Expression) : obj =
        match expr with
        | :? ConstantExpression as c -> c.Value
        | _ ->
            let lambda = Expression.Lambda(expr)
            let fn = lambda.Compile()
            fn.DynamicInvoke()

    let rec unwrapQuote (expr: Expression) : Expression =
        match expr with
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Quote -> unwrapQuote u.Operand
        | _ -> expr

    let rec private getTupleDepth (t: Type) : int =
        if not (isNull t) && t.IsGenericType && (t.Name.StartsWith("Tuple`") || t.Name.StartsWith("ValueTuple`")) then
            let args = t.GetGenericArguments()
            if args.Length = 2 then
                1 + getTupleDepth args.[0]
            else
                args.Length - 1
        else
            0

    /// Resolves a member expression (possibly navigating a nested tuple hierarchy from multi-table joins)
    /// into an optional table alias and the column name.
    let resolveMemberRef (plan: PushdownQueryPlan) (expr: Expression) : (string option * string) =
        let unwrapped = unwrapQuote expr
        match unwrapped with
        | :? MemberExpression as m ->
            let colName = m.Member.Name
            let mutable matchedAlias: string option = None
            if not (isNull m.Expression) && plan.Joins.Count > 0 then
                let exprType = m.Expression.Type
                if not (exprType.Name.StartsWith("AnonymousObject") || exprType.Name.StartsWith("Tuple") || exprType.Name.StartsWith("ValueTuple")) then
                    let baseMatches = if plan.BaseRowType = Some exprType then 1 else 0
                    let matchingJoins = plan.Joins |> Seq.filter (fun j -> j.RowType = Some exprType) |> Seq.toList
                    let totalMatches = baseMatches + matchingJoins.Length
                    if totalMatches = 1 then
                        if baseMatches = 1 then
                            matchedAlias <- Some plan.BaseAlias
                        else
                            matchedAlias <- Some matchingJoins.[0].Alias

            match matchedAlias with
            | Some alias -> (Some alias, colName)
            | None ->
                let path = ResizeArray<string>()
                let mutable curr: Expression = m.Expression
                let mutable rootParam: ParameterExpression option = None

                let mutable keepWalking = true
                while keepWalking && not (isNull curr) do
                    match curr with
                    | :? MemberExpression as parentM ->
                        path.Add(parentM.Member.Name)
                        curr <- parentM.Expression
                    | :? ParameterExpression as p ->
                        rootParam <- Some p
                        keepWalking <- false
                    | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert ->
                        curr <- u.Operand
                    | _ ->
                        keepWalking <- false

                path.Reverse()

                if path.Count = 0 then
                    (None, colName)
                else
                    let tupleItems = path |> Seq.filter (fun s -> s.StartsWith("Item")) |> Seq.toList
                    if not (List.isEmpty tupleItems) then
                        if tupleItems.Length = 1 then
                            // Flat tuple / AnonymousObject: root.Item{X}
                            let itemStr = tupleItems.[0].Substring(4)
                            match Int32.TryParse(itemStr) with
                            | true, n when n >= 1 ->
                                let tableIdx = n - 1
                                let alias = if tableIdx = 0 then plan.BaseAlias else sprintf "_t%d" tableIdx
                                (Some alias, colName)
                            | _ ->
                                (None, colName)
                        else
                            // Check for overflow tuple (Item8)
                            let first = tupleItems.[0]
                            if first = "Item8" && tupleItems.Length = 2 then
                                let subStr = tupleItems.[1].Substring(4)
                                match Int32.TryParse(subStr) with
                                | true, subIdx when subIdx >= 1 ->
                                    let leftJoins = plan.Joins |> Seq.filter (fun j -> j.JoinType = "LEFT JOIN") |> Seq.toList
                                    let tableIdx =
                                        if leftJoins.Length >= subIdx then
                                            let joinTarget = leftJoins.[subIdx - 1]
                                            let idx = plan.Joins |> Seq.findIndex (fun j -> obj.ReferenceEquals(j, joinTarget))
                                            idx + 1
                                        else
                                            let baseOffset = max 0 (plan.Joins.Count - leftJoins.Length)
                                            baseOffset + subIdx
                                    (Some (sprintf "_t%d" tableIdx), colName)
                                | _ -> (None, colName)
                            else
                                // Nested left-associative tuple tree: root.Item1.Item2...
                                let k =
                                    match rootParam with
                                    | Some p ->
                                        let d = getTupleDepth p.Type
                                        if d > 0 then d else plan.Joins.Count
                                    | None -> plan.Joins.Count

                                let leadingOnes =
                                    let mutable count = 0
                                    let mutable stopped = false
                                    for item in tupleItems do
                                        if not stopped then
                                            if item = "Item1" then count <- count + 1
                                            else stopped <- true
                                    count

                                let hasItem2 = tupleItems |> List.exists (fun s -> s = "Item2")
                                let tableIdx =
                                    if not hasItem2 then 0
                                    else max 0 (k - leadingOnes)

                                let alias = if tableIdx = 0 then plan.BaseAlias else sprintf "_t%d" tableIdx
                                (Some alias, colName)
                    else
                        (None, colName)
        | _ ->
            (None, unwrapped.ToString())

    let rec extractColumnNameWithAlias (plan: PushdownQueryPlan) (expr: Expression) : string =
        let unwrapped = unwrapQuote expr
        match unwrapped with
        | :? LambdaExpression as l -> extractColumnNameWithAlias plan l.Body
        | :? MemberExpression as m ->
            let aliasOpt, colName = resolveMemberRef plan m
            match aliasOpt with
            | Some alias when plan.Joins.Count > 0 -> $"{alias}.{colName}"
            | _ ->
                if plan.Joins.Count > 0 && not (colName.Contains ".") then
                    $"{plan.BaseAlias}.{colName}"
                else
                    colName
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert ->
            extractColumnNameWithAlias plan u.Operand
        | _ ->
            unwrapped.ToString()

    let extractColumnName (param: ParameterExpression) (expr: Expression) : string =
        match expr with
        | :? MemberExpression as m when m.Expression = (param :> Expression) ->
            m.Member.Name
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert ->
            match u.Operand with
            | :? MemberExpression as m when m.Expression = (param :> Expression) -> m.Member.Name
            | other -> other.ToString()
        | _ ->
            expr.ToString()

    let rec extractKeyColumns (plan: PushdownQueryPlan) (fallbackAlias: string) (param: ParameterExpression) (expr: Expression) : string list =
        let unwrapped = unwrapQuote expr
        match unwrapped with
        | :? MemberExpression as m ->
            let aliasOpt, colName = resolveMemberRef plan m
            let alias = defaultArg aliasOpt fallbackAlias
            [ $"{alias}.{colName}" ]
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert ->
            extractKeyColumns plan fallbackAlias param u.Operand
        | :? NewExpression as ne ->
            [ for arg in ne.Arguments do
                yield! extractKeyColumns plan fallbackAlias param arg ]
        | :? MemberInitExpression as mie ->
            [ for binding in mie.Bindings do
                match binding with
                | :? MemberAssignment as ma ->
                    yield! extractKeyColumns plan fallbackAlias param ma.Expression
                | _ -> () ]
        | :? MethodCallExpression as mc when mc.Method.Name = "Create" && (mc.Method.DeclaringType = typeof<Tuple> || mc.Method.DeclaringType.Name.StartsWith("Tuple")) ->
            [ for arg in mc.Arguments do
                yield! extractKeyColumns plan fallbackAlias param arg ]
        | _ ->
            let single = extractColumnName param unwrapped
            if not (String.IsNullOrWhiteSpace single) && not (single.Contains "(") && not (single.Contains " ") then
                let ref = if single.Contains(".") then single else $"{fallbackAlias}.{single}"
                [ ref ]
            else []

    let rec tryExtractFromValue (value: obj) : (string * IQueryableConnector * Type option) option =
        if isNull value then None
        else
            let valType = value.GetType()
            let isPushdown = valType.Name.StartsWith("SqlPushdownQueryable")
            if isPushdown then
                let planProp = valType.GetProperty("Plan")
                let connProp = valType.GetProperty("Connector")
                let elemType = if valType.IsGenericType then Some (valType.GetGenericArguments().[0]) else None
                if not (isNull planProp) && not (isNull connProp) then
                    let p = planProp.GetValue(value) :?> PushdownQueryPlan
                    let conn = connProp.GetValue(value) :?> IQueryableConnector
                    Some (p.BaseSql, conn, elemType)
                else
                    None
            elif typeof<IQueryable>.IsAssignableFrom(valType) then
                let q = value :?> IQueryable
                if not (isNull q.Expression) then
                    match q.Expression with
                    | :? ConstantExpression as ce when obj.ReferenceEquals(ce.Value, value) ->
                        None
                    | otherExpr ->
                        tryExtractQueryableInfo otherExpr
                else
                    None
            else
                let props = valType.GetProperties()
                let qProp = props |> Array.tryFind (fun p -> typeof<IQueryable>.IsAssignableFrom(p.PropertyType))
                match qProp with
                | Some p ->
                    try
                        let propVal = p.GetValue(value)
                        tryExtractFromValue propVal
                    with _ -> None
                | None -> None

    and tryExtractQueryableInfo (expr: Expression) : (string * IQueryableConnector * Type option) option =
        let unwrapped = unwrapQuote expr
        match unwrapped with
        | :? ConstantExpression as c when not (isNull c.Value) ->
            tryExtractFromValue c.Value
        | :? MemberExpression as m ->
            try
                let target =
                    if isNull m.Expression then null
                    else
                        match m.Expression with
                        | :? ConstantExpression as ce -> ce.Value
                        | other ->
                            let lambda = Expression.Lambda(other)
                            lambda.Compile().DynamicInvoke()
                let value =
                    match m.Member with
                    | :? System.Reflection.FieldInfo as fi -> fi.GetValue(target)
                    | :? System.Reflection.PropertyInfo as pi -> pi.GetValue(target)
                    | _ -> null
                tryExtractFromValue value
            with _ -> None
        | :? MethodCallExpression as mc ->
            if typeof<IQueryable>.IsAssignableFrom(mc.Type) then
                try
                    let lambda = Expression.Lambda(mc)
                    let res = lambda.Compile().DynamicInvoke()
                    match tryExtractFromValue res with
                    | Some info -> Some info
                    | None ->
                        if mc.Arguments.Count > 0 then
                            tryExtractQueryableInfo mc.Arguments.[0]
                        else None
                with _ ->
                    if mc.Arguments.Count > 0 then
                        tryExtractQueryableInfo mc.Arguments.[0]
                    else None
            elif mc.Arguments.Count > 0 then
                tryExtractQueryableInfo mc.Arguments.[0]
            else
                None
        | _ ->
            None

    let rec isDefaultIfEmptyFlattening (expr: Expression) : bool =
        let unwrapped = unwrapQuote expr
        match unwrapped with
        | :? MethodCallExpression as mc ->
            if mc.Method.Name = "DefaultIfEmpty" then true
            elif mc.Method.Name = "SelectMany" then
                if mc.Arguments.Count >= 2 then
                    let inner = unwrapQuote mc.Arguments.[1]
                    let innerBody =
                        match inner with
                        | :? LambdaExpression as l -> l.Body
                        | other -> other
                    isDefaultIfEmptyFlattening innerBody || isDefaultIfEmptyFlattening mc.Arguments.[0]
                else false
            else
                mc.Arguments |> Seq.exists isDefaultIfEmptyFlattening
        | :? LambdaExpression as l -> isDefaultIfEmptyFlattening l.Body
        | :? UnaryExpression as u -> isDefaultIfEmptyFlattening u.Operand
        | _ -> false

    let canPushdownAllJoins (rootExpr: Expression) (outerConn: IQueryableConnector) : bool =
        let methodCalls = ResizeArray<MethodCallExpression>()
        let rec collect (e: Expression) =
            match e with
            | null -> ()
            | :? MethodCallExpression as mc ->
                if mc.Method.Name = "Join" || mc.Method.Name = "GroupJoin" || mc.Method.Name = "SelectMany" then
                    methodCalls.Add(mc)
                for a in mc.Arguments do collect a
            | :? LambdaExpression as l -> collect l.Body
            | :? UnaryExpression as u -> collect u.Operand
            | :? BinaryExpression as b -> collect b.Left; collect b.Right
            | _ -> ()
        collect rootExpr
        if methodCalls.Count = 0 then true
        else
            methodCalls |> Seq.forall (fun mc ->
                if (mc.Method.Name = "Join" || mc.Method.Name = "GroupJoin") && mc.Arguments.Count >= 5 then
                    match tryExtractQueryableInfo mc.Arguments.[1] with
                    | Some (_, innerConn, _) ->
                        obj.ReferenceEquals(outerConn, innerConn)
                        || (
                            (outerConn :> IConnector).Kind = (innerConn :> IConnector).Kind
                            && String.Equals(outerConn.ConnectionString, innerConn.ConnectionString, StringComparison.OrdinalIgnoreCase)
                        )
                    | None -> false
                elif mc.Method.Name = "SelectMany" && mc.Arguments.Count >= 2 then
                    let innerArg = unwrapQuote mc.Arguments.[1]
                    let innerExpr =
                        match innerArg with
                        | :? LambdaExpression as l -> l.Body
                        | other -> other
                    match tryExtractQueryableInfo innerExpr with
                    | Some (_, innerConn, _) ->
                        obj.ReferenceEquals(outerConn, innerConn)
                        || (
                            (outerConn :> IConnector).Kind = (innerConn :> IConnector).Kind
                            && String.Equals(outerConn.ConnectionString, innerConn.ConnectionString, StringComparison.OrdinalIgnoreCase)
                        )
                    | None ->
                        isDefaultIfEmptyFlattening innerExpr
                else false
            )

    let rec translatePredicate (param: ParameterExpression) (plan: PushdownQueryPlan) (dialect: SqlDialect) (expr: Expression) : string option =
        match expr with
        | null -> None
        | :? BinaryExpression as b ->
            match b.NodeType with
            | ExpressionType.AndAlso ->
                match translatePredicate param plan dialect b.Left, translatePredicate param plan dialect b.Right with
                | Some l, Some r -> Some $"({l} AND {r})"
                | Some l, None -> Some l
                | None, Some r -> Some r
                | None, None -> None
            | ExpressionType.OrElse ->
                match translatePredicate param plan dialect b.Left, translatePredicate param plan dialect b.Right with
                | Some l, Some r -> Some $"({l} OR {r})"
                | _ -> None
            | ExpressionType.Equal
            | ExpressionType.NotEqual
            | ExpressionType.GreaterThan
            | ExpressionType.GreaterThanOrEqual
            | ExpressionType.LessThan
            | ExpressionType.LessThanOrEqual ->
                let op =
                    match b.NodeType with
                    | ExpressionType.Equal -> "="
                    | ExpressionType.NotEqual -> "<>"
                    | ExpressionType.GreaterThan -> ">"
                    | ExpressionType.GreaterThanOrEqual -> ">="
                    | ExpressionType.LessThan -> "<"
                    | ExpressionType.LessThanOrEqual -> "<="
                    | _ -> "="

                let leftHasParam = dependsOnParameter param b.Left
                let rightHasParam = dependsOnParameter param b.Right

                if leftHasParam && not rightHasParam then
                    let col = extractColumnNameWithAlias plan b.Left
                    let clientVal = evaluateClientValue b.Right
                    if isNull clientVal then
                        if op = "=" then Some $"({col} IS NULL)"
                        elif op = "<>" then Some $"({col} IS NOT NULL)"
                        else None
                    else
                        let pName = $"@p{plan.Parameters.Count}"
                        plan.Parameters.Add(pName, clientVal)
                        Some $"({col} {op} {pName})"
                elif not leftHasParam && rightHasParam then
                    let col = extractColumnNameWithAlias plan b.Right
                    let clientVal = evaluateClientValue b.Left
                    let invOp =
                        match op with
                        | ">" -> "<"
                        | ">=" -> "<="
                        | "<" -> ">"
                        | "<=" -> ">="
                        | other -> other
                    if isNull clientVal then
                        if invOp = "=" then Some $"({col} IS NULL)"
                        elif invOp = "<>" then Some $"({col} IS NOT NULL)"
                        else None
                    else
                        let pName = $"@p{plan.Parameters.Count}"
                        plan.Parameters.Add(pName, clientVal)
                        Some $"({col} {invOp} {pName})"
                elif leftHasParam && rightHasParam then
                    let leftCol = extractColumnNameWithAlias plan b.Left
                    let rightCol = extractColumnNameWithAlias plan b.Right
                    if not (String.IsNullOrWhiteSpace leftCol) && not (String.IsNullOrWhiteSpace rightCol) then
                        Some $"({leftCol} {op} {rightCol})"
                    else
                        None
                else
                    None
            | _ -> None
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Not ->
            match translatePredicate param plan dialect u.Operand with
            | Some inner -> Some $"(NOT {inner})"
            | None -> None
        | :? MethodCallExpression as mc ->
            if mc.Method.Name = "Contains" then
                let sourceExpr, itemExpr =
                    if mc.Method.IsStatic && mc.Arguments.Count >= 2 then
                        mc.Arguments.[0], mc.Arguments.[1]
                    elif not (isNull mc.Object) && mc.Arguments.Count >= 1 then
                        mc.Object, mc.Arguments.[0]
                    else null, null
                if not (isNull sourceExpr) && not (isNull itemExpr) && dependsOnParameter param itemExpr then
                    let col = extractColumnNameWithAlias plan itemExpr
                    match tryExtractQueryableInfo sourceExpr with
                    | Some (subTable, _, subRowType) ->
                        let subPlan = PushdownQueryPlan.Create(subTable, [], ?baseRowType = subRowType)
                        let targetExpr =
                            match unwrapQuote sourceExpr with
                            | :? ConstantExpression as ce when not (isNull ce.Value) ->
                                match ce.Value with
                                | :? IQueryable as iq -> iq.Expression
                                | _ -> sourceExpr
                            | other -> other
                        mergeExpressionIntoPlan targetExpr subPlan dialect
                        let mutable subSql = subPlan.BuildSql(dialect)
                        for (pName, pVal) in subPlan.Parameters do
                            let uniqueName = $"@p{plan.Parameters.Count}"
                            subSql <- subSql.Replace(pName, uniqueName)
                            plan.Parameters.Add(uniqueName, pVal)
                        Some $"({col} IN ({subSql}))"
                    | None ->
                        try
                            let collVal = evaluateClientValue sourceExpr
                            match collVal with
                            | :? System.Collections.IEnumerable as enum when not (collVal :? string) ->
                                let items = [ for item in enum -> item ]
                                if items.IsEmpty then
                                    Some "(1=0)"
                                else
                                    let pNames = ResizeArray<string>()
                                    for item in items do
                                        let pName = $"@p{plan.Parameters.Count}"
                                        plan.Parameters.Add(pName, item)
                                        pNames.Add(pName)
                                    let inList = String.concat ", " pNames
                                    Some $"({col} IN ({inList}))"
                            | _ -> None
                        with _ -> None
                elif mc.Method.DeclaringType = typeof<string> && mc.Arguments.Count = 1 && dependsOnParameter param mc.Object && not (dependsOnParameter param mc.Arguments.[0]) then
                    let col = extractColumnNameWithAlias plan mc.Object
                    let rawVal = evaluateClientValue mc.Arguments.[0]
                    if isNull rawVal then None
                    else
                        let sVal = string rawVal
                        let pName = $"@p{plan.Parameters.Count}"
                        match mc.Method.Name with
                        | "Contains" ->
                            plan.Parameters.Add(pName, box ("%" + sVal + "%"))
                            Some $"({col} LIKE {pName})"
                        | "StartsWith" ->
                            plan.Parameters.Add(pName, box (sVal + "%"))
                            Some $"({col} LIKE {pName})"
                        | "EndsWith" ->
                            plan.Parameters.Add(pName, box ("%" + sVal))
                            Some $"({col} LIKE {pName})"
                        | _ -> None
                else
                    None
            elif mc.Method.Name = "Any" then
                let sourceExpr =
                    if mc.Method.IsStatic && mc.Arguments.Count >= 1 then mc.Arguments.[0]
                    elif not (isNull mc.Object) then mc.Object
                    else null
                if not (isNull sourceExpr) then
                    match tryExtractQueryableInfo sourceExpr with
                    | Some (subTable, _, subRowType) ->
                        let subPlan = PushdownQueryPlan.Create(subTable, [], ?baseRowType = subRowType)
                        let targetExpr =
                            match unwrapQuote sourceExpr with
                            | :? ConstantExpression as ce when not (isNull ce.Value) ->
                                match ce.Value with
                                | :? IQueryable as iq -> iq.Expression
                                | _ -> sourceExpr
                            | other -> other
                        mergeExpressionIntoPlan targetExpr subPlan dialect
                        if mc.Arguments.Count >= 2 then
                            let predLambda = unwrapQuote mc.Arguments.[1] :?> LambdaExpression
                            match translatePredicate predLambda.Parameters.[0] subPlan dialect predLambda.Body with
                            | Some pSql -> subPlan.WhereClauses.Add(pSql)
                            | None -> ()
                        let mutable subSql = subPlan.BuildSql(dialect)
                        for (pName, pVal) in subPlan.Parameters do
                            let uniqueName = $"@p{plan.Parameters.Count}"
                            subSql <- subSql.Replace(pName, uniqueName)
                            plan.Parameters.Add(uniqueName, pVal)
                        Some $"EXISTS ({subSql})"
                    | None -> None
                else None
            elif mc.Method.DeclaringType = typeof<string> && mc.Arguments.Count = 1 && dependsOnParameter param mc.Object && not (dependsOnParameter param mc.Arguments.[0]) then
                let col = extractColumnNameWithAlias plan mc.Object
                let rawVal = evaluateClientValue mc.Arguments.[0]
                if isNull rawVal then None
                else
                    let sVal = string rawVal
                    let pName = $"@p{plan.Parameters.Count}"
                    match mc.Method.Name with
                    | "StartsWith" ->
                        plan.Parameters.Add(pName, box (sVal + "%"))
                        Some $"({col} LIKE {pName})"
                    | "EndsWith" ->
                        plan.Parameters.Add(pName, box ("%" + sVal))
                        Some $"({col} LIKE {pName})"
                    | _ -> None
            else
                None
        | _ -> None

    and translateScalarCondition (dialect: SqlDialect) (plan: PushdownQueryPlan) (expr: Expression) : string option =
        let unwrapped = unwrapQuote expr
        match unwrapped with
        | :? BinaryExpression as b ->
            let op =
                match b.NodeType with
                | ExpressionType.Equal -> "="
                | ExpressionType.NotEqual -> "<>"
                | ExpressionType.GreaterThan -> ">"
                | ExpressionType.GreaterThanOrEqual -> ">="
                | ExpressionType.LessThan -> "<"
                | ExpressionType.LessThanOrEqual -> "<="
                | _ -> "="
            match translateScalarExpr dialect plan b.Left, translateScalarExpr dialect plan b.Right with
            | Some l, Some r ->
                if r = "NULL" then
                    if op = "=" then Some $"({l} IS NULL)"
                    else Some $"({l} IS NOT NULL)"
                elif l = "NULL" then
                    if op = "=" then Some $"({r} IS NULL)"
                    else Some $"({r} IS NOT NULL)"
                else
                    Some $"({l} {op} {r})"
            | _ -> None
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Not ->
            match translateScalarCondition dialect plan u.Operand with
            | Some inner -> Some $"(NOT {inner})"
            | None -> None
        | _ -> None

    and translateScalarExpr (dialect: SqlDialect) (plan: PushdownQueryPlan) (expr: Expression) : string option =
        let unwrapped = unwrapQuote expr
        match unwrapped with
        | null -> None
        | :? ConstantExpression as c ->
            if isNull c.Value then Some "NULL"
            elif c.Type = typeof<string> then
                let s = string c.Value
                let escaped = s.Replace("'", "''")
                Some $"'{escaped}'"
            elif c.Type = typeof<bool> then
                let b = c.Value :?> bool
                if dialect = SqlDialect.PostgreSql then
                    Some (if b then "TRUE" else "FALSE")
                else
                    Some (if b then "1" else "0")
            elif c.Type = typeof<DateTime> then
                let dt = c.Value :?> DateTime
                let dtStr = dt.ToString("yyyy-MM-dd HH:mm:ss.fff")
                Some $"'{dtStr}'"
            else
                Some (Convert.ToString(c.Value, CultureInfo.InvariantCulture))
        | :? MemberExpression as m when not (isNull m.Expression) && (m.Expression.Type = typeof<DateTime> || m.Expression.Type = typeof<DateOnly>) &&
                                         (m.Member.Name = "Date" || m.Member.Name = "Year" || m.Member.Name = "Month" || m.Member.Name = "Day" || m.Member.Name = "Hour" || m.Member.Name = "Minute" || m.Member.Name = "Second") ->
            match translateScalarExpr dialect plan m.Expression with
            | Some o ->
                match m.Member.Name with
                | "Year" ->
                    match dialect with
                    | SqlDialect.Sqlite -> Some $"CAST(strftime('%%Y', {o}) AS INTEGER)"
                    | SqlDialect.SqlServer -> Some $"YEAR({o})"
                    | SqlDialect.PostgreSql -> Some $"EXTRACT(YEAR FROM {o})"
                | "Month" ->
                    match dialect with
                    | SqlDialect.Sqlite -> Some $"CAST(strftime('%%m', {o}) AS INTEGER)"
                    | SqlDialect.SqlServer -> Some $"MONTH({o})"
                    | SqlDialect.PostgreSql -> Some $"EXTRACT(MONTH FROM {o})"
                | "Day" ->
                    match dialect with
                    | SqlDialect.Sqlite -> Some $"CAST(strftime('%%d', {o}) AS INTEGER)"
                    | SqlDialect.SqlServer -> Some $"DAY({o})"
                    | SqlDialect.PostgreSql -> Some $"EXTRACT(DAY FROM {o})"
                | "Date" ->
                    match dialect with
                    | SqlDialect.Sqlite -> Some $"date({o})"
                    | SqlDialect.SqlServer -> Some $"CAST({o} AS DATE)"
                    | SqlDialect.PostgreSql -> Some $"CAST({o} AS DATE)"
                | "Hour" ->
                    match dialect with
                    | SqlDialect.Sqlite -> Some $"CAST(strftime('%%H', {o}) AS INTEGER)"
                    | SqlDialect.SqlServer -> Some $"DATEPART(HOUR, {o})"
                    | SqlDialect.PostgreSql -> Some $"EXTRACT(HOUR FROM {o})"
                | "Minute" ->
                    match dialect with
                    | SqlDialect.Sqlite -> Some $"CAST(strftime('%%M', {o}) AS INTEGER)"
                    | SqlDialect.SqlServer -> Some $"DATEPART(MINUTE, {o})"
                    | SqlDialect.PostgreSql -> Some $"EXTRACT(MINUTE FROM {o})"
                | "Second" ->
                    match dialect with
                    | SqlDialect.Sqlite -> Some $"CAST(strftime('%%S', {o}) AS INTEGER)"
                    | SqlDialect.SqlServer -> Some $"DATEPART(SECOND, {o})"
                    | SqlDialect.PostgreSql -> Some $"EXTRACT(SECOND FROM {o})"
                | _ -> None
            | None -> None
        | :? MemberExpression as m ->
            let aliasOpt, colName = resolveMemberRef plan m
            match aliasOpt with
            | Some alias -> Some $"{alias}.{colName}"
            | None ->
                match m.Expression with
                | null ->
                    let v = evaluateClientValue expr
                    translateScalarExpr dialect plan (Expression.Constant(v, expr.Type))
                | _ ->
                    if plan.Joins.Count > 0 && not (colName.Contains ".") then
                        Some $"{plan.BaseAlias}.{colName}"
                    else
                        Some colName
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert ->
            translateScalarExpr dialect plan u.Operand
        | :? BinaryExpression as b ->
            match b.NodeType with
            | ExpressionType.Add ->
                if b.Type = typeof<string> then
                    let op = if dialect = SqlDialect.SqlServer then " + " else " || "
                    match translateScalarExpr dialect plan b.Left, translateScalarExpr dialect plan b.Right with
                    | Some l, Some r -> Some $"({l}{op}{r})"
                    | _ -> None
                else
                    match translateScalarExpr dialect plan b.Left, translateScalarExpr dialect plan b.Right with
                    | Some l, Some r -> Some $"({l} + {r})"
                    | _ -> None
            | ExpressionType.Subtract ->
                match translateScalarExpr dialect plan b.Left, translateScalarExpr dialect plan b.Right with
                | Some l, Some r -> Some $"({l} - {r})"
                | _ -> None
            | ExpressionType.Multiply ->
                match translateScalarExpr dialect plan b.Left, translateScalarExpr dialect plan b.Right with
                | Some l, Some r -> Some $"({l} * {r})"
                | _ -> None
            | ExpressionType.Divide ->
                match translateScalarExpr dialect plan b.Left, translateScalarExpr dialect plan b.Right with
                | Some l, Some r -> Some $"({l} / {r})"
                | _ -> None
            | _ -> None
        | :? ConditionalExpression as c ->
            let isNullCheck (e: Expression) : bool =
                match unwrapQuote e with
                | :? MethodCallExpression as mc when mc.Method.Name = "IsNull" || mc.Method.Name = "ReferenceEquals" -> true
                | :? BinaryExpression as b when b.NodeType = ExpressionType.Equal ->
                    let isNullConst (x: Expression) =
                        match unwrapQuote x with
                        | :? ConstantExpression as ce -> isNull ce.Value
                        | _ -> false
                    isNullConst b.Left || isNullConst b.Right
                | _ -> false

            let isNotNullCheck (e: Expression) : bool =
                match unwrapQuote e with
                | :? UnaryExpression as u when u.NodeType = ExpressionType.Not -> isNullCheck u.Operand
                | :? BinaryExpression as b when b.NodeType = ExpressionType.NotEqual ->
                    let isNullConst (x: Expression) =
                        match unwrapQuote x with
                        | :? ConstantExpression as ce -> isNull ce.Value
                        | _ -> false
                    isNullConst b.Left || isNullConst b.Right
                | _ -> false

            if isNullCheck c.Test then
                match translateScalarExpr dialect plan c.IfFalse, translateScalarExpr dialect plan c.IfTrue with
                | Some valExpr, Some defExpr -> Some $"COALESCE({valExpr}, {defExpr})"
                | _ -> None
            elif isNotNullCheck c.Test then
                match translateScalarExpr dialect plan c.IfTrue, translateScalarExpr dialect plan c.IfFalse with
                | Some valExpr, Some defExpr -> Some $"COALESCE({valExpr}, {defExpr})"
                | _ -> None
            else
                match translateScalarCondition dialect plan c.Test, translateScalarExpr dialect plan c.IfTrue, translateScalarExpr dialect plan c.IfFalse with
                | Some testExpr, Some trueExpr, Some falseExpr ->
                    Some $"CASE WHEN {testExpr} THEN {trueExpr} ELSE {falseExpr} END"
                | _ -> None
        | :? MethodCallExpression as mc ->
            if mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "Concat" then
                let op = if dialect = SqlDialect.SqlServer then " + " else " || "
                let argParts = mc.Arguments |> Seq.map (translateScalarExpr dialect plan) |> Seq.toList
                if argParts |> List.forall Option.isSome then
                    let joined = argParts |> List.map Option.get |> String.concat op
                    Some $"({joined})"
                else None
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "ToUpper" && mc.Arguments.Count = 0 then
                match translateScalarExpr dialect plan mc.Object with
                | Some o -> Some $"UPPER({o})"
                | None -> None
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "ToLower" && mc.Arguments.Count = 0 then
                match translateScalarExpr dialect plan mc.Object with
                | Some o -> Some $"LOWER({o})"
                | None -> None
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "Trim" && mc.Arguments.Count = 0 then
                match translateScalarExpr dialect plan mc.Object with
                | Some o -> Some $"TRIM({o})"
                | None -> None
            elif mc.Method.Name = "Count" then
                if mc.Arguments.Count >= 2 then
                    match translateScalarExpr dialect plan mc.Arguments.[1] with
                    | Some col -> Some $"COUNT({col})"
                    | None -> Some "COUNT(*)"
                else Some "COUNT(*)"
            elif mc.Method.Name = "LongCount" then
                Some "COUNT(*)"
            elif mc.Method.Name = "Sum" && mc.Arguments.Count >= 2 then
                match translateScalarExpr dialect plan mc.Arguments.[1] with
                | Some col -> Some $"SUM({col})"
                | None -> None
            elif mc.Method.Name = "Min" then
                if mc.Arguments.Count >= 2 then
                    match translateScalarExpr dialect plan mc.Arguments.[1] with
                    | Some col -> Some $"MIN({col})"
                    | None -> None
                elif not (isNull mc.Object) then
                    match translateScalarExpr dialect plan mc.Object with
                    | Some col -> Some $"MIN({col})"
                    | None -> None
                else None
            elif mc.Method.Name = "Max" then
                if mc.Arguments.Count >= 2 then
                    match translateScalarExpr dialect plan mc.Arguments.[1] with
                    | Some col -> Some $"MAX({col})"
                    | None -> None
                elif not (isNull mc.Object) then
                    match translateScalarExpr dialect plan mc.Object with
                    | Some col -> Some $"MAX({col})"
                    | None -> None
                else None
            elif (mc.Method.Name = "Average" || mc.Method.Name = "Avg") then
                if mc.Arguments.Count >= 2 then
                    match translateScalarExpr dialect plan mc.Arguments.[1] with
                    | Some col -> Some $"AVG({col})"
                    | None -> None
                elif not (isNull mc.Object) then
                    match translateScalarExpr dialect plan mc.Object with
                    | Some col -> Some $"AVG({col})"
                    | None -> None
                else None
            elif mc.Method.Name = "ToString" then
                let objExpr =
                    if not (isNull mc.Object) then
                        translateScalarExpr dialect plan mc.Object
                    elif mc.Arguments.Count > 0 then
                        translateScalarExpr dialect plan mc.Arguments.[0]
                    else None

                match objExpr with
                | Some o ->
                    let formatArg =
                        if not (isNull mc.Object) && mc.Arguments.Count > 0 then
                            match unwrapQuote mc.Arguments.[0] with
                            | :? ConstantExpression as ce when not (isNull ce.Value) -> Some (string ce.Value)
                            | _ -> None
                        elif isNull mc.Object && mc.Arguments.Count > 1 then
                            match unwrapQuote mc.Arguments.[1] with
                            | :? ConstantExpression as ce when not (isNull ce.Value) -> Some (string ce.Value)
                            | _ -> None
                        else None

                    match formatArg with
                    | Some fmt ->
                        match dialect with
                        | SqlDialect.Sqlite ->
                            let sqliteFmt =
                                fmt.Replace("yyyy", "%Y")
                                   .Replace("YYYY", "%Y")
                                   .Replace("yy", "%Y")
                                   .Replace("MM", "%m")
                                   .Replace("dd", "%d")
                                   .Replace("DD", "%d")
                                   .Replace("HH", "%H")
                                   .Replace("hh", "%H")
                                   .Replace("mm", "%M")
                                   .Replace("ss", "%S")
                                   .Replace("SS", "%S")
                            if sqliteFmt.Contains("%") then
                                Some $"strftime('{sqliteFmt}', {o})"
                            else
                                Some $"CAST({o} AS TEXT)"
                        | SqlDialect.SqlServer ->
                            Some $"FORMAT({o}, '{fmt}')"
                        | SqlDialect.PostgreSql ->
                            let pgFmt =
                                fmt.Replace("yyyy", "YYYY")
                                   .Replace("YYYY", "YYYY")
                                   .Replace("yy", "YY")
                                   .Replace("MM", "MM")
                                   .Replace("dd", "DD")
                                   .Replace("DD", "DD")
                                   .Replace("HH", "HH24")
                                   .Replace("hh", "HH12")
                                   .Replace("mm", "MI")
                                   .Replace("ss", "SS")
                                   .Replace("SS", "SS")
                            Some $"to_char({o}, '{pgFmt}')"
                    | None ->
                        match dialect with
                        | SqlDialect.Sqlite -> Some $"CAST({o} AS TEXT)"
                        | SqlDialect.SqlServer -> Some $"CAST({o} AS NVARCHAR(MAX))"
                        | SqlDialect.PostgreSql -> Some $"CAST({o} AS TEXT)"
                | None -> None
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "Substring" then
                match translateScalarExpr dialect plan mc.Object with
                | Some o ->
                    let startArg = if mc.Arguments.Count > 0 then translateScalarExpr dialect plan mc.Arguments.[0] else None
                    let lenArg = if mc.Arguments.Count > 1 then translateScalarExpr dialect plan mc.Arguments.[1] else None
                    match startArg, lenArg with
                    | Some s, Some l ->
                        match dialect with
                        | SqlDialect.SqlServer -> Some $"SUBSTRING({o}, ({s}) + 1, {l})"
                        | _ -> Some $"SUBSTR({o}, ({s}) + 1, {l})"
                    | Some s, None ->
                        match dialect with
                        | SqlDialect.SqlServer -> Some $"SUBSTRING({o}, ({s}) + 1, LEN({o}))"
                        | _ -> Some $"SUBSTR({o}, ({s}) + 1)"
                    | _ -> None
                | None -> None
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "Replace" && mc.Arguments.Count = 2 then
                match translateScalarExpr dialect plan mc.Object, translateScalarExpr dialect plan mc.Arguments.[0], translateScalarExpr dialect plan mc.Arguments.[1] with
                | Some o, Some oldVal, Some newVal -> Some $"REPLACE({o}, {oldVal}, {newVal})"
                | _ -> None
            elif mc.Method.DeclaringType = typeof<Math> && mc.Method.Name = "Round" then
                match mc.Arguments.Count with
                | 1 ->
                    match translateScalarExpr dialect plan mc.Arguments.[0] with
                    | Some v -> Some $"ROUND({v})"
                    | None -> None
                | 2 ->
                    match translateScalarExpr dialect plan mc.Arguments.[0], translateScalarExpr dialect plan mc.Arguments.[1] with
                    | Some v, Some digits -> Some $"ROUND({v}, {digits})"
                    | _ -> None
                | _ -> None
            elif mc.Method.DeclaringType = typeof<Math> && mc.Method.Name = "Abs" && mc.Arguments.Count = 1 then
                match translateScalarExpr dialect plan mc.Arguments.[0] with
                | Some v -> Some $"ABS({v})"
                | None -> None
            else
                None
        | _ -> None

    and mergeExpressionIntoPlan (rootExpr: Expression) (plan: PushdownQueryPlan) (dialect: SqlDialect) : unit =
        let methodCalls = ResizeArray<MethodCallExpression>()
        let rec collectCalls (e: Expression) =
            match e with
            | :? MethodCallExpression as mc ->
                methodCalls.Add(mc)
                if mc.Arguments.Count > 0 then
                    collectCalls mc.Arguments.[0]
            | _ -> ()

        collectCalls rootExpr

        // Process calls from inner to outer so Join -> Where -> OrderBy -> Select build naturally
        let reversed = methodCalls |> Seq.toArray |> Array.rev
        let mutable currentProjectionBody: Expression option = None

        for mc in reversed do
            match mc.Method.Name with
            | ("Join" | "GroupJoin") as mName when mc.Arguments.Count >= 5 ->
                if plan.Joins.Count = 0 && plan.BaseRowType.IsNone then
                    match tryExtractQueryableInfo mc.Arguments.[0] with
                    | Some (_, _, outerRowType) -> plan.BaseRowType <- outerRowType
                    | None -> ()

                let innerArg = mc.Arguments.[1]
                match tryExtractQueryableInfo innerArg with
                | Some (innerTable, _, innerRowType) ->
                    let nextIdx = plan.Joins.Count + 1
                    let innerAlias = sprintf "_t%d" nextIdx
                    let defaultOuterAlias = if plan.Joins.Count = 0 then plan.BaseAlias else sprintf "_t%d" plan.Joins.Count
                    let outerKeyL = unwrapQuote mc.Arguments.[2] :?> LambdaExpression
                    let innerKeyL = unwrapQuote mc.Arguments.[3] :?> LambdaExpression
                    let outerCols = extractKeyColumns plan defaultOuterAlias outerKeyL.Parameters.[0] outerKeyL.Body
                    let innerCols = extractKeyColumns plan innerAlias innerKeyL.Parameters.[0] innerKeyL.Body
                    let onClause =
                        if outerCols.Length > 0 && outerCols.Length = innerCols.Length then
                            List.zip outerCols innerCols
                            |> List.map (fun (oRef, iRef) -> $"{oRef} = {iRef}")
                            |> String.concat " AND "
                        else
                            let outerCol = extractColumnName outerKeyL.Parameters.[0] outerKeyL.Body
                            let innerCol = extractColumnName innerKeyL.Parameters.[0] innerKeyL.Body
                            let outerColRef = if outerCol.Contains(".") then outerCol else $"{defaultOuterAlias}.{outerCol}"
                            let innerColRef = if innerCol.Contains(".") then innerCol else $"{innerAlias}.{innerCol}"
                            $"{outerColRef} = {innerColRef}"

                    let joinType = if mName = "GroupJoin" then "LEFT JOIN" else "INNER JOIN"
                    plan.Joins.Add({
                        JoinType = joinType
                        SourceTable = innerTable
                        Alias = innerAlias
                        OnClause = onClause
                        RowType = innerRowType
                    })
                | None -> ()
            | "SelectMany" when mc.Arguments.Count >= 2 ->
                let innerArg = unwrapQuote mc.Arguments.[1]
                let innerExpr =
                    match innerArg with
                    | :? LambdaExpression as l -> l.Body
                    | other -> other
                match tryExtractQueryableInfo innerExpr with
                | Some (innerTable, _, innerRowType) ->
                    let nextIdx = plan.Joins.Count + 1
                    let innerAlias = sprintf "_t%d" nextIdx
                    plan.Joins.Add({
                        JoinType = "INNER JOIN"
                        SourceTable = innerTable
                        Alias = innerAlias
                        OnClause = "1=1"
                        RowType = innerRowType
                    })
                | None -> ()
            | "Where" when mc.Arguments.Count >= 2 ->
                let arg1 = unwrapQuote mc.Arguments.[1]
                match arg1 with
                | :? LambdaExpression as l ->
                    let param = l.Parameters.[0]
                    match translatePredicate param plan dialect l.Body with
                    | Some sqlPred ->
                        plan.WhereClauses.Add(sqlPred)
                    | None ->
                        let compiled = l.Compile()
                        plan.FallbackPredicates.Add(fun item -> unbox<bool> (compiled.DynamicInvoke(item)))
                | _ -> ()
            | "GroupBy" when mc.Arguments.Count >= 2 ->
                let keyArg = unwrapQuote mc.Arguments.[1]
                match keyArg with
                | :? LambdaExpression as l ->
                    let cols = extractKeyColumns plan plan.BaseAlias l.Parameters.[0] l.Body
                    for c in cols do
                        if not (plan.GroupBy.Contains c) then
                            plan.GroupBy.Add(c)
                | _ -> ()
            | "Distinct" ->
                plan.IsDistinct <- true
            | "OrderBy" when mc.Arguments.Count >= 2 ->
                let col = extractColumnNameWithAlias plan mc.Arguments.[1]
                plan.OrderBy.Add({ ColumnName = col; Direction = Ascending })
            | "OrderByDescending" when mc.Arguments.Count >= 2 ->
                let col = extractColumnNameWithAlias plan mc.Arguments.[1]
                plan.OrderBy.Add({ ColumnName = col; Direction = Descending })
            | "ThenBy" when mc.Arguments.Count >= 2 ->
                let col = extractColumnNameWithAlias plan mc.Arguments.[1]
                plan.OrderBy.Add({ ColumnName = col; Direction = Ascending })
            | "ThenByDescending" when mc.Arguments.Count >= 2 ->
                let col = extractColumnNameWithAlias plan mc.Arguments.[1]
                plan.OrderBy.Add({ ColumnName = col; Direction = Descending })
            | "Take" when mc.Arguments.Count >= 2 ->
                let count = evaluateClientValue mc.Arguments.[1] :?> int
                plan.Take <- Some count
            | "Skip" when mc.Arguments.Count >= 2 ->
                let count = evaluateClientValue mc.Arguments.[1] :?> int
                plan.Skip <- Some count
            | ("First" | "FirstOrDefault" | "Head" | "HeadOrDefault") ->
                plan.Take <- Some 1
            | ("Single" | "SingleOrDefault") ->
                plan.Take <- Some 2
            | ("Count" | "LongCount") ->
                plan.Projection <- Some "COUNT(*)"
            | "Sum" when mc.Arguments.Count >= 2 ->
                let col = extractColumnNameWithAlias plan mc.Arguments.[1]
                plan.Projection <- Some $"SUM({col})"
            | "Min" when mc.Arguments.Count >= 2 ->
                let col = extractColumnNameWithAlias plan mc.Arguments.[1]
                plan.Projection <- Some $"MIN({col})"
            | "Max" when mc.Arguments.Count >= 2 ->
                let col = extractColumnNameWithAlias plan mc.Arguments.[1]
                plan.Projection <- Some $"MAX({col})"
            | ("Average" | "Avg") when mc.Arguments.Count >= 2 ->
                let col = extractColumnNameWithAlias plan mc.Arguments.[1]
                plan.Projection <- Some $"AVG({col})"
            | "Select" when mc.Arguments.Count >= 2 ->
                let arg1 = unwrapQuote mc.Arguments.[1]
                match arg1 with
                | :? LambdaExpression as l ->
                    let unwrappedBody = unwrapBlock l.Body
                    match currentProjectionBody with
                    | Some prevBody ->
                        let prevMap = extractMemberMap prevBody
                        let replacer = ParameterMemberReplacer(l.Parameters.[0], prevMap)
                        let inlined = replacer.Visit(unwrappedBody)
                        currentProjectionBody <- Some inlined
                    | None ->
                        currentProjectionBody <- Some unwrappedBody
                | _ -> ()
            | _ -> ()

        match currentProjectionBody with
        | Some rawBody ->
            let body = unwrapBlock rawBody
            match body with
            | :? NewExpression as newExpr ->
                let ctor = newExpr.Constructor
                let paramInfos = if isNull ctor then [||] else ctor.GetParameters()
                let colParts = ResizeArray<string>()
                let mutable ok = true
                for idx in 0 .. newExpr.Arguments.Count - 1 do
                    let arg = newExpr.Arguments.[idx]
                    let itemNum = idx + 1
                    let alias =
                        if not (isNull newExpr.Members) && idx < newExpr.Members.Count then
                            newExpr.Members.[idx].Name
                        elif idx < paramInfos.Length then
                            paramInfos.[idx].Name
                        else
                            sprintf "Item%d" itemNum
                    match translateScalarExpr dialect plan arg with
                    | Some sqlExpr ->
                        let qAlias = SqlDialect.quoteIdentifier dialect alias
                        colParts.Add($"{sqlExpr} AS {qAlias}")
                    | None ->
                        ok <- false
                if ok && colParts.Count > 0 then
                    plan.Projection <- Some (String.concat ", " colParts)
            | :? MemberExpression as m ->
                match translateScalarExpr dialect plan m with
                | Some sqlExpr ->
                    let qProp = SqlDialect.quoteIdentifier dialect m.Member.Name
                    plan.Projection <- Some $"{sqlExpr} AS {qProp}"
                | None -> ()
            | :? BinaryExpression as b ->
                match translateScalarExpr dialect plan b with
                | Some sqlExpr ->
                    let qVal = SqlDialect.quoteIdentifier dialect "Value"
                    plan.Projection <- Some $"{sqlExpr} AS {qVal}"
                | None -> ()
            | :? MethodCallExpression as mcExpr ->
                match translateScalarExpr dialect plan mcExpr with
                | Some sqlExpr ->
                    let qVal = SqlDialect.quoteIdentifier dialect "Value"
                    plan.Projection <- Some $"{sqlExpr} AS {qVal}"
                | None -> ()
            | _ -> ()
        | None -> ()

    let rec hasJoin (expr: Expression) : bool =
        match expr with
        | null -> false
        | :? MethodCallExpression as mc ->
            if mc.Method.Name = "Join" || mc.Method.Name = "GroupJoin" || mc.Method.Name = "SelectMany" then true
            else
                (if isNull mc.Object then false else hasJoin mc.Object)
                || (mc.Arguments |> Seq.exists hasJoin)
        | :? LambdaExpression as l -> hasJoin l.Body
        | :? UnaryExpression as u -> hasJoin u.Operand
        | :? BinaryExpression as b -> hasJoin b.Left || hasJoin b.Right
        | _ -> false

    let extractPlanFromExpression (rootExpr: Expression) (baseSql: string) (baseParameters: (string * obj) list) (dialect: SqlDialect) : PushdownQueryPlan =
        let plan = PushdownQueryPlan.Create(baseSql, baseParameters)
        mergeExpressionIntoPlan rootExpr plan dialect
        plan

module LinqInMemoryFallback =
    let rec evaluate (expr: Expression) : obj =
        match expr with
        | null -> null
        | :? ConstantExpression as c -> c.Value
        | :? MethodCallExpression as mc when mc.Method.DeclaringType = typeof<Queryable> ->
            let evaluatedArgs = [|
                for a in mc.Arguments do
                    match a with
                    | :? UnaryExpression as u when u.NodeType = ExpressionType.Quote ->
                        match u.Operand with
                        | :? LambdaExpression as l ->
                            box (l.Compile())
                        | other ->
                            evaluate other
                    | :? LambdaExpression as l ->
                        box (l.Compile())
                    | other ->
                        evaluate other
            |]
            let methodGenericArgs = mc.Method.GetGenericArguments()
            let enumMethods = typeof<Enumerable>.GetMethods()
            let targetMethod =
                enumMethods
                |> Array.find (fun m ->
                    if m.Name <> mc.Method.Name
                       || not m.IsGenericMethod
                       || m.GetGenericArguments().Length <> methodGenericArgs.Length
                       || m.GetParameters().Length <> evaluatedArgs.Length then
                        false
                    else
                        try
                            let candidate = m.MakeGenericMethod(methodGenericArgs)
                            let pars = candidate.GetParameters()
                            Array.zip pars evaluatedArgs
                            |> Array.forall (fun (p, a) ->
                                if isNull a then true
                                else
                                    let argType = a.GetType()
                                    if p.ParameterType.IsAssignableFrom(argType) then true
                                    elif typeof<Delegate>.IsAssignableFrom(argType) && typeof<Delegate>.IsAssignableFrom(p.ParameterType) then
                                        let aInvoke = argType.GetMethod("Invoke")
                                        let pInvoke = p.ParameterType.GetMethod("Invoke")
                                        if not (isNull aInvoke) && not (isNull pInvoke) then
                                            aInvoke.GetParameters().Length = pInvoke.GetParameters().Length
                                        else true
                                    else false
                            )
                        with _ ->
                            false
                )
            let specialized = targetMethod.MakeGenericMethod(methodGenericArgs)
            specialized.Invoke(null, evaluatedArgs)
        | _ ->
            let lambda = Expression.Lambda(expr)
            let fn = lambda.Compile()
            fn.DynamicInvoke()

/// LINQ Query Provider that inspects expression trees and executes parameterized SQL pushdown queries.
type SqlPushdownQueryProvider(connector: IQueryableConnector, baseSql: string, baseParameters: (string * obj) list) =
    let dialect = SqlDialect.fromConnectorKind (connector :> IConnector).Kind

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
            ExpressionPushdownTranslator.extractPlanFromExpression expression baseSql baseParameters dialect

    member this.ToSqlWithParameters(expression: Expression, ?seedPlan: PushdownQueryPlan) : string * (string * obj) list =
        let plan = this.ExtractPlan(expression, ?seedPlan = seedPlan)
        let sql = plan.BuildSql(dialect)
        let allParams = (plan.BaseParameters @ (plan.Parameters |> List.ofSeq))
        sql, allParams

    member this.ToRawSql(expression: Expression, ?seedPlan: PushdownQueryPlan) : string =
        let sql, allParams = this.ToSqlWithParameters(expression, ?seedPlan = seedPlan)
        SqlPushdownFormatting.formatSqlAndParameters sql allParams

    member this.ExecutePlanOrStream<'T>(expression: Expression, ?seedPlan: PushdownQueryPlan) : 'T seq =
        let isJoin = ExpressionPushdownTranslator.hasJoin expression
        let canPushdown = if isJoin then ExpressionPushdownTranslator.canPushdownAllJoins expression connector else true
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
                | None ->
                    ExpressionPushdownTranslator.extractPlanFromExpression expression baseSql baseParameters dialect
            let sql = plan.BuildSql(dialect)
            let allParams = (plan.BaseParameters @ (plan.Parameters |> List.ofSeq))

            let rows: 'T seq =
                match connector with
                | :? IStreamingQueryableConnector as streaming ->
                    streaming.ExecuteReaderStreamWithColumns<'T> sql allParams CompiledRecordMapper.getCompiledRecordMapper<'T>
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
                                yield unbox<'T> (ctor args)
                            else
                                if row.Length > 0 then
                                    yield unbox<'T> (RecordReaders.ConvertValue row.[0] targetType)
                    }

            if plan.FallbackPredicates.Count > 0 then
                rows |> Seq.filter (fun item -> plan.FallbackPredicates |> Seq.forall (fun p -> p (box item)))
            else
                rows

    member this.ExecuteStream<'T>(expression: Expression) : 'T seq =
        this.ExecutePlanOrStream<'T>(expression)

    member this.ExecuteScalar<'TResult>(expression: Expression) : 'TResult =
        let plan = ExpressionPushdownTranslator.extractPlanFromExpression expression baseSql baseParameters dialect
        let sql = plan.BuildSql(dialect)
        let allParams = (plan.BaseParameters @ (plan.Parameters |> List.ofSeq))
        let res = connector.ExecuteQuery sql allParams
        let firstRow = res.Rows |> Seq.tryHead
        match firstRow with
        | Some row when row.Length > 0 && not (isNull row.[0]) && not (Convert.IsDBNull(row.[0])) ->
            unbox<'TResult> (RecordReaders.ConvertValue row.[0] typeof<'TResult>)
        | _ ->
            Unchecked.defaultof<'TResult>

    interface IQueryProvider with
        member this.CreateQuery(expression: Expression) : IQueryable =
            let elemType =
                let t = expression.Type
                if t.IsGenericType && typeof<IEnumerable>.IsAssignableFrom(t) then
                    t.GetGenericArguments().[0]
                else
                    t
            let plan = PushdownQueryPlan.Create(baseSql, baseParameters)
            Activator.CreateInstance(
                typedefof<SqlPushdownQueryable<_>>.MakeGenericType(elemType),
                this, expression, connector, baseSql, baseParameters, plan
            ) :?> IQueryable

        member this.CreateQuery<'TElement>(expression: Expression) : IQueryable<'TElement> =
            let plan = PushdownQueryPlan.Create(baseSql, baseParameters)
            SqlPushdownQueryable<'TElement>(this, expression, connector, baseSql, baseParameters, plan) :> IQueryable<'TElement>

        member this.Execute(expression: Expression) : obj =
            this.ExecuteScalar<obj>(expression)

        member this.Execute<'TResult>(expression: Expression) : 'TResult =
            let targetType = typeof<'TResult>
            if typeof<IEnumerable>.IsAssignableFrom(targetType) && targetType <> typeof<string> then
                let elemType =
                    if targetType.IsGenericType then targetType.GetGenericArguments().[0]
                    elif targetType.IsArray then targetType.GetElementType()
                    else typeof<obj>
                let method = typeof<SqlPushdownQueryProvider>.GetMethod("ExecuteStream").MakeGenericMethod([| elemType |])
                method.Invoke(this, [| expression |]) :?> 'TResult
            else
                this.ExecuteScalar<'TResult>(expression)

/// Strongly-typed IQueryable that captures LINQ expressions and provides pushdown filtering, paging, and sorting.
and SqlPushdownQueryable<'T>(
    provider: IQueryProvider,
    expression: Expression,
    connector: IQueryableConnector,
    baseSql: string,
    baseParameters: (string * obj) list,
    plan: PushdownQueryPlan
) =
    new(connector: IQueryableConnector, baseSql: string, ?baseParameters: (string * obj) list) =
        let p = defaultArg baseParameters []
        let provider = SqlPushdownQueryProvider(connector, baseSql, p)
        let plan = PushdownQueryPlan.Create(baseSql, p, baseRowType = typeof<'T>)
        SqlPushdownQueryable<'T>(provider, Expression.Constant(null, typeof<IQueryable<'T>>), connector, baseSql, p, plan)

    member _.Plan = plan
    member _.Connector = connector
    member _.BaseSql = baseSql
    member _.BaseParameters = baseParameters

    /// Appends a raw parameterized WHERE clause directly to the pushdown query.
    member this.Where(filterSql: string, ?parameters: (string * obj) list) : SqlPushdownQueryable<'T> =
        let p = defaultArg parameters []
        let newPlan = plan.Clone()
        newPlan.WhereClauses.Add($"({filterSql})")
        for (k, v) in p do
            newPlan.Parameters.Add(k, v)
        SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

    /// Configures 1-based pagination directly pushed down to database LIMIT/OFFSET or SQL Server OFFSET/FETCH.
    member this.Page(page: int, pageSize: int) : SqlPushdownQueryable<'T> =
        let validPage = if page < 1 then 1 else page
        let validSize = if pageSize < 1 then 10 else pageSize
        let skip = (validPage - 1) * validSize
        let newPlan = plan.Clone()
        newPlan.Skip <- Some skip
        newPlan.Take <- Some validSize
        SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

    /// Extracts the raw SQL query generated for this queryable without executing it against the database.
    member this.ToRawSql() : string =
        let p = provider :?> SqlPushdownQueryProvider
        let expr = (this :> IQueryable<'T>).Expression
        p.ToRawSql(expr, plan)

    /// Extracts the raw SQL query and parameter bindings for this queryable without executing it against the database.
    member this.ToSqlWithParameters() : string * (string * obj) list =
        let p = provider :?> SqlPushdownQueryProvider
        let expr = (this :> IQueryable<'T>).Expression
        p.ToSqlWithParameters(expr, plan)

    interface IQueryable<'T> with
        member _.Provider = provider
        member this.Expression = if isNull expression || (match expression with :? ConstantExpression as c -> isNull c.Value | _ -> false) then Expression.Constant(this, typeof<IQueryable<'T>>) :> Expression else expression
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

    let rec tryFindPushdownSource (expr: Expression) : (IQueryable * PushdownQueryPlan option) option =
        match expr with
        | null -> None
        | :? ConstantExpression as c when not (isNull c.Value) ->
            match c.Value with
            | :? IQueryable as iq when not (isNull iq.Provider) && iq.Provider.GetType() = typeof<SqlPushdownQueryProvider> ->
                let t = c.Value.GetType()
                let planProp = t.GetProperty("Plan")
                let plan = if isNull planProp then None else Some (planProp.GetValue(c.Value) :?> PushdownQueryPlan)
                Some (iq, plan)
            | _ -> None
        | :? MethodCallExpression as m ->
            m.Arguments
            |> Seq.tryPick tryFindPushdownSource
            |> Option.orElseWith (fun () -> tryFindPushdownSource m.Object)
        | :? UnaryExpression as u ->
            tryFindPushdownSource u.Operand
        | _ -> None

    let extractSqlWithParameters (query: obj) : string * (string * obj) list =
        match query with
        | null -> ("", [])
        | :? string as s -> (s, [])
        | :? PushdownQueryPlan as plan ->
            let sql = plan.BuildSql(SqlDialect.Sqlite)
            let parms = plan.BaseParameters @ (plan.Parameters |> List.ofSeq)
            (sql, parms)
        | :? IQueryable as iq ->
            if not (isNull iq.Provider) && iq.Provider.GetType() = typeof<SqlPushdownQueryProvider> then
                let provider = iq.Provider :?> SqlPushdownQueryProvider
                let planProp = iq.GetType().GetProperty("Plan")
                let seedPlan = if isNull planProp then None else Some (planProp.GetValue(iq) :?> PushdownQueryPlan)
                provider.ToSqlWithParameters(iq.Expression, ?seedPlan = seedPlan)
            else
                match tryFindPushdownSource iq.Expression with
                | Some (sourceIq, seedPlan) ->
                    let provider = sourceIq.Provider :?> SqlPushdownQueryProvider
                    provider.ToSqlWithParameters(iq.Expression, ?seedPlan = seedPlan)
                | None ->
                    (string iq, [])
        | other ->
            let t = other.GetType()
            let toRawSqlMethod = t.GetMethod("ToRawSql", Type.EmptyTypes)
            if not (isNull toRawSqlMethod) then
                let res = toRawSqlMethod.Invoke(other, [||]) :?> string
                (res, [])
            else
                (string other, [])

    let extractRawSql (query: obj) : string =
        let sql, parms = extractSqlWithParameters query
        formatSqlAndParameters sql parms

    do
        ResultKind.RegisterRawSqlExtractor(extractRawSql, extractSqlWithParameters)



