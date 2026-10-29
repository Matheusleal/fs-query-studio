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

/// Identifies the SQL dialect for pushdown syntax generation (e.g. LIMIT vs TOP/OFFSET).
[<RequireQualifiedAccess>]
type SqlDialect =
    | Sqlite
    | PostgreSql
    | SqlServer

module SqlDialect =
    let fromConnectorKind (kind: ConnectorKind) =
        if kind = ConnectorKind("PostgreSql") then SqlDialect.PostgreSql
        elif kind = ConnectorKind("SqlServer") then SqlDialect.SqlServer
        else SqlDialect.Sqlite

/// Static helper methods for reading primitive types directly from IDataRecord without boxing.
type RecordReaders =
    static member ReadInt32(record: IDataRecord, idx: int) : int =
        if record.IsDBNull(idx) then 0
        else
            try record.GetInt32(idx)
            with _ ->
                let v = record.GetValue(idx)
                match v with
                | :? int as i -> i
                | :? int64 as i64 -> int i64
                | :? int16 as i16 -> int i16
                | :? byte as b -> int b
                | :? decimal as d -> int d
                | :? double as d -> int d
                | :? string as s ->
                    match Int32.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture) with
                    | true, i -> i
                    | false, _ -> Convert.ToInt32(v, CultureInfo.InvariantCulture)
                | _ -> Convert.ToInt32(v, CultureInfo.InvariantCulture)

    static member ReadInt64(record: IDataRecord, idx: int) : int64 =
        if record.IsDBNull(idx) then 0L
        else
            try record.GetInt64(idx)
            with _ ->
                let v = record.GetValue(idx)
                match v with
                | :? int64 as i64 -> i64
                | :? int as i -> int64 i
                | :? int16 as i16 -> int64 i16
                | :? byte as b -> int64 b
                | :? decimal as d -> int64 d
                | :? double as d -> int64 d
                | :? string as s ->
                    match Int64.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture) with
                    | true, i64 -> i64
                    | false, _ -> Convert.ToInt64(v, CultureInfo.InvariantCulture)
                | _ -> Convert.ToInt64(v, CultureInfo.InvariantCulture)

    static member ReadInt16(record: IDataRecord, idx: int) : int16 =
        if record.IsDBNull(idx) then 0s
        else
            try record.GetInt16(idx)
            with _ ->
                let v = record.GetValue(idx)
                match v with
                | :? int16 as i16 -> i16
                | :? int as i -> int16 i
                | :? int64 as i64 -> int16 i64
                | :? byte as b -> int16 b
                | _ -> Convert.ToInt16(v, CultureInfo.InvariantCulture)

    static member ReadByte(record: IDataRecord, idx: int) : byte =
        if record.IsDBNull(idx) then 0uy
        else
            try record.GetByte(idx)
            with _ -> Convert.ToByte(record.GetValue(idx), CultureInfo.InvariantCulture)

    static member ReadDecimal(record: IDataRecord, idx: int) : decimal =
        if record.IsDBNull(idx) then 0M
        else
            try record.GetDecimal(idx)
            with _ ->
                let v = record.GetValue(idx)
                match v with
                | :? decimal as d -> d
                | :? double as d -> Convert.ToDecimal(d, CultureInfo.InvariantCulture)
                | :? single as f -> Convert.ToDecimal(f, CultureInfo.InvariantCulture)
                | :? int as i -> decimal i
                | :? int64 as i64 -> decimal i64
                | :? string as s ->
                    match Decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture) with
                    | true, d -> d
                    | false, _ -> Convert.ToDecimal(v, CultureInfo.InvariantCulture)
                | _ -> Convert.ToDecimal(v, CultureInfo.InvariantCulture)

    static member ReadDouble(record: IDataRecord, idx: int) : double =
        if record.IsDBNull(idx) then 0.0
        else
            try record.GetDouble(idx)
            with _ -> Convert.ToDouble(record.GetValue(idx), CultureInfo.InvariantCulture)

    static member ReadSingle(record: IDataRecord, idx: int) : single =
        if record.IsDBNull(idx) then 0.0f
        else
            try record.GetFloat(idx)
            with _ -> Convert.ToSingle(record.GetValue(idx), CultureInfo.InvariantCulture)

    static member ReadBool(record: IDataRecord, idx: int) : bool =
        if record.IsDBNull(idx) then false
        else
            try record.GetBoolean(idx)
            with _ ->
                let v = record.GetValue(idx)
                match v with
                | :? bool as b -> b
                | :? int as i -> i <> 0
                | :? int64 as i64 -> i64 <> 0L
                | :? string as s ->
                    match Boolean.TryParse(s) with
                    | true, b -> b
                    | false, _ -> s = "1" || s.Equals("yes", StringComparison.OrdinalIgnoreCase)
                | _ -> Convert.ToBoolean(v, CultureInfo.InvariantCulture)

    static member ReadString(record: IDataRecord, idx: int) : string =
        if record.IsDBNull(idx) then null
        else
            try record.GetString(idx)
            with _ -> string (record.GetValue(idx))

    static member ReadGuid(record: IDataRecord, idx: int) : Guid =
        if record.IsDBNull(idx) then Guid.Empty
        else
            try record.GetGuid(idx)
            with _ ->
                let v = record.GetValue(idx)
                match v with
                | :? Guid as g -> g
                | :? string as s -> Guid.Parse(s)
                | :? (byte array) as b -> Guid(b)
                | _ -> Guid.Parse(string v)

    static member ReadDateTime(record: IDataRecord, idx: int) : DateTime =
        if record.IsDBNull(idx) then DateTime.MinValue
        else
            try record.GetDateTime(idx)
            with _ ->
                let v = record.GetValue(idx)
                match v with
                | :? DateTime as dt -> dt
                | :? DateOnly as d -> d.ToDateTime(TimeOnly.MinValue)
                | :? DateTimeOffset as dto -> dto.DateTime
                | :? string as s ->
                    match DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None) with
                    | true, dt -> dt
                    | false, _ -> Convert.ToDateTime(v, CultureInfo.InvariantCulture)
                | _ -> Convert.ToDateTime(v, CultureInfo.InvariantCulture)

    static member ReadDateOnly(record: IDataRecord, idx: int) : DateOnly =
        if record.IsDBNull(idx) then DateOnly.MinValue
        else
            let v = record.GetValue(idx)
            match v with
            | :? DateOnly as d -> d
            | :? DateTime as dt -> DateOnly.FromDateTime(dt)
            | :? DateTimeOffset as dto -> DateOnly.FromDateTime(dto.DateTime)
            | :? string as s ->
                match DateOnly.TryParse(s, CultureInfo.InvariantCulture) with
                | true, d -> d
                | false, _ -> DateOnly.FromDateTime(Convert.ToDateTime(v, CultureInfo.InvariantCulture))
            | _ -> DateOnly.FromDateTime(Convert.ToDateTime(v, CultureInfo.InvariantCulture))

    static member ConvertValue (raw: obj) (targetType: Type) : obj =
        if isNull raw || Convert.IsDBNull(raw) then
            if targetType.IsValueType && Nullable.GetUnderlyingType(targetType) = null then
                Activator.CreateInstance(targetType)
            else
                null
        elif targetType = typeof<obj> then
            raw
        elif raw.GetType() = targetType then
            raw
        elif targetType = typeof<string> then
            box (string raw)
        elif raw :? string then
            let rawStr = (raw :?> string).Trim().Trim('"', '\'')
            if targetType = typeof<bool> then
                match Boolean.TryParse(rawStr) with
                | true, v -> box v
                | false, _ -> box (rawStr = "1" || String.Equals(rawStr, "yes", StringComparison.OrdinalIgnoreCase))
            elif targetType = typeof<int> then
                match Int32.TryParse(rawStr, NumberStyles.Any, CultureInfo.InvariantCulture) with
                | true, v -> box v
                | false, _ -> Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)
            elif targetType = typeof<int64> then
                match Int64.TryParse(rawStr, NumberStyles.Any, CultureInfo.InvariantCulture) with
                | true, v -> box v
                | false, _ -> Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)
            elif targetType = typeof<decimal> then
                match Decimal.TryParse(rawStr, NumberStyles.Any, CultureInfo.InvariantCulture) with
                | true, v -> box v
                | false, _ -> Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)
            elif targetType = typeof<double> then
                match Double.TryParse(rawStr, NumberStyles.Any, CultureInfo.InvariantCulture) with
                | true, v -> box v
                | false, _ -> Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)
            elif targetType = typeof<DateTime> then
                match DateTime.TryParse(rawStr, CultureInfo.InvariantCulture, DateTimeStyles.None) with
                | true, v -> box v
                | false, _ ->
                    match DateTime.TryParse(rawStr, CultureInfo.CurrentCulture, DateTimeStyles.None) with
                    | true, v -> box v
                    | false, _ -> Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)
            elif targetType = typeof<DateOnly> then
                match DateOnly.TryParse(rawStr, CultureInfo.InvariantCulture) with
                | true, v -> box v
                | false, _ ->
                    match DateTime.TryParse(rawStr, CultureInfo.InvariantCulture, DateTimeStyles.None) with
                    | true, dt -> box (DateOnly.FromDateTime(dt))
                    | false, _ -> Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)
            elif targetType = typeof<Guid> then
                match Guid.TryParse(rawStr) with
                | true, v -> box v
                | false, _ -> Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)
            else
                Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)
        else
            if targetType = typeof<decimal> then
                match raw with
                | :? double as d -> box (Convert.ToDecimal(d, CultureInfo.InvariantCulture))
                | :? single as f -> box (Convert.ToDecimal(f, CultureInfo.InvariantCulture))
                | :? int as i -> box (decimal i)
                | :? int64 as i64 -> box (decimal i64)
                | _ -> Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture)
            elif targetType = typeof<DateTime> then
                match raw with
                | :? DateOnly as d -> box (d.ToDateTime(TimeOnly.MinValue))
                | :? DateTimeOffset as dto -> box dto.DateTime
                | _ -> Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture)
            elif targetType = typeof<DateOnly> then
                match raw with
                | :? DateTime as dt -> box (DateOnly.FromDateTime(dt))
                | :? DateTimeOffset as dto -> box (DateOnly.FromDateTime(dto.DateTime))
                | _ -> DateOnly.Parse(string raw, CultureInfo.InvariantCulture) |> box
            else
                Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture)

    static member ReadOption<'U>(record: IDataRecord, idx: int) : 'U option =
        if record.IsDBNull(idx) then None
        else
            let raw = record.GetValue(idx)
            let isOptionNone =
                isNull raw || Convert.IsDBNull(raw) ||
                (match raw with
                 | :? string as s ->
                     let t = s.Trim().Trim('"', '\'')
                     String.IsNullOrEmpty t
                     || String.Equals(t, "n/a", StringComparison.OrdinalIgnoreCase)
                     || t = "-"
                     || String.Equals(t, "null", StringComparison.OrdinalIgnoreCase)
                 | _ -> false)

            if isOptionNone then None
            else
                let converted = RecordReaders.ConvertValue raw typeof<'U>
                if isNull converted then None
                else Some (unbox<'U> converted)

    static member ReadObject(record: IDataRecord, idx: int, targetType: Type) : obj =
        if record.IsDBNull(idx) then null
        else RecordReaders.ConvertValue (record.GetValue(idx)) targetType

/// High-performance compiled mapper generator that caches Func<IDataRecord, 'T> delegates.
module CompiledRecordMapper =
    let private compiledMapperCache = ConcurrentDictionary<Type * string, obj>()

    let getCompiledRecordMapper<'T> (columns: string list) : (IDataRecord -> 'T) =
        let targetType = typeof<'T>
        let key = (targetType, String.concat "," columns)
        let raw =
            compiledMapperCache.GetOrAdd(
                key,
                Func<Type * string, obj>(fun _ ->
                    if targetType = typeof<obj array> then
                        box (fun (record: IDataRecord) ->
                            [| for i in 0 .. record.FieldCount - 1 -> record.GetValue(i) |] :> obj
                        )
                    elif FSharpType.IsRecord(targetType) then
                        let fields = FSharpType.GetRecordFields(targetType)
                        let colIndices =
                            fields
                            |> Array.mapi (fun defaultIdx f ->
                                let byName =
                                    columns
                                    |> List.tryFindIndex (fun c -> String.Equals(c, f.Name, StringComparison.OrdinalIgnoreCase))
                                match byName with
                                | Some idx -> idx
                                | None -> if defaultIdx < columns.Length then defaultIdx else 0
                            )

                        let recordParam = Expression.Parameter(typeof<IDataRecord>, "record")
                        let args: Expression list =
                            fields
                            |> Array.mapi (fun i f ->
                                let idx = colIndices.[i]
                                let ft = f.PropertyType
                                if ft = typeof<int> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadInt32"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<int64> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadInt64"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<int16> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadInt16"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<byte> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadByte"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<decimal> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadDecimal"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<double> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadDouble"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<single> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadSingle"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<bool> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadBool"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<string> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadString"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<Guid> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadGuid"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<DateTime> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadDateTime"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<DateOnly> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadDateOnly"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft.IsGenericType && ft.GetGenericTypeDefinition() = typedefof<option<_>> then
                                    let innerType = ft.GetGenericArguments().[0]
                                    let readOpt = typeof<RecordReaders>.GetMethod("ReadOption").MakeGenericMethod([| innerType |])
                                    Expression.Call(readOpt, recordParam, Expression.Constant(idx)) :> Expression
                                else
                                    let readObj = typeof<RecordReaders>.GetMethod("ReadObject")
                                    let callExpr = Expression.Call(readObj, recordParam, Expression.Constant(idx), Expression.Constant(ft))
                                    Expression.Convert(callExpr, ft) :> Expression
                            )
                            |> List.ofArray

                        let ctor =
                            match targetType.GetConstructors() with
                            | [| c |] -> c
                            | ctors -> ctors |> Array.maxBy (fun c -> c.GetParameters().Length)
                        let newExpr = Expression.New(ctor, args :> seq<Expression>)
                        let lambda = Expression.Lambda<Func<IDataRecord, 'T>>(newExpr, recordParam)
                        let compiled = lambda.Compile()
                        box (fun (record: IDataRecord) -> compiled.Invoke(record) :> obj)
                    else
                        box (fun (record: IDataRecord) ->
                            if record.FieldCount > 0 && not (record.IsDBNull(0)) then
                                RecordReaders.ConvertValue (record.GetValue(0)) targetType
                            else
                                null
                        )
                )
            )
        let typedFunc = raw :?> (IDataRecord -> obj)
        fun (record: IDataRecord) -> unbox<'T> (typedFunc record)

