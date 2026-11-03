namespace FSharpQueryStudio.Fsac

open System
open System.Text.Json
open FSharpQueryStudio.Protocol

/// Parsers and domain mappers for converting raw LSP JSON payloads into FSharpQueryStudio protocol types.
module FsacParsing =

    let mapSeverity (severity: int option) : DiagnosticSeverity =
        match severity with
        | Some 1 -> DiagnosticSeverity.Error
        | Some 2 -> DiagnosticSeverity.Warning
        | _ -> DiagnosticSeverity.Information

    /// Parses textDocument/publishDiagnostics notifications into domain Diagnostic records,
    /// shifting line indices back to original un-prefixed document space.
    let parseDiagnostics (originalDocUri: string) (paramsEl: JsonElement) : Diagnostic list =
        paramsEl.GetProperty("diagnostics").EnumerateArray()
        |> Seq.map (fun d ->
            let range = d.GetProperty("range")
            let start = range.GetProperty("start")
            let end_ = range.GetProperty("end")

            let severity =
                match d.TryGetProperty("severity") with
                | true, s when s.ValueKind = JsonValueKind.Number -> Some(s.GetInt32())
                | _ -> None

            let code =
                match d.TryGetProperty("code") with
                | true, c when c.ValueKind = JsonValueKind.String -> Some(c.GetString())
                | true, c when c.ValueKind = JsonValueKind.Number -> Some(string (c.GetInt32()))
                | _ -> None

            { Message = d.GetProperty("message").GetString()
              Severity = mapSeverity severity
              StartLine = Prologue.shiftDownForDocument originalDocUri (start.GetProperty("line").GetInt32() + 1) - 1
              StartColumn = start.GetProperty("character").GetInt32()
              EndLine = Prologue.shiftDownForDocument originalDocUri (end_.GetProperty("line").GetInt32() + 1) - 1
              EndColumn = end_.GetProperty("character").GetInt32()
              Code = code })
        |> List.ofSeq

    let private elementText (el: JsonElement) : string =
        match el.ValueKind with
        | JsonValueKind.Object -> el.GetProperty("value").GetString()
        | JsonValueKind.String -> el.GetString()
        | _ -> ""

    let private contentsElements (contentsEl: JsonElement) : string list =
        match contentsEl.ValueKind with
        | JsonValueKind.Array -> contentsEl.EnumerateArray() |> Seq.map elementText |> List.ofSeq
        | _ -> [ elementText contentsEl ]

    let private isBoilerplateLine (line: string) : bool =
        line.StartsWith("<a href") || line.StartsWith("*Full name:") || line.StartsWith("*Assembly:")

    let private symbolKindPrefixes =
        [ "static member "
          "abstract member "
          "active pattern "
          "member "
          "property "
          "val "
          "type "
          "module "
          "namespace " ]

    let stripSymbolKind (signature: string) : string =
        match symbolKindPrefixes |> List.tryFind signature.StartsWith with
        | Some prefix -> signature.Substring(prefix.Length)
        | None -> signature

    /// Parses textDocument/hover markdown contents into structured DisplayName, Type, Signature and XmlDoc.
    let parseHoverMarkdown (contentsEl: JsonElement) : HoverInfo =
        let signatureElement, docElements =
            match contentsElements contentsEl with
            | [] -> "", []
            | first :: rest -> first, rest

        let signatureText =
            signatureElement.Replace("\r\n", "\n").Split('\n')
            |> Array.map (fun l -> l.Trim())
            |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))
            |> String.concat " "
            |> fun s -> s.Trim('`', ' ')

        let docLines =
            docElements
            |> List.collect (fun el -> el.Replace("\r\n", "\n").Split('\n') |> Array.toList)
            |> List.filter (fun l -> not (String.IsNullOrWhiteSpace l))
            |> List.filter (fun l -> not (isBoilerplateLine l))

        let strippedSignature = stripSymbolKind signatureText
        let nameTypeSplit = strippedSignature.Split([| ':' |], 2)

        let displayName =
            if nameTypeSplit.Length = 2 then nameTypeSplit.[0].Trim() else strippedSignature.Trim()

        let typeText =
            if nameTypeSplit.Length = 2 then nameTypeSplit.[1].Trim() else signatureText

        { DisplayName = displayName
          Type = typeText
          Signature = if signatureText.Contains("->") then Some signatureText else None
          XmlDoc = if docLines.IsEmpty then None else Some(docLines |> String.concat "\n") }

    /// Parses the first resolved location from a textDocument/definition result (Location or LocationLink).
    let parseFirstLocation (resultEl: JsonElement) : (string * JsonElement) option =
        match resultEl.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.Array when resultEl.GetArrayLength() = 0 -> None
        | JsonValueKind.Array ->
            let first = resultEl.[0]
            let rangeEl =
                match first.TryGetProperty("range") with
                | true, r -> r
                | false, _ -> first.GetProperty("targetRange")
            let uri =
                match first.TryGetProperty("uri") with
                | true, u -> u.GetString()
                | false, _ -> first.GetProperty("targetUri").GetString()
            Some(uri, rangeEl)
        | JsonValueKind.Object -> Some(resultEl.GetProperty("uri").GetString(), resultEl.GetProperty("range"))
        | _ -> None

    /// Splits arrow-delimited function signature text at top-level depth, respecting nested parentheses.
    let splitTopLevelArrows (text: string) : string list =
        let segments = ResizeArray<string>()
        let current = System.Text.StringBuilder()
        let mutable depth = 0
        let mutable i = 0

        while i < text.Length do
            if text.[i] = '(' then
                depth <- depth + 1
                current.Append(text.[i]) |> ignore
                i <- i + 1
            elif text.[i] = ')' then
                depth <- depth - 1
                current.Append(text.[i]) |> ignore
                i <- i + 1
            elif depth = 0 && i + 1 < text.Length && text.[i] = '-' && text.[i + 1] = '>' then
                segments.Add(current.ToString())
                current.Clear() |> ignore
                i <- i + 2
            else
                current.Append(text.[i]) |> ignore
                i <- i + 1

        segments.Add(current.ToString())
        segments |> Seq.map (fun s -> s.Trim()) |> List.ofSeq

    let simplifyTypeName (t: string) : string =
        let trimmed = t.Trim()
        match trimmed with
        | "System.String" | "String" -> "string"
        | "System.Int32" | "Int32" -> "int"
        | "System.Int64" | "Int64" -> "int64"
        | "System.Int16" | "Int16" -> "int16"
        | "System.Boolean" | "Boolean" -> "bool"
        | "System.Decimal" | "Decimal" -> "decimal"
        | "System.Double" | "Double" -> "float"
        | "System.Single" | "Single" -> "float32"
        | "System.Byte" | "Byte" -> "byte"
        | "System.DateTime" -> "DateTime"
        | "System.Guid" -> "Guid"
        | "System.Object" | "Object" -> "obj"
        | other -> other

    let extractTypeFromDetail (detail: string) : string =
        let clean =
            let idx = detail.LastIndexOf(':')
            if idx >= 0 && idx + 1 < detail.Length then
                let afterColon = detail.Substring(idx + 1).Trim()
                let withIdx = afterColon.IndexOf("with", StringComparison.OrdinalIgnoreCase)
                let withoutWith =
                    if withIdx > 0 then afterColon.Substring(0, withIdx).Trim()
                    else afterColon
                withoutWith
            else
                detail.Trim()
        let finalType = if String.IsNullOrWhiteSpace clean then detail else clean
        simplifyTypeName finalType

    let parseItem (el: JsonElement) : AnalysisCompletionItem option =
        let labelOpt =
            match el.TryGetProperty("label") with
            | true, l when l.ValueKind = JsonValueKind.String -> Some(l.GetString())
            | true, l when l.ValueKind = JsonValueKind.Object ->
                match l.TryGetProperty("label") with
                | true, inner when inner.ValueKind = JsonValueKind.String -> Some(inner.GetString())
                | _ -> None
            | _ -> None

        match labelOpt with
        | None -> None
        | Some label ->
            let kindOpt =
                match el.TryGetProperty("kind") with
                | true, k when k.ValueKind = JsonValueKind.Number -> Some(k.GetInt32())
                | _ -> None

            let rawDetailOpt =
                match el.TryGetProperty("detail") with
                | true, d when d.ValueKind = JsonValueKind.String ->
                    let s = d.GetString()
                    if String.IsNullOrWhiteSpace s then None else Some(s.Trim())
                | _ ->
                    match el.TryGetProperty("label") with
                    | true, l when l.ValueKind = JsonValueKind.Object ->
                        match l.TryGetProperty("description") with
                        | true, desc when desc.ValueKind = JsonValueKind.String ->
                            let s = desc.GetString()
                            if String.IsNullOrWhiteSpace s then None else Some(s.Trim())
                        | _ -> None
                    | _ -> None

            let detail =
                match rawDetailOpt with
                | Some rawDetail ->
                    match kindOpt with
                    | Some 5 | Some 10 | Some 6 | Some 12 ->
                        Some(extractTypeFromDetail rawDetail)
                    | Some 2 ->
                        Some "method"
                    | _ ->
                        if rawDetail.Contains(":") then
                            Some(extractTypeFromDetail rawDetail)
                        else
                            Some rawDetail
                | None ->
                    match kindOpt with
                    | Some 1 -> Some "text"
                    | Some 2 -> Some "method"
                    | Some 3 -> Some "function"
                    | Some 4 -> Some "constructor"
                    | Some 5 -> Some "field"
                    | Some 6 -> Some "variable"
                    | Some 7 -> Some "class"
                    | Some 8 -> Some "interface"
                    | Some 9 -> Some "module"
                    | Some 10 -> Some "property"
                    | Some 11 -> Some "unit"
                    | Some 12 -> Some "value"
                    | Some 13 -> Some "enum"
                    | Some 14 -> Some "keyword"
                    | Some 15 -> Some "snippet"
                    | Some 21 -> Some "constant"
                    | Some 22 -> Some "struct"
                    | Some 23 -> Some "event"
                    | Some 24 -> Some "operator"
                    | Some 25 -> Some "type"
                    | _ -> None
            Some { Label = label; Detail = detail }

    /// Parses completion results from raw JSON response element, removing duplicates.
    let parseCompletionItems (resultEl: JsonElement) : AnalysisCompletionItem list =
        let raw =
            match resultEl.ValueKind with
            | JsonValueKind.Null -> []
            | JsonValueKind.Array ->
                resultEl.EnumerateArray()
                |> Seq.choose parseItem
                |> List.ofSeq
            | JsonValueKind.Object ->
                match resultEl.TryGetProperty("items") with
                | true, itemsEl when itemsEl.ValueKind = JsonValueKind.Array ->
                    itemsEl.EnumerateArray()
                    |> Seq.choose parseItem
                    |> List.ofSeq
                | _ -> []
            | _ -> []

        raw |> List.distinctBy (fun i -> i.Label)

