namespace FSharpQueryStudio.Connectors

open System
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

    member this.Dialect: ISqlDialect =
        match this with
        | Sqlite -> SqliteDialect.Instance
        | PostgreSql -> PostgreSqlDialect.Instance
        | SqlServer -> SqlServerDialect.Instance
        | MySql -> MySqlDialect.Instance
        | MariaDb -> MariaDbDialect.Instance
        | Oracle -> OracleDialect.Instance

module SqlDialect =
    let fromConnectorKind (kind: ConnectorKind) =
        if kind = ConnectorKind("PostgreSql") then
            SqlDialect.PostgreSql
        elif kind = ConnectorKind("SqlServer") then
            SqlDialect.SqlServer
        elif kind = ConnectorKind("MySql") then
            SqlDialect.MySql
        elif kind = ConnectorKind("MariaDb") then
            SqlDialect.MariaDb
        elif kind = ConnectorKind("Oracle") then
            SqlDialect.Oracle
        else
            SqlDialect.Sqlite

    let fromConnector (connector: IConnector) : ISqlDialect =
        match box connector with
        | :? ISqlDialectProvider as sdp -> sdp.Dialect
        | _ -> (fromConnectorKind connector.Kind).Dialect

    /// Safely quotes an identifier according to the dialect rules if it contains special characters, spaces, or keywords.
    let quoteIdentifier (dialect: SqlDialect) (name: string) = dialect.Dialect.QuoteIdentifier name
