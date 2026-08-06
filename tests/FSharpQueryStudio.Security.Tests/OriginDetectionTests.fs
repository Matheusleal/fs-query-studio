module OriginDetectionTests

open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

let private downloadsDir = @"C:\Users\someone\Downloads"

[<Fact>]
let ``CreatedFresh detects as Local`` () =
    Assert.Equal(ScriptOrigin.Local, OriginDetection.detectOrigin downloadsDir WorkspaceOpenSource.CreatedFresh)

[<Fact>]
let ``ExternalPath outside the downloads folder detects as Imported`` () =
    let source = WorkspaceOpenSource.ExternalPath @"D:\shared-drive\some-workspace"

    Assert.Equal(ScriptOrigin.Imported, OriginDetection.detectOrigin downloadsDir source)

[<Fact>]
let ``ExternalPath under the downloads folder detects as Downloaded`` () =
    let source = WorkspaceOpenSource.ExternalPath @"C:\Users\someone\Downloads\some-workspace"

    Assert.Equal(ScriptOrigin.Downloaded, OriginDetection.detectOrigin downloadsDir source)

[<Fact>]
let ``ExternalPath under the downloads folder is case-insensitive`` () =
    let source = WorkspaceOpenSource.ExternalPath @"c:\users\someone\downloads\some-workspace"

    Assert.Equal(ScriptOrigin.Downloaded, OriginDetection.detectOrigin downloadsDir source)

[<Fact>]
let ``MarkedShared detects as SharedWorkspace`` () =
    Assert.Equal(ScriptOrigin.SharedWorkspace, OriginDetection.detectOrigin downloadsDir WorkspaceOpenSource.MarkedShared)
