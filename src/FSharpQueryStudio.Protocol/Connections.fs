namespace FSharpQueryStudio.Protocol

open System
open System.Text.Json
open System.Text.Json.Serialization

type ConnectorKindJsonConverter() =
    inherit JsonConverter<ConnectorKind>()

    override _.Read(reader: byref<Utf8JsonReader>, _: Type, _: JsonSerializerOptions) : ConnectorKind =
        match reader.TokenType with
        | JsonTokenType.String ->
            ConnectorKind(reader.GetString())
        | JsonTokenType.StartObject ->
            use doc = JsonDocument.ParseValue(&reader)
            let root = doc.RootElement
            let mutable caseProp = Unchecked.defaultof<JsonElement>
            if root.TryGetProperty("Case", &caseProp) && caseProp.ValueKind = JsonValueKind.String then
                ConnectorKind(caseProp.GetString())
            elif root.TryGetProperty("case", &caseProp) && caseProp.ValueKind = JsonValueKind.String then
                ConnectorKind(caseProp.GetString())
            else
                ConnectorKind(root.GetRawText())
        | _ ->
            ConnectorKind(reader.GetString())

    override _.Write(writer: Utf8JsonWriter, value: ConnectorKind, _: JsonSerializerOptions) : unit =
        writer.WriteStartObject()
        writer.WriteString("Case", value.Id)
        writer.WriteEndObject()

/// Open, connector-agnostic identifier representing a connector kind on the wire.
/// Concrete connector modules define their own kind identifiers without coupling the protocol.
and [<Struct; CustomEquality; NoComparison; JsonConverter(typeof<ConnectorKindJsonConverter>)>] ConnectorKind(id: string) =
    member _.Id: string = if isNull id then "" else id
    static member Custom(id: string) : ConnectorKind = ConnectorKind(id)
    override this.ToString() = this.Id
    override this.Equals(other: obj) =
        match other with
        | :? ConnectorKind as k -> String.Equals(this.Id, k.Id, StringComparison.OrdinalIgnoreCase)
        | _ -> false
    override this.GetHashCode() =
        StringComparer.OrdinalIgnoreCase.GetHashCode(this.Id)

/// A registered, non-secret connection binding. `Configuration` is
/// connector-specific; `SecretReference` points into the OS credential store
/// rather than carrying any credential value itself, so this type is always
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
