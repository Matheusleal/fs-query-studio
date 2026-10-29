namespace FSharpQueryStudio.Connectors

open System
open System.Text.Json
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

/// The data/input type for a declarative connector configuration field.
[<RequireQualifiedAccess>]
type ConnectorFieldType =
    | String
    | File
    | Password
    | Select of options: string list
    | Boolean

/// Rules governing secret extraction and persistence in the OS credential store.
[<RequireQualifiedAccess>]
type SecretHandling =
    | None
    | IsolatedCredential
    | EmbedInConnectionString of placeholder: string

/// Declarative specification of one input field in the connection creation wizard.
type ConnectorFormField =
    { Name: string
      Label: string
      Type: ConnectorFieldType
      Required: bool
      Placeholder: string option
      DefaultValue: string option
      SecretHandling: SecretHandling }

/// High-level category for typed access (`Connections.sql`, `Connections.csv`, etc.).
[<RequireQualifiedAccess>]
type ConnectorCategory =
    | Sql
    | TabularFile
    | Excel
    | Rest
    | Custom of string

    member this.Identifier: string =
        match this with
        | Sql -> "sql"
        | TabularFile -> "csv"
        | Excel -> "excel"
        | Rest -> "rest"
        | Custom s -> s

/// Pluggable entity code generator invoked by the background mapper.
type IConnectorEntityGenerator =
    abstract Category: ConnectorCategory
    abstract EmitModuleCode: connectionName: string * connector: IConnector * uniqueModuleName: string -> string
    abstract EmitExtensionCode: connectionName: string * uniqueModuleName: string * propertyName: string -> string

/// Factory responsible for instantiating concrete connectors from serialized JSON configuration.
type IConnectorFactory =
    abstract Create: configuration: JsonElement * secretStore: ISecretStore * secretReference: string option -> IConnector

/// Self-describing connector metadata and factory provider (SPI).
type IConnectorDescriptor =
    abstract KindId: string
    abstract DisplayName: string
    abstract Category: ConnectorCategory
    abstract Icon: string
    abstract FormFields: ConnectorFormField list
    abstract Factory: IConnectorFactory
    abstract EntityGenerator: IConnectorEntityGenerator option

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ConnectorDescriptor =
    let toDto (desc: IConnectorDescriptor) : ConnectorDescriptorDto =
        { KindId = desc.KindId
          DisplayName = desc.DisplayName
          Category = desc.Category.Identifier
          Icon = desc.Icon
          FormFields =
            desc.FormFields
            |> List.map (fun f ->
                let typeStr, opts =
                    match f.Type with
                    | ConnectorFieldType.String -> "string", None
                    | ConnectorFieldType.File -> "file", None
                    | ConnectorFieldType.Password -> "password", None
                    | ConnectorFieldType.Select o -> "select", Some o
                    | ConnectorFieldType.Boolean -> "boolean", None
                let secretHandlingStr =
                    match f.SecretHandling with
                    | SecretHandling.None -> "none"
                    | SecretHandling.IsolatedCredential -> "isolated"
                    | SecretHandling.EmbedInConnectionString p -> $"connectionString:{p}"
                { Name = f.Name
                  Label = f.Label
                  Type = typeStr
                  Required = f.Required
                  Placeholder = f.Placeholder
                  DefaultValue = f.DefaultValue
                  Options = opts
                  SecretHandling = secretHandlingStr }: ConnectorFormFieldDto) }

module JsonElementHelpers =
    let tryGetString (propName: string) (elem: JsonElement) : string option =
        if elem.ValueKind = JsonValueKind.Object then
            elem.EnumerateObject()
            |> Seq.tryFind (fun p -> String.Equals(p.Name, propName, StringComparison.OrdinalIgnoreCase))
            |> Option.bind (fun p ->
                if p.Value.ValueKind = JsonValueKind.String then Some (p.Value.GetString())
                elif p.Value.ValueKind = JsonValueKind.Null then None
                else Some (p.Value.GetRawText().Trim('"')))
        else None

module CodeGenerationHelpers =
    open System.Text.RegularExpressions

    let cleanIdentifier (name: string) : string =
        let cleaned = Regex.Replace(name, @"[^a-zA-Z0-9_]", "_")
        if Regex.IsMatch(cleaned, @"^[0-9]") then $"_{cleaned}" else cleaned

    let private keywords =
        set [ "abstract"; "and"; "as"; "assert"; "base"; "begin"; "class"; "default"; "delegate";
              "do"; "done"; "downcast"; "downto"; "elif"; "else"; "end"; "exception"; "extern";
              "false"; "finally"; "fixed"; "for"; "fun"; "function"; "global"; "if"; "in";
              "inherit"; "inline"; "interface"; "internal"; "lazy"; "let"; "match"; "member";
              "module"; "mutable"; "namespace"; "new"; "not"; "null"; "of"; "open"; "or";
              "override"; "private"; "public"; "rec"; "return"; "static"; "struct"; "then";
              "to"; "true"; "try"; "type"; "upcast"; "use"; "val"; "void"; "when"; "while";
              "with"; "yield"; "atomic"; "break"; "checked"; "component"; "const"; "constraint";
              "constructor"; "continue"; "eager"; "event"; "external"; "functor"; "include";
              "method"; "mixin"; "object"; "parallel"; "process"; "protected"; "pure"; "sealed";
              "tailcall"; "trait"; "virtual"; "volatile" ]

    let escapeIdent (s: string) : string =
        if Regex.IsMatch(s, @"^[a-zA-Z][a-zA-Z0-9_]*$") && not (Set.contains s keywords) then
            s
        else
            $"``{s}``"

    let sanitizeColumns (cols: string list) : string list =
        let seen = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        cols
        |> List.mapi (fun idx col ->
            let baseCol =
                if String.IsNullOrWhiteSpace col then $"Column{idx + 1}"
                else col.Trim()
            let mutable candidate = baseCol
            let mutable counter = 2
            while seen.Contains candidate do
                candidate <- $"{baseCol}_{counter}"
                counter <- counter + 1
            seen.Add candidate |> ignore
            candidate)
