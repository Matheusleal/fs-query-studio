namespace FSharpQueryStudio.Connectors

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.SqlClient
open FSharpQueryStudio.Protocol

/// SQL Server connector — Section 10, same capability shape as
/// `SqliteConnector`: connection, parameterized commands, reader-based
/// paging, schema discovery.
type SqlServerConnector(connectionString: string) =

    /// Binds every `(name, value)` pair as a real `SqlParameter` — never
    /// string-concatenated into `sql`. The structural guarantee C.11's
    /// cross-connector regression test checks.
    static member BindParameters(command: SqlCommand, parameters: (string * obj) list) =
        for name, value in parameters do
            command.Parameters.AddWithValue(name, (if isNull value then box DBNull.Value else value))
            |> ignore

    /// Runs `sql` with `parameters` bound, returning columns immediately
    /// and rows as a lazy sequence that owns the connection/reader until
    /// fully enumerated or disposed — Section 10's reader-based paging.
    member _.ExecuteQuery (sql: string) (parameters: (string * obj) list) : ConnectorQueryResult =
        let connection = new SqlConnection(connectionString)
        connection.Open()
        let command = connection.CreateCommand()
        command.CommandText <- sql
        SqlServerConnector.BindParameters(command, parameters)
        let reader = command.ExecuteReader()
        let columns = [ for i in 0 .. reader.FieldCount - 1 -> reader.GetName(i) ]

        let rows =
            seq {
                use connection = connection
                use command = command
                use reader = reader

                while reader.Read() do
                    yield [| for i in 0 .. reader.FieldCount - 1 -> reader.GetValue(i) |]
            }

        { Columns = columns; Rows = rows }

    /// Every base table and column via `INFORMATION_SCHEMA` — Section
    /// 10's "schema discovery".
    member _.DiscoverSchemaAsync(ct: CancellationToken) : Task<ConnectorTable list> =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync(ct)
            use command = connection.CreateCommand()

            command.CommandText <-
                "select t.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE \
                 from INFORMATION_SCHEMA.TABLES t \
                 join INFORMATION_SCHEMA.COLUMNS c \
                   on c.TABLE_NAME = t.TABLE_NAME and c.TABLE_SCHEMA = t.TABLE_SCHEMA \
                 where t.TABLE_TYPE = 'BASE TABLE' \
                 order by t.TABLE_NAME, c.ORDINAL_POSITION"

            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<string * string * string>()
            let mutable hasRow = true

            while hasRow do
                let! more = reader.ReadAsync(ct)

                if more then
                    rows.Add(reader.GetString(0), reader.GetString(1), reader.GetString(2))
                else
                    hasRow <- false

            return
                rows
                |> Seq.groupBy (fun (tableName, _, _) -> tableName)
                |> Seq.map (fun (tableName, tableRows) ->
                    { Name = tableName
                      Columns =
                        tableRows
                        |> Seq.map (fun (_, columnName, dataType) -> { Name = columnName; DataType = dataType })
                        |> List.ofSeq })
                |> List.ofSeq
        }

    interface IConnector with
        member _.Kind = ConnectorKind.SqlServer

        member _.TestConnectionAsync(ct: CancellationToken) : Task<bool> =
            task {
                try
                    use connection = new SqlConnection(connectionString)
                    do! connection.OpenAsync(ct)
                    return true
                with _ ->
                    return false
            }

        member this.DiscoverSchemaAsync(ct: CancellationToken) = this.DiscoverSchemaAsync(ct)

    interface IQueryableConnector with
        member this.ExecuteQuery (sql: string) (parameters: (string * obj) list) = this.ExecuteQuery sql parameters
