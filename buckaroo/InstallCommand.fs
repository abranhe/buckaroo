module Buckaroo.InstallCommand

open System
open System.IO
open FSharpx.Control
open Buckaroo
open Buckaroo.BuckConfig
open Buckaroo.Tasks
open Buckaroo.Console
open Buckaroo.RichOutput

type Logger =
  {
    Info : string -> Unit
    Success : string -> Unit
    Trace : string -> Unit
    Warning : string -> Unit
    Error : string -> Unit
  }

let private createLogger (console : ConsoleManager) =
  let prefix =
    "info "
    |> text
    |> foreground ConsoleColor.Blue

  let info (x : string) =
    console.Write (prefix + x, LoggingLevel.Info)

  let prefix =
    "success "
    |> text
    |> foreground ConsoleColor.Green

  let success (x : string) =
    console.Write (prefix + x, LoggingLevel.Info)

  let trace (x : string) =
    console.Write (x, LoggingLevel.Trace)

  let prefix =
    "warning "
    |> text
    |> foreground ConsoleColor.Yellow

  let warning (x : string) =
    console.Write (prefix + x, LoggingLevel.Info)

  let prefix =
    "error "
    |> text
    |> foreground ConsoleColor.Red

  let error (x : string) =
    console.Write (prefix + x, LoggingLevel.Info)

  {
    Info = info;
    Success = success;
    Trace = trace;
    Warning = warning;
    Error = error;
  }

let private fetchManifestFromLock (lock : Lock) (sourceExplorer : ISourceExplorer) (package : PackageIdentifier) = async {
  let location =
    match lock.Packages |> Map.tryFind package with
    | Some lockedPackage -> (lockedPackage.Location, lockedPackage.Versions)
    | None ->
      new Exception("Lock file does not contain " + (PackageIdentifier.show package))
      |> raise

  return! sourceExplorer.FetchManifest location
}

let private fetchDependencyTargets (lock : Lock) (sourceExplorer : ISourceExplorer) (manifest : Manifest) = async {
  let! targetIdentifiers =
    manifest.Dependencies
    |> Seq.map (fun d -> async {
      let! targets =
        match d.Targets with
        | Some targets -> async {
            return targets |> List.toSeq
          }
        | None -> async {
            let! manifest = fetchManifestFromLock lock sourceExplorer d.Package
            return manifest.Targets |> Set.toSeq
          }

      return
        targets
        |> Seq.map (fun target ->
          { Package = d.Package; Target = target }
        )
    })
    |> Async.Parallel

  return
    targetIdentifiers
    |> Seq.collect id
    |> Seq.toList
}

let private buckarooMacros =
  [
    "def buckaroo_cell(package): ";
    "  cell = native.read_config('buckaroo', package, '').strip()";
    "  if cell == '': ";
    "    fail('Buckaroo does not have \"' + package + '\" installed. ')";
    "  return cell";
    "";
    "def buckaroo_deps(): ";
    "  raw = native.read_config('buckaroo', 'dependencies', '').split(' ')";
    "  return [ x.strip() for x in raw if len(x.strip()) > 0 ]";
    "";
    "def buckaroo_deps_from_package(package): ";
    "  cell = buckaroo_cell(package)";
    "  all_deps = buckaroo_deps()";
    "  return [ x for x in all_deps if x.startswith(cell) ]";
    "";
  ]
  |> String.concat "\n"

let rec computeCellIdentifier (parents : PackageIdentifier list) (package : PackageIdentifier) =
  match parents with
  | [] ->
    (
      match package with
      | PackageIdentifier.GitHub gitHub ->
        [ "buckaroo"; "github"; gitHub.Owner; gitHub.Project ]
      | PackageIdentifier.BitBucket bitBucket ->
        [ "buckaroo"; "bitbucket"; bitBucket.Owner; bitBucket.Project ]
      | PackageIdentifier.GitLab gitLab ->
        [ "buckaroo"; "gitlab"; gitLab.Owner; gitLab.Project ]
      | PackageIdentifier.Adhoc adhoc ->
        [ "buckaroo"; "adhoc"; adhoc.Owner; adhoc.Project ]
    )
    |> String.concat "."
  | head::tail ->
    (computeCellIdentifier tail head) + "." + (computeCellIdentifier [] package)

