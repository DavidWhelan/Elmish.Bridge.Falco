namespace Elmish.Bridge

open Falco.Routing

module Falco =
    open System
    open Falco
    open System.Net.WebSockets
    open Microsoft.AspNetCore.Builder
    open Microsoft.AspNetCore.Http
    open System.Threading

    let createHandler inboxCreator : HttpHandler =
        fun (ctx: HttpContext) ->
            task {
                if ctx.WebSockets.IsWebSocketRequest then
                    let! webSocket = ctx.WebSockets.AcceptWebSocketAsync()

                    let (sender, closer) =
                        inboxCreator (fun (s: string) ->
                            let resp = s |> System.Text.Encoding.UTF8.GetBytes |> ArraySegment

                            task {
                                if
                                    webSocket.State = WebSocketState.Open
                                    || webSocket.State = WebSocketState.CloseReceived
                                then
                                    do!
                                        webSocket.SendAsync(
                                            resp,
                                            WebSocketMessageType.Text,
                                            true,
                                            CancellationToken.None
                                        )
                            }
                            |> Async.AwaitTask)

                    let skt =
                        task {
                            let buffer = Array.zeroCreate 4096
                            let mutable loop = true
                            let mutable frame = []

                            while loop do
                                let! msg = webSocket.ReceiveAsync(ArraySegment(buffer), CancellationToken.None)

                                match
                                    msg.MessageType, buffer.[0 .. msg.Count - 1], msg.EndOfMessage, msg.CloseStatus
                                with
                                | _, _, _, s when s.HasValue ->
                                    do!
                                        webSocket.CloseOutputAsync(
                                            WebSocketCloseStatus.NormalClosure,
                                            null,
                                            CancellationToken.None
                                        )

                                    loop <- false
                                | WebSocketMessageType.Text, data, complete, _ ->

                                    if complete then
                                        match frame with
                                        | [] -> data
                                        | xs ->
                                            frame <- []
                                            (data :: xs) |> List.rev |> Array.concat

                                        |> System.Text.Encoding.UTF8.GetString
                                        |> sender
                                    else
                                        frame <- data :: frame
                                | _ -> ()
                        }

                    try
                        do! skt
                    with _ ->
                        ()

                    closer ()
            }
    
    let server endpoint inboxCreator : HttpEndpoint =
        any endpoint (createHandler inboxCreator)