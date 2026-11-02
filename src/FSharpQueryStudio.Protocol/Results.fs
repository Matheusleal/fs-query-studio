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

    static member RegisterRawSqlExtractor(extractor: obj -> string, ?withParamsExtractor: obj -> string * (string * obj) list) =
        ResultKindState.rawSqlExtractor <- extractor
        match withParamsExtractor with
        | Some wpe -> ResultKindState.rawSqlWithParamsExtractor <- wpe
        | None -> ()

    /// Extracts the raw SQL query generated for a queryable or SQL expression without executing it against the database.
    static member toRawSql(query: obj) : string =
        ResultKindState.rawSqlExtractor query

    /// Extracts the raw SQL query and parameter bindings for a queryable or SQL expression without executing it against the database.
    static member toRawSqlWithParameters(query: obj) : string * (string * obj) list =
        ResultKindState.rawSqlWithParamsExtractor query

type Column = { Name: string; DisplayType: string }

type OutputStream =
    | Stdout
    | Stderr

type OutputLine = { Stream: OutputStream; Text: string }

type ResultEnvelope =
    { Kind: ResultKind
      DisplayText: string option
      Schema: Column list
      Rows: obj array array
      IsTruncated: bool
      ContinuationToken: string option
      ElapsedMs: int64
      Output: OutputLine list }
