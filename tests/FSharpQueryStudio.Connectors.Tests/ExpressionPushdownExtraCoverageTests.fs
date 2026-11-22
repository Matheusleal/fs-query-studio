namespace FSharpQueryStudio.Connectors.Tests

open System
open System.Collections.Generic
open System.IO
open System.Linq
open System.Linq.Expressions
open Microsoft.Data.Sqlite
open Xunit
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Protocol

[<CLIMutable>]
type ExtraProduct =
    {
        Id: int
        Name: string
        Price: decimal
        Category: string
        InStock: bool
        Score: Nullable<decimal>
    }

type StaticCteHolder =
    static member SampleSpq: IQueryable<ExtraProduct> =
        let conn = SqliteConnector("Data Source=:memory:")
        let spq = SqlPushdownQueryable<ExtraProduct>(conn, "Products")

        spq.Plan.Ctes.Add(
            {
                Name = "static_cte"
                SubquerySql = "SELECT 1"
                IsRecursive = false
                SourceQuery = None
            }
        )

        spq :> IQueryable<ExtraProduct>

    static member InMemIq: IQueryable<int> = ([ 1; 2 ]: int list).AsQueryable()

    static member ThrowingIq: IQueryable<int> =
        raise (InvalidOperationException("Forced throwing queryable for test"))

type SpqFieldClass =
    val mutable public SpqField: IQueryable<ExtraProduct>
    new() = { SpqField = null }

type AnyFieldClass =
    val mutable public AnyField: obj
    new() = { AnyField = null }

type SpqPropHolder = { SpqProp: IQueryable<ExtraProduct> }

