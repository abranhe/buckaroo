namespace Buckaroo

type SemVer = { Major : int; Minor : int; Patch : int; Increment : int }

module SemVer =

  open System
  open FParsec

  let zero : SemVer = { Major = 0; Minor = 0; Patch = 0; Increment = 0 }

  let create (major, minor, patch, increment) =
    {
      zero with
        Major = major;
        Minor = minor;
        Patch = patch;
        Increment = increment;
    }

  let compare (x : SemVer) (y : SemVer) =
    match x.Major.CompareTo y.Major with
    | 0 ->
      match x.Minor.CompareTo y.Minor with
      | 0 ->
        match x.Patch.CompareTo y.Patch with
        | 0 -> x.Increment.CompareTo y.Increment
        | c -> c
      | c -> c
    | c -> c

  let show (x : SemVer) : string =
    let elements =
      if x.Increment = 0
      then [ x.Major; x.Minor; x.Patch ]
      else [ x.Major; x.Minor; x.Patch; x.Increment ]
    elements
      |> Seq.map string
      |> String.concat "."

  let ToString (x : SemVer) = show x

  let integerParser = parse {
    let! digits = CharParsers.digit |> Primitives.many1
    let inline charToInt c = int c - int '0'
    return
      digits
      |> Seq.map charToInt
      |> Seq.fold (fun acc elem -> acc * 10 + elem) 0
  }

  let parser : Parser<SemVer, unit> = parse {
    do! CharParsers.spaces
    do! CharParsers.skipString "v" <|> CharParsers.skipString "V" |> Primitives.optional
    let! major = integerParser
    let segment = parse {
      do! CharParsers.skipString "."
      return! integerParser
    }
    let! minor = segment |> Primitives.opt
    let! patch = segment |> Primitives.opt
    let! increment = segment |> Primitives.opt

    return {
      Major = major;
      Minor = minor |> Option.defaultValue 0;
      Patch = patch |> Option.defaultValue 0;
      Increment = increment |> Option.defaultValue 0
    }
  }

  let private onlySemVerParser = parse {
    do! CharParsers.spaces

    let! semVer = parser

    do! CharParsers.spaces
    do! CharParsers.eof

    return semVer
  }

  let parse (x : string) : Result<SemVer, String> =
    match run onlySemVerParser x with
    | Success(result, _, _) -> Result.Ok result
    | Failure(errorMsg, _, _) -> Result.Error errorMsg
