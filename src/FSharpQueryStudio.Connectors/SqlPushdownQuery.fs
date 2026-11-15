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
        | :? ParameterExpression as p -> p = param || (not (isNull p.Name) && not (isNull param.Name) && p.Name = param.Name)
        | :? MemberExpression as m -> dependsOnParameter param m.Expression
        | :? BinaryExpression as b -> dependsOnParameter param b.Left || dependsOnParameter param b.Right
        | :? UnaryExpression as u -> dependsOnParameter param u.Operand
        | :? LambdaExpression as l -> dependsOnParameter param l.Body
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
                let valProp = t.GetProperty("Value")
                let hasValProp = t.GetProperty("IsSome")
                let hasVal =
                    if not (isNull hasValProp) then hasValProp.GetValue(v) :?> bool
                    else
                        let hasValProp2 = t.GetProperty("HasValue")
                        if not (isNull hasValProp2) then hasValProp2.GetValue(v) :?> bool
                        else true
                if hasVal && not (isNull valProp) then
                    valProp.GetValue(v)
                else
                    null
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
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert ->
            let inner = unwrapOptionOrNullableMember u.Operand
            if inner <> u.Operand then inner else expr
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
            methodCalls |> Seq.forall (fun mc ->
                if (mc.Method.Name = "Join" || mc.Method.Name = "GroupJoin") && mc.Arguments.Count >= 5 then
                    match tryExtractQueryableInfo mc.Arguments.[1] with
                    | Some (_, innerConn, _) ->
                        obj.ReferenceEquals(outerConn, innerConn)
                        || (
                            (outerConn :> IConnector).Kind = (innerConn :> IConnector).Kind
                            && String.Equals(outerConn.ConnectionString, innerConn.ConnectionString, StringComparison.OrdinalIgnoreCase)
                        )
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
                        | Some (_, innerConn, _) ->
                            obj.ReferenceEquals(outerConn, innerConn)
                            || (
                                (outerConn :> IConnector).Kind = (innerConn :> IConnector).Kind
                                && String.Equals(outerConn.ConnectionString, innerConn.ConnectionString, StringComparison.OrdinalIgnoreCase)
                            )
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

    and translatePredicate (param: ParameterExpression) (plan: PushdownQueryPlan) (dialect: SqlDialect) (expr: Expression) : string option =
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

                match tryTranslateSubqueryPredicate param plan dialect b with
                | Some sql -> Some sql
                | None ->
                    let leftHasParam = dependsOnParameter param b.Left
                    let rightHasParam = dependsOnParameter param b.Right
                    let leftHasOuter = hasAnyOuterParam plan b.Left || (hasAnyParameter b.Left && not leftHasParam)
                    let rightHasOuter = hasAnyOuterParam plan b.Right || (hasAnyParameter b.Right && not rightHasParam)

                    match translateScalarExpr dialect plan b.Left with
                    | Some lSql when (lSql.StartsWith("COUNT(") || lSql.StartsWith("SUM(") || lSql.StartsWith("MIN(") || lSql.StartsWith("MAX(") || lSql.StartsWith("AVG(") || lSql.StartsWith("("))
                                     && not rightHasParam && not rightHasOuter ->
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
                        if (leftHasParam || leftHasOuter) && (rightHasParam || rightHasOuter) then
                            let leftCol = extractColumnNameWithAlias plan b.Left
                            let rightCol = extractColumnNameWithAlias plan b.Right
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
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Not ->
            match translatePredicate param plan dialect u.Operand with
            | Some inner -> Some $"(NOT {inner})"
            | None | _ -> None
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
                    if mc.Method.IsStatic && mc.Arguments.Count >= 1 then mc.Arguments.[0]
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
                            let predLambda = unwrapQuote mc.Arguments.[1] :?> LambdaExpression
                            match translatePredicate predLambda.Parameters.[0] subPlan dialect predLambda.Body with
                            | Some pSql -> subPlan.WhereClauses.Add(pSql)
                            | None | _ -> ()
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
                    if mc.Method.IsStatic && mc.Arguments.Count >= 1 then mc.Arguments.[0]
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
                            let predLambda = unwrapQuote mc.Arguments.[1] :?> LambdaExpression
                            match translatePredicate predLambda.Parameters.[0] subPlan dialect predLambda.Body with
                            | Some pSql -> subPlan.WhereClauses.Add(pSql)
                            | None | _ -> ()
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
            if dialect = SqlDialect.Sqlite || dialect = SqlDialect.SqlServer then
                Some $"({col} = 1)"
            else
                Some $"({col} = TRUE)"
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
                if dialect = SqlDialect.SqlServer && (mName = "rowNumber" || mName = "RowNumber" || mName = "rank" || mName = "Rank" || mName = "denseRank" || mName = "DenseRank" || mName = "ntile" || mName = "Ntile") then
                    if not (overClause.Contains("ORDER BY")) then
                        if overClause = "OVER ()" then "OVER (ORDER BY (SELECT NULL))"
                        else overClause.Replace(")", " ORDER BY (SELECT NULL))")
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
                let offsetArg =
                    let pList = mc.Method.GetParameters()
                    let oIdx = pList |> Array.tryFindIndex (fun p -> p.Name = "offset")
                    match oIdx with
                    | Some idx when idx < mc.Arguments.Count -> tryUnwrapOptionArg mc.Arguments.[idx]
                    | _ -> None
                let offsetStr =
                    match offsetArg with
                    | Some e -> string (evaluateClientValue e)
                    | None -> "1"
                Some $"LAG({col}, {offsetStr}) {safeOverClause}"
            | "lead" | "Lead" ->
                let col = if mc.Arguments.Count > 0 then defaultArg (translateScalarExpr dialect plan mc.Arguments.[0]) (extractColumnNameWithAlias plan mc.Arguments.[0]) else "*"
                let offsetArg =
                    let pList = mc.Method.GetParameters()
                    let oIdx = pList |> Array.tryFindIndex (fun p -> p.Name = "offset")
                    match oIdx with
                    | Some idx when idx < mc.Arguments.Count -> tryUnwrapOptionArg mc.Arguments.[idx]
                    | _ -> None
                let offsetStr =
                    match offsetArg with
                    | Some e -> string (evaluateClientValue e)
                    | None -> "1"
                Some $"LEAD({col}, {offsetStr}) {safeOverClause}"
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
                match dialect with
                | SqlDialect.SqlServer ->
                    Some $"JSON_VALUE({colSql}, '{pathStr}')"
                | SqlDialect.Sqlite ->
                    Some $"json_extract({colSql}, '{pathStr}')"
                | SqlDialect.PostgreSql ->
                    let trimmed = if pathStr.StartsWith("$.") then pathStr.Substring(2) else pathStr
                    if trimmed.Contains(".") then
                        let parts = trimmed.Split('.') |> String.concat "', '"
                        Some $"({colSql} #>> '{{{parts}}}')"
                    else
                        Some $"({colSql} ->> '{trimmed}')"
            else None
        else None

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
            | None | _ -> None
        | _ -> None

    and translateScalarExpr (dialect: SqlDialect) (plan: PushdownQueryPlan) (expr: Expression) : string option =
        let unwrapped = unwrapQuote expr
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
                let op = if dialect = SqlDialect.SqlServer then " + " else " || "
                let argParts = mc.Arguments |> Seq.map (translateScalarExpr dialect plan) |> Seq.toList
                if argParts |> List.forall Option.isSome then
                    let joined = argParts |> List.map Option.get |> String.concat op
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
                | Some col -> Some $"SUM({col})"
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
                    | None | _ ->
                        match dialect with
                        | SqlDialect.Sqlite -> Some $"CAST({o} AS TEXT)"
                        | SqlDialect.SqlServer -> Some $"CAST({o} AS NVARCHAR(MAX))"
                        | SqlDialect.PostgreSql -> Some $"CAST({o} AS TEXT)"
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
                                collectCalls iq.Expression
                        | _ -> ()
                    with _ -> ()
                | :? ConstantExpression as ce when typeof<IQueryable>.IsAssignableFrom(ce.Type) && not (isNull ce.Value) ->
                    if visitedQueryables.Add(ce.Value) then
                        let iq = ce.Value :?> IQueryable
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

                    let joinType =
                        match mName with
                        | "FullOuterJoin" -> "FULL OUTER JOIN"
                        | "RightOuterJoin" -> "RIGHT OUTER JOIN"
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
                            let mutable remappedSql = subSql
                            for (pName, pVal) in subParams do
                                let uniqueName = $"@p{plan.Parameters.Count}"
                                remappedSql <- remappedSql.Replace(pName, uniqueName)
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
                            let mutable remappedSql = s
                            for (pName: string, pVal: obj) in pList do
                                let uniqueName = $"@p{plan.Parameters.Count}"
                                remappedSql <- remappedSql.Replace(pName, uniqueName)
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
                let dPrefix = if plan.IsDistinct then "DISTINCT " else ""
                plan.Projection <- Some $"COUNT({dPrefix}{col})"
            | None | _ ->
                let dPrefix = if plan.IsDistinct then "DISTINCT " else ""
                plan.Projection <- Some $"COUNT({dPrefix}*)"

        | Some "Sum" ->
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
            plan.Projection <- Some $"SUM({targetCol})"

        | Some "Min" ->
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
            plan.Projection <- Some $"AVG({targetCol})"

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

/// Marker interface allowing non-generic access to pushdown query metadata and plan diagnostics.
type ISqlPushdownQueryable =
    abstract member Plan: PushdownQueryPlan
    abstract member Connector: IQueryableConnector
    abstract member Explain: unit -> ExecutionPlanReport

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
        | None | _ ->
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
                | None | _ ->
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

    member this.ExecuteScalarWithPlan<'TResult>(expression: Expression, plan: PushdownQueryPlan) : 'TResult =
        let initialProjection = plan.Projection
        if not (isNull expression) && (match expression with :? ConstantExpression as c -> isNull c.Value | _ -> false) then
            ()
        elif not (isNull expression) then
            ExpressionPushdownTranslator.mergeExpressionIntoPlan expression plan dialect

        if initialProjection.IsSome then
            match initialProjection.Value.Trim().ToUpperInvariant() with
            | p when p.StartsWith("COUNT(") || p.StartsWith("SUM(") || p.StartsWith("MIN(") || p.StartsWith("MAX(") || p.StartsWith("AVG(") ->
                match plan.Projection with
                | Some newP when not (newP.Trim().ToUpperInvariant().StartsWith("COUNT(") || newP.Trim().ToUpperInvariant().StartsWith("SUM(") || newP.Trim().ToUpperInvariant().StartsWith("MIN(") || newP.Trim().ToUpperInvariant().StartsWith("MAX(") || newP.Trim().ToUpperInvariant().StartsWith("AVG(")) ->
                    plan.Projection <- initialProjection
                | _ -> ()
            | _ -> ()

        let sql = plan.BuildSql(dialect)
        let allParams = (plan.BaseParameters @ (plan.Parameters |> List.ofSeq))
        let res = connector.ExecuteQuery sql allParams
        let firstRow = res.Rows |> Seq.tryHead
        match firstRow with
        | Some row when row.Length > 0 && not (isNull row.[0]) && not (Convert.IsDBNull(row.[0])) ->
            unbox<'TResult> (RecordReaders.ConvertValue row.[0] typeof<'TResult>)
        | _ ->
            Unchecked.defaultof<'TResult>

    member this.ExecuteScalar<'TResult>(expression: Expression, ?seedPlan: PushdownQueryPlan) : 'TResult =
        let plan =
            match seedPlan with
            | Some sp ->
                let cloned = sp.Clone()
                ExpressionPushdownTranslator.mergeExpressionIntoPlan expression cloned dialect
                cloned
            | None | _ ->
                ExpressionPushdownTranslator.extractPlanFromExpression expression baseSql baseParameters dialect
        this.ExecuteScalarWithPlan<'TResult>(null, plan)

    interface IQueryProvider with
        member this.CreateQuery(expression: Expression) : IQueryable =
            let elemType =
                let t = expression.Type
                if t.IsGenericType && typeof<IEnumerable>.IsAssignableFrom(t) then
                    t.GetGenericArguments().[0]
                else
                    t
            let plan = PushdownQueryPlan.Create(baseSql, baseParameters, baseRowType = elemType, dialect = dialect)
            Activator.CreateInstance(
                typedefof<SqlPushdownQueryable<_>>.MakeGenericType(elemType),
                this, expression, connector, baseSql, baseParameters, plan
            ) :?> IQueryable

        member this.CreateQuery<'TElement>(expression: Expression) : IQueryable<'TElement> =
            let plan = PushdownQueryPlan.Create(baseSql, baseParameters, baseRowType = typeof<'TElement>, dialect = dialect)
            SqlPushdownQueryable<'TElement>(this, expression, connector, baseSql, baseParameters, plan) :> IQueryable<'TElement>

        member this.Execute(expression: Expression) : obj =
            let seedPlan =
                match ExpressionPushdownTranslator.tryFindPushdownSource expression with
                | Some (_, Some p) -> Some p
                | _ -> None
            this.ExecuteScalar<obj>(expression, ?seedPlan = seedPlan)

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
                let seedPlan =
                    match ExpressionPushdownTranslator.tryFindPushdownSource expression with
                    | Some (_, Some p) -> Some p
                    | _ -> None
                this.ExecuteScalar<'TResult>(expression, ?seedPlan = seedPlan)

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
        let plan = PushdownQueryPlan.Create(baseSql, p, baseRowType = typeof<'T>, dialect = provider.Dialect)
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

    /// Appends one or more GROUP BY columns directly (e.g. "Category", "_t0.ProductId").
    member this.GroupBy(columns: string) : SqlPushdownQueryable<'T> =
        let newPlan = plan.Clone()
        let parts = columns.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        for p in parts do
            if not (newPlan.GroupBy.Contains p) then
                newPlan.GroupBy.Add p
        SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

    /// Appends a raw HAVING clause for filtering aggregated results.
    member this.Having(havingSql: string, ?parameters: (string * obj) list) : SqlPushdownQueryable<'T> =
        let p = defaultArg parameters []
        let newPlan = plan.Clone()
        newPlan.Having.Add($"({havingSql})")
        for (k, v) in p do
            newPlan.Parameters.Add(k, v)
        SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

    /// Appends a CROSS APPLY join (transpiled to CROSS JOIN LATERAL on PostgreSQL, CROSS JOIN on SQLite).
    member this.CrossApply(subquerySql: string, alias: string) : SqlPushdownQueryable<'T> =
        let newPlan = plan.Clone()
        newPlan.Joins.Add({
            JoinType = "CROSS APPLY"
            SourceTable = subquerySql
            Alias = alias
            OnClause = ""
            RowType = None
        })
        SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

    /// Appends an OUTER APPLY join (transpiled to LEFT JOIN LATERAL ... ON true on PostgreSQL, LEFT JOIN on SQLite).
    member this.OuterApply(subquerySql: string, alias: string, ?onClause: string) : SqlPushdownQueryable<'T> =
        let newPlan = plan.Clone()
        newPlan.Joins.Add({
            JoinType = "OUTER APPLY"
            SourceTable = subquerySql
            Alias = alias
            OnClause = defaultArg onClause "1=1"
            RowType = None
        })
        SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

    /// Appends a standard CROSS JOIN clause without an ON predicate.
    member this.CrossJoin(tableOrSubquery: string, alias: string) : SqlPushdownQueryable<'T> =
        let newPlan = plan.Clone()
        newPlan.Joins.Add({
            JoinType = "CROSS JOIN"
            SourceTable = tableOrSubquery
            Alias = alias
            OnClause = ""
            RowType = None
        })
        SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

    /// Appends a FULL OUTER JOIN clause with an ON predicate.
    member this.FullOuterJoin(tableOrSubquery: string, alias: string, onClause: string) : SqlPushdownQueryable<'T> =
        let newPlan = plan.Clone()
        newPlan.Joins.Add({
            JoinType = "FULL OUTER JOIN"
            SourceTable = tableOrSubquery
            Alias = alias
            OnClause = onClause
            RowType = None
        })
        SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

    /// Appends a RIGHT OUTER JOIN clause with an ON predicate.
    member this.RightOuterJoin(tableOrSubquery: string, alias: string, onClause: string) : SqlPushdownQueryable<'T> =
        let newPlan = plan.Clone()
        newPlan.Joins.Add({
            JoinType = "RIGHT OUTER JOIN"
            SourceTable = tableOrSubquery
            Alias = alias
            OnClause = onClause
            RowType = None
        })
        SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

    /// Overrides the SELECT projection columns directly (e.g. "Id, Name, COUNT(*) OVER() AS Total").
    member this.SelectProjection(columnsSql: string) : SqlPushdownQueryable<'T> =
        let newPlan = plan.Clone()
        newPlan.Projection <- Some columnsSql
        SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

    /// Appends a WHERE EXISTS subquery clause.
    member this.WhereExists(subquerySql: string, ?parameters: (string * obj) list) : SqlPushdownQueryable<'T> =
        let p = defaultArg parameters []
        let newPlan = plan.Clone()
        newPlan.WhereClauses.Add($"EXISTS ({subquerySql})")
        for (k, v) in p do
            newPlan.Parameters.Add(k, v)
        SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

    /// Appends a WHERE NOT EXISTS subquery clause.
    member this.WhereNotExists(subquerySql: string, ?parameters: (string * obj) list) : SqlPushdownQueryable<'T> =
        let p = defaultArg parameters []
        let newPlan = plan.Clone()
        newPlan.WhereClauses.Add($"NOT EXISTS ({subquerySql})")
        for (k, v) in p do
            newPlan.Parameters.Add(k, v)
        SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

    /// Appends a WHERE column IN (subquery) clause.
    member this.WhereIn(column: string, subquerySql: string, ?parameters: (string * obj) list) : SqlPushdownQueryable<'T> =
        let p = defaultArg parameters []
        let newPlan = plan.Clone()
        newPlan.WhereClauses.Add($"({column} IN ({subquerySql}))")
        for (k, v) in p do
            newPlan.Parameters.Add(k, v)
        SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

    /// Appends a WHERE column IN (val1, val2, ...) clause with safe parameterized values.
    member this.WhereIn(column: string, values: 'V seq) : SqlPushdownQueryable<'T> =
        let valList = values |> Seq.map box |> List.ofSeq
        if valList.IsEmpty then
            let newPlan = plan.Clone()
            newPlan.WhereClauses.Add("1 = 0")
            SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)
        else
            let newPlan = plan.Clone()
            let paramNames = ResizeArray<string>()
            for i, v in valList |> List.indexed do
                let pName = $"@in_p{newPlan.Parameters.Count}_{i}"
                paramNames.Add(pName)
                newPlan.Parameters.Add(pName, v)
            let inList = String.concat ", " paramNames
            newPlan.WhereClauses.Add($"({column} IN ({inList}))")
            SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

    /// Appends a WHERE column NOT IN (subquery) clause.
    member this.WhereNotIn(column: string, subquerySql: string, ?parameters: (string * obj) list) : SqlPushdownQueryable<'T> =
        let p = defaultArg parameters []
        let newPlan = plan.Clone()
        newPlan.WhereClauses.Add($"({column} NOT IN ({subquerySql}))")
        for (k, v) in p do
            newPlan.Parameters.Add(k, v)
        SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

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
            SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

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
        let qCol = SqlDialect.quoteIdentifier (SqlDialect.fromConnectorKind (connector :> IConnector).Kind) columnName
        let dPrefix = if newPlan.IsDistinct then "DISTINCT " else ""
        newPlan.Projection <- Some $"COUNT({dPrefix}{qCol})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<int>(pExpr, newPlan)

    /// Direct scalar Count of a strongly-typed column selector lambda (e.g. fun p -> p.Id).
    member this.Count(colSelector: Expression<Func<'T, 'U>>) : int =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()
        let col = ExpressionPushdownTranslator.extractColumnNameWithAlias newPlan colSelector.Body
        let dPrefix = if newPlan.IsDistinct then "DISTINCT " else ""
        newPlan.Projection <- Some $"COUNT({dPrefix}{col})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<int>(pExpr, newPlan)

    /// Direct scalar COUNT(DISTINCT column) pushdown.
    member this.CountDistinct(columnName: string) : int =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()
        let qCol = SqlDialect.quoteIdentifier (SqlDialect.fromConnectorKind (connector :> IConnector).Kind) columnName
        newPlan.Projection <- Some $"COUNT(DISTINCT {qCol})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<int>(pExpr, newPlan)

    /// Direct scalar COUNT(DISTINCT colSelector) pushdown.
    member this.CountDistinct(colSelector: Expression<Func<'T, 'U>>) : int =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()
        let col = ExpressionPushdownTranslator.extractColumnNameWithAlias newPlan colSelector.Body
        newPlan.Projection <- Some $"COUNT(DISTINCT {col})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<int>(pExpr, newPlan)

    /// Direct scalar Sum pushdown on a column.
    member this.Sum<'TResult>(columnName: string) : 'TResult =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()
        let qCol = SqlDialect.quoteIdentifier (SqlDialect.fromConnectorKind (connector :> IConnector).Kind) columnName
        newPlan.Projection <- Some $"SUM({qCol})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<'TResult>(pExpr, newPlan)

    /// Direct scalar Sum pushdown on a strongly-typed column selector.
    member this.Sum<'TResult>(colSelector: Expression<Func<'T, 'TResult>>) : 'TResult =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()
        let col = ExpressionPushdownTranslator.extractColumnNameWithAlias newPlan colSelector.Body
        newPlan.Projection <- Some $"SUM({col})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<'TResult>(pExpr, newPlan)

    /// Direct scalar Min pushdown on a column.
    member this.Min<'TResult>(columnName: string) : 'TResult =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()
        let qCol = SqlDialect.quoteIdentifier (SqlDialect.fromConnectorKind (connector :> IConnector).Kind) columnName
        newPlan.Projection <- Some $"MIN({qCol})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<'TResult>(pExpr, newPlan)

    /// Direct scalar Min pushdown on a strongly-typed column selector.
    member this.Min<'TResult>(colSelector: Expression<Func<'T, 'TResult>>) : 'TResult =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()
        let col = ExpressionPushdownTranslator.extractColumnNameWithAlias newPlan colSelector.Body
        newPlan.Projection <- Some $"MIN({col})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<'TResult>(pExpr, newPlan)

    /// Direct scalar Max pushdown on a column.
    member this.Max<'TResult>(columnName: string) : 'TResult =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()
        let qCol = SqlDialect.quoteIdentifier (SqlDialect.fromConnectorKind (connector :> IConnector).Kind) columnName
        newPlan.Projection <- Some $"MAX({qCol})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<'TResult>(pExpr, newPlan)

    /// Direct scalar Max pushdown on a strongly-typed column selector.
    member this.Max<'TResult>(colSelector: Expression<Func<'T, 'TResult>>) : 'TResult =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()
        let col = ExpressionPushdownTranslator.extractColumnNameWithAlias newPlan colSelector.Body
        newPlan.Projection <- Some $"MAX({col})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<'TResult>(pExpr, newPlan)

    /// Direct scalar Average pushdown on a column.
    member this.Average<'TResult>(columnName: string) : 'TResult =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()
        let qCol = SqlDialect.quoteIdentifier (SqlDialect.fromConnectorKind (connector :> IConnector).Kind) columnName
        newPlan.Projection <- Some $"AVG({qCol})"
        let pExpr = (this :> IQueryable<'T>).Expression
        p.ExecuteScalarWithPlan<'TResult>(pExpr, newPlan)

    /// Direct scalar Average pushdown on a strongly-typed column selector.
    member this.Average<'TResult>(colSelector: Expression<Func<'T, 'TResult>>) : 'TResult =
        let p = provider :?> SqlPushdownQueryProvider
        let newPlan = plan.Clone()
        let col = ExpressionPushdownTranslator.extractColumnNameWithAlias newPlan colSelector.Body
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
        let fullThisPlan = p.ExtractPlan((this :> IQueryable<'T>).Expression, seedPlan = plan)
        let newPlan = fullThisPlan.Clone()
        let otherForSub = otherPlan.Clone()
        otherForSub.Ctes.Clear()
        let mutable subSql = otherForSub.BuildSql(SqlDialect.fromConnectorKind (connector :> IConnector).Kind)
        let allOtherParams = otherForSub.BaseParameters @ (otherForSub.Parameters |> List.ofSeq)
        let paramMapping = ResizeArray<string * string>()
        for (pName, pVal) in allOtherParams do
            let uniqueName = $"@p{newPlan.Parameters.Count}"
            paramMapping.Add(pName, uniqueName)
            subSql <- subSql.Replace(pName, uniqueName)
            newPlan.Parameters.Add(uniqueName, pVal)
        for cte in otherPlan.Ctes do
            if not (newPlan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                let mutable cteSql = cte.SubquerySql
                for (oldP, newP) in paramMapping do
                    cteSql <- cteSql.Replace(oldP, newP)
                newPlan.Ctes.Add({ cte with SubquerySql = cteSql })
        newPlan.SetOperations.Add({ Operation = op; TargetSql = subSql })
        SqlPushdownQueryable<'T>(provider, Expression.Constant(null, typeof<IQueryable<'T>>), connector, baseSql, baseParameters, newPlan)

    /// Combines this query with another query using SQL UNION (distinct results).
    member this.Union(other: SqlPushdownQueryable<'T>) : SqlPushdownQueryable<'T> =
        let op = (other :> IQueryable).Provider :?> SqlPushdownQueryProvider
        let fullOtherPlan = op.ExtractPlan((other :> IQueryable<'T>).Expression, seedPlan = other.Plan)
        this.ApplySetOperation(SetOperation.Union, fullOtherPlan)

    /// Combines this query with another query using SQL UNION (distinct results).
    member this.Union(other: System.Linq.IQueryable<'T>) : SqlPushdownQueryable<'T> =
        match box other with
        | :? SqlPushdownQueryable<'T> as s -> this.Union(s)
        | _ ->
            match ExpressionPushdownTranslator.tryFindPushdownSource other.Expression with
            | Some (sourceIq, seedPlan) ->
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
        let fullOtherPlan = op.ExtractPlan((other :> IQueryable<'T>).Expression, seedPlan = other.Plan)
        this.ApplySetOperation(SetOperation.UnionAll, fullOtherPlan)

    /// Combines this query with another query using SQL UNION ALL (all rows preserved).
    member this.UnionAll(other: System.Linq.IQueryable<'T>) : SqlPushdownQueryable<'T> =
        match box other with
        | :? SqlPushdownQueryable<'T> as s -> this.UnionAll(s)
        | _ ->
            match ExpressionPushdownTranslator.tryFindPushdownSource other.Expression with
            | Some (sourceIq, seedPlan) ->
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
        let fullOtherPlan = op.ExtractPlan((other :> IQueryable<'T>).Expression, seedPlan = other.Plan)
        this.ApplySetOperation(SetOperation.Intersect, fullOtherPlan)

    /// Combines this query with another query using SQL INTERSECT.
    member this.Intersect(other: System.Linq.IQueryable<'T>) : SqlPushdownQueryable<'T> =
        match box other with
        | :? SqlPushdownQueryable<'T> as s -> this.Intersect(s)
        | _ ->
            match ExpressionPushdownTranslator.tryFindPushdownSource other.Expression with
            | Some (sourceIq, seedPlan) ->
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
        let fullOtherPlan = op.ExtractPlan((other :> IQueryable<'T>).Expression, seedPlan = other.Plan)
        this.ApplySetOperation(SetOperation.Except, fullOtherPlan)

    /// Combines this query with another query using SQL EXCEPT.
    member this.Except(other: System.Linq.IQueryable<'T>) : SqlPushdownQueryable<'T> =
        match box other with
        | :? SqlPushdownQueryable<'T> as s -> this.Except(s)
        | _ ->
            match ExpressionPushdownTranslator.tryFindPushdownSource other.Expression with
            | Some (sourceIq, seedPlan) ->
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

    /// Prepends a Common Table Expression (WITH cte AS (...)) to this query.
    member this.WithCte(name: string, cteQuery: System.Linq.IQueryable<'U>, ?isRecursive: bool) : SqlPushdownQueryable<'T> =
        let p = (this :> IQueryable).Provider :?> SqlPushdownQueryProvider
        let fullThisPlan = p.ExtractPlan((this :> IQueryable<'T>).Expression, seedPlan = plan)
        let newPlan = fullThisPlan.Clone()
        match (cteQuery :> IQueryable).Provider with
        | :? SqlPushdownQueryProvider as cp ->
            let seedPlan =
                match box cteQuery with
                | :? ISqlPushdownQueryable as spq -> spq.Plan
                | _ -> PushdownQueryPlan.Create("", [])
            let fullCtePlan = cp.ExtractPlan(cteQuery.Expression, seedPlan = seedPlan)
            let mutable subSql = fullCtePlan.BuildSql(SqlDialect.fromConnectorKind (connector :> IConnector).Kind)
            for (pName, pVal) in fullCtePlan.Parameters do
                let uniqueName = $"@p{newPlan.Parameters.Count}"
                subSql <- subSql.Replace(pName, uniqueName)
                newPlan.Parameters.Add(uniqueName, pVal)
            newPlan.Ctes.Add({ Name = name; SubquerySql = subSql; IsRecursive = defaultArg isRecursive false; SourceQuery = Some (box cteQuery) })
        | _ -> ()
        SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

    /// Prepends a Common Table Expression (WITH cte AS (...)) to this query with an auto-generated name.
    member this.WithCte(cteQuery: System.Linq.IQueryable<'U>, ?isRecursive: bool) : SqlPushdownQueryable<'T> =
        let autoName = $"cte_{plan.Ctes.Count + 1}"
        this.WithCte(autoName, cteQuery, ?isRecursive = isRecursive)

    /// Prepends a Common Table Expression (WITH cte AS (...)) to this query.
    member this.WithCte(name: string, cteQuery: SqlPushdownQueryable<'U>, ?isRecursive: bool) : SqlPushdownQueryable<'T> =
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
    member this.WithCte(subquerySql: string, ?parameters: (string * obj) list, ?isRecursive: bool) : SqlPushdownQueryable<'T> =
        let autoName = $"cte_{plan.Ctes.Count + 1}"
        this.WithCte(autoName, subquerySql, ?parameters = parameters, ?isRecursive = isRecursive)

    /// Prepends a Common Table Expression (WITH cte AS (...)) with raw SQL.
    member this.WithCte(name: string, subquerySql: string, ?parameters: (string * obj) list, ?isRecursive: bool) : SqlPushdownQueryable<'T> =
        let newPlan = plan.Clone()
        let p = defaultArg parameters []
        let mutable remappedSql = subquerySql
        for (k, v) in p do
            let uniqueName = $"@p{newPlan.Parameters.Count}"
            remappedSql <- remappedSql.Replace(k, uniqueName)
            newPlan.Parameters.Add(uniqueName, v)
        newPlan.Ctes.Add({ Name = name; SubquerySql = remappedSql; IsRecursive = defaultArg isRecursive false; SourceQuery = None })
        SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

    /// Returns a structured query execution plan report for diagnostics.
    member this.Explain() : ExecutionPlanReport =
        let dialect = defaultArg plan.Dialect SqlDialect.Sqlite
        let formatted = plan.BuildSql(dialect)
        let parms = plan.BaseParameters @ (plan.Parameters |> List.ofSeq)
        let planText =
            try
                match dialect with
                | SqlDialect.Sqlite ->
                    let (res: ConnectorQueryResult) = connector.ExecuteQuery $"EXPLAIN QUERY PLAN {formatted}" parms
                    let lines =
                        res.Rows
                        |> Seq.map (fun row ->
                            if row.Length >= 4 then $"detail: {row.[3]}"
                            elif row.Length > 0 then string row.[0]
                            else "")
                        |> String.concat "\n"
                    $"-- SQLite EXPLAIN QUERY PLAN:\n{lines}"
                | SqlDialect.PostgreSql ->
                    let (res: ConnectorQueryResult) = connector.ExecuteQuery $"EXPLAIN {formatted}" parms
                    let lines =
                        res.Rows
                        |> Seq.map (fun row -> if row.Length > 0 then string row.[0] else "")
                        |> String.concat "\n"
                    $"-- PostgreSQL EXPLAIN:\n{lines}"
                | SqlDialect.SqlServer ->
                    let (res: ConnectorQueryResult) = connector.ExecuteQuery $"SET SHOWPLAN_TEXT ON; {formatted}; SET SHOWPLAN_TEXT OFF;" parms
                    let lines =
                        res.Rows
                        |> Seq.map (fun row -> if row.Length > 0 then string row.[0] else "")
                        |> String.concat "\n"
                    $"-- SQL Server SHOWPLAN:\n{lines}"
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
    open System.Reflection

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
            | other when not (isNull other) ->
                let fields = other.GetType().GetFields(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance)
                fields
                |> Seq.tryPick (fun f ->
                    try
                        match f.GetValue(other) with
                        | :? IQueryable as iq when not (isNull iq.Provider) && iq.Provider.GetType() = typeof<SqlPushdownQueryProvider> ->
                            let planProp = iq.GetType().GetProperty("Plan")
                            let plan = if isNull planProp then None else Some (planProp.GetValue(iq) :?> PushdownQueryPlan)
                            Some (iq, plan)
                        | _ -> None
                    with _ -> None)
            | _ -> None
        | :? MethodCallExpression as m ->
            m.Arguments
            |> Seq.tryPick tryFindPushdownSource
            |> Option.orElseWith (fun () -> tryFindPushdownSource m.Object)
        | :? LambdaExpression as l ->
            tryFindPushdownSource l.Body
        | :? MemberExpression as m ->
            tryFindPushdownSource m.Expression
            |> Option.orElseWith (fun () ->
                try
                    if isNull m.Expression && m.Member :? PropertyInfo then
                        let p = m.Member :?> PropertyInfo
                        match p.GetValue(null) with
                        | :? IQueryable as iq when not (isNull iq.Provider) && iq.Provider.GetType() = typeof<SqlPushdownQueryProvider> ->
                            let planProp = iq.GetType().GetProperty("Plan")
                            let plan = if isNull planProp then None else Some (planProp.GetValue(iq) :?> PushdownQueryPlan)
                            Some (iq, plan)
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
                | None | _ ->
                    (string iq, [])
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

    let toRawSql (query: obj) : string =
        extractRawSql query

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
                | Some (_, Some p) -> p.Dialect
                | _ -> None
            | _ -> None

        let connectorOpt =
            match query with
            | :? IQueryable as iq ->
                let t = iq.GetType()
                let p = t.GetProperty("Connector")
                if not (isNull p) then
                    try Some (p.GetValue(iq) :?> IQueryableConnector) with _ -> None
                else None
            | _ -> None

        let dialect =
            match dialectOpt with
            | Some d -> d
            | None ->
                match connectorOpt with
                | Some c -> SqlDialect.fromConnectorKind (c :> IConnector).Kind
                | None -> SqlDialect.Sqlite

        let planText =
            match connectorOpt with
            | Some conn ->
                try
                    match dialect with
                    | SqlDialect.Sqlite ->
                        let explainSql = $"EXPLAIN QUERY PLAN {sql}"
                        let res = conn.ExecuteQuery explainSql parms
                        let lines =
                            res.Rows
                            |> Seq.map (fun row ->
                                if row.Length >= 4 && not (isNull row.[3]) then string row.[3]
                                elif row.Length > 0 then String.concat " | " (row |> Seq.map string)
                                else "")
                            |> Seq.filter (not << String.IsNullOrWhiteSpace)
                            |> String.concat "\n"
                        if String.IsNullOrWhiteSpace lines then "-- SQLite EXPLAIN: Plan generated successfully."
                        else $"-- SQLite EXPLAIN QUERY PLAN:\n{lines}"
                    | SqlDialect.PostgreSql ->
                        let explainSql = $"EXPLAIN (ANALYZE false, FORMAT TEXT) {sql}"
                        let res = conn.ExecuteQuery explainSql parms
                        let lines =
                            res.Rows
                            |> Seq.map (fun row -> if row.Length > 0 then string row.[0] else "")
                            |> String.concat "\n"
                        $"-- PostgreSQL EXPLAIN:\n{lines}"
                    | SqlDialect.SqlServer ->
                        let res = conn.ExecuteQuery $"SET SHOWPLAN_TEXT ON; {sql}; SET SHOWPLAN_TEXT OFF;" parms
                        let lines =
                            res.Rows
                            |> Seq.map (fun row -> if row.Length > 0 then string row.[0] else "")
                            |> String.concat "\n"
                        $"-- SQL Server SHOWPLAN:\n{lines}"
                with ex ->
                    $"-- EXPLAIN diagnostic report:\n--   Engine: {dialect}\n--   Status: {ex.Message}"
            | None ->
                $"-- Diagnostic execution plan estimate:\n--   Engine: {dialect}\n--   Target SQL:\n{formatted}"

        {
            Dialect = dialect
            PlanText = planText
            FormattedSql = formatted
            Parameters = parms
        }

    do
        ResultKind.RegisterRawSqlExtractor(extractRawSql, extractSqlWithParameters)

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

    /// Strongly-typed F# computation expression builder providing dedicated keywords
    /// for subqueries, EXISTS, NOT EXISTS, CROSS APPLY / LATERAL, HAVING, CTEs, and Set Operations.
    type FSqlQueryBuilder() =
        inherit QueryBuilder()

        [<CustomOperation("whereExists", MaintainsVariableSpace = true)>]
        member inline _.WhereExists(source: QuerySource<'T, 'Q>, [<ProjectionParameter>] subquery: 'T -> #seq<'U>) : QuerySource<'T, 'Q> =
            failwith "whereExists marker"

        [<CustomOperation("whereExists", MaintainsVariableSpace = true)>]
        member inline _.WhereExistsPred(source: QuerySource<'T, 'Q>, [<ProjectionParameter>] predicate: 'T -> bool) : QuerySource<'T, 'Q> =
            failwith "whereExists marker"

        [<CustomOperation("whereNotExists", MaintainsVariableSpace = true)>]
        member inline _.WhereNotExists(source: QuerySource<'T, 'Q>, [<ProjectionParameter>] subquery: 'T -> #seq<'U>) : QuerySource<'T, 'Q> =
            failwith "whereNotExists marker"

        [<CustomOperation("whereNotExists", MaintainsVariableSpace = true)>]
        member inline _.WhereNotExistsPred(source: QuerySource<'T, 'Q>, [<ProjectionParameter>] predicate: 'T -> bool) : QuerySource<'T, 'Q> =
            failwith "whereNotExists marker"

        [<CustomOperation("having", MaintainsVariableSpace = true)>]
        member inline _.Having(source: QuerySource<'T, 'Q>, [<ProjectionParameter>] predicate: 'T -> bool) : QuerySource<'T, 'Q> =
            failwith "having marker"

        [<CustomOperation("union")>]
        member inline _.Union(source: QuerySource<'T, 'Q>, other: System.Collections.Generic.IEnumerable<'T>) : QuerySource<'T, 'Q> =
            failwith "union marker"

        [<CustomOperation("unionAll")>]
        member inline _.UnionAll(source: QuerySource<'T, 'Q>, other: System.Collections.Generic.IEnumerable<'T>) : QuerySource<'T, 'Q> =
            failwith "unionAll marker"

        [<CustomOperation("intersect")>]
        member inline _.Intersect(source: QuerySource<'T, 'Q>, other: System.Collections.Generic.IEnumerable<'T>) : QuerySource<'T, 'Q> =
            failwith "intersect marker"

        [<CustomOperation("except")>]
        member inline _.Except(source: QuerySource<'T, 'Q>, other: System.Collections.Generic.IEnumerable<'T>) : QuerySource<'T, 'Q> =
            failwith "except marker"

        member _.union (lhs: System.Linq.IQueryable<'T>) (rhs: System.Linq.IQueryable<'T>) : System.Linq.IQueryable<'T> =
            match box lhs with
            | :? SqlPushdownQueryable<'T> as s -> s.Union(rhs) :> System.Linq.IQueryable<'T>
            | _ -> System.Linq.Queryable.Union(lhs, rhs)

        member _.unionAll (lhs: System.Linq.IQueryable<'T>) (rhs: System.Linq.IQueryable<'T>) : System.Linq.IQueryable<'T> =
            match box lhs with
            | :? SqlPushdownQueryable<'T> as s -> s.UnionAll(rhs) :> System.Linq.IQueryable<'T>
            | _ -> System.Linq.Queryable.Concat(lhs, rhs)

        member _.intersect (lhs: System.Linq.IQueryable<'T>) (rhs: System.Linq.IQueryable<'T>) : System.Linq.IQueryable<'T> =
            match box lhs with
            | :? SqlPushdownQueryable<'T> as s -> s.Intersect(rhs) :> System.Linq.IQueryable<'T>
            | _ -> System.Linq.Queryable.Intersect(lhs, rhs)

        member _.except (lhs: System.Linq.IQueryable<'T>) (rhs: System.Linq.IQueryable<'T>) : System.Linq.IQueryable<'T> =
            match box lhs with
            | :? SqlPushdownQueryable<'T> as s -> s.Except(rhs) :> System.Linq.IQueryable<'T>
            | _ -> System.Linq.Queryable.Except(lhs, rhs)

        [<CustomOperation("withCte", MaintainsVariableSpace = true)>]
        member inline _.WithCte(source: QuerySource<'T, 'Q>, name: string, cteQuery: System.Collections.Generic.IEnumerable<'U>) : QuerySource<'T, 'Q> =
            failwith "withCte marker"

        [<CustomOperation("withCte", MaintainsVariableSpace = true)>]
        member inline _.WithCte(source: QuerySource<'T, 'Q>, cteQuery: System.Collections.Generic.IEnumerable<'U>) : QuerySource<'T, 'Q> =
            failwith "withCte marker"

        [<CustomOperation("withCte", MaintainsVariableSpace = true)>]
        member inline _.WithCteSql(source: QuerySource<'T, 'Q>, name: string, subquerySql: string) : QuerySource<'T, 'Q> =
            failwith "withCte marker"

        [<CustomOperation("withCte", MaintainsVariableSpace = true)>]
        member inline _.WithCteSql(source: QuerySource<'T, 'Q>, subquerySql: string) : QuerySource<'T, 'Q> =
            failwith "withCte marker"

        [<CustomOperation("withRecursiveCte", MaintainsVariableSpace = true)>]
        member inline _.WithRecursiveCte(source: QuerySource<'T, 'Q>, name: string, cteQuery: System.Collections.Generic.IEnumerable<'U>) : QuerySource<'T, 'Q> =
            failwith "withRecursiveCte marker"

        [<CustomOperation("withRecursiveCte", MaintainsVariableSpace = true)>]
        member inline _.WithRecursiveCte(source: QuerySource<'T, 'Q>, cteQuery: System.Collections.Generic.IEnumerable<'U>) : QuerySource<'T, 'Q> =
            failwith "withRecursiveCte marker"

        [<CustomOperation("withRecursiveCte", MaintainsVariableSpace = true)>]
        member inline _.WithRecursiveCteSql(source: QuerySource<'T, 'Q>, name: string, subquerySql: string) : QuerySource<'T, 'Q> =
            failwith "withRecursiveCte marker"

        [<CustomOperation("withRecursiveCte", MaintainsVariableSpace = true)>]
        member inline _.WithRecursiveCteSql(source: QuerySource<'T, 'Q>, subquerySql: string) : QuerySource<'T, 'Q> =
            failwith "withRecursiveCte marker"

        [<CustomOperation("fullOuterJoin", MaintainsVariableSpace = true)>]
        member inline _.FullOuterJoin(source: QuerySource<'T, 'Q>, tableOrQuery: string, alias: string, onClause: string) : QuerySource<'T, 'Q> =
            failwith "fullOuterJoin marker"

        [<CustomOperation("rightOuterJoin", MaintainsVariableSpace = true)>]
        member inline _.RightOuterJoin(source: QuerySource<'T, 'Q>, tableOrQuery: string, alias: string, onClause: string) : QuerySource<'T, 'Q> =
            failwith "rightOuterJoin marker"

        [<CustomOperation("crossJoin", MaintainsVariableSpace = true)>]
        member inline _.CrossJoin(source: QuerySource<'T, 'Q>, tableOrQuery: string, alias: string) : QuerySource<'T, 'Q> =
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
            let queryBuilderRunMethod =
                typeof<QueryBuilder>.GetMethods()
                |> Seq.find (fun m ->
                    m.Name = "Run"
                    && m.IsGenericMethod
                    && m.GetParameters().Length = 1)

            let ctes = ResizeArray<string * obj * bool>()
            let explicitJoins = ResizeArray<string * string * string * string>()
            let mutable fromCteName: string option = None

            let rec tryFindRootSource (e: Expr) : obj option =
                match e with
                | Call(_, mi, firstArg :: _) when mi.DeclaringType = typeof<QueryBuilder> || mi.DeclaringType = typeof<FSqlQueryBuilder> ->
                    tryFindRootSource firstArg
                    |> Option.orElseWith (fun () ->
                        try
                            let v = LeafExpressionConverter.EvaluateQuotation firstArg
                            if isNull v then None
                            else
                                let t = v.GetType()
                                let srcProp = t.GetProperty("Source")
                                if not (isNull srcProp) then Some (srcProp.GetValue(v))
                                else Some v
                        with _ -> None)
                | _ ->
                    try
                        let v = LeafExpressionConverter.EvaluateQuotation e
                        if isNull v then None
                        else
                            let t = v.GetType()
                            let srcProp = t.GetProperty("Source")
                            if not (isNull srcProp) then Some (srcProp.GetValue(v))
                            else Some v
                    with _ -> None

            let isSourceMatch (sourceObj: obj) (targetCte: obj) =
                if isNull sourceObj || isNull targetCte then false
                elif obj.ReferenceEquals(sourceObj, targetCte) then true
                else
                    match sourceObj, targetCte with
                    | (:? IQueryable as sqIq), (:? IQueryable as cteIq) ->
                        obj.ReferenceEquals(sqIq.Expression, cteIq.Expression)
                        || (
                            match SqlPushdownQuery.tryFindPushdownSource sqIq.Expression, SqlPushdownQuery.tryFindPushdownSource cteIq.Expression with
                            | Some (s1, _), Some (s2, _) -> obj.ReferenceEquals(s1, s2)
                            | _ -> false
                        )
                    | _ -> false

            let rec peelCustomOps (expr: Expr) : Expr =
                match expr with
                | Call(instanceOpt, mi, [ innerSource; nameArg; cteArg ]) when mi.Name = "WithCte" || mi.Name = "WithCteSql" || mi.Name = "WithRecursiveCte" || mi.Name = "WithRecursiveCteSql" ->
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
                | Call(instanceOpt, mi, [ innerSource; cteArg ]) when mi.Name = "WithCte" || mi.Name = "WithCteSql" || mi.Name = "WithRecursiveCte" || mi.Name = "WithRecursiveCteSql" ->
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
                | Call(instanceOpt, mi, [ innerSource; tableArg; aliasArg; onArg ]) when mi.Name = "FullOuterJoin" || mi.Name = "RightOuterJoin" ->
                    let tbl = LeafExpressionConverter.EvaluateQuotation(tableArg) :?> string
                    let alias = LeafExpressionConverter.EvaluateQuotation(aliasArg) :?> string
                    let onCl = LeafExpressionConverter.EvaluateQuotation(onArg) :?> string
                    let jt = if mi.Name = "FullOuterJoin" then "FULL OUTER JOIN" else "RIGHT OUTER JOIN"
                    explicitJoins.Add((jt, tbl, alias, onCl))
                    peelCustomOps innerSource
                | Call(instanceOpt, mi, [ innerSource; tableArg; aliasArg ]) when mi.Name = "CrossJoin" ->
                    let tbl = LeafExpressionConverter.EvaluateQuotation(tableArg) :?> string
                    let alias = LeafExpressionConverter.EvaluateQuotation(aliasArg) :?> string
                    explicitJoins.Add(("CROSS JOIN", tbl, alias, ""))
                    peelCustomOps innerSource
                | ExprShape.ShapeVar v -> Expr.Var v
                | ExprShape.ShapeLambda(v, b) -> Expr.Lambda(v, peelCustomOps b)
                | ExprShape.ShapeCombination(shape, args) -> ExprShape.RebuildShapeCombination(shape, List.map peelCustomOps args)

            let rec rewrite (expr: Expr) : Expr =
                match expr with
                | Call(instanceOpt, mi, [ innerArg ]) when mi.Name = "Run" && mi.DeclaringType = typeof<FSqlQueryBuilder> ->
                    let elemType = mi.GetGenericArguments().[0]
                    let genericRun = queryBuilderRunMethod.MakeGenericMethod(elemType)
                    let newInner = rewrite innerArg
                    match instanceOpt with
                    | Some inst -> Expr.Call(inst, genericRun, [ newInner ])
                    | None -> Expr.Call(genericRun, [ newInner ])
                | Call(Some instance, mi, [ source; Lambda(var, body) ]) when mi.Name = "WhereExists" ->
                    let srcType = mi.GetGenericArguments().[0]
                    let qType = mi.GetGenericArguments().[1]
                    let genericWhere = whereMethod.MakeGenericMethod(srcType, qType)
                    let subElemType =
                        if body.Type.IsGenericType then body.Type.GetGenericArguments().[0]
                        else typeof<obj>
                    let genericCount = countMethod.MakeGenericMethod(subElemType)
                    let countCall = Expr.Call(genericCount, [ rewrite body ])
                    let gtExpr = <@@ (%%countCall : int) > 0 @@>
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
                        if body.Type.IsGenericType then body.Type.GetGenericArguments().[0]
                        else typeof<obj>
                    let genericCount = countMethod.MakeGenericMethod(subElemType)
                    let countCall = Expr.Call(genericCount, [ rewrite body ])
                    let eqExpr = <@@ (%%countCall : int) = 0 @@>
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
                | ExprShape.ShapeCombination(shape, args) -> ExprShape.RebuildShapeCombination(shape, List.map rewrite args)

            let attachToPlan (plan: PushdownQueryPlan) =
                for (name, cteVal, isRec) in ctes do
                    if not (plan.Ctes |> Seq.exists (fun c -> c.Name = name)) then
                        match cteVal with
                        | :? string as rawSql ->
                            plan.Ctes.Add({ Name = name; SubquerySql = rawSql; IsRecursive = isRec; SourceQuery = None })
                        | _ ->
                            let (subSql, subParams) = SqlPushdownQuery.extractSqlWithParameters cteVal
                            let mutable remappedSql = subSql
                            for (pName, pVal) in subParams do
                                let uniqueName = $"@p{plan.Parameters.Count}"
                                remappedSql <- remappedSql.Replace(pName, uniqueName)
                                plan.Parameters.Add(uniqueName, pVal)
                            plan.Ctes.Add({ Name = name; SubquerySql = remappedSql; IsRecursive = isRec; SourceQuery = Some cteVal })
                for (jt, tbl, alias, onCl) in explicitJoins do
                    plan.Joins.Add({ JoinType = jt; SourceTable = tbl; Alias = alias; OnClause = onCl; RowType = None })
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
                        | Some (_, Some p) -> Some p
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
                                | Some (sourceIq, _) -> obj.ReferenceEquals(sourceIq, cteVal)
                                | None -> false
                        if isBaseFromThisCte then
                            plan.BaseSql <- name
                            plan.WhereClauses.Clear()
                    queryable
                | None -> queryable

            let toSpq (q: obj) : SqlPushdownQueryable<'T> option =
                match q with
                | :? SqlPushdownQueryable<'T> as s -> Some s
                | :? IQueryable as iq when not (isNull iq.Provider) && iq.Provider.GetType().Name.StartsWith("SqlPushdownQueryProvider") ->
                    match ExpressionPushdownTranslator.tryFindPushdownSource iq.Expression with
                    | Some (sourceIq, seedPlan) ->
                        let prov = sourceIq.Provider :?> SqlPushdownQueryProvider
                        let p = prov.ExtractPlan(iq.Expression, ?seedPlan = seedPlan)
                        match seedPlan with
                        | Some sp ->
                            for cte in sp.Ctes do
                                if not (p.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                                    p.Ctes.Add(cte)
                        | None -> ()
                        Some (SqlPushdownQueryable<'T>(prov, Expression.Constant(null, typeof<IQueryable<'T>>), prov.Connector, prov.BaseSql, prov.BaseParameters, p))
                    | None -> None
                | _ -> None

            let doUnion (lhs: System.Linq.IQueryable<'T>) (rhs: obj) : System.Linq.IQueryable<'T> =
                match toSpq lhs, toSpq rhs with
                | Some spq, Some rspq ->
                    spq.Union(rspq) :> System.Linq.IQueryable<'T>
                | _ ->
                    match rhs with
                    | :? System.Linq.IQueryable<'T> as rhsIq -> System.Linq.Queryable.Union(lhs, rhsIq)
                    | :? System.Collections.Generic.IEnumerable<'T> as rhsSeq -> System.Linq.Queryable.Union(lhs, rhsSeq.AsQueryable())
                    | _ -> lhs

            let doUnionAll (lhs: System.Linq.IQueryable<'T>) (rhs: obj) : System.Linq.IQueryable<'T> =
                match toSpq lhs, toSpq rhs with
                | Some spq, Some rspq ->
                    spq.UnionAll(rspq) :> System.Linq.IQueryable<'T>
                | _ ->
                    match rhs with
                    | :? System.Linq.IQueryable<'T> as rhsIq -> System.Linq.Queryable.Concat(lhs, rhsIq)
                    | :? System.Collections.Generic.IEnumerable<'T> as rhsSeq -> System.Linq.Queryable.Concat(lhs, rhsSeq.AsQueryable())
                    | _ -> lhs

            let doIntersect (lhs: System.Linq.IQueryable<'T>) (rhs: obj) : System.Linq.IQueryable<'T> =
                match toSpq lhs, toSpq rhs with
                | Some spq, Some rspq ->
                    spq.Intersect(rspq) :> System.Linq.IQueryable<'T>
                | _ ->
                    match rhs with
                    | :? System.Linq.IQueryable<'T> as rhsIq -> System.Linq.Queryable.Intersect(lhs, rhsIq)
                    | :? System.Collections.Generic.IEnumerable<'T> as rhsSeq -> System.Linq.Queryable.Intersect(lhs, rhsSeq.AsQueryable())
                    | _ -> lhs

            let doExcept (lhs: System.Linq.IQueryable<'T>) (rhs: obj) : System.Linq.IQueryable<'T> =
                match toSpq lhs, toSpq rhs with
                | Some spq, Some rspq ->
                    spq.Except(rspq) :> System.Linq.IQueryable<'T>
                | _ ->
                    match rhs with
                    | :? System.Linq.IQueryable<'T> as rhsIq -> System.Linq.Queryable.Except(lhs, rhsIq)
                    | :? System.Collections.Generic.IEnumerable<'T> as rhsSeq -> System.Linq.Queryable.Except(lhs, rhsSeq.AsQueryable())
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
                            | Call(_, forMi, [ Call(_, srcMi, [ srcExpr ]); Lambda(var, body) ])
                                when forMi.Name = "For" && srcMi.Name = "Source" ->
                                let isId =
                                    match body with
                                    | Call(_, yieldMi, [ Var v ]) when (yieldMi.Name = "Yield" || yieldMi.Name = "Return") && v.Name = var.Name -> true
                                    | Let(vInner, Var vOuter, Call(_, yieldMi, [ Var vFinal ]))
                                        when (yieldMi.Name = "Yield" || yieldMi.Name = "Return") && vOuter.Name = var.Name && vFinal.Name = vInner.Name -> true
                                    | _ -> false
                                if isId then
                                    try
                                        let evaled = LeafExpressionConverter.EvaluateQuotation(srcExpr)
                                        match evaled with
                                        | :? System.Linq.IQueryable<'T> as iq -> Some iq
                                        | _ -> None
                                    with _ -> None
                                else None
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
                            | Some (_, Some p) -> p.Ctes |> Seq.toList
                            | _ -> []
                        | _ -> []
                    with _ -> []
                | PropertyGet(None, pi, []) when typeof<IQueryable>.IsAssignableFrom(pi.PropertyType) ->
                    try
                        let v = pi.GetValue(null)
                        match v with
                        | :? ISqlPushdownQueryable as spq -> spq.Plan.Ctes |> Seq.toList
                        | _ -> []
                    with _ -> []
                | Value(v, t) when not (isNull v) && typeof<IQueryable>.IsAssignableFrom(t) ->
                    match v with
                    | :? ISqlPushdownQueryable as spq -> spq.Plan.Ctes |> Seq.toList
                    | _ -> []
                | ExprShape.ShapeCombination(_, args) ->
                    args |> List.collect collectAllCtesFromExpr
                | ExprShape.ShapeLambda(_, b) ->
                    collectAllCtesFromExpr b
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

    type System.Linq.IQueryable<'T> with
        member this.Union(other: System.Linq.IQueryable<'T>) : System.Linq.IQueryable<'T> =
            match box this with
            | :? SqlPushdownQueryable<'T> as s1 -> s1.Union(other) :> System.Linq.IQueryable<'T>
            | _ ->
                match ExpressionPushdownTranslator.tryFindPushdownSource this.Expression with
                | Some (sourceIq, seedPlan) ->
                    let prov = sourceIq.Provider :?> SqlPushdownQueryProvider
                    let fullPlan = prov.ExtractPlan(this.Expression, ?seedPlan = seedPlan)
                    match seedPlan with
                    | Some sp ->
                        for cte in sp.Ctes do
                            if not (fullPlan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                                fullPlan.Ctes.Add(cte)
                    | None -> ()
                    let spq = SqlPushdownQueryable<'T>(prov, Expression.Constant(null, typeof<IQueryable<'T>>), prov.Connector, prov.BaseSql, prov.BaseParameters, fullPlan)
                    spq.Union(other) :> System.Linq.IQueryable<'T>
                | None -> System.Linq.Queryable.Union(this, other)

        member this.UnionAll(other: System.Linq.IQueryable<'T>) : System.Linq.IQueryable<'T> =
            match box this with
            | :? SqlPushdownQueryable<'T> as s1 -> s1.UnionAll(other) :> System.Linq.IQueryable<'T>
            | _ ->
                match ExpressionPushdownTranslator.tryFindPushdownSource this.Expression with
                | Some (sourceIq, seedPlan) ->
                    let prov = sourceIq.Provider :?> SqlPushdownQueryProvider
                    let fullPlan = prov.ExtractPlan(this.Expression, ?seedPlan = seedPlan)
                    match seedPlan with
                    | Some sp ->
                        for cte in sp.Ctes do
                            if not (fullPlan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                                fullPlan.Ctes.Add(cte)
                    | None -> ()
                    let spq = SqlPushdownQueryable<'T>(prov, Expression.Constant(null, typeof<IQueryable<'T>>), prov.Connector, prov.BaseSql, prov.BaseParameters, fullPlan)
                    spq.UnionAll(other) :> System.Linq.IQueryable<'T>
                | None -> System.Linq.Queryable.Concat(this, other)

        member this.Intersect(other: System.Linq.IQueryable<'T>) : System.Linq.IQueryable<'T> =
            match box this with
            | :? SqlPushdownQueryable<'T> as s1 -> s1.Intersect(other) :> System.Linq.IQueryable<'T>
            | _ ->
                match ExpressionPushdownTranslator.tryFindPushdownSource this.Expression with
                | Some (sourceIq, seedPlan) ->
                    let prov = sourceIq.Provider :?> SqlPushdownQueryProvider
                    let fullPlan = prov.ExtractPlan(this.Expression, ?seedPlan = seedPlan)
                    match seedPlan with
                    | Some sp ->
                        for cte in sp.Ctes do
                            if not (fullPlan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                                fullPlan.Ctes.Add(cte)
                    | None -> ()
                    let spq = SqlPushdownQueryable<'T>(prov, Expression.Constant(null, typeof<IQueryable<'T>>), prov.Connector, prov.BaseSql, prov.BaseParameters, fullPlan)
                    spq.Intersect(other) :> System.Linq.IQueryable<'T>
                | None -> System.Linq.Queryable.Intersect(this, other)

        member this.Except(other: System.Linq.IQueryable<'T>) : System.Linq.IQueryable<'T> =
            match box this with
            | :? SqlPushdownQueryable<'T> as s1 -> s1.Except(other) :> System.Linq.IQueryable<'T>
            | _ ->
                match ExpressionPushdownTranslator.tryFindPushdownSource this.Expression with
                | Some (sourceIq, seedPlan) ->
                    let prov = sourceIq.Provider :?> SqlPushdownQueryProvider
                    let fullPlan = prov.ExtractPlan(this.Expression, ?seedPlan = seedPlan)
                    match seedPlan with
                    | Some sp ->
                        for cte in sp.Ctes do
                            if not (fullPlan.Ctes |> Seq.exists (fun c -> c.Name = cte.Name)) then
                                fullPlan.Ctes.Add(cte)
                    | None -> ()
                    let spq = SqlPushdownQueryable<'T>(prov, Expression.Constant(null, typeof<IQueryable<'T>>), prov.Connector, prov.BaseSql, prov.BaseParameters, fullPlan)
                    spq.Except(other) :> System.Linq.IQueryable<'T>
                | None -> System.Linq.Queryable.Except(this, other)

        member this.WithCte(cteQuery: System.Linq.IQueryable<'U>, ?isRecursive: bool) : System.Linq.IQueryable<'T> =
            match box this with
            | :? SqlPushdownQueryable<'T> as spq -> spq.WithCte(cteQuery, ?isRecursive = isRecursive)
            | _ ->
                match SqlPushdownQuery.tryFindPushdownSource this.Expression with
                | Some (sourceIq, Some plan) ->
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
                        newPlan.Ctes.Add({ Name = autoName; SubquerySql = remappedSql; IsRecursive = defaultArg isRecursive false; SourceQuery = Some (box cteQuery) })
                        SqlPushdownQueryable<'T>(sourceIq.Provider, this.Expression, spq.Connector, "", [], newPlan) :> System.Linq.IQueryable<'T>
                    | _ -> this
                | _ -> this

        member this.WithCte(name: string, cteQuery: System.Linq.IQueryable<'U>, ?isRecursive: bool) : System.Linq.IQueryable<'T> =
            match box this with
            | :? SqlPushdownQueryable<'T> as spq -> spq.WithCte(name, cteQuery, ?isRecursive = isRecursive)
            | _ ->
                match SqlPushdownQuery.tryFindPushdownSource this.Expression with
                | Some (sourceIq, Some plan) ->
                    match box sourceIq with
                    | :? ISqlPushdownQueryable as spq ->
                        let newPlan = plan.Clone()
                        let (subSql, subParams) = SqlPushdownQuery.extractSqlWithParameters cteQuery
                        let mutable remappedSql = subSql
                        for (pName, pVal) in subParams do
                            let uniqueName = $"@p{newPlan.Parameters.Count}"
                            remappedSql <- remappedSql.Replace(pName, uniqueName)
                            newPlan.Parameters.Add(uniqueName, pVal)
                        newPlan.Ctes.Add({ Name = name; SubquerySql = remappedSql; IsRecursive = defaultArg isRecursive false; SourceQuery = Some (box cteQuery) })
                        SqlPushdownQueryable<'T>(sourceIq.Provider, this.Expression, spq.Connector, "", [], newPlan) :> System.Linq.IQueryable<'T>
                    | _ -> this
                | _ -> this