type OrderDirection =
    | Ascending
    | Descending

type OrderClause = {
    ColumnName: string
    Direction: OrderDirection
}

/// Represents the planned SQL pushdown query and any in-memory fallback predicates.
type PushdownQueryPlan = {
    BaseSql: string
    BaseParameters: (string * obj) list
    WhereClauses: ResizeArray<string>
    Parameters: ResizeArray<string * obj>
    OrderBy: ResizeArray<OrderClause>
    mutable Skip: int option
    mutable Take: int option
    mutable Projection: string option
    FallbackPredicates: ResizeArray<obj -> bool>
} with
    static member Create(baseSql: string, baseParameters: (string * obj) list) =
        {
            BaseSql = baseSql
            BaseParameters = baseParameters
            WhereClauses = ResizeArray<string>()
            Parameters = ResizeArray<string * obj>()
            OrderBy = ResizeArray<OrderClause>()
            Skip = None
            Take = None
            Projection = None
            FallbackPredicates = ResizeArray<obj -> bool>()
        }

    member this.Clone() =
        let p = PushdownQueryPlan.Create(this.BaseSql, this.BaseParameters)
        p.WhereClauses.AddRange(this.WhereClauses)
        p.Parameters.AddRange(this.Parameters)
        p.OrderBy.AddRange(this.OrderBy)
        p.Skip <- this.Skip
        p.Take <- this.Take
        p.Projection <- this.Projection
        p.FallbackPredicates.AddRange(this.FallbackPredicates)
        p

    member this.BuildSql(dialect: SqlDialect) : string =
        let isTableOnly =
            let trimmed = this.BaseSql.Trim()
            not (trimmed.StartsWith("select", StringComparison.OrdinalIgnoreCase))
            && not (trimmed.Contains(" "))

        let fromSource =
            if isTableOnly then
                this.BaseSql.Trim()
            else
                $"({this.BaseSql}) AS _src"

        let whereSql =
            if this.WhereClauses.Count > 0 then
                " WHERE " + String.concat " AND " this.WhereClauses
            else
                ""

        let orderSql =
            if this.OrderBy.Count > 0 then
                let parts =
                    this.OrderBy
                    |> Seq.map (fun o ->
                        let dir = if o.Direction = Ascending then "ASC" else "DESC"
                        $"{o.ColumnName} {dir}"
                    )
                " ORDER BY " + String.concat ", " parts
            else
                ""

        match this.Projection with
        | Some proj ->
            $"SELECT {proj} FROM {fromSource}{whereSql}"
        | None ->
            match dialect with
            | SqlDialect.SqlServer ->
                match this.Skip, this.Take with
                | Some skip, Some take ->
                    let ord = if orderSql = "" then " ORDER BY (SELECT NULL)" else orderSql
                    $"SELECT * FROM {fromSource}{whereSql}{ord} OFFSET {skip} ROWS FETCH NEXT {take} ROWS ONLY"
                | Some skip, None ->
                    let ord = if orderSql = "" then " ORDER BY (SELECT NULL)" else orderSql
                    $"SELECT * FROM {fromSource}{whereSql}{ord} OFFSET {skip} ROWS"
                | None, Some take ->
                    if orderSql <> "" then
                        $"SELECT * FROM {fromSource}{whereSql}{orderSql} OFFSET 0 ROWS FETCH NEXT {take} ROWS ONLY"
                    else
                        $"SELECT TOP ({take}) * FROM {fromSource}{whereSql}"
                | None, None ->
                    $"SELECT * FROM {fromSource}{whereSql}{orderSql}"
            | _ ->
                let limitSql =
                    match this.Take, this.Skip with
                    | Some take, Some skip -> $" LIMIT {take} OFFSET {skip}"
                    | Some take, None -> $" LIMIT {take}"
                    | None, Some skip -> $" OFFSET {skip}"
                    | None, None -> ""

                $"SELECT * FROM {fromSource}{whereSql}{orderSql}{limitSql}"

