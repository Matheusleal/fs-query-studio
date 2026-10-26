namespace FSharpQueryStudio.Connectors

open System
open System.Data
open System.Threading
open System.Threading.Tasks
open Npgsql
open FSharpQueryStudio.Protocol

/// PostgreSQL connector — Section 10, same capability shape as
/// `SqliteConnector`: connection, parameterized commands, reader-based
/// paging, schema discovery.
type PostgreSqlConnector(connectionString: string) =

    /// Binds every `(name, value)` pair as a real `NpgsqlParameter` —
    /// never string-concatenated into `sql`. The structural guarantee
    /// C.11's cross-connector regression test checks.
    static member BindParameters(command: NpgsqlCommand, parameters: (string * obj) list) =
        for name, value in parameters do
            command.Parameters.AddWithValue(name, (if isNull value then box DBNull.Value else value))
            |> ignore

    /// Runs `sql` with `parameters` bound, returning columns immediately
    /// and rows as a lazy sequence that owns the connection/reader until
    /// fully enumerated or disposed — Section 10's reader-based paging.
    member _.ExecuteQuery (sql: string) (parameters: (string * obj) list) : ConnectorQueryResult =
        let connection = new NpgsqlConnection(connectionString)
        connection.Open()
        let command = connection.CreateCommand()
        command.CommandText <- sql
        PostgreSqlConnector.BindParameters(command, parameters)
        let reader = command.ExecuteReader(CommandBehavior.CloseConnection)
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

    /// Every base table, view and column across user schemas, via
    /// `information_schema` — Section 10's "schema discovery".
    member _.DiscoverSchemaAsync(ct: CancellationToken) : Task<ConnectorTable list> =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync(ct)
            use command = connection.CreateCommand()

            command.CommandText <-
                "select t.table_schema, t.table_name, c.column_name, c.data_type \
                 from information_schema.tables t \
                 join information_schema.columns c \
                   on c.table_name = t.table_name and c.table_schema = t.table_schema \
                 where t.table_type in ('BASE TABLE', 'VIEW') \
                   and t.table_schema not in ('information_schema', 'pg_catalog') \
                   and t.table_schema not like 'pg_%' \
                 order by t.table_schema, t.table_name, c.ordinal_position"

            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<string * string * string * string>()
            let mutable hasRow = true

            while hasRow do
                let! more = reader.ReadAsync(ct)

                if more then
                    rows.Add(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
                else
                    hasRow <- false

            return
                rows
                |> Seq.groupBy (fun (schema, tableName, _, _) ->
                    if schema = "public" then tableName
                    else $"{schema}.{tableName}")
                |> Seq.map (fun (displayName, tableRows) ->
                    { Name = displayName
                      Columns =
                        tableRows
                        |> Seq.map (fun (_, _, columnName, dataType) -> ({ Name = columnName; DataType = dataType }: ConnectorColumn))
                        |> List.ofSeq }: ConnectorTable)
                |> List.ofSeq
        }

    interface IConnector with
        member _.Kind = ConnectorKind.PostgreSql

        member _.TestConnectionAsync(ct: CancellationToken) : Task<bool> =
            task {
                try
                    use connection = new NpgsqlConnection(connectionString)
                    do! connection.OpenAsync(ct)
                    return true
                with _ ->
                    return false
            }

        member this.DiscoverSchemaAsync(ct: CancellationToken) = this.DiscoverSchemaAsync(ct)

    interface IQueryableConnector with
        member this.ExecuteQuery (sql: string) (parameters: (string * obj) list) = this.ExecuteQuery sql parameters
