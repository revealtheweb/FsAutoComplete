module FsAutoComplete.Workspace

open ProjectRecognizer
open System.IO

#if NO_PROJECTCRACKER
#else
let private deduplicateReferences (opts: FSharp.Compiler.SourceCodeServices.FSharpProjectOptions, projectFiles, logMap) =
    let projs =
        opts.ReferencedProjects |> Array.map fst

    let references =
        opts.OtherOptions
        |> Array.choose (fun n -> if n.StartsWith "-r:" then Some (n.Substring(3)) else None)
        |> Array.groupBy (Path.GetFullPathSafe)
        |> Array.map (fun (_,lst) ->
            match lst |> Array.tryFind (fun n -> projs |> Array.contains n) with
            | Some s -> s
            | None -> Array.head lst )

    let oos = [|
        yield! (opts.OtherOptions |> Array.filter (fun n -> not (n.StartsWith "-r:")))
        yield! (references |> Array.map (sprintf "-r:%s"))
    |]
    let opts = {opts with OtherOptions = oos}
    opts, projectFiles, logMap

let private removeDeprecatedArgs (opts: FSharp.Compiler.SourceCodeServices.FSharpProjectOptions, projectFiles, logMap) =
    let oos = opts.OtherOptions |> Array.filter (fun n -> n <> "--times" && n <> "--no-jit-optimize")
    let opts = {opts with OtherOptions = oos}
    opts, projectFiles, logMap
#endif

let getProjectOptions notifyState (loader: Dotnet.ProjInfo.Workspace.Loader, fcsBinder: Dotnet.ProjInfo.Workspace.FCS.FCSBinder) verbose (projectFileName: SourceFilePath) =
    if not (File.Exists projectFileName) then
        Error (GenericError(projectFileName, sprintf "File '%s' does not exist" projectFileName))
    else

        let loadProj projectPath =
            loader.LoadProjects [projectPath]

            match fcsBinder.GetProjectOptions (projectPath) with
            | Some po ->
                Result.Ok (po, List.ofArray po.SourceFiles, Map.empty)
            | None -> 
                Error (GenericError(projectFileName, (sprintf "Project file '%s' parsing failed" projectFileName)))

        match projectFileName with
        | NetCoreProjectJson ->
            ProjectCrackerProjectJson.load projectFileName
        | NetCoreSdk ->
            loadProj projectFileName
        | FSharpNetSdk ->
            Error (GenericError(projectFileName, (sprintf "Project file '%s' using FSharp.NET.Sdk not supported" projectFileName)))
#if NO_PROJECTCRACKER
        | Net45 ->
            loadProj projectFileName
        | Unsupported ->
            Error (GenericError(projectFileName, (sprintf "Project file '%s' not supported" projectFileName)))
#else
        | Net45
        | Unsupported ->
            ProjectCrackerVerbose.load notifyState FSharpCompilerServiceCheckerHelper.ensureCorrectFSharpCore projectFileName verbose
            |> Result.map deduplicateReferences
            |> Result.map removeDeprecatedArgs
#endif

let private bindExtraOptions (opts: FSharp.Compiler.SourceCodeServices.FSharpProjectOptions, projectFiles, logMap) =
    match opts.ExtraProjectInfo with
    | None ->
        Error (GenericError(opts.ProjectFileName, "expected ExtraProjectInfo after project parsing, was None"))
    | Some x ->
        match x with
        | :? ExtraProjectInfoData as extraInfo ->
            Ok (opts, extraInfo, projectFiles, logMap)
        | x ->
            Error (GenericError(opts.ProjectFileName, (sprintf "expected ExtraProjectInfo after project parsing, was %A" x)))

let private parseProject' onLoaded (loader, fcsBinder) verbose projectFileName =
    projectFileName
    |> getProjectOptions onLoaded (loader, fcsBinder) verbose
    |> Result.bind bindExtraOptions

let parseProject (loader, fcsBinder) verbose projectFileName =
    projectFileName
    |> parseProject' ignore (loader, fcsBinder) verbose

let loadInBackground onLoaded (loader, fcsBinder) verbose (projects: Project list) = async {

    projects
    |> List.iter(fun project ->
        match project.Response with
        | Some res ->
            onLoaded (WorkspaceProjectState.Loaded (res.Options, res.ExtraInfo, res.Files, res.Log))
        | None ->
            project.FileName
            |> parseProject' onLoaded (loader, fcsBinder) verbose
            |> function
            | Ok (opts, extraInfo, projectFiles, logMap) ->
                    onLoaded (WorkspaceProjectState.Loaded (opts, extraInfo, projectFiles, logMap))
            | Error error ->
                    onLoaded (WorkspaceProjectState.Failed (project.FileName, error))
    )
    }
