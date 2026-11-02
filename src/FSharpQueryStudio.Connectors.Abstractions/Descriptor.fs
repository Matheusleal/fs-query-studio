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

/// Open, extensible category descriptor for typed access and entity generation.
/// Each connector category specifies its wire identifier, generated module suffix,
/// and target F# category extension type without hardcoding specific connectors in shared code.
[<Struct; CustomEquality; NoComparison>]
type ConnectorCategory(identifier: string, moduleSuffix: string, categoryTypeName: string) =
    member _.Identifier: string = if isNull identifier then "" else identifier
    member _.ModuleSuffix: string = if isNull moduleSuffix then "" else moduleSuffix
    member _.CategoryTypeName: string = if isNull categoryTypeName then "" else categoryTypeName
    static member Custom(id: string) = ConnectorCategory(id, id, "")
    override this.ToString() = this.Identifier
    override this.Equals(other: obj) =
        match other with
        | :? ConnectorCategory as c -> String.Equals(this.Identifier, c.Identifier, StringComparison.OrdinalIgnoreCase)
        | _ -> false
    override this.GetHashCode() =
        StringComparer.OrdinalIgnoreCase.GetHashCode(this.Identifier)

/// Pluggable entity code generator invoked by the background mapper.
type IConnectorEntityGenerator =
    abstract Category: ConnectorCategory
    abstract EmitModuleCode: connectionName: string * connector: IConnector * uniqueModuleName: string -> string
    abstract EmitExtensionCode: connectionName: string * uniqueModuleName: string * propertyName: string -> string

/// Factory responsible for instantiating concrete connectors from serialized JSON configuration.
type IConnectorFactory =
    abstract Create: configuration: JsonElement * secretStore: ISecretStore * secretReference: string option -> IConnector

