﻿module Nessos.MBrace.Azure.Runtime.Common

// Contains types used a table storage entities, service bus messages and blog objects.

open System

open Microsoft.WindowsAzure.Storage
open Microsoft.WindowsAzure.Storage.Table
open Microsoft.ServiceBus
open Microsoft.ServiceBus.Messaging

type AzureConfig =
    {
        StorageConnectionString : string
        ServiceBusConnectionString : string
    }

type IResource =
    abstract Uri : Uri

[<AutoOpen>]
module UriUtils =
    let guid() = Guid.NewGuid().ToString("N")
    let uri fmt = Printf.ksprintf (fun s -> new Uri(s)) fmt
    let toContainerId(res : Uri) = 
        let s = res.Segments.[0] 
        s.Substring(0, s.Length - 1), res.Segments.[1]

[<AbstractClass; Sealed>]
type ClientProvider private () =
    static let cfg = ref None
    static let acc = ref Unchecked.defaultof<CloudStorageAccount>
    static let check f = lock cfg (fun () -> if cfg.Value.IsNone then failwith "No active configuration found." else f ())

    static member Activate(config : AzureConfig) =
        let sa = CloudStorageAccount.Parse(config.StorageConnectionString)
        lock cfg (fun () -> cfg := Some config; acc := sa)

    static member ActiveConfiguration = check (fun _ -> cfg.Value.Value)
    static member TableClient = check (fun _ -> acc.Value.CreateCloudTableClient())
    static member BlobClient = check (fun _ -> acc.Value.CreateCloudBlobClient())
    static member NamespaceClient = check (fun _ -> NamespaceManager.CreateFromConnectionString(cfg.Value.Value.ServiceBusConnectionString))
    static member QueueClient(queue : string) = check (fun _ -> QueueClient.CreateFromConnectionString(cfg.Value.Value.ServiceBusConnectionString, queue))

// Parameterless public ctor is needed.
type LatchEntity(name : string, value : int) =
    inherit TableEntity(name, String.Empty)
    member val Value = value with get, set
    new () = new LatchEntity (null, 0)     

/// Named latch implementation.
type Latch private (res : Uri) =
    let table, id = toContainerId res
    let table = ClientProvider.TableClient.GetTableReference(table)
    let result = table.Execute(TableOperation.Retrieve<LatchEntity>(id, String.Empty))
    let entity = result.Result :?> LatchEntity
    
    let read () =
        let result = table.Execute(TableOperation.Retrieve<LatchEntity>(entity.PartitionKey, entity.RowKey))
        let e = result.Result :?> LatchEntity
        e

    let rec update () =
        let e = read ()
        e.Value <- e.Value + 1
        let r =        
            try
                let result = table.Execute(TableOperation.Merge(e))
                Some(result.Result :?> LatchEntity)
            with :? StorageException as se when se.RequestInformation.HttpStatusCode = 412 ->
                None
        match r with
        | None -> update ()
        | Some v -> v

    member __.Value with get () = let e = read () in e.Value

    member __.Increment () = update () |> ignore
        
    static member Init(res : Uri, ?value : int) =
        let value = defaultArg value 0
        let table, id = toContainerId res
        let table = ClientProvider.TableClient.GetTableReference(table)
        do table.CreateIfNotExists() |> ignore
        let e = new LatchEntity(id, value)
        let result = table.Execute(TableOperation.Insert(e))
        new Latch(res)

    static member Get(res : Uri) =
        new Latch(res)
     
    interface IResource with
        member __.Uri = res

    static member GetUri(container, id) = uri "latch:%s/%s" container id
    static member GetUri(container) = Latch.GetUri(container, guid())

/// Read-only blob.   
type BlobCell private (res : Uri) =
    let container, id = toContainerId res
    let container = ClientProvider.BlobClient.GetContainerReference(container)

    member __.GetValue<'T>() =  
        use s = container.GetBlockBlobReference(id).OpenRead()
        Config.serializer.Deserialize<'T>(s)

    interface IResource with
        member __.Uri = res

    static member Init(res , f : unit -> 'T) =
        let container, id = toContainerId res
        let c = ClientProvider.BlobClient.GetContainerReference(container)
        c.CreateIfNotExists() |> ignore
        use s = c.GetBlockBlobReference(id).OpenWrite()
        Config.serializer.Serialize<'T>(s, f ())
        new BlobCell(res)

    static member Get(res : Uri) = new BlobCell(res)
    static member GetUri(container, id) = uri "blobcell:%s/%s" container id
    static member GetUri(container) = BlobCell.GetUri(container, guid())

/// Queue implementation.
type Queue private (res : Uri) =
    let queueName = res.Segments.[0]
    let queue = ClientProvider.QueueClient(queueName)
    let ns = ClientProvider.NamespaceClient

    member __.Length = ns.GetQueue(queueName).MessageCount

    member __.Enqueue (t : 'T) =
        let r = BlobCell.GetUri(queueName)
        let bc = BlobCell.Init(r, fun () -> t)
        let msg = new BrokeredMessage(r)
        queue.Send(msg)

    member __.TryDequeue () : 'T option =
        let msg = queue.Receive()
        if msg = null then None
        else
            let p = msg.GetBody<Uri>()
            let t = BlobCell.Get(p)
            msg.Complete()
            Some <| t.GetValue()

    static member Get(res) = new Queue(res)

    static member Init(res : Uri) =
        let ns = ClientProvider.NamespaceClient
        let container = res.Segments.[0]
        let qd = new QueueDescription(container)
        qd.DefaultMessageTimeToLive <- TimeSpan.MaxValue
        if not <| ns.QueueExists(container) then
            ns.CreateQueue(qd) |> ignore
        new Queue(res)

    interface IResource with
        member __.Uri = res

    static member GetUri(container) = uri "queue:%s" container

type ResultCell private (res : Uri) =
    let queue = Queue.Get(Queue.GetUri(res.Segments.[0]))

    member __.SetResult(result : 'T) = queue.Enqueue(result)

    member __.TryGetResult () = queue.TryDequeue()

    member __.AwaitResult () =
        match __.TryGetResult() with
        | None -> __.AwaitResult()
        | Some r -> r

    interface IResource with
        member __.Uri = res

    static member GetUri(container) = uri "resultcell:%s/" container

    static member Get(res : Uri) =
        new ResultCell(res)

    static member Init(res : Uri) =
        let q = Queue.Init(res)
        new ResultCell(res)

