module FsProjTests

open System
open System.IO
open Xunit
open FSharpQueryStudio.Storage

let private tempProjectDir () =
    let dir = Path.Combine(Path.GetTempPath(), "fqs-fsproj-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

let private writeFsproj (dir: string) (xml: string) =
    let path = Path.Combine(dir, "Project.fsproj")
    File.WriteAllText(path, xml)
    path

[<Fact>]
let ``load resolves Compile items from a single ItemGroup in document order`` () =
    let dir = tempProjectDir ()

    let path =
        writeFsproj
            dir
            """<Project Sdk="Microsoft.NET.Sdk">
                 <ItemGroup>
                   <Compile Include="Types.fs" />
                   <Compile Include="Helpers.fs" />
                   <Compile Include="Program.fs" />
                 </ItemGroup>
               </Project>"""

    let project = FsProj.load path

    Assert.Equal<string list>([ "Types.fs"; "Helpers.fs"; "Program.fs" ], project.Files)
    Assert.Equal(path, project.ProjectPath)
    Assert.Equal(dir, project.ProjectDirectory)

[<Fact>]
let ``load preserves overall document order across multiple ItemGroups`` () =
    let dir = tempProjectDir ()

    let path =
        writeFsproj
            dir
            """<Project Sdk="Microsoft.NET.Sdk">
                 <ItemGroup>
                   <Compile Include="Types.fs" />
                 </ItemGroup>
                 <ItemGroup>
                   <Compile Include="Helpers.fs" />
                   <Compile Include="Program.fs" />
                 </ItemGroup>
               </Project>"""

    let project = FsProj.load path

    Assert.Equal<string list>([ "Types.fs"; "Helpers.fs"; "Program.fs" ], project.Files)

[<Fact>]
let ``load ignores non-Compile items such as PackageReference and None`` () =
    let dir = tempProjectDir ()

    let path =
        writeFsproj
            dir
            """<Project Sdk="Microsoft.NET.Sdk">
                 <ItemGroup>
                   <Compile Include="Program.fs" />
                   <None Include="README.md" />
                 </ItemGroup>
                 <ItemGroup>
                   <PackageReference Include="FSharp.Data" Version="6.3.0" />
                 </ItemGroup>
               </Project>"""

    let project = FsProj.load path

    Assert.Equal<string list>([ "Program.fs" ], project.Files)

[<Fact>]
let ``load parses a legacy-style project using the MSBuild 2003 XML namespace`` () =
    let dir = tempProjectDir ()

    let path =
        writeFsproj
            dir
            """<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                 <ItemGroup>
                   <Compile Include="Types.fs" />
                   <Compile Include="Program.fs" />
                 </ItemGroup>
               </Project>"""

    let project = FsProj.load path

    Assert.Equal<string list>([ "Types.fs"; "Program.fs" ], project.Files)

[<Fact>]
let ``load returns an empty file list for a project with no Compile items`` () =
    let dir = tempProjectDir ()
    let path = writeFsproj dir """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup /></Project>"""

    let project = FsProj.load path

    Assert.Empty(project.Files)

[<Fact>]
let ``resolvedFilePaths joins each file onto the project's directory as an absolute path`` () =
    let dir = tempProjectDir ()

    let path =
        writeFsproj
            dir
            """<Project Sdk="Microsoft.NET.Sdk">
                 <ItemGroup>
                   <Compile Include="src/Types.fs" />
                   <Compile Include="Program.fs" />
                 </ItemGroup>
               </Project>"""

    let project = FsProj.load path
    let resolved = FsProj.resolvedFilePaths project

    Assert.Equal<string list>(
        [ Path.GetFullPath(Path.Combine(dir, "src/Types.fs")); Path.GetFullPath(Path.Combine(dir, "Program.fs")) ],
        resolved
    )