/// Self-describing connector metadata, validation, and factory provider (SPI).
type IConnectorDescriptor =
    abstract KindId: string
    abstract DisplayName: string
    abstract Category: ConnectorCategory
    abstract Icon: string
    abstract FormFields: ConnectorFormField list
    abstract ValidateConfiguration: configuration: JsonElement -> Result<unit, string>
    abstract Factory: IConnectorFactory
    abstract EntityGenerator: IConnectorEntityGenerator option

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

    let tryGetBool (propName: string) (elem: JsonElement) : bool option =
        if elem.ValueKind = JsonValueKind.Object then
            elem.EnumerateObject()
            |> Seq.tryFind (fun p -> String.Equals(p.Name, propName, StringComparison.OrdinalIgnoreCase))
            |> Option.bind (fun p ->
                match p.Value.ValueKind with
                | JsonValueKind.True -> Some true
                | JsonValueKind.False -> Some false
                | JsonValueKind.String ->
                    let s = p.Value.GetString().ToLowerInvariant()
                    if s = "true" || s = "1" || s = "yes" then Some true
                    elif s = "false" || s = "0" || s = "no" then Some false
                    else None
                | _ -> None)
        else None

    let tryGetMap (propName: string) (elem: JsonElement) : Map<string, string> =
        if elem.ValueKind = JsonValueKind.Object then
            elem.EnumerateObject()
            |> Seq.tryFind (fun p -> String.Equals(p.Name, propName, StringComparison.OrdinalIgnoreCase))
            |> Option.map (fun p ->
                if p.Value.ValueKind = JsonValueKind.Object then
                    p.Value.EnumerateObject()
                    |> Seq.map (fun prop ->
                        let v =
                            if prop.Value.ValueKind = JsonValueKind.String then prop.Value.GetString()
                            else prop.Value.GetRawText().Trim('"')
                        prop.Name, v)
                    |> Map.ofSeq
                elif p.Value.ValueKind = JsonValueKind.String then
                    let text = p.Value.GetString()
                    text.Split([| ','; ';' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Seq.choose (fun pair ->
                        let parts = pair.Split([| '='; ':' |], 2)
                        if parts.Length = 2 then
                            Some (parts.[0].Trim(), parts.[1].Trim())
                        else None)
                    |> Map.ofSeq
                else
                    Map.empty)
            |> Option.defaultValue Map.empty
        else
            Map.empty

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ConnectorDescriptor =
    /// Generic schema-based validation of required form fields.
    /// Individual connectors can invoke this and/or apply their own domain validation rules.
    let validateRequiredFields (fields: ConnectorFormField list) (configuration: JsonElement) : Result<unit, string> =
        let missing =
            fields
            |> List.filter (fun f -> f.Required && f.Type <> ConnectorFieldType.Password)
            |> List.tryFind (fun f ->
                match JsonElementHelpers.tryGetString f.Name configuration with
                | Some v when not (String.IsNullOrWhiteSpace v) -> false
                | _ ->
                    if configuration.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(configuration.GetString())) then
                        false
                    else
                        true)
        match missing with
        | Some f -> Result.Error $"Required field '{f.Name}' ({f.Label}) is missing or empty."
        | None -> Result.Ok ()

    /// Normalizes a workspace location into the project root used as the base for
    /// relative paths: a `.fsqs` directory maps to its parent folder.
    let projectRootOf (workspaceDirectory: string) : string =
        if String.IsNullOrWhiteSpace workspaceDirectory then
            ""
        else
            let trimmed =
                workspaceDirectory.Trim().TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)
            if String.Equals(IO.Path.GetFileName trimmed, ".fsqs", StringComparison.OrdinalIgnoreCase) then
                match IO.Path.GetDirectoryName trimmed with
                | null -> trimmed
                | parent -> parent
            else
                trimmed

    /// Resolves a single local path against `baseDirectory`. Absolute paths, URIs,
    /// and special in-memory markers are returned unchanged; `~` expands to the user home.
    let resolvePath (baseDirectory: string) (rawPath: string) : string =
        if String.IsNullOrWhiteSpace rawPath then
            rawPath
        else
            let p = rawPath.Trim().Trim('"', '\'')
            let isSpecial =
                p.StartsWith(":memory:", StringComparison.OrdinalIgnoreCase)
                || p.Contains("://")
                || p.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            if isSpecial then
                p
            elif p = "~" || p.StartsWith("~/") || p.StartsWith("~\\") then
                let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                IO.Path.GetFullPath(IO.Path.Combine(home, p.Substring(1).TrimStart('/', '\\')))
            elif IO.Path.IsPathRooted p then
                p
            elif String.IsNullOrWhiteSpace baseDirectory then
                p
            else
                IO.Path.GetFullPath(IO.Path.Combine(baseDirectory, p))

    /// Rewrites every declared `File` form field whose value is a relative path so it
    /// becomes absolute against `baseDirectory`. Connector-agnostic: driven only by the
    /// descriptor's own form schema, so each connector keeps owning which fields are files.
    let resolveRelativeFilePaths
        (fields: ConnectorFormField list)
        (configuration: JsonElement)
        (baseDirectory: string)
        : JsonElement =
        let fileFields =
            fields
            |> List.filter (fun f -> f.Type = ConnectorFieldType.File)
            |> List.map (fun f -> f.Name)

        if String.IsNullOrWhiteSpace baseDirectory || List.isEmpty fileFields then
            configuration
        elif configuration.ValueKind = JsonValueKind.String then
            // Bare-string configuration (legacy shape) is treated as the single file path.
            let resolved = resolvePath baseDirectory (configuration.GetString())
            JsonSerializer.SerializeToElement(resolved)
        elif configuration.ValueKind <> JsonValueKind.Object then
            configuration
        else
            let isFileField (name: string) =
                fileFields |> List.exists (fun f -> String.Equals(f, name, StringComparison.OrdinalIgnoreCase))

            use stream = new IO.MemoryStream()
            do
                use writer = new Utf8JsonWriter(stream)
                writer.WriteStartObject()
                for prop in configuration.EnumerateObject() do
                    if isFileField prop.Name && prop.Value.ValueKind = JsonValueKind.String then
                        writer.WriteString(prop.Name, resolvePath baseDirectory (prop.Value.GetString()))
                    else
                        writer.WritePropertyName(prop.Name)
                        prop.Value.WriteTo(writer)
                writer.WriteEndObject()
                writer.Flush()
            use doc = JsonDocument.Parse(stream.ToArray())
            doc.RootElement.Clone()

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
