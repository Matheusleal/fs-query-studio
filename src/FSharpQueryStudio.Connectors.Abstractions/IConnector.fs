namespace FSharpQueryStudio.Connectors

open System.Threading
open System.Threading.Tasks
open FSharpQueryStudio.Protocol

/// One discovered column — see Section 10's "schema discovery" capability
/// shared across every connector kind.
type ConnectorColumn = { Name: string; DataType: string }

/// One discovered table/sheet/resource, named generically so it covers a
/// SQL table, an Excel sheet, or a CSV file alike.
type ConnectorTable = { Name: string; Columns: ConnectorColumn list }

/// The common surface every connector implements.
/// Deliberately thin: parameterized queries, row paging, and format-specific
/// options live on each connector's concrete type or specialized interface.
type IConnector =
    abstract Kind: ConnectorKind
    abstract TestConnectionAsync: CancellationToken -> Task<bool>
    abstract DiscoverSchemaAsync: CancellationToken -> Task<ConnectorTable list>

/// One `ExecuteQuery` result: the projected column order, and the rows
/// read lazily through the still-open DataReader behind them.
type ConnectorQueryResult = { Columns: string list; Rows: obj array seq }

/// Implemented by every SQL-capable connector (SQLite/SQL Server/
/// PostgreSQL) — lets `Connections.sql` and connector-call logging
/// reach any of them through one interface.
type IQueryableConnector =
    inherit IConnector
    abstract ConnectionString: string
    abstract ExecuteQuery: sql: string -> parameters: (string * obj) list -> ConnectorQueryResult

/// Extended interface for SQL connectors supporting zero-allocation direct reader streaming.
type IStreamingQueryableConnector =
    inherit IQueryableConnector
    abstract ExecuteReaderStream: sql: string -> parameters: (string * obj) list -> mapper: (System.Data.IDataRecord -> 'T) -> 'T seq
    abstract ExecuteReaderStreamWithColumns: sql: string -> parameters: (string * obj) list -> mapperFactory: (string list -> (System.Data.IDataRecord -> 'T)) -> 'T seq
