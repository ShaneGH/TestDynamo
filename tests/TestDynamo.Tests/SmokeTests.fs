
namespace TestDynamo.Tests

open System
open System.Threading
open System.Threading.Tasks
open Amazon.DynamoDBv2
open Amazon.DynamoDBv2.Model
open Amazon.Runtime
open TestDynamo
open TestDynamo.Api.FSharp
open TestDynamo.Client
open TestDynamo.Model
open TestDynamo.Utils
open Tests.Items
open Tests.Requests.Queries
open Tests.Utils
open Xunit
open Xunit.Abstractions

type DynamoAttributeValue = Amazon.DynamoDBv2.Model.AttributeValue

type SmokeTests(output: ITestOutputHelper) =

    let random = randomBuilder output

    [<Fact>]
    let ``countDigits smoke tests`` () =
        // Numbers are variable length, with up to 38 significant digits.
        // Leading and trailing zeroes are trimmed. The size of a number is
        // approximately (number of UTF-8-encoded bytes of attribute name) + (1 byte per two significant digits) + (1 byte).

        Assert.Equal(1, ItemSize.countDigits 0M)
        Assert.Equal(1, ItemSize.countDigits 1M)
        Assert.Equal(2, ItemSize.countDigits -1M)
        Assert.Equal(2, ItemSize.countDigits 10M)

        Assert.Equal(6, ItemSize.countDigits 123.45M)
        Assert.Equal(7, ItemSize.countDigits -123.45M)

        Assert.Equal(4, ItemSize.countDigits 0.45M)
        Assert.Equal(5, ItemSize.countDigits -0.45M)

    [<Fact>]
    let ``Create table, put item, query smoke tests`` () =

        task {
            use client = buildClient output
            let client = client.Client

            // arrange
            let! tableName = addTable client false

            let item =
                ItemBuilder.empty
                |> ItemBuilder.withTableName tableName
                |> ItemBuilder.withAttribute "PartitionKey" "S" "x"
                |> ItemBuilder.withAttribute "SortKey" "N" "8"
                |> ItemBuilder.withAttribute "Something" "S" "Else"

            let q1 =
                QueryBuilder.empty ValueNone
                |> QueryBuilder.setTableName tableName
                |> QueryBuilder.setKeyConditionExpression "PartitionKey = :p AND SortKey = :s"
                |> QueryBuilder.setExpressionAttrValues ":p" (ItemBuilder.buildItemAttribute "S" "x")
                |> QueryBuilder.setExpressionAttrValues ":s" (ItemBuilder.buildItemAttribute "N" "8")
                |> QueryBuilder.queryRequest

            // act
            let! _ = client.PutItemAsync(
                ItemBuilder.tableName item,
                ItemBuilder.dynamoDbAttributes item)

            let! result = client.QueryAsync q1

            // assert
            let item = Assert.Single result.Items
            Assert.Equal("x", item["PartitionKey"].S)
            Assert.Equal("Else", item["Something"].S)
            Assert.Equal("8", item["SortKey"].N)

            Assert.Equal(3, item.Count)
        }

    [<Fact>]
    let ``Create table, describe table smoke test`` () =

        task {
            use client = buildClient output
            let client = client.Client

            // arrange
            let! tableName = addTable client false

            // act
            let! t = client.DescribeTableAsync(tableName)

            // assert
            Assert.Equal("PartitionKey", t.Table.KeySchema[0].AttributeName)
            Assert.Equal("HASH", t.Table.KeySchema[0].KeyType.Value)
            Assert.Equal("SortKey", t.Table.KeySchema[1].AttributeName)
            Assert.Equal("RANGE", t.Table.KeySchema[1].KeyType.Value)
        }

    [<Fact>]        
    let ``addCancellationTokenNetStandard tests`` () =
        let doAndReturn7 () =
            task {
                do! Task.Delay(100)
                return 7
            }

        task {
            let! v1 = doAndReturn7() |> Io.addCancellationTokenNetStandard CancellationToken.None
            Assert.Equal(7, v1)

            use cts = new CancellationTokenSource(10)
            let! e = Assert.ThrowsAnyAsync(fun _ -> doAndReturn7() |> Io.addCancellationTokenNetStandard cts.Token |> Io.ignoreTask)
            assertError output "A task was canceled" e
        }

    [<Fact>]        
    let ``ObjPipelineInterceptor force synchronous test`` () =
        task {
            // arrange
            let db = new ApiDb()
            let delay = TimeSpan.FromSeconds(0.2)
            use subject = new ObjPipelineInterceptor(db |> Either1, delay, ValueNone, true)
            let request =
                let r = CreateTableRequest()
                r.TableName <- "NewTable"
                r.KeySchema <- MList<_>([
                    let s = KeySchemaElement()
                    s.AttributeName <- "kk"
                    s.KeyType <- KeyType.HASH
                    s
                ])
                r.AttributeDefinitions <- MList<_>([
                    let attr = AttributeDefinition()
                    attr.AttributeName <- "kk"
                    attr.AttributeType <- ScalarAttributeType.S
                    attr
                ])
                r

            // act
            let start = DateTimeOffset.Now
            do subject.InvokeSync true request CancellationToken.None |> ignoreTyped<AmazonWebServiceResponse>

            // assert
            // + 50ms because Task.Delay is not 100% accurate
            let time = DateTimeOffset.Now + TimeSpan.FromMilliseconds(50) - start 
            Assert.True(time >= delay, time.ToString())

            db.GetTable ValueNone "NewTable"
            |> Assert.NotNull
        }

    [<Fact>]
    let ``ObjPipelineInterceptor is disposed correctly`` () =

        // arrange
        use db1 = new Api.Database()
        use db2 = new Api.GlobalDatabase()

        let client1 = db1.CreateClient<AmazonDynamoDBClient>()
        let client2 = db2.CreateClient<AmazonDynamoDBClient>()
        let client3 = TestDynamoClient.CreateClient<AmazonDynamoDBClient>()
        let client4 = TestDynamoClient.CreateGlobalClient<AmazonDynamoDBClient>()

        use db3 = TestDynamoClient.GetDatabase client3
        use db4 = TestDynamoClient.GetGlobalDatabase client4

        // act
        client1.Dispose()
        client2.Dispose()
        client3.Dispose()
        client4.Dispose()

        // assert - these dbs were not disposed
        db1.TableBuilder("table1", ("pp", "S")).AddTable()
        (db2.GetDatabase({ regionId = "db-bd" })).TableBuilder("table1", ("pp", "S")).AddTable()

        // assert - these dbs were disposed
        let e = Assert.ThrowsAny(fun _ ->
            db3.TableBuilder("table1", ("pp", "S")).AddTable())
        assertError output "has been disposed" e

        let e = Assert.ThrowsAny(fun _ ->
            (db4.GetDatabase({ regionId = "db-bd" })).TableBuilder("table1", ("pp", "S")).AddTable())
        assertError output "has been disposed" e

    [<Fact>]
    let ``List tables smoke test`` () =

        task {
            use client = TestDynamoClient.CreateClient<AmazonDynamoDBClient>()

            // arrange
            let! tableName1 = addTable client false
            let! tableName2 = addTable client false

            let tableNames = [|tableName1; tableName2|] |> Array.sort

            // act
            let! t = client.ListTablesAsync()

            // assert
            Assert.Equal(2, t.TableNames.Count)

            // act
            let! t = client.ListTablesAsync("0000000")

            // assert
            Assert.Equal(2, t.TableNames.Count)

            // act
            let! t = client.ListTablesAsync(1)

            // assert
            Assert.Equal(1, t.TableNames.Count)
            Assert.Equal(tableNames[0], t.TableNames[0])

            // act
            let! t = client.ListTablesAsync(t.LastEvaluatedTableName, 10)

            // assert
            Assert.Equal(1, t.TableNames.Count)
            Assert.Equal(tableNames[1], t.TableNames[0])

            // act
            let! t = client.ListTablesAsync(tableNames[0], 1)

            // assert
            Assert.Equal(1, t.TableNames.Count)
            Assert.Equal(tableNames[1], t.TableNames[0])
        }

    [<Fact>]
    let ``List global tables smoke test`` () =

        let addGlobalTable client =
            task {
                let! tableName = addTable client true
                let req = CreateGlobalTableRequest()
                req.GlobalTableName <- tableName
                req.ReplicationGroup <- System.Collections.Generic.List()
                req.ReplicationGroup.Add(Replica())
                req.ReplicationGroup[0].RegionName <- "r2"
                do! client.CreateGlobalTableAsync req |> Io.ignoreTask
                return tableName
            }

        task {
            use host = new GlobalDatabase()
            use client = TestDynamoClient.createGlobalClient<AmazonDynamoDBClient> ValueNone true (ValueSome {regionId = "r1" }) ValueNone (ValueSome host)

            // arrange
            let! tableName1 = addGlobalTable client
            let! tableName2 = addGlobalTable client

            let tableNames = [|tableName1; tableName2|] |> Array.sort

            // act
            let req = ListGlobalTablesRequest()
            let! t = client.ListGlobalTablesAsync req

            // assert
            Assert.Equal(2, t.GlobalTables.Count)

            // act
            let req = ListGlobalTablesRequest()
            req.ExclusiveStartGlobalTableName <- "000000000000"
            let! t = client.ListGlobalTablesAsync req

            // assert
            Assert.Equal(2, t.GlobalTables.Count)

            // act
            let req = ListGlobalTablesRequest()
            req.Limit <- 1
            let! t = client.ListGlobalTablesAsync req

            // assert
            Assert.Equal(1, t.GlobalTables.Count)
            Assert.Equal(tableNames[0], t.GlobalTables[0].GlobalTableName)

            // act
            let req = ListGlobalTablesRequest()
            req.Limit <- 10
            req.ExclusiveStartGlobalTableName <- t.LastEvaluatedGlobalTableName
            let! t = client.ListGlobalTablesAsync req

            // assert
            Assert.Equal(1, t.GlobalTables.Count)
            Assert.Equal(tableNames[1], t.GlobalTables[0].GlobalTableName)

            // act
            let req = ListGlobalTablesRequest()
            req.Limit <- 1
            req.ExclusiveStartGlobalTableName <- tableNames[0]
            let! t = client.ListGlobalTablesAsync req

            // assert
            Assert.Equal(1, t.GlobalTables.Count)
            Assert.Equal(tableNames[1], t.GlobalTables[0].GlobalTableName)
        }

    [<Fact>]
    let ``Create global table, describe global table smoke test`` () =

        task {
            // arrange
            use commonHost = new GlobalDatabase()
            use client = TestDynamoClient.createGlobalClient<AmazonDynamoDBClient> ValueNone true (ValueSome {regionId = "r1" }) ValueNone (ValueSome commonHost)

            let! tableName = addTable client true
            let req = CreateGlobalTableRequest()
            req.GlobalTableName <- tableName
            req.ReplicationGroup <- System.Collections.Generic.List()
            req.ReplicationGroup.Add(Replica())
            req.ReplicationGroup[0].RegionName <- "r2"
            do! client.CreateGlobalTableAsync req |> Io.ignoreTask

            // act
            let r =
                let rr = DescribeGlobalTableRequest()
                rr.GlobalTableName <- tableName
                rr
            let! t = client.DescribeGlobalTableAsync(r)

            // assert
            Assert.Equal(tableName, t.GlobalTableDescription.GlobalTableName)
            Assert.Equal(2, t.GlobalTableDescription.ReplicationGroup.Count)
            Assert.Equal("r1", t.GlobalTableDescription.ReplicationGroup[0].RegionName)
            Assert.Equal("r2", t.GlobalTableDescription.ReplicationGroup[1].RegionName)
        }

    [<Fact>]
    let ``Create local table, describe as global, fails`` () =

        task {
            // arrange
            use commonHost = new GlobalDatabase()
            use client = TestDynamoClient.createGlobalClient<AmazonDynamoDBClient> ValueNone true (ValueSome {regionId = "r1" }) ValueNone (ValueSome commonHost)
            let! tableName = addTable client true

            let r =
                let rr = DescribeGlobalTableRequest()
                rr.GlobalTableName <- tableName
                rr

            // act
            let! err = Assert.ThrowsAnyAsync(fun _ -> client.DescribeGlobalTableAsync(r))

            // assert
            assertError output "is not a global table" err
        }

    [<Fact>]
    let ``Interceptor tests`` () =

        task {
            // arrange
            use commonHost = new GlobalDatabase()
            let expected = DescribeTableResponse()
            let interceptor =
                { new IRequestInterceptor with
                    member _.InterceptRequest db req c =
                        Assert.Equal("r1", db.Id.regionId)

                        let req = Assert.IsType<DescribeTableRequest>(req)
                        if req.TableName = "ttt"
                        then ValueTask<_>(result = expected)
                        elif random.Next() % 2 = 0
                        then Unchecked.defaultof<_>
                        else box null |> Io.retn

                    override this.InterceptResponse database request response var0 =
                        let req = Assert.IsType<DescribeTableRequest>(request)
                        if req.TableName = "ttt"
                        then
                            expected.ContentLength <- 54321
                            Io.retn (box expected)
                        elif random.Next() % 2 = 0
                        then Unchecked.defaultof<_>
                        else box null |> Io.retn

                        }

            use client = TestDynamoClient.createGlobalClient<AmazonDynamoDBClient> ValueNone true (ValueSome {regionId = "r1" }) (ValueSome interceptor) (ValueSome commonHost)

            let r =
                let rr = DescribeTableRequest()
                rr.TableName <- "ttt"
                rr

            // act 1
            let! response = client.DescribeTableAsync(r)

            // assert 1
            Assert.Equal(response, response)
            Assert.Equal(54321L, response.ContentLength)
            r.TableName <- "uuu"

            // act 2
            let! e = Assert.ThrowsAnyAsync(fun _ -> client.DescribeTableAsync(r))
            assertError output "Table uuu not found" e
        }

    [<Theory>]
    [<InlineData(0)>]
    [<InlineData(1)>]
    [<InlineData(2)>]
    [<InlineData(4)>]
    [<InlineData(5)>]
    let ``A bunch of comparer tests`` alterABit =
        let buildMap1 alterABit =
            Map.empty
            |> Map.add "P1" AttributeValue.Null
            |> Map.add "P2" (
                Map.empty
                |> Map.add "P3" (AttributeValue.String (if alterABit = 1 then "xy" else "xz"))
                |> Map.add "P4" (AttributeValue.AttributeList (Maybe.traverse [
                    AttributeSet.create [
                        AttributeValue.Number 1M; AttributeValue.Number (if alterABit = 2 then 7M else 8M)
                    ] |> AttributeValue.HashSet |> ValueSome

                    AttributeSet.create [
                        AttributeValue.String "1"; AttributeValue.String (if alterABit = 3 then "7" else "8")
                    ] |> AttributeValue.HashSet |> ValueSome

                    AttributeSet.create [
                        AttributeValue.Binary [|1uy|]; AttributeValue.Binary [|if alterABit = 4 then 7uy else 8uy|]
                    ] |> AttributeValue.HashSet |> ValueSome

                    if alterABit = 5 then AttributeValue.Null |> ValueSome else ValueNone
                ] |> Array.ofSeq |> CompressedList))
                |> AttributeValue.HashMap)
            |> AttributeValue.HashMap

        let map1 = buildMap1 0
        let map2 = buildMap1 alterABit

        match alterABit with
        | 0 ->
            Assert.True((map1 = map2))
            Assert.Equal(0, compare map1 map2)
            Assert.Equal(map1.GetHashCode(), map2.GetHashCode())
        | _ ->
            Assert.False((map1 = map2))
            Assert.NotEqual(0, compare map1 map2)
            Assert.NotEqual(map1.GetHashCode(), map2.GetHashCode())

    [<Fact>]
    let ``Memoization tests`` () =
        // arrange
        let fn = memoize (ValueSome (2, 4)) id (fun (x: int) -> obj()) >> sndT

        // act1, assert1
        let req1 = fn 1
        let req2 = fn 2
        let req3 = fn 3
        let req4 = fn 4

        Assert.Equal(req1, fn 1)
        Assert.Equal(req2, fn 2)
        Assert.Equal(req3, fn 3)
        Assert.Equal(req4, fn 4)

        // act2, assert2
        let req5 = fn 5
        Assert.Equal(req4, fn 4)
        Assert.Equal(req5, fn 5)
        Assert.NotEqual(req1, fn 1)
        Assert.NotEqual(req2, fn 2)
        Assert.NotEqual(req3, fn 3)
