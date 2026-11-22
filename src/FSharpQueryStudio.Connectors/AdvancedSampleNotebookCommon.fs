namespace FSharpQueryStudio.Connectors

open System
open System.Text.Encodings.Web
open System.Text.Json

module AdvancedSampleNotebookCommon =

    type NotebookCell =
        {
            kind: string
            language: string option
            value: string
        }

    let mkMarkdown (text: string) : NotebookCell =
        {
            kind = "markdown"
            language = None
            value = text.Trim()
        }

    let mkCode (code: string) : NotebookCell =
        {
            kind = "code"
            language = Some "fsharp"
            value = code.Trim()
        }

    let serializeNotebook (cells: NotebookCell list) : string =
        let mapped =
            cells
            |> List.map (fun c ->
                let dict = System.Collections.Generic.Dictionary<string, obj>()
                dict.["kind"] <- c.kind

                match c.language with
                | Some l -> dict.["language"] <- l
                | None -> ()

                dict.["value"] <- c.value
                dict)

        let root = System.Collections.Generic.Dictionary<string, obj>()
        root.["version"] <- 1
        root.["cells"] <- mapped
        root.["metadata"] <- System.Collections.Generic.Dictionary<string, obj>()

        let options =
            JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

        JsonSerializer.Serialize(root, options) + "\n"