let rec packageInstallPath (parents : PackageIdentifier list) (package : PackageIdentifier) =
  match parents with
  | [] ->
    let (prefix, owner, project) =
      match package with
        | PackageIdentifier.GitHub x -> ("github", x.Owner, x.Project)
        | PackageIdentifier.BitBucket x -> ("bitbucket", x.Owner, x.Project)
        | PackageIdentifier.GitLab x -> ("gitlab", x.Owner, x.Project)
        | PackageIdentifier.Adhoc x -> ("adhoc", x.Owner, x.Project)
    Path.Combine(".", Constants.PackagesDirectory, prefix, owner, project)
  | head::tail ->
    Path.Combine(packageInstallPath tail head, packageInstallPath [] package)
    |> Paths.normalize


let getReceiptPath installPath = installPath + ".receipt.toml"

let writeReceipt (installPath : string) (packageLock : PackageLock) = async {
  let receipt =
    "last_updated = " + (Toml.formatDateTime <| System.DateTime.Now.ToUniversalTime()) + "\n" +
    match packageLock with
    | PackageLock.Git g ->
      "git = \"" + g.Url + "\"\n" +
      "revision = \"" + g.Revision + "\"\n"
    | PackageLock.Http (http, sha256) ->
      "url = \"" + http.Url + "\"\n" +
      "sha256 = \"" + (sha256) + "\"\n" +
      (
        http.StripPrefix
        |> Option.map (fun x -> "strip_prefix = \"" + x + "\"\n")
        |> Option.defaultValue("")
      ) +
      (
        http.Type
        |> Option.map (fun x -> "archive_type = \"" + (string x) + "\"\n")
        |> Option.defaultValue ""
      )
    | GitHub g ->
      let package = PackageIdentifier.GitHub g.Package
      "package = \"" + (PackageIdentifier.show package) + "\"\n" +
      "revision = \"" + g.Revision + "\"\n"
    | GitLab g ->
      let package = PackageIdentifier.GitHub g.Package
      "package = \"" + (PackageIdentifier.show package) + "\"\n" +
      "revision = \"" + g.Revision + "\"\n"
    | BitBucket b ->
      let package = PackageIdentifier.GitHub b.Package
      "package = \"" + (PackageIdentifier.show package) + "\"\n" +
      "revision = \"" + b.Revision + "\"\n"

  let receiptPath = getReceiptPath installPath
  do! Files.writeFile receiptPath receipt
}

let private compareReceipt logger installPath location = async {
  let receiptPath = getReceiptPath installPath

  if File.Exists receiptPath
  then
    logger.Trace("Reading receipt at " + receiptPath + "...")

    let! content = Files.readFile receiptPath
    let maybePackageLocation =
      content
      |> Toml.parse
      |> Result.mapError Toml.TomlError.show
      |> Result.bind PackageLock.fromToml

    match maybePackageLocation with
    | Result.Ok receiptLocation ->
      if receiptLocation = location
      then
        return true
      else
        logger.Info("The receipt at " + installPath + " is outdated. ")

        return false
    | Result.Error error ->
      logger.Trace(error)
      logger.Warning("Invalid receipt at " + receiptPath + "; it will be deleted. ")

      do! Files.delete receiptPath

      return false
  else
    logger.Info("No receipt found for " + installPath + "; it will be installed. ")

    return false
}

