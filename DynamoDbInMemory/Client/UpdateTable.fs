﻿module DynamoDbInMemory.Client.UpdateTable

open Amazon.DynamoDBv2
open Amazon.DynamoDBv2.Model
open DynamoDbInMemory
open DynamoDbInMemory.Utils
open DynamoDbInMemory.Data.Monads.Operators
open DynamoDbInMemory.Client
open DynamoDbInMemory.Model
open DynamoDbInMemory.Data.BasicStructures
open DynamoDbInMemory.Api
open DynamoDbInMemory.Client.CreateTable
open DynamoDbInMemory.Client.DescribeTable.Local
open Shared

type MList<'a> = System.Collections.Generic.List<'a>

let getDeletionProtectionEnabled =
    getOptionalBool<UpdateTableRequest, bool> "DeletionProtectionEnabled"

let inputs1 (req: UpdateTableRequest) =

    let replicaInstructions =
        req.ReplicaUpdates
        |> CSharp.sanitizeSeq
        |> Seq.collect (fun x ->
            [
                x.Create
                |> CSharp.toOption
                ?|> (fun x -> CSharp.mandatory "Region name is mandatory for replica updates" x.RegionName)
                ?|> (fun x -> Create { regionId = x })

                x.Delete
                |> CSharp.toOption
                ?|> (fun x -> CSharp.mandatory "Region name is mandatory for replica updates" x.RegionName)
                ?|> (fun x -> Delete { regionId = x })

                if x.Update = null then ValueNone else notSupported "Update ReplicaUpdates are not supported"
            ] |> Maybe.traverse)
        |> List.ofSeq

    let struct (gsiCreate, gsiDelete) =
        req.GlobalSecondaryIndexUpdates
        |> CSharp.sanitizeSeq
        |> Seq.collect (fun x ->
            [
                x.Create
                |> CSharp.toOption
                ?|> (fun x -> buildGsiSchema
                                                 (CSharp.mandatory "KeySchema is mandatory for GSI updates" x.KeySchema)
                                                 (CSharp.mandatory "Projection is mandatory for GSI updates" x.Projection)
                                                 |> tpl (CSharp.mandatory "IndexName is mandatory for GSI updates" x.IndexName))
                ?|> Either1

                x.Delete
                |> CSharp.toOption
                ?|> (fun x -> CSharp.mandatory "IndexName is mandatory for GSI updates" x.IndexName)
                ?|> Either2

                if x.Update = null then ValueNone else notSupported "Update indexes are not supported"
            ] |> Maybe.traverse)
        |> List.ofSeq
        |> Either.partition

    if List.length gsiCreate + List.length gsiDelete > 1 then clientError "You can only create or delete one global secondary index per UpdateTable operation."

    { tableName = req.TableName |> CSharp.mandatory "TableName is mandatory"
      distributedTableData =
          { replicaInstructions = replicaInstructions
            createStreamsForReplication = false }
      tableData =
          { updateTableData =
                { createGsi = gsiCreate |> MapUtils.fromTuple
                  deleteGsi = gsiDelete |> Set.ofSeq
                  deletionProtection = getDeletionProtectionEnabled req
                  attributes =
                      req.AttributeDefinitions
                      |> CSharp.sanitizeSeq
                      |> fromAttributeDefinitions
                      |> List.ofSeq }
            streamConfig = buildStreamConfig req.StreamSpecification } }

let inputs2 struct (
    tableName: string,
    provisionedThroughput: ProvisionedThroughput) =

    UpdateTableRequest (tableName, provisionedThroughput) |> inputs1

let output awsAccountId ddb databaseId (table: TableDetails) =

    let output = Shared.amazonWebServiceResponse<UpdateTableResponse>()
    output.TableDescription <- tableDescription awsAccountId databaseId ddb table TableStatus.ACTIVE
    output