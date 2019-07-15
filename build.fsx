open System.Text.RegularExpressions
#r "paket: groupref build //"
#load "./.fake/build.fsx/intellisense.fsx"

#if !FAKE
#r "netstandard"
#r "Facades/netstandard" // https://github.com/ionide/ionide-vscode-fsharp/issues/839#issuecomment-396296095
#endif

open System

open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO
open Fake.IO.Globbing.Operators
open Fake.IO.FileSystemOperators

open System.IO

let project = "Gremlin.Net.CosmosDb"
let release = ReleaseNotes.load "RELEASE_NOTES.md"

let pack id =
    Shell.cleanDir <| sprintf "nuget/%s.%s" project id
    Paket.pack(fun p ->
        { p with
            Version = release.NugetVersion
            OutputPath = sprintf "nuget/%s.%s" project id
            TemplateFile = sprintf "src/%s.%s/%s.%s.fsproj.paket.template" project id project id
            MinimumFromLockFile = true
            IncludeReferencedProjects = false })

let publishPackage id =
    pack id
    Paket.push(fun p ->
        { p with 
            WorkingDir = sprintf "nuget/%s.%s" project id
            PublishUrl = "https://www.nuget.org/api/v2/package" })

module Util =

    let visitFile (visitor: string->string) (fileName : string) =
        File.ReadAllLines(fileName)
        |> Array.map (visitor)
        |> fun lines -> File.WriteAllLines(fileName, lines)

    let replaceLines (replacer: string->Match->string option) (reg: Regex) (fileName: string) =
        fileName |> visitFile (fun line ->
            let m = reg.Match(line)
            if not m.Success
            then line
            else
                match replacer line m with
                | None -> line
                | Some newLine -> newLine)


let runDotNet cmd workingDir =
    let result =
        DotNet.exec (DotNet.Options.withWorkingDirectory workingDir) cmd ""
    if result.ExitCode <> 0 then failwithf "'dotnet %s' failed in %s" cmd workingDir

let toPackageReleaseNotes (notes: string list) =
    String.Join("\n * ", notes)
    |> (fun txt -> txt.Replace("\"", "\\\""))

let createNuget (releaseNotes: ReleaseNotes.ReleaseNotes) (projFile: string) =
    let projDir = Path.GetDirectoryName(projFile)
    let result =
        DotNet.exec
            (DotNet.Options.withWorkingDirectory projDir)
            "pack"
            (sprintf "-c Release /p:PackageReleaseNotes=\"%s\"" (toPackageReleaseNotes releaseNotes.Notes))

    if not result.OK then failwithf "dotnet pack failed with code %i" result.ExitCode

let pushNuget (releaseNotes: ReleaseNotes.ReleaseNotes) (projFile: string) =
    let versionRegex = Regex("<Version>(.*?)</Version>", RegexOptions.IgnoreCase)
    let projDir = Path.GetDirectoryName(projFile)

    //if needsPublishing versionRegex releaseNotes projFile then
    (versionRegex, projFile)
    ||> Util.replaceLines (fun line _ ->
                                versionRegex.Replace(line, "<Version>"+releaseNotes.NugetVersion+"</Version>") |> Some)

    let nugetKey =
        match  Environment.environVarOrNone "NUGET_KEY" with
        | Some nugetKey -> nugetKey
        | None -> failwith "The Nuget API key must be set in a NUGET_KEY environmental variable"
    Directory.GetFiles(projDir </> "bin" </> "Release", "*.nupkg")
    |> Array.find (fun nupkg -> nupkg.Contains(releaseNotes.NugetVersion))
    |> (fun nupkg ->
        Paket.push (fun p -> { p with ApiKey = nugetKey
                                      WorkingDir = Path.getDirectory nupkg
                                      PublishUrl = "https://www.myget.org/F/danderegg2/"
                                      ToolPath = (Directory.GetCurrentDirectory() @@ ".paket/paket.exe")
                                       }))


Target.create "CreateNugets" (fun _ ->
    let proj = "src/Gremlin.Net.CosmosDb/Gremlin.Net.CosmosDb.csproj"
    createNuget release proj
    pushNuget release proj
)


open Fake.IO.Globbing.Operators


Target.create "Clean2" (fun _ ->
    !! "src/**/bin"
    ++ "src/**/obj"
    |> Shell.cleanDirs
)

Target.create "Build2" (fun _ ->
    !! "src/**/*.*proj"
    |> Seq.iter (DotNet.build id)
)

Target.create "All" ignore

"Clean2"
  ==> "Build2"
  ==> "All"

Target.runOrDefault "All"