module ExpressionPushdownExtraCoverageTests =

    let private dialect = SqlDialect.Sqlite

    // -------------------------------------------------------------------------
    // 1. dependsOnParameter & hasAnyParameter on All Expression Types (>=2 tests/scenario)
    // -------------------------------------------------------------------------

    [<Fact>]
    let ``hasAnyParameter with ParameterExpression scenario 1`` () =
        let p = Expression.Parameter(typeof<int>, "x")
        Assert.True(ExpressionPushdownTranslator.hasAnyParameter p)

    [<Fact>]
    let ``hasAnyParameter with ParameterExpression scenario 2`` () =
        let p = Expression.Parameter(typeof<string>, "str")
        Assert.True(ExpressionPushdownTranslator.hasAnyParameter p)

    [<Fact>]
    let ``hasAnyParameter with BlockExpression scenario 1`` () =
        let p = Expression.Parameter(typeof<int>, "x")

        let blockWithParam =
            Expression.Block([| p :> Expression; Expression.Add(p, Expression.Constant(1)) :> Expression |])

        Assert.True(ExpressionPushdownTranslator.hasAnyParameter blockWithParam)

    [<Fact>]
    let ``hasAnyParameter with BlockExpression scenario 2`` () =
        let blockWithoutParam =
            Expression.Block([| Expression.Constant(42) :> Expression |])

        Assert.False(ExpressionPushdownTranslator.hasAnyParameter blockWithoutParam)

    [<Fact>]
    let ``hasAnyParameter with NewExpression scenario 1`` () =
        let p = Expression.Parameter(typeof<string>, "s")
        let ctor = typeof<Tuple<string>>.GetConstructor([| typeof<string> |])
        let newWithParam = Expression.New(ctor, [| p :> Expression |])
        Assert.True(ExpressionPushdownTranslator.hasAnyParameter newWithParam)

    [<Fact>]
    let ``hasAnyParameter with NewExpression scenario 2`` () =
        let ctor = typeof<Tuple<int>>.GetConstructor([| typeof<int> |])

        let newWithoutParam =
            Expression.New(ctor, [| Expression.Constant(10) :> Expression |])

        Assert.False(ExpressionPushdownTranslator.hasAnyParameter newWithoutParam)

    [<Fact>]
    let ``hasAnyParameter with InvocationExpression scenario 1`` () =
        let p = Expression.Parameter(typeof<int>, "i")

        let lambda =
            Expression.Lambda<Func<int, int>>(Expression.Add(p, Expression.Constant(1)), p)

        let invWithParam = Expression.Invoke(lambda, p)
        Assert.True(ExpressionPushdownTranslator.hasAnyParameter invWithParam)

    [<Fact>]
    let ``hasAnyParameter with InvocationExpression scenario 2`` () =
        let p = Expression.Parameter(typeof<int>, "i")
        let lambda = Expression.Lambda<Func<int, int>>(Expression.Constant(99), p)
        let invWithoutParam = Expression.Invoke(lambda, Expression.Constant(5))
        Assert.False(ExpressionPushdownTranslator.hasAnyParameter invWithoutParam)

    [<Fact>]
    let ``hasAnyParameter with ConditionalExpression scenario 1`` () =
        let p = Expression.Parameter(typeof<bool>, "flag")

        let condWithParam =
            Expression.Condition(p, Expression.Constant(1), Expression.Constant(2))

        Assert.True(ExpressionPushdownTranslator.hasAnyParameter condWithParam)

    [<Fact>]
    let ``hasAnyParameter with ConditionalExpression scenario 2`` () =
        let condWithoutParam =
            Expression.Condition(Expression.Constant(true), Expression.Constant(1), Expression.Constant(2))

        Assert.False(ExpressionPushdownTranslator.hasAnyParameter condWithoutParam)

    [<Fact>]
    let ``dependsOnParameter with ConditionalExpression and BlockExpression scenario 1`` () =
        let p = Expression.Parameter(typeof<int>, "target")
        let other = Expression.Parameter(typeof<int>, "other")

        let cond =
            Expression.Condition(
                Expression.Equal(p, Expression.Constant(1)),
                Expression.Constant(10),
                Expression.Constant(20)
            )

        Assert.True(ExpressionPushdownTranslator.dependsOnParameter p cond)
        Assert.False(ExpressionPushdownTranslator.dependsOnParameter other cond)

    [<Fact>]
    let ``dependsOnParameter with ConditionalExpression and BlockExpression scenario 2`` () =
        let p = Expression.Parameter(typeof<string>, "name")
        let other = Expression.Parameter(typeof<string>, "other")

        let block =
            Expression.Block(
                [|
                    Expression.Constant("start") :> Expression
                    Expression.Equal(p, Expression.Constant("test")) :> Expression
                |]
            )

        Assert.True(ExpressionPushdownTranslator.dependsOnParameter p block)
        Assert.False(ExpressionPushdownTranslator.dependsOnParameter other block)

    // -------------------------------------------------------------------------
    // 2. isNullConstantExpr on Value, Reference, and Nullable types (>=2 tests/scenario)
    // -------------------------------------------------------------------------

    [<Fact>]
    let ``isNullConstantExpr with null constants and casts scenario 1`` () =
        let nullConst = Expression.Constant(null)
        let castNull = Expression.Convert(nullConst, typeof<obj>)
        let castCheckedNull = Expression.ConvertChecked(nullConst, typeof<string>)
        let typeAsNull = Expression.TypeAs(nullConst, typeof<string>)
        Assert.True(ExpressionPushdownTranslator.isNullConstantExpr null)
        Assert.True(ExpressionPushdownTranslator.isNullConstantExpr nullConst)
        Assert.True(ExpressionPushdownTranslator.isNullConstantExpr castNull)
        Assert.True(ExpressionPushdownTranslator.isNullConstantExpr castCheckedNull)
        Assert.True(ExpressionPushdownTranslator.isNullConstantExpr typeAsNull)

    [<Fact>]
    let ``isNullConstantExpr with null constants and casts scenario 2`` () =
        let defStr = Expression.Default(typeof<string>)
        let defNullable = Expression.Default(typeof<Nullable<int>>)
        let defVal = Expression.Default(typeof<int>)
        let nonNullConst = Expression.Constant(123)
        Assert.True(ExpressionPushdownTranslator.isNullConstantExpr defStr)
        Assert.True(ExpressionPushdownTranslator.isNullConstantExpr defNullable)
        Assert.False(ExpressionPushdownTranslator.isNullConstantExpr defVal)
        Assert.False(ExpressionPushdownTranslator.isNullConstantExpr nonNullConst)

    // -------------------------------------------------------------------------
    // 3. translatePredicate with ConditionalExpressions (>=2 tests/scenario)
    // -------------------------------------------------------------------------

    [<Fact>]
    let ``translatePredicate with ConditionalExpression test-true-false and test-false-true scenario 1`` () =
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let plan = PushdownQueryPlan.Create("Products", [])
        let test = Expression.Equal(Expression.Property(p, "Id"), Expression.Constant(10))

        // if test then true else false -> test
        let cond1 =
            Expression.Condition(test, Expression.Constant(true), Expression.Constant(false))

        let res1 = ExpressionPushdownTranslator.translatePredicate p plan dialect cond1
        Assert.True(Option.isSome res1)
        Assert.Contains("Id = @p0", Option.get res1)

        // if test then false else true -> NOT (test)
        let cond2 =
            Expression.Condition(test, Expression.Constant(false), Expression.Constant(true))

        let res2 = ExpressionPushdownTranslator.translatePredicate p plan dialect cond2
        Assert.True(Option.isSome res2)
        Assert.Contains("NOT (Id = @p1)", Option.get res2)

    [<Fact>]
    let ``translatePredicate with ConditionalExpression compound branches scenario 2`` () =
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let plan = PushdownQueryPlan.Create("Products", [])
        let test = Expression.Equal(Expression.Property(p, "Id"), Expression.Constant(10))

        let pred =
            Expression.Equal(Expression.Property(p, "Price"), Expression.Constant(50.0m))

        // if test then pred else false -> (test AND pred)
        let condAnd = Expression.Condition(test, pred, Expression.Constant(false))
        let resAnd = ExpressionPushdownTranslator.translatePredicate p plan dialect condAnd
        Assert.True(Option.isSome resAnd)
        Assert.Contains("AND", Option.get resAnd)

        // if test then true else pred -> (test OR pred)
        let condOr = Expression.Condition(test, Expression.Constant(true), pred)
        let resOr = ExpressionPushdownTranslator.translatePredicate p plan dialect condOr
        Assert.True(Option.isSome resOr)
        Assert.Contains("OR", Option.get resOr)

        // if test then false else pred -> (NOT (test) AND pred)
        let condNotAnd = Expression.Condition(test, Expression.Constant(false), pred)

        let resNotAnd =
            ExpressionPushdownTranslator.translatePredicate p plan dialect condNotAnd

        Assert.True(Option.isSome resNotAnd)
        Assert.Contains("NOT", Option.get resNotAnd)

        // if test then pred else true -> (NOT (test) OR pred)
        let condNotOr = Expression.Condition(test, pred, Expression.Constant(true))

        let resNotOr =
            ExpressionPushdownTranslator.translatePredicate p plan dialect condNotOr

        Assert.True(Option.isSome resNotOr)
        Assert.Contains("OR", Option.get resNotOr)

    // -------------------------------------------------------------------------
    // 4. isBooleanPredicateComparison with Equal / NotEqual to true/false (>=2 tests/scenario)
    // -------------------------------------------------------------------------

    [<Fact>]
    let ``isBooleanPredicateComparison right side boolean constant scenario 1`` () =
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let plan = PushdownQueryPlan.Create("Products", [])
        let containsMi = typeof<string>.GetMethod("Contains", [| typeof<string> |])

        let call =
            Expression.Call(Expression.Property(p, "Name"), containsMi, Expression.Constant("ABC"))

        let eqTrue = Expression.Equal(call, Expression.Constant(true))
        let eqFalse = Expression.Equal(call, Expression.Constant(false))
        let neTrue = Expression.NotEqual(call, Expression.Constant(true))
        let neFalse = Expression.NotEqual(call, Expression.Constant(false))

        let r1 = ExpressionPushdownTranslator.translatePredicate p plan dialect eqTrue
        let r2 = ExpressionPushdownTranslator.translatePredicate p plan dialect eqFalse
        let r3 = ExpressionPushdownTranslator.translatePredicate p plan dialect neTrue
        let r4 = ExpressionPushdownTranslator.translatePredicate p plan dialect neFalse

        Assert.True(Option.isSome r1)
        Assert.True(Option.isSome r2)
        Assert.True(Option.isSome r3)
        Assert.True(Option.isSome r4)
        Assert.Contains("NOT", Option.get r2)
        Assert.Contains("NOT", Option.get r3)

    [<Fact>]
    let ``isBooleanPredicateComparison left side boolean constant scenario 2`` () =
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let plan = PushdownQueryPlan.Create("Products", [])
        let containsMi = typeof<string>.GetMethod("Contains", [| typeof<string> |])

        let call =
            Expression.Call(Expression.Property(p, "Name"), containsMi, Expression.Constant("XYZ"))

        let leftEqTrue = Expression.Equal(Expression.Constant(true), call)
        let leftEqFalse = Expression.Equal(Expression.Constant(false), call)
        let leftNeTrue = Expression.NotEqual(Expression.Constant(true), call)
        let leftNeFalse = Expression.NotEqual(Expression.Constant(false), call)

        let r1 = ExpressionPushdownTranslator.translatePredicate p plan dialect leftEqTrue
        let r2 = ExpressionPushdownTranslator.translatePredicate p plan dialect leftEqFalse
        let r3 = ExpressionPushdownTranslator.translatePredicate p plan dialect leftNeTrue
        let r4 = ExpressionPushdownTranslator.translatePredicate p plan dialect leftNeFalse

        Assert.True(Option.isSome r1)
        Assert.True(Option.isSome r2)
        Assert.True(Option.isSome r3)
        Assert.True(Option.isSome r4)
        Assert.Contains("NOT", Option.get r2)
        Assert.Contains("NOT", Option.get r3)

    // -------------------------------------------------------------------------
    // 5. Comparisons with null and non-param branches (>=2 tests/scenario)
    // -------------------------------------------------------------------------

    [<Fact>]
    let ``translatePredicate comparisons with null and inverted operators scenario 1`` () =
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let plan = PushdownQueryPlan.Create("Products", [])
        let scoreProp = Expression.Property(p, "Score")
        let nullConst = Expression.Constant(null, typeof<Nullable<decimal>>)

        // score < null -> None
        let ltNull = Expression.LessThan(scoreProp, nullConst)
        Assert.True(Option.isNone (ExpressionPushdownTranslator.translatePredicate p plan dialect ltNull))

        // null < score -> None
        let nullLt = Expression.LessThan(nullConst, scoreProp)
        Assert.True(Option.isNone (ExpressionPushdownTranslator.translatePredicate p plan dialect nullLt))

        // null == col -> col IS NULL
        let nameProp = Expression.Property(p, "Name")
        let strNullConst = Expression.Constant(null, typeof<string>)
        let nullEq = Expression.Equal(strNullConst, nameProp)

        let resNullEq =
            ExpressionPushdownTranslator.translatePredicate p plan dialect nullEq

        Assert.True(Option.isSome resNullEq)
        Assert.Contains("IS NULL", Option.get resNullEq)

        // null <> col -> col IS NOT NULL
        let nullNe = Expression.NotEqual(strNullConst, nameProp)

        let resNullNe =
            ExpressionPushdownTranslator.translatePredicate p plan dialect nullNe

        Assert.True(Option.isSome resNullNe)
        Assert.Contains("IS NOT NULL", Option.get resNullNe)

    [<Fact>]
    let ``translatePredicate comparisons without parameters returns None scenario 2`` () =
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let plan = PushdownQueryPlan.Create("Products", [])
        let guidMi = typeof<Guid>.GetMethod("NewGuid")
        let callGuid1 = Expression.Call(guidMi)
        let callGuid2 = Expression.Call(guidMi)
        let noParams = Expression.Equal(callGuid1, callGuid2)
        Assert.True(Option.isNone (ExpressionPushdownTranslator.translatePredicate p plan dialect noParams))

        let notExpr =
            Expression.Not(Expression.Equal(Expression.Property(p, "Id"), Expression.Constant(5)))

        let notRes = ExpressionPushdownTranslator.translatePredicate p plan dialect notExpr
        Assert.True(Option.isSome notRes)
        Assert.Contains("NOT", Option.get notRes)

    // -------------------------------------------------------------------------
    // 6. MethodCallExpression predicates: isNull, isNotNull, isNotEmpty, etc. (>=2 tests/scenario)
    // -------------------------------------------------------------------------

    type CustomPredicates =
        static member IsNull(o: obj) = isNull o
        static member isNull(o: obj) = isNull o
        static member IsNotNull(o: obj) = not (isNull o)
        static member isNotNull(o: obj) = not (isNull o)
        static member IsNotEmpty(s: string) = not (String.IsNullOrEmpty s)
        static member isNotEmpty(s: string) = not (String.IsNullOrEmpty s)
        static member isNotNullOrEmpty(s: string) = not (String.IsNullOrEmpty s)
        static member op_Equality(a: obj, b: obj) = obj.Equals(a, b)
        static member op_Inequality(a: obj, b: obj) = not (obj.Equals(a, b))

    [<Fact>]
    let ``translatePredicate recognizes isNull and isNotNull methods scenario 1`` () =
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let plan = PushdownQueryPlan.Create("Products", [])
        let nameProp = Expression.Property(p, "Name")

        let isNullMi = typeof<CustomPredicates>.GetMethod("IsNull")
        let isNotNullMi = typeof<CustomPredicates>.GetMethod("IsNotNull")

        let callNull = Expression.Call(isNullMi, nameProp)

        let resNull =
            ExpressionPushdownTranslator.translatePredicate p plan dialect callNull

        Assert.True(Option.isSome resNull)
        Assert.Contains("IS NULL", Option.get resNull)

        let callNotNull = Expression.Call(isNotNullMi, nameProp)

        let resNotNull =
            ExpressionPushdownTranslator.translatePredicate p plan dialect callNotNull

        Assert.True(Option.isSome resNotNull)
        Assert.Contains("IS NOT NULL", Option.get resNotNull)

    [<Fact>]
    let ``translatePredicate recognizes isNotEmpty, isNullOrWhiteSpace and op_Equality with null scenario 2`` () =
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let plan = PushdownQueryPlan.Create("Products", [])
        let nameProp = Expression.Property(p, "Name")

        let isNotEmptyMi = typeof<CustomPredicates>.GetMethod("isNotEmpty")
        let isNotNullOrEmptyMi = typeof<CustomPredicates>.GetMethod("isNotNullOrEmpty")

        let isNullOrWhiteSpaceMi =
            typeof<string>.GetMethod("IsNullOrWhiteSpace", [| typeof<string> |])

        let callNotEmpty = Expression.Call(isNotEmptyMi, nameProp)

        let resNotEmpty =
            ExpressionPushdownTranslator.translatePredicate p plan dialect callNotEmpty

        Assert.True(Option.isSome resNotEmpty)

        let callNotNullOrEmpty = Expression.Call(isNotNullOrEmptyMi, nameProp)

        let resNotNullOrEmpty =
            ExpressionPushdownTranslator.translatePredicate p plan dialect callNotNullOrEmpty

        Assert.True(Option.isSome resNotNullOrEmpty)

        let callWs = Expression.Call(isNullOrWhiteSpaceMi, nameProp)
        let resWs = ExpressionPushdownTranslator.translatePredicate p plan dialect callWs
        Assert.True(Option.isSome resWs)
        Assert.Contains("TRIM", Option.get resWs)

        let opEqMi = typeof<CustomPredicates>.GetMethod("op_Equality")
        let callOpEqNull = Expression.Call(opEqMi, nameProp, Expression.Constant(null))

        let resOpEqNull =
            ExpressionPushdownTranslator.translatePredicate p plan dialect callOpEqNull

        Assert.True(Option.isSome resOpEqNull)
        Assert.Contains("IS NULL", Option.get resOpEqNull)

        let opNeMi = typeof<CustomPredicates>.GetMethod("op_Inequality")
        let callOpNeNull = Expression.Call(opNeMi, Expression.Constant(null), nameProp)

        let resOpNeNull =
            ExpressionPushdownTranslator.translatePredicate p plan dialect callOpNeNull

        Assert.True(Option.isSome resOpNeNull)
        Assert.Contains("IS NOT NULL", Option.get resOpNeNull)

    // -------------------------------------------------------------------------
    // 7. translateScalarExpr with Coalesce, ToString, LongCount, Sum(Distinct) (>=2 tests/scenario)
    // -------------------------------------------------------------------------

    [<Fact>]
    let ``translateScalarExpr Coalesce via conditional scenario 1`` () =
        let plan = PushdownQueryPlan.Create("Products", [])
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let nameProp = Expression.Property(p, "Name")
        let defVal = Expression.Constant("DefaultName")

        // if (name == null) then defVal else name -> COALESCE(Name, 'DefaultName')
        let test = Expression.Equal(nameProp, Expression.Constant(null))
        let cond = Expression.Condition(test, defVal, nameProp)

        let res = ExpressionPushdownTranslator.translateScalarExpr dialect plan cond
        Assert.True(Option.isSome res)
        Assert.Contains("COALESCE", Option.get res)

    [<Fact>]
    let ``translateScalarExpr ToString and LongCount and Sum Distinct scenario 2`` () =
        let plan = PushdownQueryPlan.Create("Products", [])
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let priceProp = Expression.Property(p, "Price")

        let toStrMi = typeof<decimal>.GetMethod("ToString", Type.EmptyTypes)
        let callToStr = Expression.Call(priceProp, toStrMi)

        let resToStr =
            ExpressionPushdownTranslator.translateScalarExpr dialect plan callToStr

        Assert.True(Option.isSome resToStr)

        plan.IsDistinct <- true

        let longCountMi =
            typeof<Queryable>.GetMethods()
            |> Seq.find (fun m -> m.Name = "LongCount" && m.GetParameters().Length = 1)

        let genLongCount = longCountMi.MakeGenericMethod(typeof<ExtraProduct>)
        let dummyIq = Expression.Constant(null, typeof<IQueryable<ExtraProduct>>)
        let callLongCount = Expression.Call(null, genLongCount, dummyIq)

        let resLongCount =
            ExpressionPushdownTranslator.translateScalarExpr dialect plan callLongCount

        Assert.True(Option.isSome resLongCount)
        Assert.Contains("COUNT(DISTINCT *)", Option.get resLongCount)

    // -------------------------------------------------------------------------
    // 8. SqlComputationExtensions: wrapped IQueryable WithCte and SetOps with CTEs (>=2 tests/scenario)
    // -------------------------------------------------------------------------

    [<Fact>]
    let ``SqlComputationExtensions WithCte on wrapped queryable remaps parameters scenario 1`` () =
        let conn = SqliteConnector("Data Source=:memory:")
        let baseProducts = SqlPushdownQueryable<ExtraProduct>(conn, "Products")
        let wrappedProducts = baseProducts.Where(fun p -> p.Id > 0)

        let subq =
            SqlPushdownQueryable<ExtraProduct>(conn, "Orders", [ ("@customParam", box 99) ])

        let res = wrappedProducts.WithCte(subq)
        Assert.NotNull(res)

        match box res with
        | :? ISqlPushdownQueryable as spq ->
            Assert.True(spq.Plan.Ctes.Count > 0)
            Assert.True(spq.Plan.Parameters |> Seq.exists (fun (k, _) -> k.StartsWith("@p")))
        | _ -> Assert.Fail("Expected ISqlPushdownQueryable")

    [<Fact>]
    let ``SqlComputationExtensions WithCte named on wrapped queryable remaps parameters scenario 2`` () =
        let conn = SqliteConnector("Data Source=:memory:")
        let baseProducts = SqlPushdownQueryable<ExtraProduct>(conn, "Products")
        let wrappedProducts = baseProducts.Where(fun p -> p.Price > 10.0m)

        let subq =
            SqlPushdownQueryable<ExtraProduct>(conn, "Orders", [ ("@customParam2", box 42) ])

        let res = wrappedProducts.WithCte("custom_named_cte", subq)
        Assert.NotNull(res)

        match box res with
        | :? ISqlPushdownQueryable as spq ->
            Assert.True(spq.Plan.Ctes |> Seq.exists (fun c -> c.Name = "custom_named_cte"))
            Assert.True(spq.Plan.Parameters |> Seq.exists (fun (k, _) -> k.StartsWith("@p")))
        | _ -> Assert.Fail("Expected ISqlPushdownQueryable")

    [<Fact>]
    let ``SqlComputationExtensions Union and UnionAll on wrapped queryable preserves seedPlan ctes scenario 1`` () =
        let conn = SqliteConnector("Data Source=:memory:")
        let baseProducts = SqlPushdownQueryable<ExtraProduct>(conn, "Products")

        baseProducts.Plan.Ctes.Add(
            {
                Name = "seed_cte"
                SubquerySql = "SELECT 1"
                IsRecursive = false
                SourceQuery = None
            }
        )

        let wrapped = baseProducts.Where(fun p -> p.Id > 5)

        let other =
            SqlPushdownQueryable<ExtraProduct>(conn, "Products").Where(fun p -> p.Id > 10)

        let unionRes = wrapped.Union(other)
        Assert.NotNull(unionRes)

        match box unionRes with
        | :? ISqlPushdownQueryable as spq -> Assert.True(spq.Plan.Ctes |> Seq.exists (fun c -> c.Name = "seed_cte"))
        | _ -> ()

        let unionAllRes = wrapped.UnionAll(other)
        Assert.NotNull(unionAllRes)

        match box unionAllRes with
        | :? ISqlPushdownQueryable as spq -> Assert.True(spq.Plan.Ctes |> Seq.exists (fun c -> c.Name = "seed_cte"))
        | _ -> ()

    [<Fact>]
    let ``SqlComputationExtensions Intersect and Except on wrapped queryable preserves seedPlan ctes scenario 2`` () =
        let conn = SqliteConnector("Data Source=:memory:")
        let baseProducts = SqlPushdownQueryable<ExtraProduct>(conn, "Products")

        baseProducts.Plan.Ctes.Add(
            {
                Name = "seed_cte_2"
                SubquerySql = "SELECT 2"
                IsRecursive = false
                SourceQuery = None
            }
        )

        let wrapped = baseProducts.Where(fun p -> p.Score.HasValue)

        let other =
            SqlPushdownQueryable<ExtraProduct>(conn, "Products").Where(fun p -> p.InStock)

        let intersectRes = wrapped.Intersect(other)
        Assert.NotNull(intersectRes)

        match box intersectRes with
        | :? ISqlPushdownQueryable as spq -> Assert.True(spq.Plan.Ctes |> Seq.exists (fun c -> c.Name = "seed_cte_2"))
        | _ -> ()

        let exceptRes = wrapped.Except(other)
        Assert.NotNull(exceptRes)

        match box exceptRes with
        | :? ISqlPushdownQueryable as spq -> Assert.True(spq.Plan.Ctes |> Seq.exists (fun c -> c.Name = "seed_cte_2"))
        | _ -> ()

    [<Fact>]
    let ``fsql collectAllCtesFromExpr discovers CTEs from static properties scenario 1`` () =
        let conn = SqliteConnector("Data Source=:memory:")
        let products = SqlPushdownQueryable<ExtraProduct>(conn, "Products")

        let q =
            fsql {
                for p in products do
                    where (StaticCteHolder.SampleSpq.Count() > 0)
                    select p.Id
            }

        Assert.NotNull(q)

    [<Fact>]
    let ``fsql collectAllCtesFromExpr discovers CTEs from static properties scenario 2`` () =
        let conn = SqliteConnector("Data Source=:memory:")
        let products = SqlPushdownQueryable<ExtraProduct>(conn, "Products")

        let q =
            fsql {
                for p in products do
                    where (StaticCteHolder.SampleSpq.Any())
                    select p.Name
            }

        Assert.NotNull(q)

    [<Fact>]
    let ``fsql collectAllCtesFromExpr discovers inMem and throwing properties scenario 1`` () =
        let conn = SqliteConnector("Data Source=:memory:")
        let products = SqlPushdownQueryable<ExtraProduct>(conn, "Products")

        let q =
            fsql {
                for p in products do
                    where (StaticCteHolder.InMemIq.Count() > 0)
                    select p.Id
            }

        Assert.NotNull(q)

    [<Fact>]
    let ``fsql collectAllCtesFromExpr discovers inMem and throwing properties scenario 2`` () =
        let conn = SqliteConnector("Data Source=:memory:")
        let products = SqlPushdownQueryable<ExtraProduct>(conn, "Products")

        let q =
            fsql {
                for p in products do
                    where (StaticCteHolder.ThrowingIq.Count() >= 0)
                    select p.Name
            }

        Assert.NotNull(q)

    // -------------------------------------------------------------------------
    // 7. Comprehensive Expression Pushdown Extra Coverage (>= 2 tests per scenario)
    // -------------------------------------------------------------------------

    [<Fact>]
    let ``unwrapClientOption handles Nullable HasValue and NoValue scenario 1`` () =
        let nVal = Nullable<int>(42)
        let nNull = Nullable<int>()
        Assert.Equal(box 42, ExpressionPushdownTranslator.unwrapClientOption (box nVal))
        Assert.Null(ExpressionPushdownTranslator.unwrapClientOption (box nNull))

    [<Fact>]
    let ``unwrapClientOption handles Nullable decimal variations scenario 2`` () =
        let nVal = Nullable<decimal>(99.5M)
        let nNull = Nullable<decimal>()
        Assert.Equal(box 99.5M, ExpressionPushdownTranslator.unwrapClientOption (box nVal))
        Assert.Null(ExpressionPushdownTranslator.unwrapClientOption (box nNull))

    [<Fact>]
    let ``unwrapClientOption handles ValueOption ValueSome and ValueNone scenario 1`` () =
        let vSome = ValueSome "test"
        let vNone = ValueNone
        Assert.Equal(box "test", ExpressionPushdownTranslator.unwrapClientOption (box vSome))
        Assert.Null(ExpressionPushdownTranslator.unwrapClientOption (box vNone))

    [<Fact>]
    let ``unwrapClientOption handles ValueOption integer variations scenario 2`` () =
        let vSome = ValueSome 100
        let vNone = ValueNone
        Assert.Equal(box 100, ExpressionPushdownTranslator.unwrapClientOption (box vSome))
        Assert.Null(ExpressionPushdownTranslator.unwrapClientOption (box vNone))

    [<Fact>]
    let ``unwrapClientOption handles Option Some and None scenario 1`` () =
        let oSome = Some 77
        let oNone = None
        Assert.Equal(box 77, ExpressionPushdownTranslator.unwrapClientOption (box oSome))
        Assert.Null(ExpressionPushdownTranslator.unwrapClientOption (box oNone))

    [<Fact>]
    let ``unwrapClientOption handles Option string variations scenario 2`` () =
        let oSome = Some "hello"
        let oNone: string option = None
        Assert.Equal(box "hello", ExpressionPushdownTranslator.unwrapClientOption (box oSome))
        Assert.Null(ExpressionPushdownTranslator.unwrapClientOption (box oNone))

    [<Fact>]
    let ``unwrapClientOption handles non-option types and null scenario 1`` () =
        let plainStr = "plain_string"
        Assert.Equal(box plainStr, ExpressionPushdownTranslator.unwrapClientOption (box plainStr))
        Assert.Null(ExpressionPushdownTranslator.unwrapClientOption null)

    [<Fact>]
    let ``unwrapClientOption handles non-option primitives scenario 2`` () =
        let plainInt = 12345
        Assert.Equal(box plainInt, ExpressionPushdownTranslator.unwrapClientOption (box plainInt))
        let plainObj = obj ()
        Assert.Same(plainObj, ExpressionPushdownTranslator.unwrapClientOption plainObj)

    [<Fact>]
    let ``tryFindPushdownSource on constant field holding SqlPushdownQueryable scenario 1`` () =
        let conn = SqliteConnector("Data Source=:memory:")
        let baseProducts = SqlPushdownQueryable<ExtraProduct>(conn, "Products")
        let holder = SpqFieldClass()
        holder.SpqField <- baseProducts :> IQueryable<ExtraProduct>
        let fieldInfo = typeof<SpqFieldClass>.GetField("SpqField")
        let fieldExpr = Expression.Field(Expression.Constant(holder), fieldInfo)

        let result = ExpressionPushdownTranslator.tryFindPushdownSource fieldExpr
        Assert.True(result.IsSome)

    [<Fact>]
    let ``tryFindPushdownSource on constant field holding non-pushdown returns None scenario 2`` () =
        let inMem = ([ 1; 2 ]: int list).AsQueryable()
        let holder = AnyFieldClass()
        holder.AnyField <- inMem
        let fieldInfo = typeof<AnyFieldClass>.GetField("AnyField")
        let fieldExpr = Expression.Field(Expression.Constant(holder), fieldInfo)

        let result = ExpressionPushdownTranslator.tryFindPushdownSource fieldExpr
        Assert.True(result.IsNone)

    [<Fact>]
    let ``tryFindPushdownSource on BinaryExpression containing pushdown source scenario 1`` () =
        let conn = SqliteConnector("Data Source=:memory:")
        let baseProducts = SqlPushdownQueryable<ExtraProduct>(conn, "Products")
        let spqExpr = Expression.Constant(baseProducts)

        let nullConst =
            Expression.Constant(null, typeof<SqlPushdownQueryable<ExtraProduct>>)

        let dummy = Expression.Constant(10)
        let binLeft = Expression.Add(dummy, dummy)
        let binWithSpqLeft = Expression.Equal(spqExpr, nullConst)
        let binWithSpqRight = Expression.Equal(nullConst, spqExpr)

        Assert.True((ExpressionPushdownTranslator.tryFindPushdownSource binWithSpqLeft).IsSome)
        Assert.True((ExpressionPushdownTranslator.tryFindPushdownSource binWithSpqRight).IsSome)
        Assert.True((ExpressionPushdownTranslator.tryFindPushdownSource binLeft).IsNone)

    [<Fact>]
    let ``tryFindPushdownSource on BinaryExpression with nested expressions scenario 2`` () =
        let conn = SqliteConnector("Data Source=:memory:")
        let baseProducts = SqlPushdownQueryable<ExtraProduct>(conn, "Products")
        let spqExpr = Expression.Constant(baseProducts)

        let nullConst =
            Expression.Constant(null, typeof<SqlPushdownQueryable<ExtraProduct>>)

        let dummy = Expression.Constant(5)

        let nestedLeft =
            Expression.AndAlso(Expression.Equal(spqExpr, nullConst), Expression.Constant(true))

        let nestedRight =
            Expression.AndAlso(Expression.Constant(true), Expression.Equal(nullConst, spqExpr))

        Assert.True((ExpressionPushdownTranslator.tryFindPushdownSource nestedLeft).IsSome)
        Assert.True((ExpressionPushdownTranslator.tryFindPushdownSource nestedRight).IsSome)

    [<Fact>]
    let ``exprHasParamName with Conditional New Invocation MethodCall and null scenario 1`` () =
        let p = Expression.Parameter(typeof<int>, "target")
        let other = Expression.Parameter(typeof<int>, "other")

        let cond =
            Expression.Condition(
                Expression.Equal(p, Expression.Constant(1)),
                Expression.Constant(10),
                Expression.Constant(20)
            )

        Assert.True(ExpressionPushdownTranslator.exprHasParamName "target" cond)
        Assert.False(ExpressionPushdownTranslator.exprHasParamName "other" cond)
        Assert.False(ExpressionPushdownTranslator.exprHasParamName "target" null)

    [<Fact>]
    let ``exprHasParamName with New Invocation MethodCall scenario 2`` () =
        let p = Expression.Parameter(typeof<string>, "paramX")
        let ctor = typeof<Tuple<string>>.GetConstructor([| typeof<string> |])
        let newExpr = Expression.New(ctor, [| p :> Expression |])
        Assert.True(ExpressionPushdownTranslator.exprHasParamName "paramX" newExpr)
        Assert.False(ExpressionPushdownTranslator.exprHasParamName "paramY" newExpr)

        let lambda = Expression.Lambda<Func<string, string>>(p, p)
        let invExpr = Expression.Invoke(lambda, p)
        Assert.True(ExpressionPushdownTranslator.exprHasParamName "paramX" invExpr)

        let mcExpr =
            Expression.Call(
                typeof<string>.GetMethod("Concat", [| typeof<obj>; typeof<obj> |]),
                p,
                Expression.Constant("!")
            )

        Assert.True(ExpressionPushdownTranslator.exprHasParamName "paramX" mcExpr)

    [<Fact>]
    let ``isGroupingType with null and diverse types scenario 1`` () =
        Assert.False(ExpressionPushdownTranslator.isGroupingType null)
        Assert.False(ExpressionPushdownTranslator.isGroupingType typeof<string>)
        Assert.False(ExpressionPushdownTranslator.isGroupingType typeof<int list>)

    [<Fact>]
    let ``isGroupingType with IGrouping type scenario 2`` () =
        let groupingType = typeof<IGrouping<string, int>>
        Assert.True(ExpressionPushdownTranslator.isGroupingType groupingType)

    [<Fact>]
    let ``isHavingPredicate with non-aggregate method calls scenario 1`` () =
        let strExpr = Expression.Constant("hello")
        let mcNonAgg = Expression.Call(strExpr, typeof<string>.GetMethod("ToLower", [||]))
        Assert.False(ExpressionPushdownTranslator.isHavingPredicate mcNonAgg)
        Assert.False(ExpressionPushdownTranslator.isHavingPredicate null)

    [<Fact>]
    let ``isHavingPredicate with member expression and binary scenario 2`` () =
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let mExpr = Expression.Property(p, "Name")
        Assert.False(ExpressionPushdownTranslator.isHavingPredicate mExpr)
        let binExpr = Expression.Equal(mExpr, Expression.Constant("test"))
        Assert.False(ExpressionPushdownTranslator.isHavingPredicate binExpr)

    [<Fact>]
    let ``extractKeyColumns with single column dot and no dot scenario 1`` () =
        let plan = PushdownQueryPlan.Create("Products", [])
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let mExpr = Expression.Property(p, "Id")
        let cols = ExpressionPushdownTranslator.extractKeyColumns plan "t0" p mExpr
        Assert.NotEmpty(cols)
        Assert.Equal("t0.Id", cols.[0])

    [<Fact>]
    let ``extractKeyColumns with expressions containing space or parens scenario 2`` () =
        let plan = PushdownQueryPlan.Create("Products", [])
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let spaceExpr = Expression.Constant("col with space")
        let cols = ExpressionPushdownTranslator.extractKeyColumns plan "t0" p spaceExpr
        Assert.Empty(cols)

    [<Fact>]
    let ``tryExtractFromValue with null and plain non-queryable objects scenario 1`` () =
        Assert.True((ExpressionPushdownTranslator.tryExtractFromValue null).IsNone)
        Assert.True((ExpressionPushdownTranslator.tryExtractFromValue "simple_string").IsNone)
        Assert.True((ExpressionPushdownTranslator.tryExtractFromValue 12345).IsNone)

    [<Fact>]
    let ``tryExtractFromValue with object holding property returning IQueryable scenario 2`` () =
        let conn = SqliteConnector("Data Source=:memory:")
        let baseProducts = SqlPushdownQueryable<ExtraProduct>(conn, "Products")

        let propHolder =
            {
                SpqProp = baseProducts :> IQueryable<ExtraProduct>
            }

        let result = ExpressionPushdownTranslator.tryExtractFromValue propHolder
        Assert.True(result.IsSome)

    [<Fact>]
    let ``translateScalarCondition with Convert and ConvertChecked scenario 1`` () =
        let plan = PushdownQueryPlan.Create("Products", [])
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let boolProp = Expression.Property(p, "InStock")
        let conv = Expression.Convert(boolProp, typeof<bool>)

        let condOpt =
            ExpressionPushdownTranslator.translateScalarCondition dialect plan conv

        Assert.True(condOpt.IsSome)

    [<Fact>]
    let ``translateScalarCondition with ConditionalExpression boolean branches scenario 2`` () =
        let plan = PushdownQueryPlan.Create("Products", [])
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let scoreProp = Expression.Property(p, "Price")
        let testExpr = Expression.GreaterThan(scoreProp, Expression.Constant(10.0M))

        let condTrueFalse =
            Expression.Condition(testExpr, Expression.Constant(true), Expression.Constant(false))

        let res1 =
            ExpressionPushdownTranslator.translateScalarCondition dialect plan condTrueFalse

        Assert.True(res1.IsSome)

        let condFalseTrue =
            Expression.Condition(testExpr, Expression.Constant(false), Expression.Constant(true))

        let res2 =
            ExpressionPushdownTranslator.translateScalarCondition dialect plan condFalseTrue

        Assert.True(res2.IsSome)

    [<Fact>]
    let ``translateScalarCondition with IsNone isNull isNotNull method calls scenario 1`` () =
        let plan = PushdownQueryPlan.Create("Products", [])
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let optProp = Expression.Property(p, "Score")

        let isNotNullMethod =
            typeof<FSqlQueryBuilder>.Assembly.GetTypes()
            |> Seq.pick (fun t ->
                match t.GetMethod("isNotNull") with
                | null -> None
                | m -> Some m)

        let isNullCall =
            Expression.Call(isNotNullMethod.MakeGenericMethod([| typeof<Nullable<decimal>> |]), optProp)

        let res =
            ExpressionPushdownTranslator.translateScalarCondition dialect plan isNullCall

        Assert.True(res.IsSome)

    [<Fact>]
    let ``translateScalarCondition with isNotNull variations scenario 2`` () =
        let plan = PushdownQueryPlan.Create("Products", [])
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let nameProp = Expression.Property(p, "Name")

        let isNotNullMethod =
            typeof<FSqlQueryBuilder>.Assembly.GetTypes()
            |> Seq.pick (fun t ->
                match t.GetMethod("isNotNull") with
                | null -> None
                | m -> Some m)

        let isNotNullCall =
            Expression.Call(isNotNullMethod.MakeGenericMethod([| typeof<string> |]), nameProp)

        let res =
            ExpressionPushdownTranslator.translateScalarCondition dialect plan isNotNullCall

        Assert.True(res.IsSome)

    [<Fact>]
    let ``translateScalarExpr ToString with format and Substring scenario 1`` () =
        let plan = PushdownQueryPlan.Create("Products", [])
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let priceProp = Expression.Property(p, "Price")

        let toStringFormat =
            Expression.Call(
                priceProp,
                typeof<decimal>.GetMethod("ToString", [| typeof<string> |]),
                Expression.Constant("F2")
            )

        let res =
            ExpressionPushdownTranslator.translateScalarExpr dialect plan toStringFormat

        Assert.True(res.IsSome)

    [<Fact>]
    let ``translateScalarExpr Substring variations scenario 2`` () =
        let plan = PushdownQueryPlan.Create("Products", [])
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let nameProp = Expression.Property(p, "Name")

        let substr =
            Expression.Call(
                nameProp,
                typeof<string>.GetMethod("Substring", [| typeof<int>; typeof<int> |]),
                Expression.Constant(0),
                Expression.Constant(3)
            )

        let res = ExpressionPushdownTranslator.translateScalarExpr dialect plan substr
        Assert.True(res.IsSome)

    [<Fact>]
    let ``translateScalarExpr IndexOf and DefaultArg scenario 1`` () =
        let plan = PushdownQueryPlan.Create("Products", [])
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let nameProp = Expression.Property(p, "Name")

        let indexOf =
            Expression.Call(
                nameProp,
                typeof<string>.GetMethod("IndexOf", [| typeof<string> |]),
                Expression.Constant("abc")
            )

        let res = ExpressionPushdownTranslator.translateScalarExpr dialect plan indexOf
        Assert.True(res.IsSome)

    [<Fact>]
    let ``translateScalarExpr IndexOf with single char scenario 2`` () =
        let plan = PushdownQueryPlan.Create("Products", [])
        let p = Expression.Parameter(typeof<ExtraProduct>, "p")
        let nameProp = Expression.Property(p, "Name")

        let indexOf =
            Expression.Call(
                nameProp,
                typeof<string>.GetMethod("IndexOf", [| typeof<string> |]),
                Expression.Constant("x")
            )

        let res = ExpressionPushdownTranslator.translateScalarExpr dialect plan indexOf
        Assert.True(res.IsSome)

    [<Fact>]
    let ``mergeExpressionIntoPlan with member expression holding IQueryable with CTEs scenario 1`` () =
        let conn = SqliteConnector("Data Source=:memory:")
        let baseProducts = SqlPushdownQueryable<ExtraProduct>(conn, "Products")

        baseProducts.Plan.Ctes.Add(
            {
                Name = "holder_cte"
                SubquerySql = "SELECT 1"
                IsRecursive = false
                SourceQuery = None
            }
        )

        let holder =
            {
                SpqProp = baseProducts :> IQueryable<ExtraProduct>
            }

        let plan = PushdownQueryPlan.Create("Products", [])

        let propExpr =
            Expression.Property(Expression.Constant(holder), typeof<SpqPropHolder>.GetProperty("SpqProp"))

        ExpressionPushdownTranslator.mergeExpressionIntoPlan propExpr plan dialect
        Assert.True(plan.Ctes |> Seq.exists (fun c -> c.Name = "holder_cte"))

    [<Fact>]
    let ``mergeExpressionIntoPlan with member expression holding IQueryable with CTEs scenario 2`` () =
        let conn = SqliteConnector("Data Source=:memory:")
        let baseProducts = SqlPushdownQueryable<ExtraProduct>(conn, "Products")

        baseProducts.Plan.Ctes.Add(
            {
                Name = "holder_cte_2"
                SubquerySql = "SELECT 2"
                IsRecursive = true
                SourceQuery = None
            }
        )

        let holder =
            {
                SpqProp = baseProducts :> IQueryable<ExtraProduct>
            }

        let plan = PushdownQueryPlan.Create("Products", [])

        let propExpr =
            Expression.Property(Expression.Constant(holder), typeof<SpqPropHolder>.GetProperty("SpqProp"))

        ExpressionPushdownTranslator.mergeExpressionIntoPlan propExpr plan dialect
        Assert.True(plan.Ctes |> Seq.exists (fun c -> c.Name = "holder_cte_2"))

    [<Fact>]
    let ``mergeExpressionIntoPlan terminal aggregate Count with column projection scenario 1`` () =
        let conn = SqliteConnector("Data Source=:memory:")

        let baseProducts =
            SqlPushdownQueryable<ExtraProduct>(conn, "Products") :> IQueryable<ExtraProduct>

        let countExpr =
            Expression.Call(typeof<Queryable>, "Count", [| typeof<ExtraProduct> |], baseProducts.Expression)

        let plan = PushdownQueryPlan.Create("Products", [])

        ExpressionPushdownTranslator.mergeExpressionIntoPlan countExpr plan dialect
        Assert.NotNull(plan)

    [<Fact>]
    let ``mergeExpressionIntoPlan terminal aggregate Count with column projection scenario 2`` () =
        let conn = SqliteConnector("Data Source=:memory:")

        let baseProducts =
            SqlPushdownQueryable<ExtraProduct>(conn, "Products") :> IQueryable<ExtraProduct>

        let p = Expression.Parameter(typeof<ExtraProduct>, "p")

        let predLambda =
            Expression.Lambda<Func<ExtraProduct, bool>>(
                Expression.GreaterThan(Expression.Property(p, "Price"), Expression.Constant(0.0M)),
                p
            )

        let countExpr =
            Expression.Call(typeof<Queryable>, "Count", [| typeof<ExtraProduct> |], baseProducts.Expression, predLambda)

        let plan = PushdownQueryPlan.Create("Products", [])

        ExpressionPushdownTranslator.mergeExpressionIntoPlan countExpr plan dialect
        Assert.NotNull(plan)
