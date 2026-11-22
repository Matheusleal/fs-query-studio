namespace FSharpQueryStudio.Connectors

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open FSharpQueryStudio.Protocol

/// One discovered column — see Section 10's "schema discovery" capability
/// shared across every connector kind.
type ConnectorColumn = { Name: string; DataType: string }

/// One discovered table/sheet/resource, named generically so it covers a
/// SQL table, an Excel sheet, or a CSV file alike.
type ConnectorTable =
    {
        Name: string
        Columns: ConnectorColumn list
    }

/// The common surface every connector implements.
/// Deliberately thin: parameterized queries, row paging, and format-specific
/// options live on each connector's concrete type or specialized interface.
type IConnector =
    abstract Kind: ConnectorKind
    abstract TestConnectionAsync: CancellationToken -> Task<bool>
    abstract DiscoverSchemaAsync: CancellationToken -> Task<ConnectorTable list>

/// One `ExecuteQuery` result: the projected column order, and the rows
/// read lazily through the still-open DataReader behind them.
type ConnectorQueryResult =
    {
        Columns: string list
        Rows: obj array seq
    }

/// Event data reported during file staging and cache population phases.
type StagingProgressEvent =
    {
        TableName: string
        FilePath: string
        Phase: string
        RowCount: int option
        CurrentIndex: int option
        TotalCount: int option
        ElapsedMs: int64 option
        Message: string option
    }

type StagingResult = { StagedCount: int; SkippedCount: int }

/// Contract for staging file-based tabular data into an embedded relational engine (e.g. SQLite).
type IStagingEngine =
    abstract DatabasePath: string

    abstract EnsureStaged:
        key: string *
        filePath: string *
        readRows: (unit -> ConnectorQueryResult) *
        ?columnTypes: Map<string, string> *
        ?currentIndex: int *
        ?totalCount: int *
        ?onProgress: (StagingProgressEvent -> unit) ->
            string

    abstract DropStaleTableIfExists: connName: string * filePath: string -> unit
    abstract EnsureAliasView: aliasName: string * targetTable: string -> unit
    abstract IsStagedAndCurrent: key: string * filePath: string -> bool

/// Implemented by file-based connectors that can stage their data into an embedded relational engine.
type IStagableConnector =
    inherit IConnector
    abstract FilePath: string

    abstract StageConnection:
        engine: IStagingEngine *
        connName: string *
        currentIndex: int *
        totalCount: int *
        onProgress: (StagingProgressEvent -> unit) option ->
            StagingResult

    abstract StagePrimaryTable: engine: IStagingEngine * connName: string -> string

/// Common SQL dialect utilities and keyword lookup shared across all connectors.
module SqlDialectCommon =
    let reservedKeywords =
        HashSet<string>(
            [|
                "ALL"
                "ALTER"
                "AND"
                "AS"
                "ASC"
                "BETWEEN"
                "BY"
                "CASE"
                "CAST"
                "CHECK"
                "COLUMN"
                "CONSTRAINT"
                "CREATE"
                "CROSS"
                "CURRENT"
                "DATABASE"
                "DEFAULT"
                "DELETE"
                "DESC"
                "DISTINCT"
                "DROP"
                "ELSE"
                "END"
                "ESCAPE"
                "EXCEPT"
                "EXISTS"
                "FOREIGN"
                "FROM"
                "FULL"
                "GROUP"
                "HAVING"
                "IN"
                "INDEX"
                "INNER"
                "INSERT"
                "INTERSECT"
                "INTO"
                "IS"
                "JOIN"
                "KEY"
                "LEFT"
                "LIKE"
                "LIMIT"
                "NOT"
                "NULL"
                "ON"
                "OR"
                "ORDER"
                "OUTER"
                "PRIMARY"
                "REFERENCES"
                "RIGHT"
                "SELECT"
                "SET"
                "TABLE"
                "THEN"
                "TO"
                "TOP"
                "TRANSACTION"
                "UNION"
                "UNIQUE"
                "UPDATE"
                "VALUES"
                "VIEW"
                "WHEN"
                "WHERE"
                "WITH"
            |],
            StringComparer.OrdinalIgnoreCase
        )

    let shouldQuote (name: string) : bool =
        if String.IsNullOrEmpty name then
            false
        elif name.StartsWith("\"") || name.StartsWith("[") || name.StartsWith("`") then
            false
        elif
            reservedKeywords.Contains name
            || name.Contains(" ")
            || not (Char.IsLetter(name.[0]) || name.[0] = '_')
        then
            true
        else
            false

/// Implemented by every SQL-capable connector (SQLite/SQL Server/PostgreSQL/MySQL/MariaDB/Oracle).
type IQueryableConnector =
    inherit IConnector
    abstract ConnectionString: string
    abstract ExecuteQuery: sql: string -> parameters: (string * obj) list -> ConnectorQueryResult

/// Extended interface for SQL connectors supporting zero-allocation direct reader streaming.
type IStreamingQueryableConnector =
    inherit IQueryableConnector

    abstract ExecuteReaderStream:
        sql: string -> parameters: (string * obj) list -> mapper: (System.Data.IDataRecord -> 'T) -> 'T seq

    abstract ExecuteReaderStreamWithColumns:
        sql: string ->
        parameters: (string * obj) list ->
        mapperFactory: (string list -> (System.Data.IDataRecord -> 'T)) ->
            'T seq

/// Represents dialect-specific SQL syntax generation, explain execution, and entity formatting rules.
type ISqlDialect =
    abstract Name: string
    abstract QuoteIdentifier: name: string -> string
    abstract FormatLimitOffset: take: int option * skip: int option * hasOrderBy: bool -> string
    abstract FormatApplyJoin: isOuter: bool * tableSql: string -> string
    abstract SupportsRecursiveCteKeyword: bool
    abstract DualTable: string option
    abstract TranslateDatePart: part: string * operand: string -> string option
    abstract TranslateDateTrunc: operand: string -> string option
    abstract TranslateJsonExtract: colSql: string * path: string -> string option
    abstract FormatExplain: connector: IQueryableConnector * sql: string * parameters: (string * obj) list -> string

    abstract FormatSqlTarget:
        topGroup: string * subSchemaOpt: string option * rawTable: string * origName: string * isDefaultGroup: bool ->
            string

    abstract SqlProviderType: string

/// Provided by SQL connectors that can supply an ISqlDialect instance.
type ISqlDialectProvider =
    abstract Dialect: ISqlDialect

/// Categorizes any IConnector instance into its primary execution capability for clean pattern matching.
[<RequireQualifiedAccess>]
type ConnectorInstance =
    | Sql of IQueryableConnector
    | Stagable of IStagableConnector
    | Other of IConnector

module ConnectorClassifier =
    let classify (connector: IConnector) : ConnectorInstance =
        match box connector with
        | :? IQueryableConnector as qc -> ConnectorInstance.Sql qc
        | :? IStagableConnector as sc -> ConnectorInstance.Stagable sc
        | _ -> ConnectorInstance.Other connector
