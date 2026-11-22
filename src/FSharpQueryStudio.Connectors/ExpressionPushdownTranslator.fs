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

/// Marker interface allowing non-generic access to pushdown query metadata and plan diagnostics.
type ISqlPushdownQueryable =
    abstract member Plan: PushdownQueryPlan
    abstract member Connector: IQueryableConnector
    abstract member Explain: unit -> ExecutionPlanReport

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

    let rec unwrapBlock (expr: Expression) : Expression =
        if isNull expr then null
        else
            let targetExpr, assignments = collectBlockAssignments expr []
            let resolvedTarget =
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
            let nestedUnwrapper =
                { new ExpressionVisitor() with
                    override _.VisitBlock(node: BlockExpression) =
                        unwrapBlock (node :> Expression)
                }
            nestedUnwrapper.Visit(resolvedTarget)

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
        | :? ParameterExpression as p -> p = param || (not (isNull p.Name) && not (isNull param.Name) && p.Name = param.Name)
        | :? MemberExpression as m -> dependsOnParameter param m.Expression
        | :? BinaryExpression as b -> dependsOnParameter param b.Left || dependsOnParameter param b.Right
        | :? UnaryExpression as u -> dependsOnParameter param u.Operand
        | :? LambdaExpression as l -> dependsOnParameter param l.Body
        | :? ConditionalExpression as c ->
            dependsOnParameter param c.Test || dependsOnParameter param c.IfTrue || dependsOnParameter param c.IfFalse
        | :? BlockExpression as be ->
            be.Expressions |> Seq.exists (dependsOnParameter param)
        | :? NewExpression as ne ->
            ne.Arguments |> Seq.exists (dependsOnParameter param)
        | :? InvocationExpression as ie ->
            dependsOnParameter param ie.Expression || (ie.Arguments |> Seq.exists (dependsOnParameter param))
        | :? MethodCallExpression as mc ->
            dependsOnParameter param mc.Object || (mc.Arguments |> Seq.exists (dependsOnParameter param))
        | _ -> false

    let rec hasAnyParameter (expr: Expression) : bool =
        match expr with
        | null -> false
        | :? ParameterExpression -> true
        | :? MemberExpression as m -> hasAnyParameter m.Expression
        | :? BinaryExpression as b -> hasAnyParameter b.Left || hasAnyParameter b.Right
        | :? UnaryExpression as u -> hasAnyParameter u.Operand
        | :? LambdaExpression as l -> hasAnyParameter l.Body
        | :? ConditionalExpression as c ->
            hasAnyParameter c.Test || hasAnyParameter c.IfTrue || hasAnyParameter c.IfFalse
        | :? BlockExpression as be ->
            be.Expressions |> Seq.exists hasAnyParameter
        | :? NewExpression as ne ->
            ne.Arguments |> Seq.exists hasAnyParameter
        | :? InvocationExpression as ie ->
            hasAnyParameter ie.Expression || (ie.Arguments |> Seq.exists hasAnyParameter)
        | :? MethodCallExpression as mc ->
            hasAnyParameter mc.Object || (mc.Arguments |> Seq.exists hasAnyParameter)
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
        | :? MethodCallExpression as mc when mc.Method.Name = "QuotationToLambdaExpression" && mc.Arguments.Count > 0 ->
            try
                let v = evaluateClientValue mc.Arguments.[0]
                match v with
                | :? Microsoft.FSharp.Quotations.Expr as fExpr ->
                    let linqExpr = Microsoft.FSharp.Linq.RuntimeHelpers.LeafExpressionConverter.QuotationToExpression fExpr
                    unwrapQuote linqExpr
                | _ -> expr
            with _ -> expr
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

    let isOptionOrNullableType (t: Type) : bool =
        if isNull t then false
        elif t.IsGenericType then
            let g = t.GetGenericTypeDefinition()
            g = typedefof<option<_>>
            || g = typedefof<voption<_>>
            || g = typedefof<Nullable<_>>
        else
            false

    let unwrapClientOption (v: obj) : obj =
        if isNull v then null
        else
            let t = v.GetType()
            if isOptionOrNullableType t then
                let g = t.GetGenericTypeDefinition()
                if g = typedefof<Nullable<_>> then
                    let hasValProp = t.GetProperty("HasValue")
                    if not (isNull hasValProp) && (hasValProp.GetValue(v) :?> bool) then
                        let valProp = t.GetProperty("Value")
                        if not (isNull valProp) then valProp.GetValue(v) else null
                    else
                        null
                elif g = typedefof<voption<_>> then
                    let isSomeProp = t.GetProperty("IsSome")
                    if not (isNull isSomeProp) && (isSomeProp.GetValue(v) :?> bool) then
                        let valProp = t.GetProperty("Value")
                        if not (isNull valProp) then valProp.GetValue(v) else null
                    else
                        null
                elif g = typedefof<option<_>> then
                    let isSomeProp = t.GetProperty("IsSome")
                    let isSome =
                        if isNull isSomeProp then false
                        elif isSomeProp.GetIndexParameters().Length > 0 then
                            isSomeProp.GetValue(null, [| v |]) :?> bool
                        else
                            isSomeProp.GetValue(v) :?> bool
                    if isSome then
                        let valProp = t.GetProperty("Value")
                        if not (isNull valProp) then valProp.GetValue(v) else null
                    else
                        null
                else
                    v
            else
                v

    let rec tryFindPushdownSource (expr: Expression) : (IQueryable * PushdownQueryPlan option) option =
        match expr with
        | null -> None
        | :? ConstantExpression as c when not (isNull c.Value) ->
            match c.Value with
            | :? IQueryable as iq when not (isNull iq.Provider) && iq.Provider.GetType().Name.StartsWith("SqlPushdownQueryProvider") ->
                let t = c.Value.GetType()
                let planProp = t.GetProperty("Plan")
                let plan = if isNull planProp then None else Some (planProp.GetValue(c.Value) :?> PushdownQueryPlan)
                Some (iq, plan)
            | other when not (isNull other) ->
                let fields = other.GetType().GetFields(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance)
                fields
                |> Seq.tryPick (fun f ->
                    try
                        match f.GetValue(other) with
                        | :? IQueryable as iq when not (isNull iq.Provider) && iq.Provider.GetType().Name.StartsWith("SqlPushdownQueryProvider") ->
                            let planProp = iq.GetType().GetProperty("Plan")
                            let plan = if isNull planProp then None else Some (planProp.GetValue(iq) :?> PushdownQueryPlan)
                            Some (iq, plan)
                        | _ -> None
                    with _ -> None)
            | _ -> None
        | :? MethodCallExpression as m ->
            m.Arguments
            |> Seq.tryPick tryFindPushdownSource
            |> Option.orElseWith (fun () -> if not (isNull m.Object) then tryFindPushdownSource m.Object else None)
        | :? LambdaExpression as l ->
            tryFindPushdownSource l.Body
        | :? MemberExpression as m ->
            tryFindPushdownSource m.Expression
            |> Option.orElseWith (fun () ->
                try
                    if isNull m.Expression && m.Member :? PropertyInfo then
                        let p = m.Member :?> PropertyInfo
                        match p.GetValue(null) with
                        | :? IQueryable as iq when not (isNull iq.Provider) && iq.Provider.GetType().Name.StartsWith("SqlPushdownQueryProvider") ->
                            let planProp = iq.GetType().GetProperty("Plan")
                            let plan = if isNull planProp then None else Some (planProp.GetValue(iq) :?> PushdownQueryPlan)
                            Some (iq, plan)
                        | _ -> None
                    elif not (isNull m.Expression) && m.Member :? FieldInfo then
                        let f = m.Member :?> FieldInfo
                        match m.Expression with
                        | :? ConstantExpression as ce when not (isNull ce.Value) ->
                            match f.GetValue(ce.Value) with
                            | :? IQueryable as iq when not (isNull iq.Provider) && iq.Provider.GetType().Name.StartsWith("SqlPushdownQueryProvider") ->
                                let planProp = iq.GetType().GetProperty("Plan")
                                let plan = if isNull planProp then None else Some (planProp.GetValue(iq) :?> PushdownQueryPlan)
                                Some (iq, plan)
                            | _ -> None
                        | _ -> None
                    else None
                with _ -> None)
        | :? InvocationExpression as inv ->
            tryFindPushdownSource inv.Expression
            |> Option.orElseWith (fun () -> inv.Arguments |> Seq.tryPick tryFindPushdownSource)
        | :? UnaryExpression as u ->
            tryFindPushdownSource u.Operand
        | :? BinaryExpression as b ->
            tryFindPushdownSource b.Left |> Option.orElseWith (fun () -> tryFindPushdownSource b.Right)
        | _ -> None

    let tryExtractSubquerySql (plan: PushdownQueryPlan) (queryObj: obj) : (string * (string * obj) list) option =
        match queryObj with
        | null -> None
        | :? IQueryable as iq ->
            match tryFindPushdownSource iq.Expression with
            | Some (sourceIq, seedPlan) ->
                let t = sourceIq.Provider.GetType()
                let m = t.GetMethod("ToSqlWithParameters", [| typeof<Expression>; typeof<PushdownQueryPlan option> |])
                if not (isNull m) then
                    let res = m.Invoke(sourceIq.Provider, [| box iq.Expression; box seedPlan |]) :?> (string * (string * obj) list)
                    Some res
                else None
            | None -> None
        | _ -> None

    let rec unwrapOptionOrNullableMember (expr: Expression) : Expression =
        match expr with
        | null -> null
        | :? BlockExpression as be ->
            unwrapOptionOrNullableMember (unwrapBlock be)
        | :? MemberExpression as m when
            not (isNull m.Expression)
            && (m.Member.Name = "Value" || m.Member.Name = "get_Value")
            && isOptionOrNullableType m.Expression.Type ->
            unwrapOptionOrNullableMember m.Expression
        | :? MethodCallExpression as mc when
            (mc.Method.Name = "get_Value" || mc.Method.Name = "get" || mc.Method.Name = "Value")
            && (
                (not (isNull mc.Object) && isOptionOrNullableType mc.Object.Type)
                || (mc.Arguments.Count > 0 && isOptionOrNullableType mc.Arguments.[0].Type)
            ) ->
            let target = if not (isNull mc.Object) then mc.Object else mc.Arguments.[0]
            unwrapOptionOrNullableMember target
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert || u.NodeType = ExpressionType.ConvertChecked || u.NodeType = ExpressionType.TypeAs ->
            unwrapOptionOrNullableMember u.Operand
        | other -> other

    let isGroupingType (t: Type) : bool =
        if isNull t then false
        else
            t.Name.StartsWith("IGrouping")
            || (t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<IGrouping<_, _>>)

    let rec exprHasParamName (paramName: string) (expr: Expression) : bool =
        match expr with
        | null -> false
        | :? ParameterExpression as p -> p.Name = paramName
        | :? MemberExpression as m -> exprHasParamName paramName m.Expression
        | :? BinaryExpression as b -> exprHasParamName paramName b.Left || exprHasParamName paramName b.Right
        | :? UnaryExpression as u -> exprHasParamName paramName u.Operand
        | :? ConditionalExpression as c ->
            exprHasParamName paramName c.Test || exprHasParamName paramName c.IfTrue || exprHasParamName paramName c.IfFalse
        | :? BlockExpression as be ->
            be.Expressions |> Seq.exists (exprHasParamName paramName)
        | :? NewExpression as ne ->
            ne.Arguments |> Seq.exists (exprHasParamName paramName)
        | :? InvocationExpression as ie ->
            exprHasParamName paramName ie.Expression || (ie.Arguments |> Seq.exists (exprHasParamName paramName))
        | :? MethodCallExpression as mc ->
            exprHasParamName paramName mc.Object || (mc.Arguments |> Seq.exists (exprHasParamName paramName))
        | :? LambdaExpression as l -> exprHasParamName paramName l.Body
        | _ -> false

    let hasAnyOuterParam (plan: PushdownQueryPlan) (expr: Expression) : bool =
        plan.OuterAliases.Keys |> Seq.exists (fun name -> exprHasParamName name expr)

    let rec isHavingPredicate (expr: Expression) : bool =
        match expr with
        | null -> false
        | :? MethodCallExpression as mc ->
            match mc.Method.Name with
            | "Count" | "LongCount" | "Sum" | "Min" | "Max" | "Average" | "Avg" -> true
            | _ ->
                (not (isNull mc.Object) && isHavingPredicate mc.Object)
                || (mc.Arguments |> Seq.exists isHavingPredicate)
        | :? MemberExpression as m ->
            m.Member.Name = "Key" || (not (isNull m.Expression) && isHavingPredicate m.Expression)
        | :? BinaryExpression as b ->
            isHavingPredicate b.Left || isHavingPredicate b.Right
        | :? UnaryExpression as u ->
            isHavingPredicate u.Operand
        | _ -> false

    /// Resolves a member expression (possibly navigating a nested tuple hierarchy from multi-table joins)
    /// into an optional table alias and the column name.
    let resolveMemberRef (plan: PushdownQueryPlan) (expr: Expression) : (string option * string) =
        let unwrapped = unwrapQuote expr |> unwrapOptionOrNullableMember
        match unwrapped with
        | :? MemberExpression as m ->
            let colName = m.Member.Name
            if colName = "Key" && not (isNull m.Expression) && isGroupingType m.Expression.Type then
                if plan.GroupBy.Count > 0 then
                    (None, plan.GroupBy.[0])
                else
                    (None, "Key")
            else
                let mutable matchedAlias: string option = None
                if not (isNull m.Expression) && (match m.Expression with :? ParameterExpression as p when not (isNull p.Name) && plan.OuterAliases.ContainsKey p.Name -> true | _ -> false) then
                    let p = m.Expression :?> ParameterExpression
                    matchedAlias <- Some plan.OuterAliases.[p.Name]
                elif not (isNull m.Expression) && plan.OuterAliases.Count > 0 && (match m.Expression with :? ParameterExpression as p when plan.BaseRowType.IsSome && plan.BaseRowType.Value = p.Type -> true | _ -> false) then
                    matchedAlias <- Some plan.BaseAlias
                elif not (isNull m.Expression) && plan.OuterAliases.Count > 0 && (match m.Expression with :? ParameterExpression as p when (plan.BaseRowType.IsNone || plan.BaseRowType.Value <> p.Type) -> true | _ -> false) then
                    let defaultOuter = plan.OuterAliases.Values |> Seq.tryHead |> defaultArg <| plan.BaseAlias
                    matchedAlias <- Some defaultOuter
                elif not (isNull m.Expression) && plan.Joins.Count > 0 then
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
                | None | _ ->
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
                                        | None | _ -> plan.Joins.Count

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
        let unwrapped = unwrapQuote expr |> unwrapOptionOrNullableMember
        match unwrapped with
        | :? LambdaExpression as l -> extractColumnNameWithAlias plan l.Body
        | :? MemberExpression as m ->
            let aliasOpt, colName = resolveMemberRef plan m
            match aliasOpt with
            | Some alias when plan.Joins.Count > 0 || plan.OuterAliases.Count > 0 -> $"{alias}.{colName}"
            | Some alias -> $"{alias}.{colName}"
            | None ->
                if (plan.Joins.Count > 0 || plan.OuterAliases.Count > 0) && not (colName.Contains ".") then
                    $"{plan.BaseAlias}.{colName}"
                else
                    colName
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert ->
            extractColumnNameWithAlias plan u.Operand
        | _ ->
            unwrapped.ToString()

    let extractColumnName (param: ParameterExpression) (expr: Expression) : string =
        let unwrapped = unwrapOptionOrNullableMember expr
        match unwrapped with
        | :? MemberExpression as m when m.Expression = (param :> Expression) ->
            m.Member.Name
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert ->
            match u.Operand |> unwrapOptionOrNullableMember with
            | :? MemberExpression as m when m.Expression = (param :> Expression) -> m.Member.Name
            | other -> other.ToString()
        | _ ->
            unwrapped.ToString()

    let rec extractKeyColumns (plan: PushdownQueryPlan) (fallbackAlias: string) (param: ParameterExpression) (expr: Expression) : string list =
        let unwrapped = unwrapQuote expr |> unwrapOptionOrNullableMember
        match unwrapped with
        | :? MemberExpression as m when not (isNull param) && m.Expression = (param :> Expression) ->
            [ $"{fallbackAlias}.{m.Member.Name}" ]
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
            let isPushdown = valType.Name.StartsWith("SqlPushdownQueryable") || typeof<ISqlPushdownQueryable>.IsAssignableFrom(valType)
            if isPushdown then
                let planProp = valType.GetProperty("Plan")
                let connProp = valType.GetProperty("Connector")
                let elemType =
                    if valType.IsGenericType then Some (valType.GetGenericArguments().[0])
                    else
                        valType.GetInterfaces()
                        |> Array.tryFind (fun i -> i.IsGenericType && i.GetGenericTypeDefinition() = typedefof<IQueryable<_>>)
                        |> Option.map (fun i -> i.GetGenericArguments().[0])
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
                | None | _ -> None

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
                    | None | _ ->
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

    let tryExtractSubqueryCall (expr: Expression) : (string * MethodCallExpression) option =
        let unwrapped = unwrapQuote expr
        match unwrapped with
        | :? MethodCallExpression as mc when mc.Arguments.Count > 0 ->
            match mc.Method.Name with
            | "Count" | "LongCount" | "Sum" | "Min" | "Max" | "Average" | "Avg" ->
                match tryExtractQueryableInfo mc.Arguments.[0] with
                | Some (table, _, _) when not (String.IsNullOrWhiteSpace table) ->
                    Some (mc.Method.Name, mc)
                | _ -> None
            | _ -> None
        | _ -> None

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
            let isSameOrStagedSqlite (c1: IQueryableConnector) (c2: IQueryableConnector) =
                obj.ReferenceEquals(c1, c2)
                || (
                    (c1 :> IConnector).Kind = (c2 :> IConnector).Kind
                    && (
                        String.Equals(c1.ConnectionString, c2.ConnectionString, StringComparison.OrdinalIgnoreCase)
                        || (
                            (c1 :> IConnector).Kind = ConnectorKind.Sqlite
                            && not (isNull c1.ConnectionString)
                            && not (isNull c2.ConnectionString)
                            && (c1.ConnectionString.IndexOf("engine.db", StringComparison.OrdinalIgnoreCase) >= 0
                                || c2.ConnectionString.IndexOf("engine.db", StringComparison.OrdinalIgnoreCase) >= 0)
                        )
                    )
                )
            methodCalls |> Seq.forall (fun mc ->
                if (mc.Method.Name = "Join" || mc.Method.Name = "GroupJoin") && mc.Arguments.Count >= 5 then
                    match tryExtractQueryableInfo mc.Arguments.[1] with
                    | Some (_, innerConn, _) -> isSameOrStagedSqlite outerConn innerConn
                    | None | _ -> false
                elif mc.Method.Name = "SelectMany" && mc.Arguments.Count >= 2 then
                    let innerArg = unwrapQuote mc.Arguments.[1]
                    let outerParamOpt, innerExpr =
                        match innerArg with
                        | :? LambdaExpression as l -> Some l.Parameters.[0], l.Body
                        | other -> None, other
                    let isCorrelated =
                        match outerParamOpt with
                        | Some p -> dependsOnParameter p innerExpr
                        | None -> false
                    if isCorrelated && (outerConn :> IConnector).Kind = ConnectorKind.Sqlite then
                        false
                    else
                        match tryExtractQueryableInfo innerExpr with
                        | Some (_, innerConn, _) -> isSameOrStagedSqlite outerConn innerConn
                        | None | _ ->
                            isDefaultIfEmptyFlattening innerExpr
                else false
            )

    let rec tryTranslateSubqueryScalarExpr (dialect: SqlDialect) (plan: PushdownQueryPlan) (mc: MethodCallExpression) : string option =
        match tryExtractSubqueryCall mc with
        | Some (methodName, subMc) ->
            match tryExtractQueryableInfo subMc.Arguments.[0] with
            | Some (subTable, _, subRowType) ->
                let subPlan =
                    match tryFindPushdownSource subMc.Arguments.[0] with
                    | Some (_, Some ep) ->
                        let cloned = ep.Clone()
                        cloned.BaseAlias <- sprintf "_sub%d" (plan.Joins.Count + 1)
                        if cloned.BaseRowType.IsNone then cloned.BaseRowType <- subRowType
                        cloned
                    | _ ->
                        PushdownQueryPlan.Create(subTable, [], baseAlias = sprintf "_sub%d" (plan.Joins.Count + 1), ?baseRowType = subRowType)
                for cte in subPlan.Ctes do
                    if not (plan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                        plan.Ctes.Add(cte)
                subPlan.Ctes.Clear()
                for kv in plan.OuterAliases do
                    subPlan.OuterAliases.[kv.Key] <- kv.Value
                subPlan.OuterAliases.[plan.BaseAlias] <- plan.BaseAlias
                if plan.BaseRowType.IsSome then
                    subPlan.OuterAliases.[plan.BaseRowType.Value.Name] <- plan.BaseAlias
                mergeExpressionIntoPlan subMc.Arguments.[0] subPlan dialect
                match methodName with
                | "Count" | "LongCount" ->
                    subPlan.Projection <- Some "COUNT(*)"
                | "Sum" ->
                    let col = if subMc.Arguments.Count >= 2 then extractColumnNameWithAlias subPlan subMc.Arguments.[1] else "*"
                    subPlan.Projection <- Some $"SUM({col})"
                | "Min" ->
                    let col = if subMc.Arguments.Count >= 2 then extractColumnNameWithAlias subPlan subMc.Arguments.[1] else "*"
                    subPlan.Projection <- Some $"MIN({col})"
                | "Max" ->
                    let col = if subMc.Arguments.Count >= 2 then extractColumnNameWithAlias subPlan subMc.Arguments.[1] else "*"
                    subPlan.Projection <- Some $"MAX({col})"
                | "Average" | "Avg" ->
                    let col = if subMc.Arguments.Count >= 2 then extractColumnNameWithAlias subPlan subMc.Arguments.[1] else "*"
                    subPlan.Projection <- Some $"AVG({col})"
                | _ -> ()
                let mutable subSql = subPlan.BuildSql(dialect)
                for (pName, pVal) in subPlan.Parameters do
                    let uniqueName = $"@p{plan.Parameters.Count}"
                    subSql <- subSql.Replace(pName, uniqueName)
                    plan.Parameters.Add(uniqueName, pVal)
                Some $"({subSql})"
            | None -> None
        | None -> None

    and tryTranslateSubqueryPredicate (param: ParameterExpression) (plan: PushdownQueryPlan) (dialect: SqlDialect) (b: BinaryExpression) : string option =
        let op =
            match b.NodeType with
            | ExpressionType.Equal -> "="
            | ExpressionType.NotEqual -> "<>"
            | ExpressionType.GreaterThan -> ">"
            | ExpressionType.GreaterThanOrEqual -> ">="
            | ExpressionType.LessThan -> "<"
            | ExpressionType.LessThanOrEqual -> "<="
            | _ -> "="

        let checkSubquery (subCall: MethodCallExpression) (otherExpr: Expression) (isSubOnLeft: bool) =
            match tryExtractQueryableInfo subCall.Arguments.[0] with
            | Some (subTable, _, subRowType) ->
                let subPlan =
                    match tryFindPushdownSource subCall.Arguments.[0] with
                    | Some (_, Some ep) ->
                        let cloned = ep.Clone()
                        cloned.BaseAlias <- sprintf "_sub%d" (plan.Joins.Count + 1)
                        if cloned.BaseRowType.IsNone then cloned.BaseRowType <- subRowType
                        cloned
                    | _ ->
                        PushdownQueryPlan.Create(subTable, [], baseAlias = sprintf "_sub%d" (plan.Joins.Count + 1), ?baseRowType = subRowType)
                for cte in subPlan.Ctes do
                    if not (plan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                        plan.Ctes.Add(cte)
                subPlan.Ctes.Clear()
                subPlan.OuterAliases.[param.Name] <- plan.BaseAlias
                for kv in plan.OuterAliases do
                    subPlan.OuterAliases.[kv.Key] <- kv.Value
                subPlan.OuterAliases.[plan.BaseAlias] <- plan.BaseAlias
                if plan.BaseRowType.IsSome then
                    subPlan.OuterAliases.[plan.BaseRowType.Value.Name] <- plan.BaseAlias
                mergeExpressionIntoPlan subCall.Arguments.[0] subPlan dialect

                let otherRaw = try Some (evaluateClientValue otherExpr) with _ -> None
                let otherInt =
                    match otherRaw with
                    | Some r -> try Some (Convert.ToInt32 (unwrapClientOption r)) with _ -> None
                    | None -> None

                match otherInt with
                | Some 0 when (if isSubOnLeft then op = ">" || op = "<>" else op = "<" || op = "<>") ->
                    subPlan.Projection <- Some "1"
                    let mutable subSql = subPlan.BuildSql(dialect)
                    for (pName, pVal) in subPlan.Parameters do
                        let uniqueName = $"@p{plan.Parameters.Count}"
                        subSql <- subSql.Replace(pName, uniqueName)
                        plan.Parameters.Add(uniqueName, pVal)
                    Some $"EXISTS ({subSql})"
                | Some 1 when (if isSubOnLeft then op = ">=" else op = "<=") ->
                    subPlan.Projection <- Some "1"
                    let mutable subSql = subPlan.BuildSql(dialect)
                    for (pName, pVal) in subPlan.Parameters do
                        let uniqueName = $"@p{plan.Parameters.Count}"
                        subSql <- subSql.Replace(pName, uniqueName)
                        plan.Parameters.Add(uniqueName, pVal)
                    Some $"EXISTS ({subSql})"
                | Some 0 when (if isSubOnLeft then op = "=" || op = "<=" else op = "=" || op = ">=") ->
                    subPlan.Projection <- Some "1"
                    let mutable subSql = subPlan.BuildSql(dialect)
                    for (pName, pVal) in subPlan.Parameters do
                        let uniqueName = $"@p{plan.Parameters.Count}"
                        subSql <- subSql.Replace(pName, uniqueName)
                        plan.Parameters.Add(uniqueName, pVal)
                    Some $"NOT EXISTS ({subSql})"
                | _ ->
                    subPlan.Projection <- Some "COUNT(*)"
                    let mutable subSql = subPlan.BuildSql(dialect)
                    for (pName, pVal) in subPlan.Parameters do
                        let uniqueName = $"@p{plan.Parameters.Count}"
                        subSql <- subSql.Replace(pName, uniqueName)
                        plan.Parameters.Add(uniqueName, pVal)
                    match otherRaw with
                    | Some rVal ->
                        let pName = $"@p{plan.Parameters.Count}"
                        plan.Parameters.Add(pName, unwrapClientOption rVal)
                        if isSubOnLeft then
                            Some $"({subSql} {op} {pName})"
                        else
                            Some $"({pName} {op} {subSql})"
                    | None ->
                        match translateScalarExpr dialect plan otherExpr with
                        | Some oSql ->
                            if isSubOnLeft then Some $"({subSql} {op} {oSql})"
                            else Some $"({oSql} {op} {subSql})"
                        | None -> None
            | None -> None

        match tryExtractSubqueryCall b.Left, tryExtractSubqueryCall b.Right with
        | Some ("Count", subMc), _ -> checkSubquery subMc b.Right true
        | _, Some ("Count", subMc) -> checkSubquery subMc b.Left false
        | _ -> None

    and negateSqlPredicate (inner: string) : string =
        let trimmed = inner.Trim()
        if trimmed.StartsWith("(") && trimmed.EndsWith(" IS NULL)") && not (trimmed.Contains(" AND ")) && not (trimmed.Contains(" OR ")) then
            trimmed.Substring(0, trimmed.Length - 9) + " IS NOT NULL)"
        elif trimmed.StartsWith("(") && trimmed.EndsWith(" IS NOT NULL)") && not (trimmed.Contains(" AND ")) && not (trimmed.Contains(" OR ")) then
            trimmed.Substring(0, trimmed.Length - 13) + " IS NULL)"
        else
            $"(NOT {inner})"

    and formatNotEmptySql (dialect: SqlDialect) (col: string) : string =
        if dialect = SqlDialect.Oracle then
            $"({col} IS NOT NULL AND LENGTH({col}) > 0)"
        else
            $"({col} IS NOT NULL AND {col} <> '')"

    and formatIsEmptySql (dialect: SqlDialect) (col: string) : string =
        if dialect = SqlDialect.Oracle then
            $"({col} IS NULL OR LENGTH({col}) = 0)"
        else
            $"({col} IS NULL OR {col} = '')"

    and tryExtractBoolConstant (expr: Expression) : bool option =
        match unwrapQuote expr |> unwrapBlock with
        | :? ConstantExpression as ce when ce.Type = typeof<bool> && not (isNull ce.Value) ->
            Some (ce.Value :?> bool)
        | _ -> None

    and isNullConstantExpr (expr: Expression) : bool =
        match unwrapQuote expr |> unwrapBlock with
        | null -> true
        | :? ConstantExpression as ce -> isNull ce.Value
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert || u.NodeType = ExpressionType.ConvertChecked || u.NodeType = ExpressionType.TypeAs ->
            isNullConstantExpr u.Operand
        | :? DefaultExpression as de -> not de.Type.IsValueType || isOptionOrNullableType de.Type
        | _ -> false

    and translatePredicate (param: ParameterExpression) (plan: PushdownQueryPlan) (dialect: SqlDialect) (expr: Expression) : string option =
        let unwrapped = unwrapQuote expr |> unwrapBlock
        match unwrapped with
        | null -> None
        | :? BlockExpression as be ->
            translatePredicate param plan dialect (unwrapBlock be)
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert || u.NodeType = ExpressionType.ConvertChecked || u.NodeType = ExpressionType.TypeAs ->
            translatePredicate param plan dialect u.Operand
        | :? ConditionalExpression as c ->
            match tryExtractBoolConstant c.IfTrue, tryExtractBoolConstant c.IfFalse with
            | Some true, Some false ->
                translatePredicate param plan dialect c.Test
            | Some false, Some true ->
                translatePredicate param plan dialect c.Test |> Option.map negateSqlPredicate
            | None, Some false ->
                match translatePredicate param plan dialect c.Test, translatePredicate param plan dialect c.IfTrue with
                | Some l, Some r -> Some $"({l} AND {r})"
                | _ -> None
            | Some true, None ->
                match translatePredicate param plan dialect c.Test, translatePredicate param plan dialect c.IfFalse with
                | Some l, Some r -> Some $"({l} OR {r})"
                | _ -> None
            | Some false, None ->
                match translatePredicate param plan dialect c.Test, translatePredicate param plan dialect c.IfFalse with
                | Some l, Some r -> Some $"({negateSqlPredicate l} AND {r})"
                | _ -> None
            | None, Some true ->
                match translatePredicate param plan dialect c.Test, translatePredicate param plan dialect c.IfTrue with
                | Some l, Some r -> Some $"({negateSqlPredicate l} OR {r})"
                | _ -> None
            | _ -> None
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

                let isBooleanPredicateComparison =
                    if op = "=" || op = "<>" then
                        let isCompoundBoolExpr (e: Expression) =
                            match unwrapQuote e |> unwrapBlock with
                            | :? MethodCallExpression | :? UnaryExpression | :? ConditionalExpression | :? BinaryExpression -> true
                            | _ -> false
                        match tryExtractBoolConstant b.Right with
                        | Some boolConst when isCompoundBoolExpr b.Left ->
                            match translatePredicate param plan dialect b.Left with
                            | Some predSql ->
                                let effectiveTrue = (op = "=" && boolConst) || (op = "<>" && not boolConst)
                                Some (if effectiveTrue then predSql else negateSqlPredicate predSql)
                            | None -> None
                        | _ ->
                            match tryExtractBoolConstant b.Left with
                            | Some boolConst when isCompoundBoolExpr b.Right ->
                                match translatePredicate param plan dialect b.Right with
                                | Some predSql ->
                                    let effectiveTrue = (op = "=" && boolConst) || (op = "<>" && not boolConst)
                                    Some (if effectiveTrue then predSql else negateSqlPredicate predSql)
                                | None -> None
                            | _ -> None
                    else None

                match isBooleanPredicateComparison with
                | Some sql -> Some sql
                | None ->
                    match tryTranslateSubqueryPredicate param plan dialect b with
                    | Some sql -> Some sql
                    | None ->
                        let leftHasParam = dependsOnParameter param b.Left
                        let rightHasParam = dependsOnParameter param b.Right
                        let leftHasOuter = hasAnyOuterParam plan b.Left || (hasAnyParameter b.Left && not leftHasParam)
                        let rightHasOuter = hasAnyOuterParam plan b.Right || (hasAnyParameter b.Right && not rightHasParam)

                        match translateScalarExpr dialect plan b.Left with
                        | Some lSql when not rightHasParam && not rightHasOuter ->
                            let rawVal = evaluateClientValue b.Right
                            let clientVal = unwrapClientOption rawVal
                            if isNull clientVal then
                                if op = "=" then Some $"({lSql} IS NULL)"
                                elif op = "<>" then Some $"({lSql} IS NOT NULL)"
                                else None
                            else
                                let pName = $"@p{plan.Parameters.Count}"
                                plan.Parameters.Add(pName, clientVal)
                                Some $"({lSql} {op} {pName})"
                        | _ ->
                            match translateScalarExpr dialect plan b.Right with
                            | Some rSql when not leftHasParam && not leftHasOuter ->
                                let rawVal = evaluateClientValue b.Left
                                let clientVal = unwrapClientOption rawVal
                                let invOp =
                                    match op with
                                    | ">" -> "<"
                                    | ">=" -> "<="
                                    | "<" -> ">"
                                    | "<=" -> ">="
                                    | other -> other
                                if isNull clientVal then
                                    if invOp = "=" then Some $"({rSql} IS NULL)"
                                    elif invOp = "<>" then Some $"({rSql} IS NOT NULL)"
                                    else None
                                else
                                    let pName = $"@p{plan.Parameters.Count}"
                                    plan.Parameters.Add(pName, clientVal)
                                    Some $"({rSql} {invOp} {pName})"
                            | _ ->
                                if (leftHasParam || leftHasOuter) && (rightHasParam || rightHasOuter) then
                                    let leftCol = match translateScalarExpr dialect plan b.Left with Some s -> s | None -> extractColumnNameWithAlias plan b.Left
                                    let rightCol = match translateScalarExpr dialect plan b.Right with Some s -> s | None -> extractColumnNameWithAlias plan b.Right
                                    if not (String.IsNullOrWhiteSpace leftCol) && not (String.IsNullOrWhiteSpace rightCol) then
                                        Some $"({leftCol} {op} {rightCol})"
                                    else
                                        None
                                elif (leftHasParam || leftHasOuter) && not (rightHasParam || rightHasOuter) then
                                    let col = extractColumnNameWithAlias plan b.Left
                                    let rawVal = evaluateClientValue b.Right
                                    let clientVal = unwrapClientOption rawVal
                                    if isNull clientVal then
                                        if op = "=" then Some $"({col} IS NULL)"
                                        elif op = "<>" then Some $"({col} IS NOT NULL)"
                                        else None
                                    else
                                        let pName = $"@p{plan.Parameters.Count}"
                                        plan.Parameters.Add(pName, clientVal)
                                        Some $"({col} {op} {pName})"
                                elif not (leftHasParam || leftHasOuter) && (rightHasParam || rightHasOuter) then
                                    let col = extractColumnNameWithAlias plan b.Right
                                    let rawVal = evaluateClientValue b.Left
                                    let clientVal = unwrapClientOption rawVal
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
                                else
                                    None
            | _ -> None
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Not || u.NodeType = ExpressionType.OnesComplement ->
            match translatePredicate param plan dialect u.Operand with
            | Some inner -> Some (negateSqlPredicate inner)
            | None | _ -> None
        | :? MethodCallExpression as mc ->
            let resolveColArg (arg: Expression) : string =
                match translateScalarExpr dialect plan arg with
                | Some s -> s
                | None -> extractColumnNameWithAlias plan arg
            let argDependsOnQuery (arg: Expression) : bool =
                dependsOnParameter param arg || hasAnyOuterParam plan arg || hasAnyParameter arg
            let effectiveSingleArg =
                if mc.Arguments.Count = 1 then Some mc.Arguments.[0]
                elif mc.Arguments.Count = 2 && not (isNull mc.Object) then Some mc.Arguments.[1]
                elif mc.Arguments.Count = 2 && mc.Arguments.[0].Type.Name.Contains("FSqlQueryBuilder") then Some mc.Arguments.[1]
                else None

            if (mc.Method.Name = "Not" || mc.Method.Name = "not" || mc.Method.Name = "op_BooleanNot") && mc.Arguments.Count = 1 then
                match translatePredicate param plan dialect mc.Arguments.[0] with
                | Some inner -> Some (negateSqlPredicate inner)
                | None -> None
            elif (mc.Method.Name = "IsNull" || mc.Method.Name = "isNull") && effectiveSingleArg.IsSome && argDependsOnQuery effectiveSingleArg.Value then
                let col = resolveColArg effectiveSingleArg.Value
                Some $"({col} IS NULL)"
            elif (mc.Method.Name = "isNotNull" || mc.Method.Name = "IsNotNull") && effectiveSingleArg.IsSome && argDependsOnQuery effectiveSingleArg.Value then
                let col = resolveColArg effectiveSingleArg.Value
                Some $"({col} IS NOT NULL)"
            elif (mc.Method.Name = "isNotEmpty" || mc.Method.Name = "IsNotEmpty" || mc.Method.Name = "isNotNullOrEmpty" || mc.Method.Name = "IsNotNullOrEmpty") && effectiveSingleArg.IsSome && argDependsOnQuery effectiveSingleArg.Value then
                let col = resolveColArg effectiveSingleArg.Value
                Some (formatNotEmptySql dialect col)
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "IsNullOrEmpty" && mc.Arguments.Count = 1 && argDependsOnQuery mc.Arguments.[0] then
                let col = resolveColArg mc.Arguments.[0]
                Some (formatIsEmptySql dialect col)
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "IsNullOrWhiteSpace" && mc.Arguments.Count = 1 && argDependsOnQuery mc.Arguments.[0] then
                let col = resolveColArg mc.Arguments.[0]
                Some $"({col} IS NULL OR TRIM({col}) = '')"
            elif (mc.Method.Name = "ReferenceEquals" || mc.Method.Name = "Equals" || mc.Method.Name = "op_Equality") && mc.Arguments.Count = 2 && (isNullConstantExpr mc.Arguments.[0] || isNullConstantExpr mc.Arguments.[1]) then
                let colExpr = if isNullConstantExpr mc.Arguments.[0] then mc.Arguments.[1] else mc.Arguments.[0]
                if argDependsOnQuery colExpr then
                    let col = resolveColArg colExpr
                    Some $"({col} IS NULL)"
                else None
            elif mc.Method.Name = "op_Inequality" && mc.Arguments.Count = 2 && (isNullConstantExpr mc.Arguments.[0] || isNullConstantExpr mc.Arguments.[1]) then
                let colExpr = if isNullConstantExpr mc.Arguments.[0] then mc.Arguments.[1] else mc.Arguments.[0]
                if argDependsOnQuery colExpr then
                    let col = resolveColArg colExpr
                    Some $"({col} IS NOT NULL)"
                else None
            elif mc.Method.Name = "Contains" then
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
                        let subPlan =
                            match tryFindPushdownSource sourceExpr with
                            | Some (_, Some ep) ->
                                ep.Clone()
                            | _ ->
                                PushdownQueryPlan.Create(subTable, [], ?baseRowType = subRowType)
                        for cte in subPlan.Ctes do
                            if not (plan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                                plan.Ctes.Add(cte)
                        subPlan.Ctes.Clear()
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
                    | None | _ ->
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
            elif mc.Method.Name = "Any" || mc.Method.Name = "exists" || mc.Method.Name = "Exists" || mc.Method.Name = "whereExists" then
                let sourceExpr =
                    if mc.Arguments.Count >= 1 then mc.Arguments.[0]
                    elif not (isNull mc.Object) then mc.Object
                    else null
                if not (isNull sourceExpr) then
                    match tryExtractQueryableInfo sourceExpr with
                    | Some (subTable, _, subRowType) ->
                        let subPlan =
                            match tryFindPushdownSource sourceExpr with
                            | Some (_, Some ep) ->
                                let cloned = ep.Clone()
                                cloned.BaseAlias <- sprintf "_sub%d" (plan.Joins.Count + 1)
                                if cloned.BaseRowType.IsNone then cloned.BaseRowType <- subRowType
                                cloned
                            | _ ->
                                PushdownQueryPlan.Create(subTable, [], baseAlias = sprintf "_sub%d" (plan.Joins.Count + 1), ?baseRowType = subRowType, ?dialect = plan.Dialect)
                        for cte in subPlan.Ctes do
                            if not (plan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                                plan.Ctes.Add(cte)
                        subPlan.Ctes.Clear()
                        subPlan.OuterAliases.[param.Name] <- plan.BaseAlias
                        for kv in plan.OuterAliases do
                            subPlan.OuterAliases.[kv.Key] <- kv.Value
                        subPlan.OuterAliases.[plan.BaseAlias] <- plan.BaseAlias
                        if plan.BaseRowType.IsSome then
                            subPlan.OuterAliases.[plan.BaseRowType.Value.Name] <- plan.BaseAlias
                        let targetExpr =
                            match unwrapQuote sourceExpr with
                            | :? ConstantExpression as ce when not (isNull ce.Value) ->
                                match ce.Value with
                                | :? IQueryable as iq -> iq.Expression
                                | _ -> sourceExpr
                            | other -> other
                        mergeExpressionIntoPlan targetExpr subPlan dialect
                        if mc.Arguments.Count >= 2 then
                            match unwrapQuote mc.Arguments.[1] with
                            | :? LambdaExpression as predLambda ->
                                match translatePredicate predLambda.Parameters.[0] subPlan dialect predLambda.Body with
                                | Some pSql -> subPlan.WhereClauses.Add(pSql)
                                | None | _ -> ()
                            | _ -> ()
                        subPlan.Projection <- Some "1"
                        let mutable subSql = subPlan.BuildSql(dialect)
                        for (pName, pVal) in subPlan.Parameters do
                            let uniqueName = $"@p{plan.Parameters.Count}"
                            subSql <- subSql.Replace(pName, uniqueName)
                            plan.Parameters.Add(uniqueName, pVal)
                        Some $"EXISTS ({subSql})"
                    | None | _ -> None
                else None
            elif mc.Method.Name = "notExists" || mc.Method.Name = "NotExists" || mc.Method.Name = "whereNotExists" then
                let sourceExpr =
                    if mc.Arguments.Count >= 1 then mc.Arguments.[0]
                    elif not (isNull mc.Object) then mc.Object
                    else null
                if not (isNull sourceExpr) then
                    match tryExtractQueryableInfo sourceExpr with
                    | Some (subTable, _, subRowType) ->
                        let subPlan =
                            match tryFindPushdownSource sourceExpr with
                            | Some (_, Some ep) ->
                                let cloned = ep.Clone()
                                cloned.BaseAlias <- sprintf "_sub%d" (plan.Joins.Count + 1)
                                if cloned.BaseRowType.IsNone then cloned.BaseRowType <- subRowType
                                cloned
                            | _ ->
                                PushdownQueryPlan.Create(subTable, [], baseAlias = sprintf "_sub%d" (plan.Joins.Count + 1), ?baseRowType = subRowType, ?dialect = plan.Dialect)
                        for cte in subPlan.Ctes do
                            if not (plan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                                plan.Ctes.Add(cte)
                        subPlan.Ctes.Clear()
                        subPlan.OuterAliases.[param.Name] <- plan.BaseAlias
                        for kv in plan.OuterAliases do
                            subPlan.OuterAliases.[kv.Key] <- kv.Value
                        subPlan.OuterAliases.[plan.BaseAlias] <- plan.BaseAlias
                        if plan.BaseRowType.IsSome then
                            subPlan.OuterAliases.[plan.BaseRowType.Value.Name] <- plan.BaseAlias
                        let targetExpr =
                            match unwrapQuote sourceExpr with
                            | :? ConstantExpression as ce when not (isNull ce.Value) ->
                                match ce.Value with
                                | :? IQueryable as iq -> iq.Expression
                                | _ -> sourceExpr
                            | other -> other
                        mergeExpressionIntoPlan targetExpr subPlan dialect
                        if mc.Arguments.Count >= 2 then
                            match unwrapQuote mc.Arguments.[1] with
                            | :? LambdaExpression as predLambda ->
                                match translatePredicate predLambda.Parameters.[0] subPlan dialect predLambda.Body with
                                | Some pSql -> subPlan.WhereClauses.Add(pSql)
                                | None | _ -> ()
                            | _ -> ()
                        subPlan.Projection <- Some "1"
                        let mutable subSql = subPlan.BuildSql(dialect)
                        for (pName, pVal) in subPlan.Parameters do
                            let uniqueName = $"@p{plan.Parameters.Count}"
                            subSql <- subSql.Replace(pName, uniqueName)
                            plan.Parameters.Add(uniqueName, pVal)
                        Some $"NOT EXISTS ({subSql})"
                    | None | _ -> None
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
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "IsNullOrEmpty" && mc.Arguments.Count = 1 && dependsOnParameter param mc.Arguments.[0] then
                let col = match translateScalarExpr dialect plan mc.Arguments.[0] with Some s -> s | None -> extractColumnNameWithAlias plan mc.Arguments.[0]
                Some $"({col} IS NULL OR {col} = '')"
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "IsNullOrWhiteSpace" && mc.Arguments.Count = 1 && dependsOnParameter param mc.Arguments.[0] then
                let col = match translateScalarExpr dialect plan mc.Arguments.[0] with Some s -> s | None -> extractColumnNameWithAlias plan mc.Arguments.[0]
                Some $"({col} IS NULL OR TRIM({col}) = '')"
            elif (mc.Method.Name = "isSome" || mc.Method.Name = "IsSome" || mc.Method.Name = "get_IsSome") && mc.Arguments.Count = 1 && dependsOnParameter param mc.Arguments.[0] && isOptionOrNullableType mc.Arguments.[0].Type then
                let col = extractColumnNameWithAlias plan mc.Arguments.[0]
                Some $"({col} IS NOT NULL)"
            elif (mc.Method.Name = "isNone" || mc.Method.Name = "IsNone" || mc.Method.Name = "get_IsNone") && mc.Arguments.Count = 1 && dependsOnParameter param mc.Arguments.[0] && isOptionOrNullableType mc.Arguments.[0].Type then
                let col = extractColumnNameWithAlias plan mc.Arguments.[0]
                Some $"({col} IS NULL)"
            else
                None
        | :? MemberExpression as m when not (isNull m.Expression) && isOptionOrNullableType m.Expression.Type && dependsOnParameter param m.Expression ->
            let col = extractColumnNameWithAlias plan m.Expression
            match m.Member.Name with
            | "IsSome" | "HasValue" -> Some $"({col} IS NOT NULL)"
            | "IsNone" -> Some $"({col} IS NULL)"
            | _ -> None
        | :? MemberExpression as m when m.Type = typeof<bool> && dependsOnParameter param m ->
            let col = extractColumnNameWithAlias plan m
            if dialect = SqlDialect.PostgreSql then
                Some $"({col} = TRUE)"
            else
                Some $"({col} = 1)"
        | _ -> None

    and tryUnwrapOptionArg (arg: Expression) : Expression option =
        let unwrapped = unwrapQuote arg
        match unwrapped with
        | null -> None
        | :? MethodCallExpression as mc when not (isNull mc.Method.DeclaringType) && mc.Method.DeclaringType.Name.StartsWith("FSharpOption") && mc.Method.Name = "Some" && mc.Arguments.Count > 0 ->
            Some (unwrapQuote mc.Arguments.[0])
        | :? NewExpression as ne when ne.Type.Name.StartsWith("FSharpOption") && ne.Arguments.Count > 0 ->
            Some (unwrapQuote ne.Arguments.[0])
        | :? MemberExpression as me when me.Member.Name = "Value" && me.Expression.Type.Name.StartsWith("FSharpOption") ->
            Some (unwrapQuote me.Expression)
        | other ->
            if not (isNull other.Type) && other.Type.Name.StartsWith("FSharpOption") then
                match other with
                | :? MemberExpression as me when me.Member.Name = "None" -> None
                | :? ConstantExpression as ce when isNull ce.Value -> None
                | _ -> None
            else
                Some other

    and parseWindowSpec (dialect: SqlDialect) (plan: PushdownQueryPlan) (winExpr: Expression) : string =
        let unwrapped = unwrapQuote winExpr
        match unwrapped with
        | :? MethodCallExpression as wmc when wmc.Method.Name = "window" || wmc.Method.Name = "Window" ->
            let getNamedOrIndexArg (paramName: string) (defaultIdx: int) =
                let pList = wmc.Method.GetParameters()
                let namedIdx = pList |> Array.tryFindIndex (fun p -> String.Equals(p.Name, paramName, StringComparison.OrdinalIgnoreCase))
                let idx = defaultArg namedIdx defaultIdx
                if idx < wmc.Arguments.Count then Some wmc.Arguments.[idx] else None

            let partitionArgOpt = getNamedOrIndexArg "partitionBy" 0 |> Option.bind tryUnwrapOptionArg
            let sortByArgOpt = getNamedOrIndexArg "sortBy" 1 |> Option.bind tryUnwrapOptionArg
            let sortByDescArgOpt = getNamedOrIndexArg "sortByDescending" 2 |> Option.bind tryUnwrapOptionArg

            let partitionSqlOpt =
                match partitionArgOpt with
                | Some pExpr ->
                    let unwrappedP = unwrapQuote pExpr
                    match unwrappedP with
                    | :? NewExpression as ne when not (isNull ne.Constructor) && ne.Constructor.DeclaringType.Name.StartsWith("Tuple") ->
                        let parts = [
                            for a in ne.Arguments do
                                match translateScalarExpr dialect plan a with
                                | Some s -> s
                                | None -> extractColumnNameWithAlias plan a
                        ]
                        if parts.Length > 0 then Some ("PARTITION BY " + String.concat ", " parts) else None
                    | other ->
                        match translateScalarExpr dialect plan other with
                        | Some s -> Some ("PARTITION BY " + s)
                        | None ->
                            let col = extractColumnNameWithAlias plan other
                            Some ("PARTITION BY " + col)
                | None -> None

            let orderParts = ResizeArray<string>()
            match sortByArgOpt with
            | Some sExpr ->
                let col =
                    match translateScalarExpr dialect plan sExpr with
                    | Some s -> s
                    | None -> extractColumnNameWithAlias plan sExpr
                orderParts.Add($"{col} ASC")
            | None -> ()

            match sortByDescArgOpt with
            | Some sdExpr ->
                let col =
                    match translateScalarExpr dialect plan sdExpr with
                    | Some s -> s
                    | None -> extractColumnNameWithAlias plan sdExpr
                orderParts.Add($"{col} DESC")
            | None -> ()

            let orderSqlOpt =
                if orderParts.Count > 0 then
                    Some ("ORDER BY " + String.concat ", " orderParts)
                else None

            match partitionSqlOpt, orderSqlOpt with
            | Some p, Some o -> $"OVER ({p} {o})"
            | Some p, None -> $"OVER ({p})"
            | None, Some o -> $"OVER ({o})"
            | None, None -> "OVER ()"
        | _ -> "OVER ()"

    and tryTranslateWindowFunction (dialect: SqlDialect) (plan: PushdownQueryPlan) (mc: MethodCallExpression) : string option =
        let mName = mc.Method.Name
        let isWinMethod =
            match mName with
            | "rowNumber" | "RowNumber"
            | "rank" | "Rank"
            | "denseRank" | "DenseRank"
            | "ntile" | "Ntile"
            | "lag" | "Lag"
            | "lead" | "Lead"
            | "firstValue" | "FirstValue"
            | "lastValue" | "LastValue"
            | "sumOver" | "SumOver"
            | "avgOver" | "AvgOver"
            | "minOver" | "MinOver"
            | "maxOver" | "MaxOver"
            | "countOver" | "CountOver" -> true
            | ("sum" | "Sum" | "avg" | "Avg" | "min" | "Min" | "max" | "Max" | "count" | "Count") ->
                mc.Method.GetParameters() |> Array.exists (fun p -> p.ParameterType = typeof<WindowDefinition> || p.ParameterType = typeof<WindowDefinition option> || p.Name = "over")
            | _ -> false

        if not isWinMethod then None
        else
            let overArgOpt =
                let pList = mc.Method.GetParameters()
                let overIdx = pList |> Array.tryFindIndex (fun p -> p.Name = "over" || p.ParameterType = typeof<WindowDefinition> || p.ParameterType = typeof<WindowDefinition option>)
                match overIdx with
                | Some idx when idx < mc.Arguments.Count -> tryUnwrapOptionArg mc.Arguments.[idx]
                | _ -> None

            let overClause =
                match overArgOpt with
                | Some winExpr -> parseWindowSpec dialect plan winExpr
                | None -> "OVER ()"

            let safeOverClause =
                if (dialect = SqlDialect.SqlServer || dialect = SqlDialect.Oracle) && (mName = "rowNumber" || mName = "RowNumber" || mName = "rank" || mName = "Rank" || mName = "denseRank" || mName = "DenseRank" || mName = "ntile" || mName = "Ntile") then
                    if not (overClause.Contains("ORDER BY")) then
                        let defaultOrder = if dialect = SqlDialect.Oracle then "ORDER BY NULL" else "ORDER BY (SELECT NULL)"
                        if overClause = "OVER ()" then $"OVER ({defaultOrder})"
                        else overClause.Replace(")", $" {defaultOrder})")
                    else overClause
                else
                    overClause

            match mName with
            | "rowNumber" | "RowNumber" -> Some $"ROW_NUMBER() {safeOverClause}"
            | "rank" | "Rank" -> Some $"RANK() {safeOverClause}"
            | "denseRank" | "DenseRank" -> Some $"DENSE_RANK() {safeOverClause}"
            | "ntile" | "Ntile" ->
                let tiles = if mc.Arguments.Count > 0 then string (evaluateClientValue mc.Arguments.[0]) else "1"
                Some $"NTILE({tiles}) {safeOverClause}"
            | "lag" | "Lag" ->
                let col = if mc.Arguments.Count > 0 then defaultArg (translateScalarExpr dialect plan mc.Arguments.[0]) (extractColumnNameWithAlias plan mc.Arguments.[0]) else "*"
                let pList = mc.Method.GetParameters()
                let offsetArg =
                    let oIdx = pList |> Array.tryFindIndex (fun p -> p.Name = "offset")
                    match oIdx with
                    | Some idx when idx < mc.Arguments.Count -> tryUnwrapOptionArg mc.Arguments.[idx]
                    | _ -> None
                let offsetStr =
                    match offsetArg with
                    | Some e -> string (evaluateClientValue e)
                    | None -> "1"
                let defaultArgOpt =
                    let dIdx = pList |> Array.tryFindIndex (fun p -> p.Name = "defaultValue")
                    match dIdx with
                    | Some idx when idx < mc.Arguments.Count -> tryUnwrapOptionArg mc.Arguments.[idx]
                    | _ -> None
                let defaultValStrOpt =
                    match defaultArgOpt with
                    | Some e ->
                        match translateScalarExpr dialect plan e with
                        | Some s -> Some s
                        | None ->
                            let v = evaluateClientValue e
                            match v with
                            | :? decimal as d -> Some (d.ToString(System.Globalization.CultureInfo.InvariantCulture))
                            | :? float as f -> Some (f.ToString(System.Globalization.CultureInfo.InvariantCulture))
                            | :? int as i -> Some (string i)
                            | :? string as s -> Some $"'{s}'"
                            | null -> Some "NULL"
                            | other -> Some (string other)
                    | None -> None
                match defaultValStrOpt with
                | Some defVal -> Some $"LAG({col}, {offsetStr}, {defVal}) {safeOverClause}"
                | None -> Some $"LAG({col}, {offsetStr}) {safeOverClause}"
            | "lead" | "Lead" ->
                let col = if mc.Arguments.Count > 0 then defaultArg (translateScalarExpr dialect plan mc.Arguments.[0]) (extractColumnNameWithAlias plan mc.Arguments.[0]) else "*"
                let pList = mc.Method.GetParameters()
                let offsetArg =
                    let oIdx = pList |> Array.tryFindIndex (fun p -> p.Name = "offset")
                    match oIdx with
                    | Some idx when idx < mc.Arguments.Count -> tryUnwrapOptionArg mc.Arguments.[idx]
                    | _ -> None
                let offsetStr =
                    match offsetArg with
                    | Some e -> string (evaluateClientValue e)
                    | None -> "1"
                let defaultArgOpt =
                    let dIdx = pList |> Array.tryFindIndex (fun p -> p.Name = "defaultValue")
                    match dIdx with
                    | Some idx when idx < mc.Arguments.Count -> tryUnwrapOptionArg mc.Arguments.[idx]
                    | _ -> None
                let defaultValStrOpt =
                    match defaultArgOpt with
                    | Some e ->
                        match translateScalarExpr dialect plan e with
                        | Some s -> Some s
                        | None ->
                            let v = evaluateClientValue e
                            match v with
                            | :? decimal as d -> Some (d.ToString(System.Globalization.CultureInfo.InvariantCulture))
                            | :? float as f -> Some (f.ToString(System.Globalization.CultureInfo.InvariantCulture))
                            | :? int as i -> Some (string i)
                            | :? string as s -> Some $"'{s}'"
                            | null -> Some "NULL"
                            | other -> Some (string other)
                    | None -> None
                match defaultValStrOpt with
                | Some defVal -> Some $"LEAD({col}, {offsetStr}, {defVal}) {safeOverClause}"
                | None -> Some $"LEAD({col}, {offsetStr}) {safeOverClause}"
            | "firstValue" | "FirstValue" ->
                let col = if mc.Arguments.Count > 0 then defaultArg (translateScalarExpr dialect plan mc.Arguments.[0]) (extractColumnNameWithAlias plan mc.Arguments.[0]) else "*"
                Some $"FIRST_VALUE({col}) {safeOverClause}"
            | "lastValue" | "LastValue" ->
                let col = if mc.Arguments.Count > 0 then defaultArg (translateScalarExpr dialect plan mc.Arguments.[0]) (extractColumnNameWithAlias plan mc.Arguments.[0]) else "*"
                Some $"LAST_VALUE({col}) {safeOverClause}"
            | "sum" | "Sum" | "sumOver" | "SumOver" ->
                let col = if mc.Arguments.Count > 0 then defaultArg (translateScalarExpr dialect plan mc.Arguments.[0]) (extractColumnNameWithAlias plan mc.Arguments.[0]) else "*"
                Some $"SUM({col}) {safeOverClause}"
            | "avg" | "Avg" | "avgOver" | "AvgOver" ->
                let col = if mc.Arguments.Count > 0 then defaultArg (translateScalarExpr dialect plan mc.Arguments.[0]) (extractColumnNameWithAlias plan mc.Arguments.[0]) else "*"
                Some $"AVG({col}) {safeOverClause}"
            | "min" | "Min" | "minOver" | "MinOver" ->
                let col = if mc.Arguments.Count > 0 then defaultArg (translateScalarExpr dialect plan mc.Arguments.[0]) (extractColumnNameWithAlias plan mc.Arguments.[0]) else "*"
                Some $"MIN({col}) {safeOverClause}"
            | "max" | "Max" | "maxOver" | "MaxOver" ->
                let col = if mc.Arguments.Count > 0 then defaultArg (translateScalarExpr dialect plan mc.Arguments.[0]) (extractColumnNameWithAlias plan mc.Arguments.[0]) else "*"
                Some $"MAX({col}) {safeOverClause}"
            | "count" | "Count" | "countOver" | "CountOver" ->
                let col =
                    if mc.Arguments.Count > 0 && not (mc.Arguments.[0].Type.Name.StartsWith("WindowDefinition")) && not (mc.Arguments.[0].Type.Name.StartsWith("FSharpOption`1[FSharpQueryStudio.Connectors.WindowDefinition")) then
                        defaultArg (translateScalarExpr dialect plan mc.Arguments.[0]) (extractColumnNameWithAlias plan mc.Arguments.[0])
                    else "*"
                Some $"COUNT({col}) {safeOverClause}"
            | _ -> None

    and tryTranslateJsonValue (dialect: SqlDialect) (plan: PushdownQueryPlan) (mc: MethodCallExpression) : string option =
        if mc.Method.Name = "jsonValue" || mc.Method.Name = "JsonValue" then
            if mc.Arguments.Count >= 2 then
                let colSql = defaultArg (translateScalarExpr dialect plan mc.Arguments.[0]) (extractColumnNameWithAlias plan mc.Arguments.[0])
                let rawPath = evaluateClientValue mc.Arguments.[1]
                let pathStr = if isNull rawPath then "$" else string rawPath
                let normalizedPath =
                    if String.IsNullOrEmpty pathStr || pathStr = "$" then "$"
                    elif not (pathStr.StartsWith("$")) then
                        if pathStr.StartsWith(".") then "$" + pathStr else "$." + pathStr
                    else pathStr
                match dialect with
                | SqlDialect.SqlServer | SqlDialect.Oracle ->
                    Some $"JSON_VALUE({colSql}, '{normalizedPath}')"
                | SqlDialect.Sqlite ->
                    Some $"json_extract({colSql}, '{normalizedPath}')"
                | SqlDialect.MySql | SqlDialect.MariaDb ->
                    Some $"JSON_UNQUOTE(JSON_EXTRACT({colSql}, '{normalizedPath}'))"
                | SqlDialect.PostgreSql ->
                    let trimmed =
                        if normalizedPath.StartsWith("$.") then normalizedPath.Substring(2)
                        elif normalizedPath.StartsWith("$") then normalizedPath.Substring(1)
                        else normalizedPath
                    if trimmed.Contains(".") then
                        let parts = trimmed.Split('.') |> String.concat "', '"
                        Some $"({colSql} #>> '{{{parts}}}')"
                    else
                        Some $"({colSql} ->> '{trimmed}')"
            else None
        else None

    and translateScalarCondition (dialect: SqlDialect) (plan: PushdownQueryPlan) (expr: Expression) : string option =
        let unwrapped = unwrapBlock (unwrapQuote expr)
        match unwrapped with
        | :? BinaryExpression as b when b.NodeType = ExpressionType.AndAlso ->
            match translateScalarCondition dialect plan b.Left, translateScalarCondition dialect plan b.Right with
            | Some l, Some r -> Some $"({l} AND {r})"
            | _ -> None
        | :? BinaryExpression as b when b.NodeType = ExpressionType.OrElse ->
            match translateScalarCondition dialect plan b.Left, translateScalarCondition dialect plan b.Right with
            | Some l, Some r -> Some $"({l} OR {r})"
            | _ -> None
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
            | Some inner -> Some (negateSqlPredicate inner)
            | None | _ -> None
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert || u.NodeType = ExpressionType.ConvertChecked || u.NodeType = ExpressionType.TypeAs ->
            translateScalarCondition dialect plan u.Operand
        | :? ConditionalExpression as ce ->
            let isConstTrue (e: Expression) =
                match unwrapBlock (unwrapQuote e) with
                | :? ConstantExpression as c when c.Type = typeof<bool> -> (c.Value :?> bool) = true
                | _ -> false
            let isConstFalse (e: Expression) =
                match unwrapBlock (unwrapQuote e) with
                | :? ConstantExpression as c when c.Type = typeof<bool> -> (c.Value :?> bool) = false
                | _ -> false
            if isConstTrue ce.IfTrue && isConstFalse ce.IfFalse then
                translateScalarCondition dialect plan ce.Test
            elif isConstFalse ce.IfTrue && isConstTrue ce.IfFalse then
                match translateScalarCondition dialect plan ce.Test with
                | Some inner -> Some (negateSqlPredicate inner)
                | None -> None
            else None
        | :? MemberExpression as m when not (isNull m.Expression) && isOptionOrNullableType m.Expression.Type && (m.Member.Name = "HasValue" || m.Member.Name = "IsSome") ->
            match translateScalarExpr dialect plan m.Expression with
            | Some s -> Some $"({s} IS NOT NULL)"
            | None | _ ->
                let col = extractColumnNameWithAlias plan m.Expression
                Some $"({col} IS NOT NULL)"
        | :? MemberExpression as m when not (isNull m.Expression) && isOptionOrNullableType m.Expression.Type && m.Member.Name = "IsNone" ->
            match translateScalarExpr dialect plan m.Expression with
            | Some s -> Some $"({s} IS NULL)"
            | None | _ ->
                let col = extractColumnNameWithAlias plan m.Expression
                Some $"({col} IS NULL)"
        | :? MemberExpression as m when m.Type = typeof<bool> ->
            match translateScalarExpr dialect plan m with
            | Some s ->
                if dialect = SqlDialect.PostgreSql then Some $"({s} = TRUE)"
                else Some $"({s} = 1)"
            | None | _ -> None
        | :? MethodCallExpression as mc when (mc.Method.Name = "IsNull" || mc.Method.Name = "isNull") && mc.Arguments.Count = 1 ->
            match translateScalarExpr dialect plan mc.Arguments.[0] with
            | Some s -> Some $"({s} IS NULL)"
            | None -> None
        | :? MethodCallExpression as mc when (mc.Method.Name = "isNotNull" || mc.Method.Name = "IsNotNull") && mc.Arguments.Count = 1 ->
            match translateScalarExpr dialect plan mc.Arguments.[0] with
            | Some s -> Some $"({s} IS NOT NULL)"
            | None -> None
        | :? MethodCallExpression as mc when (mc.Method.Name = "isNotEmpty" || mc.Method.Name = "isNotNullOrEmpty" || mc.Method.Name = "IsNotEmpty" || mc.Method.Name = "IsNotNullOrEmpty") && mc.Arguments.Count = 1 ->
            match translateScalarExpr dialect plan mc.Arguments.[0] with
            | Some s -> Some (formatNotEmptySql dialect s)
            | None -> None
        | :? MethodCallExpression as mc when mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "IsNullOrEmpty" && mc.Arguments.Count = 1 ->
            match translateScalarExpr dialect plan mc.Arguments.[0] with
            | Some s -> Some (formatIsEmptySql dialect s)
            | None | _ -> None
        | :? MethodCallExpression as mc when mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "IsNullOrWhiteSpace" && mc.Arguments.Count = 1 ->
            match translateScalarExpr dialect plan mc.Arguments.[0] with
            | Some s -> Some $"({s} IS NULL OR TRIM({s}) = '')"
            | None | _ -> None
        | :? MethodCallExpression as mc when mc.Method.DeclaringType = typeof<string> && mc.Arguments.Count = 1 && (mc.Method.Name = "Contains" || mc.Method.Name = "StartsWith" || mc.Method.Name = "EndsWith") ->
            match translateScalarExpr dialect plan mc.Object, translateScalarExpr dialect plan mc.Arguments.[0] with
            | Some s, Some sub ->
                match mc.Method.Name with
                | "Contains" -> Some $"({s} LIKE '%%' || {sub} || '%%')"
                | "StartsWith" -> Some $"({s} LIKE {sub} || '%%')"
                | "EndsWith" -> Some $"({s} LIKE '%%' || {sub})"
                | _ -> None
            | _ -> None
        | _ -> None

    and translateScalarExpr (dialect: SqlDialect) (plan: PushdownQueryPlan) (expr: Expression) : string option =
        let unwrapped = unwrapBlock (unwrapQuote expr)
        match unwrapped with
        | null -> None
        | :? ConstantExpression as c when not (isNull c.Value) && typeof<IQueryable>.IsAssignableFrom(c.Type) ->
            let iq = c.Value :?> IQueryable
            match tryFindPushdownSource iq.Expression with
            | Some (sourceIq, seedPlan) ->
                let t = sourceIq.Provider.GetType()
                let m = t.GetMethod("ToSqlWithParameters", [| typeof<Expression>; typeof<PushdownQueryPlan option> |])
                if not (isNull m) then
                    let res = m.Invoke(sourceIq.Provider, [| box iq.Expression; box seedPlan |]) :?> (string * (string * obj) list)
                    let subSql, subParams = res
                    let mutable remappedSql = subSql
                    for (pName, pVal) in subParams do
                        let uniqueName = $"@p{plan.Parameters.Count}"
                        remappedSql <- remappedSql.Replace(pName, uniqueName)
                        plan.Parameters.Add(uniqueName, pVal)
                    Some $"({remappedSql})"
                else None
            | None | _ -> None
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
        | :? MemberExpression as m when not (isNull m.Expression) && (m.Expression.Type = typeof<DateTime> || m.Expression.Type = typeof<DateOnly> || m.Expression.Type = typeof<DateTimeOffset>) &&
                                         (m.Member.Name = "Date" || m.Member.Name = "Year" || m.Member.Name = "Month" || m.Member.Name = "Day" || m.Member.Name = "Hour" || m.Member.Name = "Minute" || m.Member.Name = "Second") ->
            match translateScalarExpr dialect plan m.Expression with
            | Some o ->
                match m.Member.Name with
                | "Year" ->
                    match dialect with
                    | SqlDialect.Sqlite -> Some $"CAST(strftime('%%Y', {o}) AS INTEGER)"
                    | SqlDialect.SqlServer | SqlDialect.MySql | SqlDialect.MariaDb -> Some $"YEAR({o})"
                    | SqlDialect.PostgreSql | SqlDialect.Oracle -> Some $"EXTRACT(YEAR FROM {o})"
                | "Month" ->
                    match dialect with
                    | SqlDialect.Sqlite -> Some $"CAST(strftime('%%m', {o}) AS INTEGER)"
                    | SqlDialect.SqlServer | SqlDialect.MySql | SqlDialect.MariaDb -> Some $"MONTH({o})"
                    | SqlDialect.PostgreSql | SqlDialect.Oracle -> Some $"EXTRACT(MONTH FROM {o})"
                | "Day" ->
                    match dialect with
                    | SqlDialect.Sqlite -> Some $"CAST(strftime('%%d', {o}) AS INTEGER)"
                    | SqlDialect.SqlServer | SqlDialect.MySql | SqlDialect.MariaDb -> Some $"DAY({o})"
                    | SqlDialect.PostgreSql | SqlDialect.Oracle -> Some $"EXTRACT(DAY FROM {o})"
                | "Date" ->
                    match dialect with
                    | SqlDialect.Sqlite -> Some $"date({o})"
                    | SqlDialect.SqlServer | SqlDialect.PostgreSql -> Some $"CAST({o} AS DATE)"
                    | SqlDialect.MySql | SqlDialect.MariaDb -> Some $"DATE({o})"
                    | SqlDialect.Oracle -> Some $"TRUNC({o})"
                | "Hour" ->
                    match dialect with
                    | SqlDialect.Sqlite -> Some $"CAST(strftime('%%H', {o}) AS INTEGER)"
                    | SqlDialect.SqlServer -> Some $"DATEPART(HOUR, {o})"
                    | SqlDialect.MySql | SqlDialect.MariaDb -> Some $"HOUR({o})"
                    | SqlDialect.Oracle -> Some $"TO_NUMBER(TO_CHAR({o}, 'HH24'))"
                    | SqlDialect.PostgreSql -> Some $"EXTRACT(HOUR FROM {o})"
                | "Minute" ->
                    match dialect with
                    | SqlDialect.Sqlite -> Some $"CAST(strftime('%%M', {o}) AS INTEGER)"
                    | SqlDialect.SqlServer -> Some $"DATEPART(MINUTE, {o})"
                    | SqlDialect.MySql | SqlDialect.MariaDb -> Some $"MINUTE({o})"
                    | SqlDialect.Oracle -> Some $"TO_NUMBER(TO_CHAR({o}, 'MI'))"
                    | SqlDialect.PostgreSql -> Some $"EXTRACT(MINUTE FROM {o})"
                | "Second" ->
                    match dialect with
                    | SqlDialect.Sqlite -> Some $"CAST(strftime('%%S', {o}) AS INTEGER)"
                    | SqlDialect.SqlServer -> Some $"DATEPART(SECOND, {o})"
                    | SqlDialect.MySql | SqlDialect.MariaDb -> Some $"SECOND({o})"
                    | SqlDialect.Oracle -> Some $"TO_NUMBER(TO_CHAR({o}, 'SS'))"
                    | SqlDialect.PostgreSql -> Some $"EXTRACT(SECOND FROM {o})"
                | _ -> None
            | None | _ -> None
        | :? MemberExpression as m when not (isNull m.Expression) && m.Expression.Type = typeof<string> && m.Member.Name = "Length" ->
            match translateScalarExpr dialect plan m.Expression with
            | Some o ->
                match dialect with
                | SqlDialect.SqlServer -> Some $"LEN({o})"
                | _ -> Some $"LENGTH({o})"
            | None | _ -> None
        | :? MemberExpression as m ->
            let aliasOpt, colName = resolveMemberRef plan m
            match aliasOpt with
            | Some alias -> Some $"{alias}.{colName}"
            | None | _ ->
                match m.Expression with
                | null ->
                    let v = evaluateClientValue expr
                    translateScalarExpr dialect plan (Expression.Constant(v, expr.Type))
                | _ ->
                    if plan.Joins.Count > 0 && not (colName.Contains ".") then
                        Some $"{plan.BaseAlias}.{colName}"
                    else
                        Some colName
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert || u.NodeType = ExpressionType.ConvertChecked || u.NodeType = ExpressionType.TypeAs ->
            translateScalarExpr dialect plan u.Operand
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Not ->
            match translateScalarCondition dialect plan u.Operand with
            | Some inner -> Some (negateSqlPredicate inner)
            | None -> None
        | :? BinaryExpression as b ->
            match b.NodeType with
            | ExpressionType.Add ->
                if b.Type = typeof<string> then
                    match translateScalarExpr dialect plan b.Left, translateScalarExpr dialect plan b.Right with
                    | Some l, Some r ->
                        match dialect with
                        | SqlDialect.SqlServer -> Some $"({l} + {r})"
                        | SqlDialect.MySql | SqlDialect.MariaDb -> Some $"CONCAT({l}, {r})"
                        | _ -> Some $"({l} || {r})"
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
            | ExpressionType.Modulo ->
                match translateScalarExpr dialect plan b.Left, translateScalarExpr dialect plan b.Right with
                | Some l, Some r -> Some $"({l} %% {r})"
                | _ -> None
            | _ -> None
        | :? NewExpression as ne when isOptionOrNullableType ne.Type ->
            if ne.Arguments.Count = 0 then Some "NULL"
            else translateScalarExpr dialect plan ne.Arguments.[0]
        | :? DefaultExpression as de when isOptionOrNullableType de.Type ->
            Some "NULL"
        | :? ConditionalExpression as c ->
            let rec isNullCheck (e: Expression) : bool =
                match unwrapBlock (unwrapQuote e) with
                | :? MethodCallExpression as mc when mc.Method.Name = "IsNull" || mc.Method.Name = "ReferenceEquals" || mc.Method.Name = "isNull" || mc.Method.Name = "whereIsNull" -> true
                | :? ConditionalExpression as ce ->
                    let isConstTrue (x: Expression) =
                        match unwrapBlock (unwrapQuote x) with
                        | :? ConstantExpression as k when k.Type = typeof<bool> -> (k.Value :?> bool) = true
                        | _ -> false
                    let isConstFalse (x: Expression) =
                        match unwrapBlock (unwrapQuote x) with
                        | :? ConstantExpression as k when k.Type = typeof<bool> -> (k.Value :?> bool) = false
                        | _ -> false
                    isConstTrue ce.IfTrue && isConstFalse ce.IfFalse && isNullCheck ce.Test
                | :? BinaryExpression as b when b.NodeType = ExpressionType.Equal ->
                    let isNullConst (x: Expression) =
                        match unwrapBlock (unwrapQuote x) with
                        | :? ConstantExpression as ce -> isNull ce.Value
                        | _ -> false
                    isNullConst b.Left || isNullConst b.Right
                | _ -> false

            let rec isNotNullCheck (e: Expression) : bool =
                match unwrapBlock (unwrapQuote e) with
                | :? UnaryExpression as u when u.NodeType = ExpressionType.Not -> isNullCheck u.Operand
                | :? MethodCallExpression as mc when mc.Method.Name = "isNotNull" || mc.Method.Name = "IsNotNull" -> true
                | :? ConditionalExpression as ce ->
                    let isConstTrue (x: Expression) =
                        match unwrapBlock (unwrapQuote x) with
                        | :? ConstantExpression as k when k.Type = typeof<bool> -> (k.Value :?> bool) = true
                        | _ -> false
                    let isConstFalse (x: Expression) =
                        match unwrapBlock (unwrapQuote x) with
                        | :? ConstantExpression as k when k.Type = typeof<bool> -> (k.Value :?> bool) = false
                        | _ -> false
                    isConstFalse ce.IfTrue && isConstTrue ce.IfFalse && isNullCheck ce.Test
                | :? BinaryExpression as b when b.NodeType = ExpressionType.NotEqual ->
                    let isNullConst (x: Expression) =
                        match unwrapBlock (unwrapQuote x) with
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
            let isGrouping =
                (not (isNull mc.Method.DeclaringType) && isGroupingType mc.Method.DeclaringType)
                || (mc.Arguments.Count > 0 && isGroupingType mc.Arguments.[0].Type)
                || (not (isNull mc.Object) && isGroupingType mc.Object.Type)

            let subquerySqlOpt = tryTranslateSubqueryScalarExpr dialect plan mc
            let winOpt = if subquerySqlOpt.IsSome then None else tryTranslateWindowFunction dialect plan mc
            let jsonOpt = if subquerySqlOpt.IsSome || winOpt.IsSome then None else tryTranslateJsonValue dialect plan mc

            if subquerySqlOpt.IsSome then
                subquerySqlOpt
            elif winOpt.IsSome then
                winOpt
            elif jsonOpt.IsSome then
                jsonOpt
            elif isGrouping then
                match mc.Method.Name with
                | "Count" | "LongCount" -> Some "COUNT(*)"
                | "Sum" ->
                    let col = if mc.Arguments.Count >= 2 then extractColumnNameWithAlias plan mc.Arguments.[1] else "*"
                    Some $"SUM({col})"
                | "Min" ->
                    let col = if mc.Arguments.Count >= 2 then extractColumnNameWithAlias plan mc.Arguments.[1] else "*"
                    Some $"MIN({col})"
                | "Max" ->
                    let col = if mc.Arguments.Count >= 2 then extractColumnNameWithAlias plan mc.Arguments.[1] else "*"
                    Some $"MAX({col})"
                | "Average" | "Avg" ->
                    let col = if mc.Arguments.Count >= 2 then extractColumnNameWithAlias plan mc.Arguments.[1] else "*"
                    Some $"AVG({col})"
                | _ -> None
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "Concat" then
                let argParts = mc.Arguments |> Seq.map (translateScalarExpr dialect plan) |> Seq.toList
                if argParts |> List.forall Option.isSome then
                    let parts = argParts |> List.map Option.get
                    match dialect with
                    | SqlDialect.SqlServer ->
                        let joined = String.concat " + " parts
                        Some $"({joined})"
                    | SqlDialect.MySql | SqlDialect.MariaDb ->
                        let joined = String.concat ", " parts
                        Some $"CONCAT({joined})"
                    | _ ->
                        let joined = String.concat " || " parts
                        Some $"({joined})"
                else None
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "ToUpper" && mc.Arguments.Count = 0 then
                match translateScalarExpr dialect plan mc.Object with
                | Some o -> Some $"UPPER({o})"
                | None | _ -> None
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "ToLower" && mc.Arguments.Count = 0 then
                match translateScalarExpr dialect plan mc.Object with
                | Some o -> Some $"LOWER({o})"
                | None | _ -> None
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "Trim" && mc.Arguments.Count = 0 then
                match translateScalarExpr dialect plan mc.Object with
                | Some o -> Some $"TRIM({o})"
                | None | _ -> None
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "IsNullOrEmpty" && mc.Arguments.Count = 1 then
                match translateScalarExpr dialect plan mc.Arguments.[0] with
                | Some s -> Some $"({s} IS NULL OR {s} = '')"
                | None | _ -> None
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "IsNullOrWhiteSpace" && mc.Arguments.Count = 1 then
                match translateScalarExpr dialect plan mc.Arguments.[0] with
                | Some s -> Some $"({s} IS NULL OR TRIM({s}) = '')"
                | None | _ -> None
            elif mc.Method.Name = "Count" then
                let isDistinct =
                    let isD (e: Expression) =
                        match unwrapQuote e with
                        | :? MethodCallExpression as innerMc -> innerMc.Method.Name = "Distinct"
                        | _ -> false
                    (mc.Arguments.Count > 0 && isD mc.Arguments.[0]) || (not (isNull mc.Object) && isD mc.Object)

                let innerCol =
                    let extractFromSource (e: Expression) =
                        match unwrapQuote e with
                        | :? MethodCallExpression as innerMc when innerMc.Method.Name = "Select" && innerMc.Arguments.Count >= 2 ->
                            extractColumnNameWithAlias plan innerMc.Arguments.[1]
                        | _ -> ""
                    let fromArg = if mc.Arguments.Count > 0 then extractFromSource mc.Arguments.[0] else ""
                    if fromArg <> "" then fromArg
                    elif not (isNull mc.Object) then extractFromSource mc.Object
                    else ""

                if mc.Arguments.Count >= 2 then
                    let arg1 = unwrapQuote mc.Arguments.[1]
                    match arg1 with
                    | :? LambdaExpression as l ->
                        let col = extractColumnNameWithAlias plan l.Body
                        let dPrefix = if isDistinct || plan.IsDistinct then "DISTINCT " else ""
                        Some $"COUNT({dPrefix}{col})"
                    | _ ->
                        match translateScalarExpr dialect plan mc.Arguments.[1] with
                        | Some col ->
                            let dPrefix = if isDistinct || plan.IsDistinct then "DISTINCT " else ""
                            Some $"COUNT({dPrefix}{col})"
                        | None | _ -> Some (if isDistinct then "COUNT(DISTINCT *)" else "COUNT(*)")
                elif innerCol <> "" then
                    let dPrefix = if isDistinct || plan.IsDistinct then "DISTINCT " else ""
                    Some $"COUNT({dPrefix}{innerCol})"
                else
                    let dPrefix = if isDistinct || plan.IsDistinct then "DISTINCT " else ""
                    Some $"COUNT({dPrefix}*)"
            elif mc.Method.Name = "LongCount" then
                let dPrefix = if plan.IsDistinct then "DISTINCT " else ""
                Some $"COUNT({dPrefix}*)"
            elif mc.Method.Name = "Sum" then
                let isDistinct =
                    let isD (e: Expression) =
                        match unwrapQuote e with
                        | :? MethodCallExpression as innerMc -> innerMc.Method.Name = "Distinct"
                        | _ -> false
                    (mc.Arguments.Count > 0 && isD mc.Arguments.[0]) || (not (isNull mc.Object) && isD mc.Object) || plan.IsDistinct
                let dPrefix = if isDistinct then "DISTINCT " else ""
                let colOpt =
                    if mc.Arguments.Count >= 2 then
                        match unwrapQuote mc.Arguments.[1] with
                        | :? LambdaExpression as l -> Some (extractColumnNameWithAlias plan l.Body)
                        | other -> translateScalarExpr dialect plan other
                    elif not (isNull mc.Object) then
                        translateScalarExpr dialect plan mc.Object
                    elif mc.Arguments.Count = 1 then
                        translateScalarExpr dialect plan mc.Arguments.[0]
                    else None
                match colOpt with
                | Some col -> Some $"SUM({dPrefix}{col})"
                | None | _ -> None
            elif mc.Method.Name = "Min" then
                let colOpt =
                    if mc.Arguments.Count >= 2 then
                        match unwrapQuote mc.Arguments.[1] with
                        | :? LambdaExpression as l -> Some (extractColumnNameWithAlias plan l.Body)
                        | other -> translateScalarExpr dialect plan other
                    elif not (isNull mc.Object) then
                        translateScalarExpr dialect plan mc.Object
                    elif mc.Arguments.Count = 1 then
                        translateScalarExpr dialect plan mc.Arguments.[0]
                    else None
                match colOpt with
                | Some col -> Some $"MIN({col})"
                | None | _ -> None
            elif mc.Method.Name = "Max" then
                let colOpt =
                    if mc.Arguments.Count >= 2 then
                        match unwrapQuote mc.Arguments.[1] with
                        | :? LambdaExpression as l -> Some (extractColumnNameWithAlias plan l.Body)
                        | other -> translateScalarExpr dialect plan other
                    elif not (isNull mc.Object) then
                        translateScalarExpr dialect plan mc.Object
                    elif mc.Arguments.Count = 1 then
                        translateScalarExpr dialect plan mc.Arguments.[0]
                    else None
                match colOpt with
                | Some col -> Some $"MAX({col})"
                | None | _ -> None
            elif (mc.Method.Name = "Average" || mc.Method.Name = "Avg") then
                let colOpt =
                    if mc.Arguments.Count >= 2 then
                        match unwrapQuote mc.Arguments.[1] with
                        | :? LambdaExpression as l -> Some (extractColumnNameWithAlias plan l.Body)
                        | other -> translateScalarExpr dialect plan other
                    elif not (isNull mc.Object) then
                        translateScalarExpr dialect plan mc.Object
                    elif mc.Arguments.Count = 1 then
                        translateScalarExpr dialect plan mc.Arguments.[0]
                    else None
                match colOpt with
                | Some col -> Some $"AVG({col})"
                | None | _ -> None
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
                        | SqlDialect.MySql | SqlDialect.MariaDb ->
                            let myFmt =
                                fmt.Replace("yyyy", "%Y")
                                   .Replace("YYYY", "%Y")
                                   .Replace("yy", "%y")
                                   .Replace("MM", "%m")
                                   .Replace("dd", "%d")
                                   .Replace("DD", "%d")
                                   .Replace("HH", "%H")
                                   .Replace("hh", "%h")
                                   .Replace("mm", "%i")
                                   .Replace("ss", "%s")
                                   .Replace("SS", "%s")
                            Some $"DATE_FORMAT({o}, '{myFmt}')"
                        | SqlDialect.Oracle ->
                            let oraFmt =
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
                            Some $"TO_CHAR({o}, '{oraFmt}')"
                    | None | _ ->
                        match dialect with
                        | SqlDialect.Sqlite -> Some $"CAST({o} AS TEXT)"
                        | SqlDialect.SqlServer -> Some $"CAST({o} AS NVARCHAR(MAX))"
                        | SqlDialect.PostgreSql -> Some $"CAST({o} AS TEXT)"
                        | SqlDialect.MySql | SqlDialect.MariaDb -> Some $"CAST({o} AS CHAR)"
                        | SqlDialect.Oracle -> Some $"TO_CHAR({o})"
                | None | _ -> None
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
                | None | _ -> None
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "Replace" && mc.Arguments.Count = 2 then
                match translateScalarExpr dialect plan mc.Object, translateScalarExpr dialect plan mc.Arguments.[0], translateScalarExpr dialect plan mc.Arguments.[1] with
                | Some o, Some oldVal, Some newVal -> Some $"REPLACE({o}, {oldVal}, {newVal})"
                | _ -> None
            elif mc.Method.DeclaringType = typeof<Math> && mc.Method.Name = "Round" then
                match mc.Arguments.Count with
                | 1 ->
                    match translateScalarExpr dialect plan mc.Arguments.[0] with
                    | Some v -> Some $"ROUND({v})"
                    | None | _ -> None
                | 2 ->
                    match translateScalarExpr dialect plan mc.Arguments.[0], translateScalarExpr dialect plan mc.Arguments.[1] with
                    | Some v, Some digits -> Some $"ROUND({v}, {digits})"
                    | _ -> None
                | _ -> None
            elif mc.Method.DeclaringType = typeof<Math> && mc.Method.Name = "Abs" && mc.Arguments.Count = 1 then
                match translateScalarExpr dialect plan mc.Arguments.[0] with
                | Some v -> Some $"ABS({v})"
                | None | _ -> None
            elif mc.Method.DeclaringType = typeof<Math> && mc.Method.Name = "Floor" && mc.Arguments.Count = 1 then
                match translateScalarExpr dialect plan mc.Arguments.[0] with
                | Some v -> Some $"FLOOR({v})"
                | None | _ -> None
            elif mc.Method.DeclaringType = typeof<Math> && mc.Method.Name = "Ceiling" && mc.Arguments.Count = 1 then
                match translateScalarExpr dialect plan mc.Arguments.[0] with
                | Some v ->
                    match dialect with
                    | SqlDialect.PostgreSql | SqlDialect.Sqlite | SqlDialect.Oracle -> Some $"CEIL({v})"
                    | _ -> Some $"CEILING({v})"
                | None | _ -> None
            elif mc.Method.DeclaringType = typeof<Math> && mc.Method.Name = "Sqrt" && mc.Arguments.Count = 1 then
                match translateScalarExpr dialect plan mc.Arguments.[0] with
                | Some v -> Some $"SQRT({v})"
                | None | _ -> None
            elif mc.Method.DeclaringType = typeof<Math> && mc.Method.Name = "Pow" && mc.Arguments.Count = 2 then
                match translateScalarExpr dialect plan mc.Arguments.[0], translateScalarExpr dialect plan mc.Arguments.[1] with
                | Some x, Some y -> Some $"POWER({x}, {y})"
                | _ -> None
            elif mc.Method.Name = "GetValueOrDefault" then
                let targetOpt =
                    if not (isNull mc.Object) then translateScalarExpr dialect plan mc.Object
                    elif mc.Arguments.Count > 0 then translateScalarExpr dialect plan mc.Arguments.[0]
                    else None
                match targetOpt with
                | Some targetSql ->
                    if mc.Arguments.Count = 1 && not (isNull mc.Object) then
                        match translateScalarExpr dialect plan mc.Arguments.[0] with
                        | Some defSql -> Some $"COALESCE({targetSql}, {defSql})"
                        | None | _ -> Some $"COALESCE({targetSql}, 0)"
                    elif mc.Arguments.Count = 2 && isNull mc.Object then
                        match translateScalarExpr dialect plan mc.Arguments.[1] with
                        | Some defSql -> Some $"COALESCE({targetSql}, {defSql})"
                        | None | _ -> Some $"COALESCE({targetSql}, 0)"
                    else
                        Some $"COALESCE({targetSql}, 0)"
                | None | _ -> None
            elif (mc.Method.Name = "DefaultValue" || mc.Method.Name = "defaultValue") && mc.Arguments.Count = 2 then
                match translateScalarExpr dialect plan mc.Arguments.[1], translateScalarExpr dialect plan mc.Arguments.[0] with
                | Some optSql, Some defSql -> Some $"COALESCE({optSql}, {defSql})"
                | _ -> None
            elif (mc.Method.Name = "DefaultArg" || mc.Method.Name = "defaultArg") && mc.Arguments.Count = 2 then
                match translateScalarExpr dialect plan mc.Arguments.[0], translateScalarExpr dialect plan mc.Arguments.[1] with
                | Some optSql, Some defSql -> Some $"COALESCE({optSql}, {defSql})"
                | _ -> None
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "IndexOf" && mc.Arguments.Count >= 1 then
                match translateScalarExpr dialect plan mc.Object, translateScalarExpr dialect plan mc.Arguments.[0] with
                | Some strSql, Some subSql ->
                    match dialect with
                    | SqlDialect.SqlServer -> Some $"(CHARINDEX({subSql}, {strSql}) - 1)"
                    | SqlDialect.PostgreSql -> Some $"(POSITION({subSql} IN {strSql}) - 1)"
                    | _ -> Some $"(INSTR({strSql}, {subSql}) - 1)"
                | _ -> None
            else
                None
        | _ -> None

    and mergeExpressionIntoPlan (rootExpr: Expression) (plan: PushdownQueryPlan) (dialect: SqlDialect) : unit =
        let methodCalls = ResizeArray<MethodCallExpression>()
        let visited = System.Collections.Generic.HashSet<Expression>()
        let visitedQueryables = System.Collections.Generic.HashSet<obj>()
        let rec collectCalls (e: Expression) =
            if not (isNull e) && visited.Add(e) then
                match e with
                | :? MethodCallExpression as mc ->
                    methodCalls.Add(mc)
                    if mc.Arguments.Count > 0 then
                        collectCalls mc.Arguments.[0]
                    elif not (isNull mc.Object) then
                        collectCalls mc.Object
                | :? UnaryExpression as u ->
                    collectCalls u.Operand
                | :? MemberExpression as me when typeof<IQueryable>.IsAssignableFrom(me.Type) ->
                    try
                        let target =
                            if isNull me.Expression then null
                            else
                                match me.Expression with
                                | :? ConstantExpression as ce -> ce.Value
                                | other ->
                                    let lambda = Expression.Lambda(other)
                                    lambda.Compile().DynamicInvoke()
                        let v =
                            match me.Member with
                            | :? System.Reflection.FieldInfo as fi -> fi.GetValue(target)
                            | :? System.Reflection.PropertyInfo as pi -> pi.GetValue(target)
                            | _ -> null
                        match v with
                        | :? IQueryable as iq when not (isNull iq.Expression) && not (obj.ReferenceEquals(iq.Expression, me)) ->
                            if visitedQueryables.Add(v) then
                                let planProp = iq.GetType().GetProperty("Plan")
                                if not (isNull planProp) then
                                    let p = planProp.GetValue(iq) :?> PushdownQueryPlan
                                    for cte in p.Ctes do
                                        if not (plan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                                            plan.Ctes.Add(cte)
                                collectCalls iq.Expression
                        | _ -> ()
                    with _ -> ()
                | :? ConstantExpression as ce when typeof<IQueryable>.IsAssignableFrom(ce.Type) && not (isNull ce.Value) ->
                    if visitedQueryables.Add(ce.Value) then
                        let iq = ce.Value :?> IQueryable
                        let planProp = iq.GetType().GetProperty("Plan")
                        if not (isNull planProp) then
                            let p = planProp.GetValue(iq) :?> PushdownQueryPlan
                            for cte in p.Ctes do
                                if not (plan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                                    plan.Ctes.Add(cte)
                        if not (isNull iq.Expression) && not (obj.ReferenceEquals(iq.Expression, ce)) then
                            match iq.Expression with
                            | :? ConstantExpression as c when isNull c.Value -> ()
                            | other -> collectCalls other
                | _ -> ()

        collectCalls rootExpr

        // Process calls from inner to outer so Join -> Where -> OrderBy -> Select build naturally
        let reversed = methodCalls |> Seq.toArray |> Array.rev
        let mutable currentProjectionBody: Expression option = None
        let mutable terminalAggregate: string option = None
        let mutable terminalAggregateCol: string option = None

        for mc in reversed do
            match mc.Method.Name with
            | ("Join" | "GroupJoin" | "FullOuterJoin" | "RightOuterJoin") as mName when mc.Arguments.Count >= 5 ->
                if plan.Joins.Count = 0 then
                    match tryExtractQueryableInfo mc.Arguments.[0] with
                    | Some (_, _, outerRowType) when outerRowType.IsSome -> plan.BaseRowType <- outerRowType
                    | None | _ -> ()

                let innerArg = mc.Arguments.[1]
                let innerObjOpt =
                    try
                        match unwrapQuote innerArg with
                        | :? ConstantExpression as ce when not (isNull ce.Value) -> Some ce.Value
                        | :? MemberExpression as me ->
                            let target =
                                if isNull me.Expression then null
                                else
                                    match me.Expression with
                                    | :? ConstantExpression as ce -> ce.Value
                                    | other ->
                                        let lambda = Expression.Lambda(other)
                                        lambda.Compile().DynamicInvoke()
                            let v =
                                match me.Member with
                                | :? System.Reflection.FieldInfo as fi -> fi.GetValue(target)
                                | :? System.Reflection.PropertyInfo as pi -> pi.GetValue(target)
                                | _ -> null
                            if not (isNull v) then Some v else None
                        | other when typeof<IQueryable>.IsAssignableFrom(other.Type) ->
                            let lambda = Expression.Lambda(other)
                            let v = lambda.Compile().DynamicInvoke()
                            if not (isNull v) then Some v else None
                        | _ -> None
                    with _ -> None

                let matchingCte =
                    match innerObjOpt with
                    | Some objVal ->
                        plan.Ctes
                        |> Seq.tryFind (fun c ->
                            match c.SourceQuery with
                            | Some sq ->
                                obj.ReferenceEquals(sq, objVal)
                                || (
                                    // Compare underlying pushdown source/expression
                                    match box sq, box objVal with
                                    | (:? IQueryable as sqIq), (:? IQueryable as objIq) ->
                                        obj.ReferenceEquals(sqIq.Expression, objIq.Expression)
                                        || (
                                            match tryFindPushdownSource sqIq.Expression, tryFindPushdownSource objIq.Expression with
                                            | Some (s1, _), Some (s2, _) -> obj.ReferenceEquals(s1, s2)
                                            | _ -> false
                                        )
                                    | _ -> false
                                )
                            | None -> false)
                        |> Option.orElseWith (fun () ->
                            match tryExtractSubquerySql plan objVal with
                            | Some (subSql, _) ->
                                plan.Ctes |> Seq.tryFind (fun c -> c.SubquerySql = subSql)
                            | None -> None)
                    | None -> None

                let innerTableOpt, innerRowTypeOpt =
                    match matchingCte with
                    | Some cte ->
                        let rType =
                            match tryExtractQueryableInfo innerArg with
                            | Some (_, _, t) -> t
                            | None ->
                                if innerArg.Type.IsGenericType then Some (innerArg.Type.GetGenericArguments().[0])
                                else None
                        (Some cte.Name, rType)
                    | None ->
                        match innerObjOpt with
                        | Some objVal when typeof<IQueryable>.IsAssignableFrom(objVal.GetType()) ->
                            let isActualSubquery =
                                match objVal with
                                | :? IQueryable as iq ->
                                    match iq.Expression with
                                    | :? MethodCallExpression -> true
                                    | _ ->
                                        match tryFindPushdownSource iq.Expression with
                                        | Some (_, Some p) ->
                                            p.WhereClauses.Count > 0 || p.Joins.Count > 0 || p.GroupBy.Count > 0 || p.Projection.IsSome || p.Ctes.Count > 0 || p.Having.Count > 0
                                        | _ -> false
                                | _ -> false
                            if isActualSubquery then
                                match tryExtractSubquerySql plan objVal with
                                | Some (subSql, subParams) ->
                                    let autoCteName = $"cte_{plan.Ctes.Count + 1}"
                                    let mutable remappedSql = subSql
                                    for (pName, pVal) in subParams do
                                        let uniqueName = $"@p{plan.Parameters.Count}"
                                        remappedSql <- remappedSql.Replace(pName, uniqueName)
                                        plan.Parameters.Add(uniqueName, pVal)
                                    plan.Ctes.Add({ Name = autoCteName; SubquerySql = remappedSql; IsRecursive = false; SourceQuery = Some objVal })
                                    let rType =
                                        match tryExtractQueryableInfo innerArg with
                                        | Some (_, _, t) -> t
                                        | None ->
                                            if innerArg.Type.IsGenericType then Some (innerArg.Type.GetGenericArguments().[0])
                                            else None
                                    (Some autoCteName, rType)
                                | None ->
                                    match tryExtractQueryableInfo innerArg with
                                    | Some (tbl, _, rt) -> (Some tbl, rt)
                                    | None -> (None, None)
                            else
                                match tryExtractQueryableInfo innerArg with
                                | Some (tbl, _, rt) -> (Some tbl, rt)
                                | None -> (None, None)
                        | _ ->
                            match tryExtractQueryableInfo innerArg with
                            | Some (tbl, _, rt) -> (Some tbl, rt)
                            | None -> (None, None)

                match innerTableOpt with
                | Some innerTable when not (String.IsNullOrWhiteSpace innerTable) ->
                    let innerRowType = innerRowTypeOpt
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

                    let isFullOuter =
                        plan.FullOuterJoinTables.Contains("*")
                        || (plan.FullOuterJoinTables |> Seq.exists (fun tbl -> String.Equals(tbl, innerTable, StringComparison.OrdinalIgnoreCase)))
                    let isRightOuter =
                        plan.RightOuterJoinTables.Contains("*")
                        || (plan.RightOuterJoinTables |> Seq.exists (fun tbl -> String.Equals(tbl, innerTable, StringComparison.OrdinalIgnoreCase)))
                    let joinType =
                        match mName with
                        | "FullOuterJoin" -> "FULL OUTER JOIN"
                        | "RightOuterJoin" -> "RIGHT OUTER JOIN"
                        | "Join" when isRightOuter -> "RIGHT OUTER JOIN"
                        | "Join" when isFullOuter -> "FULL OUTER JOIN"
                        | "GroupJoin" when isRightOuter -> "RIGHT OUTER JOIN"
                        | "GroupJoin" when isFullOuter -> "FULL OUTER JOIN"
                        | "GroupJoin" -> "LEFT JOIN"
                        | _ -> "INNER JOIN"
                    plan.Joins.Add({
                        JoinType = joinType
                        SourceTable = innerTable
                        Alias = innerAlias
                        OnClause = onClause
                        RowType = innerRowType
                    })
                | _ -> ()
            | "SelectMany" when mc.Arguments.Count >= 2 ->
                let innerArg = unwrapQuote mc.Arguments.[1]
                let outerParamOpt, innerExpr =
                    match innerArg with
                    | :? LambdaExpression as l -> Some l.Parameters.[0], l.Body
                    | other -> None, other
                let isOuterApply = isDefaultIfEmptyFlattening innerExpr
                match tryExtractQueryableInfo innerExpr with
                | Some (innerTable, _, innerRowType) ->
                    let nextIdx = plan.Joins.Count + 1
                    let innerAlias = sprintf "_t%d" nextIdx
                    let isCorrelated =
                        match outerParamOpt with
                        | Some p -> dependsOnParameter p innerExpr
                        | None -> false
                    if isCorrelated then
                        let subPlan =
                            match tryFindPushdownSource innerExpr with
                            | Some (_, Some ep) ->
                                let cloned = ep.Clone()
                                cloned.BaseAlias <- innerAlias
                                if cloned.BaseRowType.IsNone then cloned.BaseRowType <- innerRowType
                                cloned
                            | _ ->
                                PushdownQueryPlan.Create(innerTable, [], baseAlias = innerAlias, ?baseRowType = innerRowType)
                        for cte in subPlan.Ctes do
                            if not (plan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                                plan.Ctes.Add(cte)
                        subPlan.Ctes.Clear()
                        match outerParamOpt with
                        | Some p -> subPlan.OuterAliases.[p.Name] <- plan.BaseAlias
                        | None -> ()
                        for kv in plan.OuterAliases do
                            subPlan.OuterAliases.[kv.Key] <- kv.Value
                        subPlan.OuterAliases.[plan.BaseAlias] <- plan.BaseAlias
                        mergeExpressionIntoPlan innerExpr subPlan dialect
                        let mutable subSql = subPlan.BuildSql(dialect)
                        for (pName, pVal) in subPlan.Parameters do
                            let uniqueName = $"@p{plan.Parameters.Count}"
                            subSql <- subSql.Replace(pName, uniqueName)
                            plan.Parameters.Add(uniqueName, pVal)
                        let joinType = if isOuterApply then "OUTER APPLY" else "CROSS APPLY"
                        plan.Joins.Add({
                            JoinType = joinType
                            SourceTable = subSql
                            Alias = innerAlias
                            OnClause = ""
                            RowType = innerRowType
                        })
                    else
                        let joinType = if isOuterApply then "LEFT JOIN" else "INNER JOIN"
                        plan.Joins.Add({
                            JoinType = joinType
                            SourceTable = innerTable
                            Alias = innerAlias
                            OnClause = "1=1"
                            RowType = innerRowType
                        })
                | None | _ -> ()
            | "Where" when mc.Arguments.Count >= 2 ->
                let arg1 = unwrapQuote mc.Arguments.[1]
                match arg1 with
                | :? LambdaExpression as l ->
                    let param = l.Parameters.[0]
                    let isGrouping =
                        (not (isNull param.Type)) && (
                            param.Type.Name.StartsWith("IGrouping")
                            || (param.Type.IsGenericType && param.Type.GetGenericTypeDefinition() = typedefof<IGrouping<_, _>>)
                        )
                    match translatePredicate param plan dialect l.Body with
                    | Some sqlPred ->
                        if not (plan.WhereClauses.Contains sqlPred) then
                            if isGrouping || (plan.GroupBy.Count > 0 && isHavingPredicate l.Body) then
                                plan.Having.Add(sqlPred)
                            else
                                plan.WhereClauses.Add(sqlPred)
                    | None | _ ->
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
            | ("Concat" | "Union" | "Intersect" | "Except") as opName when mc.Arguments.Count >= 2 ->
                let otherArg = mc.Arguments.[1]
                let targetSqlOpt =
                    match tryFindPushdownSource otherArg with
                    | Some (sourceIq, seedPlan) ->
                        let t = sourceIq.Provider.GetType()
                        let m = t.GetMethod("ToSqlWithParameters", [| typeof<Expression>; typeof<PushdownQueryPlan option> |])
                        if not (isNull m) then
                            let res = m.Invoke(sourceIq.Provider, [| box otherArg; box seedPlan |]) :?> (string * (string * obj) list)
                            let subSql, subParams = res
                            let pOffset = plan.Parameters.Count
                            let remappedSql =
                                System.Text.RegularExpressions.Regex.Replace(subSql, @"@p(\d+)", fun m ->
                                    let idx = int m.Groups.[1].Value
                                    $"@p{pOffset + idx}"
                                )
                            for (_, pVal) in subParams do
                                let uniqueName = $"@p{plan.Parameters.Count}"
                                plan.Parameters.Add(uniqueName, pVal)
                            Some remappedSql
                        else None
                    | None -> None

                let targetSql =
                    match targetSqlOpt with
                    | Some s -> s
                    | None ->
                        let v = try evaluateClientValue otherArg with _ -> box otherArg
                        match v with
                        | :? PushdownQueryPlan as targetPlan ->
                            let s = targetPlan.BuildSql(defaultArg plan.Dialect SqlDialect.Sqlite)
                            let pList = targetPlan.BaseParameters @ (targetPlan.Parameters |> List.ofSeq)
                            let pOffset = plan.Parameters.Count
                            let remappedSql =
                                System.Text.RegularExpressions.Regex.Replace(s, @"@p(\d+)", fun m ->
                                    let idx = int m.Groups.[1].Value
                                    $"@p{pOffset + idx}"
                                )
                            for (_, pVal) in pList do
                                let uniqueName = $"@p{plan.Parameters.Count}"
                                plan.Parameters.Add(uniqueName, pVal)
                            remappedSql
                        | other ->
                            string other

                let op =
                    match opName with
                    | "Concat" -> SetOperation.UnionAll
                    | "Union" -> SetOperation.Union
                    | "Intersect" -> SetOperation.Intersect
                    | _ -> SetOperation.Except

                plan.SetOperations.Add({ Operation = op; TargetSql = targetSql })
            | ("First" | "FirstOrDefault" | "Head" | "HeadOrDefault") ->
                plan.Take <- Some 1
            | ("Single" | "SingleOrDefault") ->
                plan.Take <- Some 2
            | ("Count" | "LongCount") ->
                terminalAggregate <- Some "Count"
                if mc.Arguments.Count >= 2 then
                    let arg1 = unwrapQuote mc.Arguments.[1]
                    match arg1 with
                    | :? LambdaExpression as l when l.ReturnType = typeof<bool> ->
                        match translatePredicate l.Parameters.[0] plan dialect l.Body with
                        | Some pSql -> plan.WhereClauses.Add(pSql)
                        | None | _ -> ()
                    | :? LambdaExpression as l ->
                        terminalAggregateCol <- Some (extractColumnNameWithAlias plan l.Body)
                    | other ->
                        terminalAggregateCol <- Some (extractColumnNameWithAlias plan other)
            | "Sum" ->
                terminalAggregate <- Some "Sum"
                if mc.Arguments.Count >= 2 then
                    terminalAggregateCol <- Some (extractColumnNameWithAlias plan mc.Arguments.[1])
            | "Min" ->
                terminalAggregate <- Some "Min"
                if mc.Arguments.Count >= 2 then
                    terminalAggregateCol <- Some (extractColumnNameWithAlias plan mc.Arguments.[1])
            | "Max" ->
                terminalAggregate <- Some "Max"
                if mc.Arguments.Count >= 2 then
                    terminalAggregateCol <- Some (extractColumnNameWithAlias plan mc.Arguments.[1])
            | ("Average" | "Avg") ->
                terminalAggregate <- Some "Avg"
                if mc.Arguments.Count >= 2 then
                    terminalAggregateCol <- Some (extractColumnNameWithAlias plan mc.Arguments.[1])
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
                    | None | _ ->
                        currentProjectionBody <- Some unwrappedBody
                | _ -> ()
            | _ -> ()

        match terminalAggregate with
        | Some "Count" ->
            let isD = plan.IsDistinct
            plan.IsDistinct <- false
            let dPrefix = if isD then "DISTINCT " else ""
            let targetCol =
                match terminalAggregateCol with
                | Some c -> Some c
                | None | _ ->
                    match currentProjectionBody with
                    | Some rawBody ->
                        let body = unwrapBlock rawBody
                        match body with
                        | :? MemberExpression as m ->
                            match translateScalarExpr dialect plan m with
                            | Some sqlExpr -> Some sqlExpr
                            | None | _ -> Some (extractColumnNameWithAlias plan m)
                        | other ->
                            match translateScalarExpr dialect plan other with
                            | Some sqlExpr -> Some sqlExpr
                            | None | _ -> Some (extractColumnNameWithAlias plan other)
                    | None | _ -> None

            match targetCol with
            | Some col ->
                plan.Projection <- Some $"COUNT({dPrefix}{col})"
            | None | _ ->
                plan.Projection <- Some "COUNT(*)"

        | Some "Sum" ->
            let isD = plan.IsDistinct
            plan.IsDistinct <- false
            let dPrefix = if isD then "DISTINCT " else ""
            let targetCol =
                match terminalAggregateCol with
                | Some c -> c
                | None | _ ->
                    match currentProjectionBody with
                    | Some rawBody ->
                        match translateScalarExpr dialect plan rawBody with
                        | Some sqlExpr -> sqlExpr
                        | None | _ -> extractColumnNameWithAlias plan rawBody
                    | None | _ -> "*"
            plan.Projection <- Some $"SUM({dPrefix}{targetCol})"

        | Some "Min" ->
            plan.IsDistinct <- false
            let targetCol =
                match terminalAggregateCol with
                | Some c -> c
                | None | _ ->
                    match currentProjectionBody with
                    | Some rawBody ->
                        match translateScalarExpr dialect plan rawBody with
                        | Some sqlExpr -> sqlExpr
                        | None | _ -> extractColumnNameWithAlias plan rawBody
                    | None | _ -> "*"
            plan.Projection <- Some $"MIN({targetCol})"

        | Some "Max" ->
            plan.IsDistinct <- false
            let targetCol =
                match terminalAggregateCol with
                | Some c -> c
                | None | _ ->
                    match currentProjectionBody with
                    | Some rawBody ->
                        match translateScalarExpr dialect plan rawBody with
                        | Some sqlExpr -> sqlExpr
                        | None | _ -> extractColumnNameWithAlias plan rawBody
                    | None | _ -> "*"
            plan.Projection <- Some $"MAX({targetCol})"

        | Some "Avg" ->
            let isD = plan.IsDistinct
            plan.IsDistinct <- false
            let dPrefix = if isD then "DISTINCT " else ""
            let targetCol =
                match terminalAggregateCol with
                | Some c -> c
                | None | _ ->
                    match currentProjectionBody with
                    | Some rawBody ->
                        match translateScalarExpr dialect plan rawBody with
                        | Some sqlExpr -> sqlExpr
                        | None | _ -> extractColumnNameWithAlias plan rawBody
                    | None | _ -> "*"
            plan.Projection <- Some $"AVG({dPrefix}{targetCol})"

        | Some _ -> ()

        | None ->
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
                        | None | _ ->
                            ok <- false
                    if ok && colParts.Count > 0 then
                        plan.Projection <- Some (String.concat ", " colParts)
                | :? MemberExpression as m ->
                    let unwrappedM = unwrapOptionOrNullableMember m
                    match unwrappedM with
                    | :? MemberExpression as realM ->
                        match translateScalarExpr dialect plan realM with
                        | Some sqlExpr ->
                            let qProp = SqlDialect.quoteIdentifier dialect realM.Member.Name
                            plan.Projection <- Some $"{sqlExpr} AS {qProp}"
                        | None | _ -> ()
                    | _ ->
                        match translateScalarExpr dialect plan m with
                        | Some sqlExpr ->
                            let qProp = SqlDialect.quoteIdentifier dialect m.Member.Name
                            plan.Projection <- Some $"{sqlExpr} AS {qProp}"
                        | None | _ -> ()
                | :? BinaryExpression as b ->
                    match translateScalarExpr dialect plan b with
                    | Some sqlExpr ->
                        let qVal = SqlDialect.quoteIdentifier dialect "Value"
                        plan.Projection <- Some $"{sqlExpr} AS {qVal}"
                    | None | _ -> ()
                | :? MethodCallExpression as mcExpr ->
                    match translateScalarExpr dialect plan mcExpr with
                    | Some sqlExpr ->
                        let qVal = SqlDialect.quoteIdentifier dialect "Value"
                        plan.Projection <- Some $"{sqlExpr} AS {qVal}"
                    | None | _ -> ()
                | :? ConditionalExpression as condExpr ->
                    match translateScalarExpr dialect plan condExpr with
                    | Some sqlExpr ->
                        let qVal = SqlDialect.quoteIdentifier dialect "Value"
                        plan.Projection <- Some $"{sqlExpr} AS {qVal}"
                    | None | _ -> ()
                | _ -> ()
            | None | _ -> ()

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
        let plan = PushdownQueryPlan.Create(baseSql, baseParameters, dialect = dialect)
        mergeExpressionIntoPlan rootExpr plan dialect
        plan

