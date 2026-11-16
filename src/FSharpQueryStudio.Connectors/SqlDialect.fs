namespace FSharpQueryStudio.Connectors

open System
open System.Collections.Generic
open FSharpQueryStudio.Protocol

/// Identifies the target SQL dialect for pushdown syntax generation (e.g. LIMIT vs TOP/OFFSET).
[<RequireQualifiedAccess>]
type SqlDialect =
    | Sqlite
    | PostgreSql
    | SqlServer
    | MySql
    | MariaDb
    | Oracle

module SqlDialect =
    let fromConnectorKind (kind: ConnectorKind) =
        if kind = ConnectorKind("PostgreSql") then SqlDialect.PostgreSql
        elif kind = ConnectorKind("SqlServer") then SqlDialect.SqlServer
        elif kind = ConnectorKind("MySql") then SqlDialect.MySql
        elif kind = ConnectorKind("MariaDb") then SqlDialect.MariaDb
        elif kind = ConnectorKind("Oracle") then SqlDialect.Oracle
        else SqlDialect.Sqlite

    let private reservedKeywords =
        HashSet<string>(
            [|
                "ALL"; "ALTER"; "AND"; "AS"; "ASC"; "BETWEEN"; "BY"; "CASE"; "CAST"; "CHECK"; "COLUMN";
                "CONSTRAINT"; "CREATE"; "CROSS"; "CURRENT"; "DATABASE"; "DEFAULT"; "DELETE"; "DESC";
                "DISTINCT"; "DROP"; "ELSE"; "END"; "ESCAPE"; "EXCEPT"; "EXISTS"; "FOREIGN"; "FROM";
                "FULL"; "GROUP"; "HAVING"; "IN"; "INDEX"; "INNER"; "INSERT"; "INTERSECT"; "INTO";
                "IS"; "JOIN"; "KEY"; "LEFT"; "LIKE"; "LIMIT"; "NOT"; "NULL"; "ON"; "OR"; "ORDER";
                "OUTER"; "PRIMARY"; "REFERENCES"; "RIGHT"; "SELECT"; "SET"; "TABLE"; "THEN"; "TO";
                "TOP"; "TRANSACTION"; "UNION"; "UNIQUE"; "UPDATE"; "VALUES"; "VIEW"; "WHEN"; "WHERE";
                "WITH"
            |],
            StringComparer.OrdinalIgnoreCase
        )

    /// Safely quotes an identifier according to the dialect rules if it contains special characters, spaces, or keywords.
    let quoteIdentifier (dialect: SqlDialect) (name: string) =
        if String.IsNullOrEmpty name then name
        elif name.StartsWith("\"") || name.StartsWith("[") || name.StartsWith("`") then name
        elif reservedKeywords.Contains name || name.Contains(" ") || not (Char.IsLetter(name.[0]) || name.[0] = '_') then
            match dialect with
            | SqlDialect.SqlServer -> $"[{name}]"
            | SqlDialect.MySql | SqlDialect.MariaDb -> $"`{name}`"
            | _ -> $"\"{name}\""
        else
            name

