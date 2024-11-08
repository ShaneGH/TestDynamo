
namespace TestDynamo.Tests

open System.Threading
open TestDynamo
open TestDynamo.Api.FSharp
open TestDynamo.Client
open TestDynamo.Model
open TestDynamo.Utils
open Tests.Items
open Tests.Loggers
open Tests.Table
open Tests.Utils
open Xunit
open Xunit.Abstractions

/// <summary>Not tests, experiments</summary>
type Exploration(output: ITestOutputHelper) =

    [<Fact>]
    let ``Explore async replication`` () =

        task {
            let dbFrom = { regionId = "from-region" }
            let dbTo = { regionId = "to-region" }
            use writer = new TestLogger(output)
            let subscriberBehaviour =
                { delay = GlobalDataPropagationBehaviour.RunAsynchronously (System.TimeSpan.FromSeconds(0.1))
                  subscriberTimeout = System.TimeSpan.FromSeconds(2) }

            use host = new GlobalDatabase(logger = writer)
            use clientFrom = TestDynamoClient.Create(host, dbFrom, writer)
            use clientTo = TestDynamoClient.Create(host, dbTo, writer)
            clientFrom.ProcessingDelay <- System.TimeSpan.Zero
            clientTo.ProcessingDelay <- System.TimeSpan.Zero

            // add a table with data
            let! tableName = addTable clientFrom true
            let itemReq =
                ItemBuilder.empty
                |> ItemBuilder.withTableName tableName
                |> ItemBuilder.withAttribute "PartitionKey" "S" "### Item1"
                |> ItemBuilder.withAttribute "SortKey" "N" "8"
                |> ItemBuilder.withAttribute "Something" "S" "Else"
                |> ItemBuilder.withReturnValues "old" 
                |> ItemBuilder.asPutReq
            do!
                itemReq
                |> clientFrom.PutItemAsync
                |> Io.ignoreTask

            // replicate
            do!
                TableBuilder.empty
                |> TableBuilder.withTableName tableName
                |> TableBuilder.withReplication dbTo.regionId
                |> TableBuilder.updateReq
                |> clientFrom.UpdateTableAsync
                |> Io.ignoreTask
                
            let key =
                { fromDb = dbFrom
                  toDb = dbTo
                  tableName = tableName }
                
            host.UpdateReplication (ValueSome writer) key subscriberBehaviour true

            let! putResponse =
                itemReq
                |> clientTo.PutItemAsync

            // If put response contains an old item, then replication is synchronous
            Assert.NotEmpty(putResponse.Attributes)

            do! host.AwaitAllSubscribers ValueNone CancellationToken.None
        }