module ExpressionPushdownTranslator =
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

    let rec extractColumnName (param: ParameterExpression) (expr: Expression) : string =
        match expr with
        | :? MemberExpression as m when m.Expression = (param :> Expression) ->
            m.Member.Name
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert ->
            extractColumnName param u.Operand
        | _ ->
            expr.ToString()

    let rec translatePredicate (param: ParameterExpression) (planParams: ResizeArray<string * obj>) (expr: Expression) : string option =
        match expr with
        | null -> None
        | :? BinaryExpression as b ->
            match b.NodeType with
            | ExpressionType.AndAlso ->
                match translatePredicate param planParams b.Left, translatePredicate param planParams b.Right with
                | Some l, Some r -> Some $"({l} AND {r})"
                | Some l, None -> Some l
                | None, Some r -> Some r
                | None, None -> None
            | ExpressionType.OrElse ->
                match translatePredicate param planParams b.Left, translatePredicate param planParams b.Right with
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
                    let col = extractColumnName param b.Left
                    let clientVal = evaluateClientValue b.Right
                    if isNull clientVal then
                        if op = "=" then Some $"({col} IS NULL)"
                        elif op = "<>" then Some $"({col} IS NOT NULL)"
                        else None
                    else
                        let pName = $"@p{planParams.Count}"
                        planParams.Add(pName, clientVal)
                        Some $"({col} {op} {pName})"
                elif not leftHasParam && rightHasParam then
                    let col = extractColumnName param b.Right
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
                        let pName = $"@p{planParams.Count}"
                        planParams.Add(pName, clientVal)
                        Some $"({col} {invOp} {pName})"
                else
                    None
            | _ -> None
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Not ->
            match translatePredicate param planParams u.Operand with
            | Some inner -> Some $"(NOT {inner})"
            | None -> None
        | :? MethodCallExpression as mc ->
            if mc.Method.DeclaringType = typeof<string> && mc.Arguments.Count = 1 && dependsOnParameter param mc.Object && not (dependsOnParameter param mc.Arguments.[0]) then
                let col = extractColumnName param mc.Object
                let rawVal = evaluateClientValue mc.Arguments.[0]
                if isNull rawVal then None
                else
                    let sVal = string rawVal
                    let pName = $"@p{planParams.Count}"
                    match mc.Method.Name with
                    | "Contains" ->
                        planParams.Add(pName, box ("%" + sVal + "%"))
                        Some $"({col} LIKE {pName})"
                    | "StartsWith" ->
                        planParams.Add(pName, box (sVal + "%"))
                        Some $"({col} LIKE {pName})"
                    | "EndsWith" ->
                        planParams.Add(pName, box ("%" + sVal))
                        Some $"({col} LIKE {pName})"
                    | _ -> None
            else
                None
        | _ -> None

    let rec unwrapQuote (expr: Expression) : Expression =
        match expr with
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Quote -> unwrapQuote u.Operand
        | _ -> expr

    let rec extractMemberName (expr: Expression) : string =
        let unwrapped = unwrapQuote expr
        match unwrapped with
        | :? LambdaExpression as l -> extractMemberName l.Body
        | :? MemberExpression as m -> m.Member.Name
        | :? UnaryExpression as u -> extractMemberName u.Operand
        | _ -> unwrapped.ToString()

    let mergeExpressionIntoPlan (rootExpr: Expression) (plan: PushdownQueryPlan) : unit =
        let methodCalls = ResizeArray<MethodCallExpression>()
        let rec collectCalls (e: Expression) =
            match e with
            | :? MethodCallExpression as mc ->
                methodCalls.Add(mc)
                if mc.Arguments.Count > 0 then
                    collectCalls mc.Arguments.[0]
            | _ -> ()

        collectCalls rootExpr

        // Process calls from inner to outer so ORDER BY and WHERE build naturally
        let reversed = methodCalls |> Seq.toArray |> Array.rev

        for mc in reversed do
            match mc.Method.Name with
            | "Where" when mc.Arguments.Count >= 2 ->
                let arg1 = unwrapQuote mc.Arguments.[1]
                match arg1 with
                | :? LambdaExpression as l ->
                    let param = l.Parameters.[0]
                    match translatePredicate param plan.Parameters l.Body with
                    | Some sqlPred ->
                        plan.WhereClauses.Add(sqlPred)
                    | None ->
                        let compiled = l.Compile()
                        plan.FallbackPredicates.Add(fun item -> unbox<bool> (compiled.DynamicInvoke(item)))
                | _ -> ()
            | "OrderBy" when mc.Arguments.Count >= 2 ->
                let col = extractMemberName mc.Arguments.[1]
                plan.OrderBy.Add({ ColumnName = col; Direction = Ascending })
            | "OrderByDescending" when mc.Arguments.Count >= 2 ->
                let col = extractMemberName mc.Arguments.[1]
                plan.OrderBy.Add({ ColumnName = col; Direction = Descending })
            | "ThenBy" when mc.Arguments.Count >= 2 ->
                let col = extractMemberName mc.Arguments.[1]
                plan.OrderBy.Add({ ColumnName = col; Direction = Ascending })
            | "ThenByDescending" when mc.Arguments.Count >= 2 ->
                let col = extractMemberName mc.Arguments.[1]
                plan.OrderBy.Add({ ColumnName = col; Direction = Descending })
            | "Take" when mc.Arguments.Count >= 2 ->
                let count = evaluateClientValue mc.Arguments.[1] :?> int
                plan.Take <- Some count
            | "Skip" when mc.Arguments.Count >= 2 ->
                let count = evaluateClientValue mc.Arguments.[1] :?> int
                plan.Skip <- Some count
            | "Count" ->
                plan.Projection <- Some "COUNT(*)"
            | "Sum" when mc.Arguments.Count >= 2 ->
                let col = extractMemberName mc.Arguments.[1]
                plan.Projection <- Some $"SUM({col})"
            | _ -> ()

    let rec hasJoin (expr: Expression) : bool =
        match expr with
        | null -> false
        | :? MethodCallExpression as mc ->
            if mc.Method.Name = "Join" || mc.Method.Name = "GroupJoin" then true
            else
                (if isNull mc.Object then false else hasJoin mc.Object)
                || (mc.Arguments |> Seq.exists hasJoin)
        | :? LambdaExpression as l -> hasJoin l.Body
        | :? UnaryExpression as u -> hasJoin u.Operand
        | :? BinaryExpression as b -> hasJoin b.Left || hasJoin b.Right
        | _ -> false

    let extractPlanFromExpression (rootExpr: Expression) (baseSql: string) (baseParameters: (string * obj) list) : PushdownQueryPlan =
        let plan = PushdownQueryPlan.Create(baseSql, baseParameters)
        mergeExpressionIntoPlan rootExpr plan
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
                    m.Name = mc.Method.Name
                    && m.IsGenericMethod
                    && m.GetGenericArguments().Length = methodGenericArgs.Length
                    && m.GetParameters().Length = evaluatedArgs.Length
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

    member this.ExecutePlanOrStream<'T>(expression: Expression, ?seedPlan: PushdownQueryPlan) : 'T seq =
        if ExpressionPushdownTranslator.hasJoin expression then
            let res = LinqInMemoryFallback.evaluate expression
            res :?> IEnumerable<'T>
        else
            let plan =
                match seedPlan with
                | Some sp ->
                    let cloned = sp.Clone()
                    ExpressionPushdownTranslator.mergeExpressionIntoPlan expression cloned
                    cloned
                | None ->
                    ExpressionPushdownTranslator.extractPlanFromExpression expression baseSql baseParameters
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
        let plan = ExpressionPushdownTranslator.extractPlanFromExpression expression baseSql baseParameters
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
            Activator.CreateInstance(
                typedefof<SqlPushdownQueryable<_>>.MakeGenericType(elemType),
                this, expression, connector, baseSql, baseParameters, PushdownQueryPlan.Create(baseSql, baseParameters)
            ) :?> IQueryable

        member this.CreateQuery<'TElement>(expression: Expression) : IQueryable<'TElement> =
            SqlPushdownQueryable<'TElement>(this, expression, connector, baseSql, baseParameters, PushdownQueryPlan.Create(baseSql, baseParameters)) :> IQueryable<'TElement>

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
        let plan = PushdownQueryPlan.Create(baseSql, p)
        SqlPushdownQueryable<'T>(provider, Expression.Constant(null, typeof<IQueryable<'T>>), connector, baseSql, p, plan)

    member _.Plan = plan

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


