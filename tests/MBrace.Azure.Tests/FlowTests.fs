﻿namespace MBrace.Azure.Tests.Runtime

open MBrace.Core
open MBrace.Core.Internals
open MBrace.Core.Tests

open MBrace.Azure
open MBrace.Azure.Runtime
open MBrace.Azure.Tests

open NUnit.Framework

[<AbstractClass; TestFixture>]
type ``Azure CloudFlow Tests`` (sbus, storage, localWorkers) as self =
    inherit ``CloudFlow tests`` ()

    let config = new Configuration(storage, sbus)

    let session = new RuntimeSession(config, localWorkers)

    let run (wf : Cloud<'T>) = self.RunOnCloud wf

    member __.Configuration = config

    [<TestFixtureSetUp>]
    abstract Init : unit -> unit
    default __.Init () = session.Start()

    [<TestFixtureTearDown>]
    abstract Fini : unit -> unit
    default __.Fini () = session.Stop()

    override __.IsSupportedStorageLevel _ = true

    override __.RunOnCloud (workflow : Cloud<'T>) = 
        session.Runtime.RunOnCloud(workflow)

    override __.RunOnCurrentProcess(workflow : Cloud<'T>) = session.Runtime.RunOnCurrentProcess(workflow)

    override __.FsCheckMaxNumberOfTests = 3
    override __.FsCheckMaxNumberOfIOBoundTests = 3

type ``CloudFlow Compute - Storage Emulator`` () =
    inherit ``Azure CloudFlow Tests``(Utils.selectEnv "azureservicebusconn", "UseDevelopmentStorage=true", 0)
    
type ``CloudFlow Standalone - Storage Emulator`` () =
    inherit ``Azure CloudFlow Tests``(Utils.selectEnv "azureservicebusconn", "UseDevelopmentStorage=true", 4)

type ``CloudFlow Standalone`` () =
    inherit ``Azure CloudFlow Tests``(Utils.selectEnv "azureservicebusconn", Utils.selectEnv "azurestorageconn", 4)