namespace Microsoft.FSharp.Compiler.SourceCodeServices
open MonoDevelop.Core
open System.Threading.Tasks

[<AutoOpen>]
module Misc =
    let orElse ifNone option = match option with None -> ifNone | Some _ -> option

module CommonRoslynHelpers =
    let StartAsyncAsTask cancellationToken computation =
        let computation =
            async {
                //try
                return! computation
                //with e ->
                //    LoggingService.LogError("Exception", e)
                //    return Unchecked.defaultof<_>
            }
        Async.StartAsTask(computation, TaskCreationOptions.None, cancellationToken)

    let StartAsyncUnitAsTask cancellationToken (computation:Async<unit>) = 
        StartAsyncAsTask cancellationToken computation  :> Task