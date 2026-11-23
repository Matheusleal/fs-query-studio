namespace FSharpQueryStudio.Protocol

/// Bounded, protocol-neutral result shape — see Section 11. `Results`
/// produces these from raw evaluation outcomes; it doesn't own the types.
/// Qualified access required: an unqualified `Error` case would shadow
/// F#'s `Result.Error` for any file that opens this namespace.
module private ResultKindState =
    let defaultExtractor (q: obj) : string =
        match q with
        | null -> ""
        | :? string as s -> s
        | other ->
            try
                let t = other.GetType()
                let m = t.GetMethod("ToRawSql", System.Type.EmptyTypes)

                if not (isNull m) then
                    m.Invoke(other, [||]) :?> string
                else
                    string other
            with _ ->
                string other

    let mutable rawSqlExtractor: (obj -> string) = defaultExtractor

    let mutable rawSqlWithParamsExtractor: (obj -> string * (string * obj) list) =
        (fun q -> (rawSqlExtractor q, []))

[<RequireQualifiedAccess>]
type ResultKind =
    | Scalar
    | Text
    | Table
    | Object
    | Error
    | NoValue

    static member RegisterRawSqlExtractor
        (extractor: obj -> string, ?withParamsExtractor: obj -> string * (string * obj) list)
        =
        ResultKindState.rawSqlExtractor <- extractor

        match withParamsExtractor with
        | Some wpe -> ResultKindState.rawSqlWithParamsExtractor <- wpe
        | None -> ()

    /// Extracts the raw SQL query generated for a queryable or SQL expression without executing it against the database.
    static member toRawSql(query: obj) : string = ResultKindState.rawSqlExtractor query

    /// Extracts the raw SQL query and parameter bindings for a queryable or SQL expression without executing it against the database.
    static member toRawSqlWithParameters(query: obj) : string * (string * obj) list =
        ResultKindState.rawSqlWithParamsExtractor query

type Column = { Name: string; DisplayType: string }

type OutputStream =
    | Stdout
    | Stderr

type OutputLine = { Stream: OutputStream; Text: string }

type QueryParameter =
    {
        Name: string
        Value: obj
        DisplayType: string
    }

    static member InferDisplayType(value: obj) : string =
        match value with
        | null -> "null"
        | :? string -> "string"
        | :? int -> "int"
        | :? int64 -> "int64"
        | :? double -> "float"
        | :? single -> "float32"
        | :? decimal -> "decimal"
        | :? bool -> "bool"
        | :? System.DateTime -> "datetime"
        | :? System.DateTimeOffset -> "datetimeoffset"
        | :? System.DateOnly -> "date"
        | :? System.TimeOnly -> "time"
        | :? System.Guid -> "guid"
        | :? (byte array) -> "byte[]"
        | other ->
            let t = other.GetType()

            if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>> then
                let innerT = t.GetGenericArguments().[0]
                sprintf "%s option" innerT.Name
            else
                t.Name

    static member Create(name: string, value: obj) =
        {
            Name = name
            Value = value
            DisplayType = QueryParameter.InferDisplayType value
        }

[<AllowNullLiteral>]
type private TrackedQuery(sql: string, parameters: (string * obj) list) =
    member _.Sql = sql
    member _.Parameters = parameters

type LastQueryTracker private () =
    static let currentQuery = new System.Threading.AsyncLocal<TrackedQuery>()

    static member Record(sql: string, parameters: (string * obj) list) =
        currentQuery.Value <- TrackedQuery(sql, parameters)

    static member Reset() = currentQuery.Value <- null

    static member Current: (string * (string * obj) list) option =
        match currentQuery.Value with
        | null -> None
        | t when not (System.String.IsNullOrWhiteSpace t.Sql) -> Some(t.Sql, t.Parameters)
        | _ -> None

type ResultEnvelope =
    {
        Kind: ResultKind
        DisplayText: string option
        Schema: Column list
        Rows: obj array array
        IsTruncated: bool
        ContinuationToken: string option
        ElapsedMs: int64
        Output: OutputLine list
        RawQuery: string option
        Parameters: QueryParameter list
    }
