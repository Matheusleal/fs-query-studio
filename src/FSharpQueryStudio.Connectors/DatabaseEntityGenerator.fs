namespace FSharpQueryStudio.Connectors

open System
open System.Text
open System.Text.RegularExpressions
open System.Threading
open FSharpQueryStudio.Protocol
open Serilog

module DatabaseEntityGenerator =

    let generateEntities
        (name: string)
        (connStr: string)
        (kind: ConnectorKind)
        (tables: ConnectorTable list)
        (moduleName: string option)
        : string =
        let modName =
            match moduleName with
            | Some m -> m
            | None ->
                let clean =
                    System.Text.RegularExpressions.Regex.Replace(name, @"[^a-zA-Z0-9_]", "_")

                $"{clean}Db"

        let keywords =
            set
                [
                    "abstract"
                    "and"
                    "as"
                    "assert"
                    "base"
                    "begin"
                    "class"
                    "default"
                    "delegate"
                    "do"
                    "done"
                    "downcast"
                    "downto"
                    "elif"
                    "else"
                    "end"
                    "exception"
                    "extern"
                    "false"
                    "finally"
                    "fixed"
                    "for"
                    "fun"
                    "function"
                    "global"
                    "if"
                    "in"
                    "inherit"
                    "inline"
                    "interface"
                    "internal"
                    "lazy"
                    "let"
                    "match"
                    "member"
                    "module"
                    "mutable"
                    "namespace"
                    "new"
                    "not"
                    "null"
                    "of"
                    "open"
                    "or"
                    "override"
                    "private"
                    "public"
                    "rec"
                    "return"
                    "static"
                    "struct"
                    "then"
                    "to"
                    "true"
                    "try"
                    "type"
                    "upcast"
                    "use"
                    "val"
                    "void"
                    "when"
                    "while"
                    "with"
                    "yield"
                    "atomic"
                    "break"
                    "checked"
                    "component"
                    "const"
                    "constraint"
                    "constructor"
                    "continue"
                    "eager"
                    "event"
                    "external"
                    "functor"
                    "include"
                    "method"
                    "mixin"
                    "object"
                    "parallel"
                    "process"
                    "protected"
                    "pure"
                    "sealed"
                    "tailcall"
                    "trait"
                    "virtual"
                    "volatile"
                ]

        let cleanIdent (s: string) =
            let cleaned = System.Text.RegularExpressions.Regex.Replace(s, @"[^\p{L}0-9_]", "_")

            if System.Text.RegularExpressions.Regex.IsMatch(cleaned, @"^[0-9]") then
                $"_{cleaned}"
            elif String.IsNullOrWhiteSpace cleaned then
                "Item"
            else
                cleaned

        let escapeIdent (s: string) =
            let cleaned = cleanIdent s

            if
                System.Text.RegularExpressions.Regex.IsMatch(cleaned, @"^[a-zA-Z_\p{L}][a-zA-Z0-9_\p{L}]*$")
                && not (Set.contains cleaned keywords)
            then
                cleaned
            else
                $"``{cleaned}``"

        let mapType (t: string) =
            let lower = t.ToLowerInvariant().Trim()

            if
                lower = "string"
                || lower.Contains("string")
                || lower.Contains("char")
                || lower.Contains("text")
                || lower.Contains("json")
                || lower.Contains("clob")
                || lower.Contains("xml")
                || lower.Contains("varchar")
                || lower.Contains("nvarchar")
            then
                "string"
            elif
                lower.Contains("bigint")
                || lower.Contains("int8")
                || lower.Contains("bigserial")
                || lower = "int64"
                || lower = "long"
            then
                "int64"
            elif
                lower.Contains("integer")
                || lower.Contains("int4")
                || lower.Contains("serial")
                || lower = "int"
                || lower = "int32"
                || lower = "system.int32"
                || lower.Contains("mediumint")
            then
                "int"
            elif
                lower.Contains("smallint")
                || lower.Contains("int2")
                || lower = "int16"
                || lower = "short"
            then
                "int16"
            elif
                lower.Contains("tinyint(1)")
                || lower = "bool"
                || lower = "boolean"
                || lower.Contains("bit")
            then
                "bool"
            elif lower.Contains("tinyint") || lower = "byte" then
                "byte"
            elif
                lower.Contains("uuid")
                || lower.Contains("uniqueidentifier")
                || lower = "guid"
                || lower = "system.guid"
            then
                "System.Guid"
            elif lower.Contains("time") || lower.Contains("date") then
                "System.DateTime"
            elif
                lower.Contains("decimal")
                || lower.Contains("numeric")
                || lower.Contains("money")
                || lower.Contains("number")
            then
                "decimal"
            elif lower.Contains("double") || lower.Contains("float8") || lower = "float" then
                "double"
            elif lower.Contains("real") || lower.Contains("float4") || lower = "single" then
                "single"
            elif
                lower.Contains("bytea")
                || lower.Contains("blob")
                || lower.Contains("binary")
                || lower.Contains("raw")
                || lower.Contains("image")
            then
                "byte[]"
            else
                "obj"

        let sb = System.Text.StringBuilder()
        sb.AppendLine($"module {escapeIdent modName} =") |> ignore
        sb.AppendLine() |> ignore

        let parsedTables =
            tables
            |> List.map (fun t ->
                let parts = t.Name.Split('.')

                if parts.Length <= 1 then
                    ("public", None, t.Name, t)
                elif parts.Length = 2 then
                    (parts.[0], None, parts.[1], t)
                else
                    let subSchema = parts.[1]
                    let rawTbl = String.concat "." parts.[2..]
                    (parts.[0], Some subSchema, rawTbl, t))

        let configuredDbOpt =
            let connStr = if isNull connStr then "" else connStr

            let m =
                System.Text.RegularExpressions.Regex.Match(
                    connStr,
                    @"(?i)(?:Database|Initial Catalog|User Id|UID|Username)\s*=\s*([^;\s]+)"
                )

            if m.Success then
                Some(m.Groups.[1].Value.Trim().Trim('"', '\'', '[', ']'))
            else
                None

        let schemaGroups =
            parsedTables
            |> Seq.groupBy (fun (topGroup, _, _, _) -> topGroup)
            |> Seq.sortBy fst
            |> Seq.toList

        let defaultTopGroupOpt =
            let allGroups = schemaGroups |> List.map fst

            match allGroups |> List.tryFind (fun g -> g = "public" || g = "dbo" || g = "main") with
            | Some g -> Some g
            | None ->
                match configuredDbOpt with
                | Some cfgDb ->
                    allGroups
                    |> List.tryFind (fun g -> String.Equals(g, cfgDb, StringComparison.OrdinalIgnoreCase))
                    |> Option.orElseWith (fun () ->
                        allGroups
                        |> List.tryFind (fun g ->
                            not (String.Equals(g, "postgres", StringComparison.OrdinalIgnoreCase))
                            && not (String.Equals(g, "master", StringComparison.OrdinalIgnoreCase)))
                        |> Option.orElse (List.tryHead allGroups))
                | None ->
                    allGroups
                    |> List.tryFind (fun g ->
                        not (String.Equals(g, "postgres", StringComparison.OrdinalIgnoreCase))
                        && not (String.Equals(g, "master", StringComparison.OrdinalIgnoreCase)))
                    |> Option.orElse (List.tryHead allGroups)

        let formatTreeSqlTarget
            (topGroup: string)
            (subSchemaOpt: string option)
            (rawTable: string)
            (origName: string)
            : string =
            let isDefaultGroup =
                match defaultTopGroupOpt with
                | Some dg -> String.Equals(topGroup, dg, StringComparison.OrdinalIgnoreCase)
                | None -> false

            let isKnownSchemaOnly = topGroup = "public" || topGroup = "dbo" || topGroup = "main"

            match subSchemaOpt with
            | None ->
                if origName.IndexOf('.') < 0 then
                    rawTable
                elif isKnownSchemaOnly then
                    origName
                else
                    let dialect = SqlDialect.fromConnectorKind kind
                    dialect.Dialect.FormatSqlTarget(topGroup, None, rawTable, origName, isDefaultGroup)
            | Some _ ->
                let dialect = SqlDialect.fromConnectorKind kind
                dialect.Dialect.FormatSqlTarget(topGroup, subSchemaOpt, rawTable, origName, isDefaultGroup)

        let formatTopLevelSqlTarget
            (topGroup: string)
            (subSchemaOpt: string option)
            (rawTable: string)
            (origName: string)
            : string =
            let isKnownSchemaOnly = topGroup = "public" || topGroup = "dbo" || topGroup = "main"

            match subSchemaOpt with
            | None ->
                if origName.IndexOf('.') < 0 then rawTable
                elif isKnownSchemaOnly then origName
                else rawTable
            | Some subSchema ->
                if subSchema = "public" || subSchema = "dbo" || subSchema = "main" then
                    rawTable
                elif
                    kind = ConnectorKind.Oracle
                    && String.Equals(subSchema, "SYSTEM", StringComparison.OrdinalIgnoreCase)
                then
                    rawTable
                else
                    $"{subSchema}.{rawTable}"

        for (schema, entries) in schemaGroups do
            sb.AppendLine($"    module {escapeIdent schema} =") |> ignore

            let directEntries =
                entries |> Seq.filter (fun (_, subOpt, _, _) -> subOpt.IsNone) |> Seq.toList

            let subSchemaEntries =
                entries
                |> Seq.choose (fun (_, subOpt, rawTbl, t) -> subOpt |> Option.map (fun s -> (s, rawTbl, t)))
                |> Seq.groupBy (fun (s, _, _) -> s)
                |> Seq.sortBy fst
                |> Seq.toList

            let emittedDirectTypes =
                System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

            for (_, _, rawTableName, t) in directEntries do
                let typeName = escapeIdent rawTableName

                if emittedDirectTypes.Add(typeName) then
                    sb.AppendLine($"        type {typeName} =") |> ignore
                    sb.AppendLine("            {") |> ignore

                    for col in t.Columns do
                        sb.AppendLine($"                {escapeIdent col.Name}: {mapType col.DataType}")
                        |> ignore

                    sb.AppendLine("            }") |> ignore
                    sb.AppendLine() |> ignore

            for (subSchema, subTbls) in subSchemaEntries do
                sb.AppendLine($"        module {escapeIdent subSchema} =") |> ignore

                let emittedSubTypes =
                    System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

                for (_, rawTableName, t) in subTbls do
                    let typeName = escapeIdent rawTableName

                    if emittedSubTypes.Add(typeName) then
                        sb.AppendLine($"            type {typeName} =") |> ignore
                        sb.AppendLine("                {") |> ignore

                        for col in t.Columns do
                            sb.AppendLine($"                    {escapeIdent col.Name}: {mapType col.DataType}")
                            |> ignore

                        sb.AppendLine("                }") |> ignore
                        sb.AppendLine() |> ignore

                if
                    subSchema = "public"
                    || subSchema = "dbo"
                    || subSchema = "main"
                    || String.Equals(subSchema, "SYSTEM", StringComparison.OrdinalIgnoreCase)
                    || subSchemaEntries.Length = 1
                then
                    for (_, rawTableName, _) in subTbls do
                        let typeName = escapeIdent rawTableName

                        if emittedDirectTypes.Add(typeName) then
                            sb.AppendLine($"        type {typeName} = {escapeIdent subSchema}.{typeName}")
                            |> ignore

                    sb.AppendLine() |> ignore

        sb.AppendLine("    type Database(conn: FSharpQueryStudio.Connectors.Connections.DatabaseConnection) =")
        |> ignore

        sb.AppendLine(
            $"        new(connName: string) = Database(FSharpQueryStudio.Connectors.Connections.getDatabaseConnection(connName))"
        )
        |> ignore

        sb.AppendLine($"        new() = Database(\"{name}\")") |> ignore

        sb.AppendLine(
            "        member _.rawQuery(sqlText: string, ?parameters: (string * obj) list) : FSharpQueryStudio.Connectors.ConnectorQueryResult = conn.rawQuery(sqlText, ?parameters = parameters)"
        )
        |> ignore

        sb.AppendLine(
            "        member _.rawQuery(sqlText: string) : FSharpQueryStudio.Connectors.ConnectorQueryResult = conn.rawQuery(sqlText)"
        )
        |> ignore

        sb.AppendLine(
            "        member _.Execute(sqlText: string, ?parameters: (string * obj) list) : FSharpQueryStudio.Connectors.ConnectorQueryResult = conn.Execute(sqlText, ?parameters = parameters)"
        )
        |> ignore

        sb.AppendLine(
            "        member _.Table<'T>(tableName: string) : FSharpQueryStudio.Connectors.SqlPushdownQueryable<'T> = conn.Table<'T>(tableName)"
        )
        |> ignore

        sb.AppendLine("        member _.Connection = conn") |> ignore

        let emittedTopLevelProps =
            System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)

        for (schema, _) in schemaGroups do
            emittedTopLevelProps.Add(escapeIdent schema) |> ignore
            emittedTopLevelProps.Add(escapeIdent (schema.ToLowerInvariant())) |> ignore
            emittedTopLevelProps.Add(escapeIdent (schema.ToUpperInvariant())) |> ignore

        // Emit top-level properties for the default database/schema first (backwards compatibility)
        for (schema, entries) in schemaGroups do
            let isPrimaryGroup =
                schema = "public"
                || schema = "dbo"
                || schema = "main"
                || (match defaultTopGroupOpt with
                    | Some dg -> String.Equals(schema, dg, StringComparison.OrdinalIgnoreCase)
                    | None -> false)

            if isPrimaryGroup then
                let subSchemaNames =
                    entries |> Seq.choose (fun (_, s, _, _) -> s) |> Seq.distinct |> Seq.toList

                for (_, subOpt, rawTableName, t) in entries do
                    let isDefaultSub =
                        match subOpt with
                        | None -> true
                        | Some s ->
                            s = "public"
                            || s = "dbo"
                            || s = "main"
                            || String.Equals(s, "SYSTEM", StringComparison.OrdinalIgnoreCase)
                            || (subSchemaNames.Length = 1)

                    if isDefaultSub then
                        let propName = escapeIdent rawTableName

                        let emitTopProp (nameToEmit: string) =
                            if emittedTopLevelProps.Add(nameToEmit) then
                                let typeRef =
                                    match subOpt with
                                    | None -> $"{escapeIdent schema}.{propName}"
                                    | Some s -> $"{escapeIdent schema}.{escapeIdent s}.{propName}"

                                let sqlTarget = formatTopLevelSqlTarget schema subOpt rawTableName t.Name

                                sb.AppendLine(
                                    $"        member _.{nameToEmit} : FSharpQueryStudio.Connectors.SqlPushdownQueryable<{typeRef}> = conn.Table<{typeRef}>(\"{sqlTarget}\")"
                                )
                                |> ignore

                        emitTopProp propName

                        if rawTableName <> rawTableName.ToLowerInvariant() then
                            emitTopProp (escapeIdent (rawTableName.ToLowerInvariant()))

                        if rawTableName <> rawTableName.ToUpperInvariant() then
                            emitTopProp (escapeIdent (rawTableName.ToUpperInvariant()))

                        let pascal =
                            rawTableName.Substring(0, 1).ToUpperInvariant()
                            + (if rawTableName.Length > 1 then
                                   rawTableName.Substring(1)
                               else
                                   "")

                        if pascal <> rawTableName then
                            emitTopProp (escapeIdent pascal)

        // Emit direct sub-schema properties if the primary group has nested schemas (e.g. SYSTEM or dbo)
        for (schema, entries) in schemaGroups do
            let isPrimaryGroup =
                schema = "public"
                || schema = "dbo"
                || schema = "main"
                || (match defaultTopGroupOpt with
                    | Some dg -> String.Equals(schema, dg, StringComparison.OrdinalIgnoreCase)
                    | None -> false)

            if isPrimaryGroup then
                let subSchemaEntries =
                    entries
                    |> Seq.choose (fun (_, subOpt, rawTbl, t) -> subOpt |> Option.map (fun s -> (s, rawTbl, t)))
                    |> Seq.groupBy (fun (s, _, _) -> s)
                    |> Seq.sortBy fst
                    |> Seq.toList

                for (subSchema, subTbls) in subSchemaEntries do
                    let subPropName = escapeIdent subSchema

                    if emittedTopLevelProps.Add(subPropName) then
                        sb.AppendLine($"        member _.{subPropName} =") |> ignore
                        sb.AppendLine("            {|") |> ignore

                        let emittedSubFields =
                            System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

                        for (_, rawTableName, t) in subTbls do
                            let propName = escapeIdent rawTableName

                            if emittedSubFields.Add(propName) then
                                let sqlTarget = formatTreeSqlTarget schema (Some subSchema) rawTableName t.Name

                                sb.AppendLine(
                                    $"                {propName} = conn.Table<{escapeIdent schema}.{subPropName}.{propName}>(\"{sqlTarget}\")"
                                )
                                |> ignore

                        sb.AppendLine("            |}") |> ignore
                        sb.AppendLine() |> ignore

        // Emit database / schema tree properties
        for (schema, entries) in schemaGroups do
            let directEntries =
                entries |> Seq.filter (fun (_, subOpt, _, _) -> subOpt.IsNone) |> Seq.toList

            let subSchemaEntries =
                entries
                |> Seq.choose (fun (_, subOpt, rawTbl, t) -> subOpt |> Option.map (fun s -> (s, rawTbl, t)))
                |> Seq.groupBy (fun (s, _, _) -> s)
                |> Seq.sortBy fst
                |> Seq.toList

            sb.AppendLine($"        member _.{escapeIdent schema} =") |> ignore
            sb.AppendLine("            {|") |> ignore

            let emittedRecordFields =
                System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

            for (subSchema, _) in subSchemaEntries do
                emittedRecordFields.Add(escapeIdent subSchema) |> ignore

            for (_, subOpt, rawTableName, t) in directEntries do
                let propName = escapeIdent rawTableName

                if emittedRecordFields.Add(propName) then
                    let sqlTarget = formatTreeSqlTarget schema subOpt rawTableName t.Name

                    sb.AppendLine(
                        $"                {propName} = conn.Table<{escapeIdent schema}.{propName}>(\"{sqlTarget}\")"
                    )
                    |> ignore

            for (subSchema, subTbls) in subSchemaEntries do
                if subSchema = "public" || subSchema = "dbo" || subSchema = "main" then
                    for (_, rawTableName, t) in subTbls do
                        let propName = escapeIdent rawTableName

                        if emittedRecordFields.Add(propName) then
                            let sqlTarget = formatTreeSqlTarget schema (Some subSchema) rawTableName t.Name

                            sb.AppendLine(
                                $"                {propName} = conn.Table<{escapeIdent schema}.{escapeIdent subSchema}.{propName}>(\"{sqlTarget}\")"
                            )
                            |> ignore

            for (subSchema, subTbls) in subSchemaEntries do
                let subPropName = escapeIdent subSchema
                sb.AppendLine($"                {subPropName} =") |> ignore
                sb.AppendLine("                    {|") |> ignore

                let emittedSubFields =
                    System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

                for (_, rawTableName, t) in subTbls do
                    let propName = escapeIdent rawTableName

                    if emittedSubFields.Add(propName) then
                        let sqlTarget = formatTreeSqlTarget schema (Some subSchema) rawTableName t.Name

                        sb.AppendLine(
                            $"                        {propName} = conn.Table<{escapeIdent schema}.{subPropName}.{propName}>(\"{sqlTarget}\")"
                        )
                        |> ignore

                sb.AppendLine("                    |}") |> ignore

            sb.AppendLine("            |}") |> ignore
            sb.AppendLine() |> ignore

        sb.AppendLine("    let database () : Database = Database()") |> ignore
        sb.AppendLine("    let db () : Database = Database()") |> ignore
        sb.AppendLine() |> ignore

        sb.ToString()


    let generateSqlProvider (connStr: string) (kind: ConnectorKind) (typeAlias: string option) : string =
        let alias = defaultArg typeAlias "Db"
        let dialect = SqlDialect.fromConnectorKind kind
        let vendor = dialect.Dialect.SqlProviderType

        $"open FSharp.Data.Sql\n\n[<Literal>]\nlet private connStr = \"{connStr}\"\n\ntype {alias} = SqlDataProvider<\n    DatabaseVendor = {vendor},\n    ConnectionString = connStr >\n\nlet db = {alias}.GetDataContext()\n"
