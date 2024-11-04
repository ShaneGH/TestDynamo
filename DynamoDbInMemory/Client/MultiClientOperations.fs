﻿namespace DynamoDbInMemory.Client

open System.Runtime.CompilerServices
open Amazon.DynamoDBv2.Model
open DynamoDbInMemory
open DynamoDbInMemory.Api
open DynamoDbInMemory.Client
open DynamoDbInMemory.Client.GetItem.Batch
open DynamoDbInMemory.Model
open Microsoft.Extensions.Logging
open DynamoDbInMemory.Data.Monads.Operators
open DynamoDbInMemory.Model.ExpressionExecutors.Fetch
open Utils

module MultiClientOperations =

    let private distributedOnly = "Some update table requests only work on DistributedDatabases. Please ensure that this InMemoryDynamoDbClient was initiated with the correct args"

    type ApiDb = DynamoDbInMemory.Api.Database

    let private chooseDatabase databaseId =
        Either.map2Of2 (fun struct (db: DistributedDatabase, _) ->
            match db.TryGetDatabase databaseId with
            | ValueNone -> clientError $"No resources have been created in DB region {databaseId.regionId}"
            | ValueSome db -> db)
        >> Either.reduce

    [<Struct; IsReadOnly>]    
    type private ResponseAggregator<'found, 'notProcessed> =
        { notProcessed: Map<BatchItemKey, 'found list>
          found: Map<BatchItemKey, 'notProcessed list> }

    module private ResponseAggregator =

        let private addNotProcessed k v agg =
            let change _ v_old v =
                match v_old with
                | ValueNone -> [v]
                | ValueSome ks -> v::ks
            { agg with notProcessed = MapUtils.change change k v agg.notProcessed }: ResponseAggregator<_, _>

        let private addFound k v agg =
            let change _ v_old v =
                match v_old with
                | ValueNone -> [v]
                | ValueSome x -> v::x
            { agg with found =  MapUtils.change change k v agg.found }: ResponseAggregator<_, _>

        let rec private addResults key result = function
            | [] -> result
            | Either1 head::tail -> result |> addFound key head |> flip (addResults key) tail
            | Either2 head::tail -> result |> addNotProcessed key head |> flip (addResults key) tail

        let private execute'
            operation
            (database: ApiDb)
            struct (state, result: ResponseAggregator<_, _>)
            (struct (k: GetItem.Batch.BatchItemKey, v) & kv) =

            if database.Id <> k.databaseId then notSupported distributedOnly

            operation database state kv
            |> mapSnd (addResults k result)

        let private execute operation database acc (struct (batchGetKey, _) & inputs) =

            chooseDatabase batchGetKey.databaseId database
            |> execute' operation
            <| acc
            <| inputs

        let executeBatch operation initialState (database: Either<ApiDb, struct (DistributedDatabase * DatabaseId)>) (batchRequests: struct (BatchItemKey * _) seq) =
            batchRequests
            |> Seq.fold (execute operation database) struct (initialState, { notProcessed = Map.empty; found = Map.empty })
            |> sndT

    module BatchGetItem =

        let private execute
            logger
            (database: ApiDb)
            remaining
            struct (k: GetItem.Batch.BatchItemKey, v: GetItem.Batch.BatchGetValue) =

            match struct (remaining, database) with
            | remaining, _ when remaining <= 0 -> struct (remaining, [Either2 v])
            | remaining, db ->
                let args =
                    { maxPageSizeBytes = remaining
                      keys = v.keys
                      conditionExpression =
                          { tableName = k.tableName
                            returnValues =
                                v.projectionExpression
                                |> ValueOption.map (ValueSome >> ProjectedAttributes)
                                |> ValueOption.defaultValue AllAttributes
                            expressionAttrNames = v.expressionAttrNames
                            expressionAttrValues = Map.empty
                            conditionExpression = ValueNone } }: GetItemArgs

                let response = db.Get logger args
                [
                    if response.unevaluatedKeys.Length = 0
                        then ValueNone
                        else { v with keys = response.unevaluatedKeys } |> Either2 |> ValueSome
                    if response.items.Length = 0
                        then ValueNone
                        else response.items |> Either1 |> ValueSome
                ]
                |> Maybe.traverse
                |> List.ofSeq
                |> tpl (remaining - response.itemSizeBytes)

        let private noBatchGetValue =
            { keys = Array.empty
              consistentRead = false
              projectionExpression = ValueNone
              expressionAttrNames = Map.empty } : BatchGetValue

        let batchGetItem (database: Either<ApiDb, struct (DistributedDatabase * DatabaseId)>) logger (req: GetItem.Batch.BatchGetRequest) =

            let requests =
                req.requests
                |> MapUtils.toSeq
                |> Seq.collect (function
                    // simulate inconsistent read by splitting req into smaller parts  
                    | struct (_, {consistentRead = true}) & x
                    | (_, {keys = [||]}) & x
                    | (_, {keys = [|_|]}) & x -> [x] |> Seq.ofList
                    | k, v -> v.keys |> Seq.map (fun keys -> struct (k, { v with keys = [|keys|] })))

            ResponseAggregator.executeBatch
            <| (execute logger)
            <| Settings.BatchItems.BatchGetItemMaxSizeBytes
            <| database
            <| requests
            |> fun x ->
                { notProcessed =
                      x.notProcessed
                      |> Map.map (fun _ -> function
                          | [] -> noBatchGetValue
                          | [x] -> x
                          | head::_ & values -> { head with keys = values |> Seq.collect (fun x -> x.keys) |> Array.ofSeq })
                  found =
                      x.found
                      |> Map.map (fun _ xs ->
                          Seq.collect id xs
                          |> Array.ofSeq) }: BatchGetResponse

    module BatchWriteItem =

        open PutItem.BatchWrite

        let tryExecute (logger: ILogger voption) f v =
            try
                f v |> Either1
            with
            | e ->
                logger ?|> (_.LogError(e, "")) |> ValueOption.defaultValue ()
                v |> Either2

        let private execute
            logger
            (database: ApiDb)
            remaining
            struct (k: GetItem.Batch.BatchItemKey, v: Write list) =

            match struct (remaining, database) with
            | remaining, _ when remaining <= 0 -> struct (remaining, [Either2 v])
            | remaining, db ->
                List.fold (fun struct (remaining, acc) ->
                    function
                    | Delete x ->
                        tryExecute logger (db.Delete logger) x
                        |> Either.map1Of2 ignoreTyped<Map<string,AttributeValue> voption>
                        |> Either.map2Of2 (Delete >> List.singleton)
                        |> flip Collection.prependL acc
                        |> tpl (remaining - ItemSize.calculate x.key)
                    | Put x ->
                        tryExecute logger (db.Put logger) x
                        |> Either.map1Of2 ignoreTyped<Map<string,AttributeValue> voption>
                        |> Either.map2Of2 (Put >> List.singleton)
                        |> flip Collection.prependL acc
                        |> tpl (remaining - ItemSize.calculate x.item)) struct (remaining, []) v

        let private noBatchGetValue =
            { keys = Array.empty
              consistentRead = false
              projectionExpression = ValueNone
              expressionAttrNames = Map.empty } : BatchGetValue

        let batchPutItem (database: Either<ApiDb, struct (DistributedDatabase * DatabaseId)>) logger (req: PutItem.BatchWrite.BatchWriteRequest) =

            let requests =
                req.requests
                |> MapUtils.toSeq

            ResponseAggregator.executeBatch
            <| (execute logger)
            <| Settings.BatchItems.BatchPutItemMaxSizeBytes
            <| database
            <| requests
            |> _.notProcessed
            |> Map.map (fun _ -> List.collect id)
            |> fun x -> { notProcessed = x }: BatchWriteResponse

    module UpdateTable =

        let private updateTable' databaseOp distributedOp request =

            let updateDistributed =
                match struct (request.distributedTableData.replicaInstructions, distributedOp) with
                | [], _ -> ValueNone
                | xs, ValueNone -> notSupported distributedOnly
                | xs, ValueSome distributedDb ->
                    fun () -> distributedDb request.tableName request.distributedTableData
                    |> ValueSome

            // do not run local table part if there is nothing to update
            // this allows op to recover from any synchronization errors if that is the intention of the request
            let eagerTableResult =
                if request.tableData = UpdateSingleTableData.empty
                then ValueNone
                else request.tableData |> ValueSome
                |> databaseOp request.tableName

            updateDistributed
            ?|> (apply ())
            |> ValueOption.defaultValue eagerTableResult

        let updateTable awsAccountId ddb databaseId databaseOp distributedOp (req: UpdateTableRequest) =            
            UpdateTable.inputs1 req
            |> updateTable' databaseOp distributedOp
            |> UpdateTable.output awsAccountId ddb databaseId

        let createGlobalTable awsAccountId ddb databaseId databaseOp distributedOp (req: CreateGlobalTableRequest) =            
            CreateGlobalTable.inputs1 req
            |> updateTable' databaseOp distributedOp
            |> CreateGlobalTable.output awsAccountId ddb databaseId

        let updateGlobalTable awsAccountId ddb databaseId databaseOp distributedOp (req: UpdateGlobalTableRequest) =            
            UpdateGlobalTable.inputs1 req
            |> updateTable' databaseOp distributedOp
            |> UpdateGlobalTable.output awsAccountId ddb databaseId