let installPackageSources (context : Tasks.TaskContext) (installPath : string) (location : PackageLock) (versions : Set<Version>) = async {
  let logger = createLogger context.Console

  let downloadManager = context.DownloadManager
  let gitManager = context.GitManager
  let! isSame = compareReceipt logger installPath location

  if isSame
  then
    logger.Success (installPath + " is already up-to-date. ")
  else
    logger.Info ("Installing " + installPath + "... ")
    let installFromGit url revision versions = async {
      let hint =
        versions
          |> Set.toSeq
          |> Seq.choose(fun v ->
            match v with
            | Version.Git (GitVersion.Branch hint) -> Some hint
            | _ -> None)
          |> Seq.sort
          |> Seq.tryHead

      do! gitManager.FindCommit url revision hint
      do! gitManager.CopyFromCache url revision installPath
    }

    match location with
    | GitHub gitHub ->
      let url = PackageLocation.gitHubUrl gitHub.Package
      do! installFromGit url gitHub.Revision versions
    | BitBucket bitBucket ->
      let url = PackageLocation.bitBucketUrl bitBucket.Package
      do! installFromGit url bitBucket.Revision versions
    | PackageLock.Git git ->
      do! installFromGit git.Url git.Revision versions
    | GitLab gitLab ->
      let url = PackageLocation.gitLabUrl gitLab.Package
      do! installFromGit url gitLab.Revision versions
    | PackageLock.Http (http, sha256) ->
      let! pathToCache = downloadManager.DownloadToCache http.Url
      let! discoveredHash = Files.sha256 pathToCache
      if discoveredHash <> sha256
      then
        return
          new Exception("Hash mismatch for " + http.Url + "! Expected " + sha256 + "but found " + discoveredHash)
          |> raise
      do! Files.deleteDirectoryIfExists installPath |> Async.Ignore
      do! Files.mkdirp installPath
      do! Archive.extractTo pathToCache installPath http.StripPrefix
    logger.Info ("Writing an installation receipt for " + installPath + "... ")
    do! writeReceipt installPath location
    logger.Success ("Installed " + installPath)
}

let private generateBuckConfig (sourceExplorer : ISourceExplorer) (parents : PackageIdentifier list) lockedPackage packages (pathToCell : string) = async {
  let pathToPackage (package : PackageIdentifier) =
    Path.Combine(
      (".." + (string Path.DirectorySeparatorChar)) |> String.replicate (Paths.depth pathToCell),
      (packageInstallPath [] package))
    |> Paths.normalize

  let pathToPrivatePackage (package : PackageIdentifier) =
    packageInstallPath [] package
    |> Paths.normalize

  let! manifest = sourceExplorer.FetchManifest (lockedPackage.Location, lockedPackage.Versions)

  let repositoriesCells =
    packages
    |> Map.toSeq
    |> Seq.filter (fun (k, _) -> manifest.Dependencies |> Seq.exists (fun d -> d.Package = k))
    |> Seq.map (fun (k, _) -> (computeCellIdentifier (parents |> List.tail) k, pathToPackage k |> INIString))
    |> Map.ofSeq

  let privateRepositoriesCells =
    lockedPackage.PrivatePackages
    |> Map.toSeq
    |> Seq.filter (fun (k, _) -> manifest.PrivateDependencies |> Seq.exists (fun d -> d.Package = k))
    |> Seq.map (fun (k, _) -> (computeCellIdentifier parents k, pathToPrivatePackage k |> INIString))
    |> Map.ofSeq

  let! targets =
    manifest.Dependencies
    |> Seq.map (fun dependency -> async {
      let lockedPackage =
        packages
        |> Map.find dependency.Package

      let! packageManifest = sourceExplorer.FetchManifest (lockedPackage.Location, lockedPackage.Versions)

      let targets =
        dependency.Targets
        |> Option.defaultValue (List.ofSeq packageManifest.Targets)

      return (computeCellIdentifier (parents |> List.tail) dependency.Package, targets)
    })
    |> Async.Parallel

  let! privateTargets =
    manifest.PrivateDependencies
    |> Seq.map (fun dependency -> async {
      let lockedPackage =
        lockedPackage.PrivatePackages
        |> Map.find dependency.Package

      let! packageManifest = sourceExplorer.FetchManifest (lockedPackage.Location, lockedPackage.Versions)

      let targets =
        dependency.Targets
        |> Option.defaultValue (List.ofSeq packageManifest.Targets)

      return (computeCellIdentifier parents dependency.Package, targets)
    })
    |> Async.Parallel

  let dependencies =
    targets
    |> Seq.append privateTargets
    |> Seq.collect (fun (p, ts) -> ts |> Seq.map (fun t -> (p, t)))
    |> Seq.distinct
    |> Seq.map (fun (cell, target) -> cell + (Target.show target))
    |> String.concat " "
    |> INIString

  let repositoriesSection =
    repositoriesCells
    |> Map.toSeq
    |> Seq.append (privateRepositoriesCells |> Map.toSeq)
    |> Map.ofSeq

  let buckarooSectionPublic =
    manifest.Dependencies
    |> Seq.map (fun d -> d.Package)
    |> Seq.map (fun package -> (PackageIdentifier.show package, computeCellIdentifier (List.tail parents) package |> INIString))

  let buckarooSectionPrivate =
    lockedPackage.PrivatePackages
    |> Map.toSeq
    |> Seq.map fst
    |> Seq.map (fun package -> (PackageIdentifier.show package, computeCellIdentifier parents package |> INIString))

  let buckarooSection =
    buckarooSectionPublic
    |> Seq.append buckarooSectionPrivate
    |> Seq.append [ ("dependencies", dependencies) ]
    |> Seq.distinct
    |> Map.ofSeq

  let buckarooConfig =
    Map.empty
    |> Map.add "repositories" repositoriesSection
    |> Map.add "buckaroo" buckarooSection

  return buckarooConfig |> BuckConfig.render
}

