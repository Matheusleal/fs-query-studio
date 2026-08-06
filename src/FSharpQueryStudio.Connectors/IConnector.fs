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

/// The common surface every connector implements — see Section 10.
/// Deliberately thin: "do not force all data sources into a
/// lowest-common-denominator query API" means parameterized queries, row
/// paging, and format-specific options live on each connector's own
/// concrete type (built in C.5-C.10), not here. This interface only
/// covers what is true for every kind — identity, connectivity, and
/// schema discovery.
type IConnector =
    abstract Kind: ConnectorKind
    abstract TestConnectionAsync: CancellationToken -> Task<bool>
    abstract DiscoverSchemaAsync: CancellationToken -> Task<ConnectorTable list>
