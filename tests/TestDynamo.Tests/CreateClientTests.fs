
namespace TestDynamo.Tests

open System.Runtime.InteropServices
open Amazon
open Amazon.DynamoDBv2
open TestDynamo
open Tests.Utils
open Xunit
open Xunit.Abstractions

type Ok1(
    [<Optional; DefaultParameterValue(null: RegionEndpoint)>] region: RegionEndpoint,
    [<Optional; DefaultParameterValue(7)>] anInt: int) =
    inherit AmazonDynamoDBClient(region)

    member _.AnInt = anInt

type Ok2(region: RegionEndpoint) =
    inherit AmazonDynamoDBClient(region)

type Fail1() =
    inherit AmazonDynamoDBClient()

type Fail2(
    region: RegionEndpoint,
    anInt: int) =
    inherit AmazonDynamoDBClient(region)

    member _.AnInt = anInt

type CreateClientTests(output: ITestOutputHelper) =

    [<Fact>]
    let ``CreateClient, smoke tests`` () =

        task {
            // arrange
            // act
            let e1 = Assert.ThrowsAny(fun () -> TestDynamoClient.CreateClient<Fail1>() |> ignore)
            let e2 = Assert.ThrowsAny(fun () -> TestDynamoClient.CreateClient<Fail2>() |> ignore)
            use client1 = TestDynamoClient.CreateClient<Ok1>()
            use _ = TestDynamoClient.CreateClient<Ok2>()

            // assert
            Assert.Equal(7, client1.AnInt)
            assertError output $"{nameof TestDynamoClient}.Attach" e1
            assertError output $"{nameof TestDynamoClient}.Attach" e2
        }