let rec private installPackages (context : Tasks.TaskContext) (root : string) (parents : PackageIdentifier list) (packages : Map<PackageIdentifier, LockedPackage>) = async {
  // Prepare workspace
  do! Files.mkdirp root
  do! Files.touch (Path.Combine(root, ".buckconfig"))

  if File.Exists (Path.Combine(root, Constants.BuckarooMacrosFileName)) |> not
  then
    do! Files.writeFile (Path.Combine(root, Constants.BuckarooMacrosFileName)) buckarooMacros

  // Install packages
  for (package, lockedPackage) in packages |> Map.toSeq do
    let installPath =
      Path.Combine(root, packageInstallPath [] package)
      |> Paths.normalize

    let childParents = (parents @ [ package ])

    // Install child package sources
    do! installPackageSources context installPath lockedPackage.Location lockedPackage.Versions

    // Install child's child (recurse)
    do! installPackages context installPath childParents lockedPackage.PrivatePackages

    // Write .buckconfig.d for child package
    let! buckarooConfig =
      generateBuckConfig context.SourceExplorer childParents lockedPackage (packages |> Map.remove package) installPath

    do! Files.mkdirp (Path.Combine(installPath, ".buckconfig.d"))
    do! Files.writeFile (Path.Combine(installPath, ".buckconfig.d", ".buckconfig.buckaroo")) buckarooConfig
}

let rec private computeNestedPackages (parents : PackageIdentifier list) packages =
  packages
  |> Map.toSeq
  |> Seq.collect (fun (k, v) -> seq {
    yield (parents, k)
    yield! computeNestedPackages (parents @ [ k ]) v.PrivatePackages
  })

let writeTopLevelFiles (context : Tasks.TaskContext) (root : string) (lock : Lock) = async {
  let nestedPackages =
    computeNestedPackages [] lock.Packages
    |> Seq.distinct
    |> Seq.toList

  let repositoriesSection =
    nestedPackages
    |> Seq.map (fun (parents, x) ->
      (
        computeCellIdentifier parents x,
        packageInstallPath parents x |> Paths.normalize |> INIString
      )
    )
    |> Map.ofSeq

  let buckarooSection =
    lock.Packages
    |> Map.toSeq
    |> Seq.map (fun (k, _) -> (PackageIdentifier.show k, INIString (computeCellIdentifier [] k)))
    |> Seq.append [
      (
        "dependencies",
        (
          lock.Dependencies
          |> Seq.map (fun d -> (computeCellIdentifier [] d.Package) + (Target.show d.Target))
          |> String.concat " "
          |> INIString
        )
      );
    ]
    |> Map.ofSeq

  let config =
    Map.empty
    |> Map.add "buckaroo" buckarooSection
    |> Map.add "repositories" repositoriesSection

  do! Files.mkdirp (Path.Combine (root, ".buckconfig.d"))
  do! Files.writeFile (Path.Combine (root, ".buckconfig.d", ".buckconfig.buckaroo")) (BuckConfig.render config)
}

let task (context : Tasks.TaskContext) = async {
  let logger = createLogger context.Console

  logger.Info "Installing packages..."

  let! lock = Tasks.readLock

  do! installPackages context "." [] lock.Packages
  do! writeTopLevelFiles context "." lock

  logger.Success "The packages folder is now up-to-date. "

  return ()
}
