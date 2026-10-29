namespace FSharpQueryStudio.Protocol

open System
open System.Text.Json
open System.Text.Json.Serialization

type ConnectorKindJsonConverter() =
    inherit JsonConverter<ConnectorKind>()

    override _.Read(reader: byref<Utf8JsonReader>, _: Type, _: JsonSerializerOptions) : ConnectorKind =
        match reader.TokenType with
        | JsonTokenType.String ->
            let s = reader.GetString()
            match s with
            | "SqlServer" -> ConnectorKind.SqlServer
            | "PostgreSql" -> ConnectorKind.PostgreSql
            | "Sqlite" -> ConnectorKind.Sqlite
            | "Csv" -> ConnectorKind.Csv
            | "Excel" -> ConnectorKind.Excel
            | "RestJson" -> ConnectorKind.RestJson
            | other -> ConnectorKind.Custom other
        | JsonTokenType.StartObject ->
            use doc = JsonDocument.ParseValue(&reader)
            let root = doc.RootElement
            let mutable caseProp = Unchecked.defaultof<JsonElement>
            if root.TryGetProperty("Case", &caseProp) && caseProp.ValueKind = JsonValueKind.String then
                let s = caseProp.GetString()
                match s with
                | "SqlServer" -> ConnectorKind.SqlServer
                | "PostgreSql" -> ConnectorKind.PostgreSql
                | "Sqlite" -> ConnectorKind.Sqlite
                | "Csv" -> ConnectorKind.Csv
                | "Excel" -> ConnectorKind.Excel
                | "RestJson" -> ConnectorKind.RestJson
                | other -> ConnectorKind.Custom other
            elif root.TryGetProperty("case", &caseProp) && caseProp.ValueKind = JsonValueKind.String then
                let s = caseProp.GetString()
                match s with
                | "SqlServer" -> ConnectorKind.SqlServer
                | "PostgreSql" -> ConnectorKind.PostgreSql
                | "Sqlite" -> ConnectorKind.Sqlite
                | "Csv" -> ConnectorKind.Csv
                | "Excel" -> ConnectorKind.Excel
                | "RestJson" -> ConnectorKind.RestJson
                | other -> ConnectorKind.Custom other
            else
                ConnectorKind.Custom (root.GetRawText())
        | _ ->
            let s = reader.GetString()
            ConnectorKind.Custom s

    override _.Write(writer: Utf8JsonWriter, value: ConnectorKind, _: JsonSerializerOptions) : unit =
        let caseName =
            match value with
            | ConnectorKind.SqlServer -> "SqlServer"
            | ConnectorKind.PostgreSql -> "PostgreSql"
            | ConnectorKind.Sqlite -> "Sqlite"
            | ConnectorKind.Csv -> "Csv"
            | ConnectorKind.Excel -> "Excel"
            | ConnectorKind.RestJson -> "RestJson"
            | ConnectorKind.Custom s -> s
        writer.WriteStartObject()
        writer.WriteString("Case", caseName)
        writer.WriteEndObject()

/// The open connector targets: native first-party targets plus extensible custom kinds.
and [<JsonConverter(typeof<ConnectorKindJsonConverter>)>] ConnectorKind =
    | SqlServer
    | PostgreSql
    | Sqlite
    | Csv
    | Excel
    | RestJson
    | Custom of string

/// A registered, non-secret connection binding. `Configuration` is
/// connector-specific (a SQLite file path, a REST base URL and headers,
/// ...); `SecretReference` points into the OS credential store rather
/// than carrying any credential value itself, so this type is always
/// safe to write to `workspace.json` as-is.
type ConnectionDefinition =
    { Id: Guid
      Name: string
      Kind: ConnectorKind
      Configuration: JsonElement
      SecretReference: string option
      IsTrusted: bool }

/// `connection/remove` request payload.
type ConnectionRemoveParams = { Id: Guid }

/// `connection/schema` request payload.
type ConnectionSchemaParams = { Id: Guid }

/// One discovered column — the wire-facing mirror of `ConnectorColumn`.
type SchemaColumn = { Name: string; DataType: string }

/// One discovered table/sheet/resource — mirrors `ConnectorTable`.
type SchemaTable = { Name: string; Columns: SchemaColumn list }

/// `connection/schema` result payload.
type ConnectionSchemaResult = { Tables: SchemaTable list }

/// Wire representation of a declarative form field for connection creation.
type ConnectorFormFieldDto =
    { Name: string
      Label: string
      Type: string
      Required: bool
      Placeholder: string option
      DefaultValue: string option
      Options: string list option
      SecretHandling: string }

/// Wire representation of a registered connector's metadata and schema.
type ConnectorDescriptorDto =
    { KindId: string
      DisplayName: string
      Category: string
      Icon: string
      FormFields: ConnectorFormFieldDto list }

/// `connector/list` result payload.
type ConnectorListResult =
    { Connectors: ConnectorDescriptorDto list }
