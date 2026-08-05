namespace FSharpQueryStudio.Analysis

/// Completions tier 1 (MVP) — see Section 8's completion rollout. Not
/// position-sensitive: keywords, snippets, and connector helper names are
/// always candidates. Tier 2 (semantic, cursor-aware) lands separately.
module Completions =

    /// F# keywords — not exhaustive, just enough to be useful for MVP.
    let keywords: string list =
        [ "let"
          "rec"
          "mutable"
          "fun"
          "match"
          "with"
          "if"
          "then"
          "else"
          "for"
          "while"
          "do"
          "type"
          "module"
          "open"
          "try"
          "finally"
          "async"
          "task"
          "yield"
          "return" ]

    /// Common snippet templates surfaced as candidate names.
    let snippets: string list = [ "printfn \"%s\""; "match x with"; "for i in" ]

    /// Controlled script API surface names — see Section 11's
    /// `Connections.sql "Sales" "select ..." [...]` example. C.11 wires the
    /// real connector helpers; this is the static MVP set.
    let connectorHelperNames: string list =
        [ "Connections.sql"
          "Connections.sqlServer"
          "Connections.postgres"
          "Connections.csv"
          "Connections.excel"
          "Connections.rest" ]

    /// `recentNames` is whatever the most recent successful `analysis/check`
    /// declared — owned and cached by the caller, not this module.
    let complete (recentNames: string list) : string list =
        keywords @ snippets @ connectorHelperNames @ recentNames |> List.distinct
