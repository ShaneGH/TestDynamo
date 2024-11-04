﻿namespace DynamoDbInMemory.Client

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Amazon.DynamoDBv2
open Amazon.DynamoDBv2.Model
open DynamoDbInMemory
open DynamoDbInMemory.Api
open DynamoDbInMemory.Client
open DynamoDbInMemory.Model
open DynamoDbInMemory.Utils
open DynamoDbInMemory.Data.Monads.Operators
open Microsoft.Extensions.Logging

type Region = string
type ApiDb = DynamoDbInMemory.Api.Database

/// <summary>
/// A client which can execute operations on an in memory Database or DistributedDatabase.
/// Use the static Create methods to build a clients
/// </summary>
type InMemoryDynamoDbClient private (
    database: Either<ApiDb, struct (DistributedDatabase * DatabaseId)>,
    disposeOfDatabase: bool,
    defaultLogger: ILogger voption) =

    let db =
        match database with
        | Either1 x -> x
        | Either2 struct (x: DistributedDatabase, id) -> x.GetDatabase defaultLogger id

    let parent =
        match database with
        | Either1 _ -> ValueNone
        | Either2 struct (x, id) -> ValueSome struct (x, id)

    let parentDdb = ValueOption.map fstT parent

    let mutable artificialDelay = TimeSpan.FromMilliseconds(5)

    let loggerOrDevNull = ValueOption.defaultValue Logger.notAnILogger defaultLogger

    static let defaultScanSizeLimits =
        { maxScanItems = Settings.ScanSizeLimits.DefaultMaxScanItems
          maxPageSizeBytes = Settings.ScanSizeLimits.DefaultMaxPageSizeBytes } : ExpressionExecutors.Fetch.ScanLimits

    let mutable scanSizeLimits = defaultScanSizeLimits

    let mutable awsAccountId = "123456789012"

    static let asTask (x: ValueTask<'a>) = x.AsTask()

    static let taskify delay (c: CancellationToken) x =
        match delay with
        | d when d < TimeSpan.Zero -> notSupported "Delay time must be greater than or equal to 0"
        | d when d = TimeSpan.Zero -> ValueTask<'a>(result = x)
        | d -> Task.Delay(d, c) |> ValueTask |> Io.normalizeVt |%|> (asLazy x)

    let execute mapIn f mapOut c =
        taskify artificialDelay c >> Io.map (mapIn >> f defaultLogger >> mapOut db.Id) >> asTask

    let executeAsync mapIn f mapOut c =
        taskify artificialDelay c >> Io.bind (mapIn >> f defaultLogger) >> Io.map (mapOut db.Id) >> asTask

    static let notSupported ``member`` = ``member`` |> sprintf "%s member is not supported" |> NotSupportedException |> raise

    static let describeRequiredTable (db: Api.Database) (logger: ILogger voption) (name: string) =
        db.TryDescribeTable logger name |> ValueOption.defaultWith (fun _ -> clientError $"Table {name} not found on database {db.Id}")

    static let maybeUpdateTable (db: Api.Database) (logger: ILogger voption) (name: string) (req: UpdateSingleTableData voption) =
        match req with
        | ValueNone -> describeRequiredTable db logger name
        | ValueSome req -> db.UpdateTable logger name req

    static member Create() =
        new InMemoryDynamoDbClient(new ApiDb() |> Either1, true, ValueNone)
        :> IInMemoryDynamoDbClient

    static member Create(logger: ILogger) =
        new InMemoryDynamoDbClient(new ApiDb() |> Either1, true, ValueSome logger)
        :> IInMemoryDynamoDbClient

    static member Create(database: ApiDb) =
        new InMemoryDynamoDbClient(Either1 database, false, database.DefaultLogger)
        :> IInMemoryDynamoDbClient

    static member Create(database: ApiDb, logger: ILogger) =
        new InMemoryDynamoDbClient(Either1 database, false, ValueSome logger)
        :> IInMemoryDynamoDbClient

    static member Create(databaseId: DatabaseId, logger: ILogger) =
        new InMemoryDynamoDbClient(new ApiDb(databaseId = databaseId) |> Either1, true, ValueSome logger)
        :> IInMemoryDynamoDbClient

    static member Create(databaseId: DatabaseId) =
        new InMemoryDynamoDbClient(new ApiDb(databaseId = databaseId) |> Either1, true, ValueNone)
        :> IInMemoryDynamoDbClient

    static member Create(database: DistributedDatabase, target: DatabaseId) =
        let data = Either2 struct (database, target)
        new InMemoryDynamoDbClient(data, false, database.DefaultLogger) :> IInMemoryDynamoDbClient

    static member Create(database: DistributedDatabase, target: DatabaseId, logger: ILogger) =
        let data = Either2 struct (database, target)
        new InMemoryDynamoDbClient(data, false, ValueSome logger) :> IInMemoryDynamoDbClient

    interface IInMemoryDynamoDbClient with

        /// <inheritdoc />
        member _.Database = db

        /// <inheritdoc />
        member _.DistributedDatabase = ValueOption.map fstT parent

        /// <inheritdoc />
        member _.DebugView = db.DebugTables

        /// <inheritdoc />
        member _.ProcessingDelay
            with get () = artificialDelay
            and set value =
                if value < TimeSpan.Zero then invalidArg "value" "value must be greater than or equal to 0"
                artificialDelay <- value

        /// <inheritdoc />
        member _.AwaitAllSubscribers c =
            match parent with
            | ValueNone -> db.AwaitAllSubscribers defaultLogger c
            | ValueSome struct (h, _) -> h.AwaitAllSubscribers defaultLogger c

        /// <inheritdoc />
        member _.DistributedDebugView =
            match parent with
            | ValueNone -> Map.add DatabaseId.defaultId db.DebugTables Map.empty
            | ValueSome struct (h, _) -> h.DebugTables

        /// <inheritdoc />
        member _.GetTable name =
            LazyDebugTable(name, (describeRequiredTable db defaultLogger name).table)

        /// <inheritdoc />
        member this.GetDistributedTable databaseId name =
            match parent with
            | ValueNone when databaseId = DatabaseId.defaultId ->
                (this :> IInMemoryDynamoDbClient).GetTable name
            | ValueNone ->
                invalidArg (nameof databaseId) $"Invalid database id \"{databaseId}\". Use DatabaseId.defaultId or the GetTable method instead."
            | ValueSome struct (h, _) ->
                let db = h.GetDatabase defaultLogger databaseId

                db.TryDescribeTable defaultLogger name
                ?|> (fun x -> LazyDebugTable(name, x.table))
                |> ValueOption.defaultWith (fun _ -> invalidArg (nameof name) $"Invalid table \"{name}\" on database \"{databaseId}\"")

        /// <inheritdoc />
        member _.SubscribeToStream table (subscriber: StreamSubscriber) =
            let config = StreamSubscriber.getStreamConfig subscriber
            let fn = StreamSubscriber.getStreamSubscriber awsAccountId db.Id.regionId subscriber
            db.SubscribeToStream ValueNone table config fn

        /// <inheritdoc />
        member _.SetScanLimits limits = scanSizeLimits <- limits

        /// <inheritdoc />
        member this.AwsAccountId
            with get () = awsAccountId
            and set value = awsAccountId <- value

        /// <inheritdoc />
        member this.BatchGetItemAsync(requestItems, returnConsumedCapacity, cancellationToken) =
            let update = MultiClientOperations.BatchGetItem.batchGetItem database
            struct (requestItems, returnConsumedCapacity)
            |> execute (GetItem.Batch.inputs2 awsAccountId db.Id) update GetItem.Batch.output cancellationToken
        /// <inheritdoc />
        member this.BatchGetItemAsync(requestItems: Dictionary<string,KeysAndAttributes>, cancellationToken: CancellationToken): Task<BatchGetItemResponse> =
            let update = MultiClientOperations.BatchGetItem.batchGetItem database
            requestItems
            |> execute (GetItem.Batch.inputs3 awsAccountId db.Id) update GetItem.Batch.output cancellationToken
        /// <inheritdoc />
        member _.BatchGetItemAsync(request: BatchGetItemRequest, cancellationToken: CancellationToken): Task<BatchGetItemResponse> =
            let update = MultiClientOperations.BatchGetItem.batchGetItem database
            request
            |> execute (GetItem.Batch.inputs1 awsAccountId db.Id) update GetItem.Batch.output cancellationToken

        /// <inheritdoc />
        member _.BatchWriteItemAsync(requestItems: Dictionary<string,List<WriteRequest>>, cancellationToken: CancellationToken): Task<BatchWriteItemResponse> = 
            let update = MultiClientOperations.BatchWriteItem.batchPutItem database
            requestItems
            |> execute (PutItem.BatchWrite.inputs2 awsAccountId db.Id) update PutItem.BatchWrite.output cancellationToken
        /// <inheritdoc />
        member _.BatchWriteItemAsync(request: BatchWriteItemRequest, cancellationToken: CancellationToken): Task<BatchWriteItemResponse> =
            let update = MultiClientOperations.BatchWriteItem.batchPutItem database
            request
            |> execute (PutItem.BatchWrite.inputs1 awsAccountId db.Id) update PutItem.BatchWrite.output cancellationToken

        /// <inheritdoc />
        member _.CreateGlobalTableAsync(request, cancellationToken) =
            let update logger =
                let t = maybeUpdateTable db logger
                let dt = parent ?|> (fun struct (p, id) -> p.UpdateTable id logger)
                MultiClientOperations.UpdateTable.createGlobalTable awsAccountId parentDdb db.Id t dt

            flip (execute id update (asLazy id)) request cancellationToken

        /// <inheritdoc />
        member _.CreateTableAsync (tableName, keySchema, attributeDefinitions, provisionedThroughput, cancellationToken) =
            struct (tableName, keySchema, attributeDefinitions, provisionedThroughput)
            |> execute CreateTable.inputs2 db.AddTable (CreateTable.output awsAccountId) cancellationToken
        /// <inheritdoc />
        member _.CreateTableAsync(request, cancellationToken) =
            request
            |> execute CreateTable.inputs1 db.AddTable (CreateTable.output awsAccountId) cancellationToken

        /// <inheritdoc />
        member _.DeleteItemAsync(tableName, key, cancellationToken) =
            struct (tableName, key)
            |> execute DeleteItem.inputs2 db.Delete DeleteItem.output cancellationToken
        /// <inheritdoc />
        member _.DeleteItemAsync(tableName, key, returnValues, cancellationToken) =
            struct (tableName, key, returnValues)
            |> execute DeleteItem.inputs3 db.Delete DeleteItem.output cancellationToken
        /// <inheritdoc />
        member _.DeleteItemAsync(request, cancellationToken) =
            request
            |> execute DeleteItem.inputs1 db.Delete DeleteItem.output cancellationToken

        /// <inheritdoc />
        member _.DeleteTableAsync(tableName: string, cancellationToken: CancellationToken): Task<DeleteTableResponse> = 
            tableName
            |> executeAsync id db.DeleteTable (DeleteTable.output awsAccountId) cancellationToken
        /// <inheritdoc />
        member _.DeleteTableAsync(request: DeleteTableRequest, cancellationToken: CancellationToken): Task<DeleteTableResponse> =  
            request.TableName
            |> executeAsync id db.DeleteTable (DeleteTable.output awsAccountId) cancellationToken

        /// <inheritdoc />
        member _.DescribeGlobalTableAsync(request, cancellationToken) =
            let cluster =
                parent
                |> ValueOption.map fstT
                |> ValueOption.defaultWith (fun _ -> notSupported "This operation is only supported on clients which have a distributed database")
                
            if cluster.IsGlobalTable defaultLogger db.Id request.GlobalTableName |> not
            then clientError $"{request.GlobalTableName} in {db.Id} is not a global table"
                
            request.GlobalTableName
            |> execute id db.DescribeTable (DescribeTable.Global.output awsAccountId (ValueSome cluster) GlobalTableStatus.ACTIVE) cancellationToken

        /// <inheritdoc />
        member _.DescribeTableAsync(tableName: string, cancellationToken: CancellationToken): Task<DescribeTableResponse> =
            tableName
            |> execute id db.DescribeTable (DescribeTable.Local.output awsAccountId) cancellationToken
        /// <inheritdoc />
        member _.DescribeTableAsync(request: DescribeTableRequest, cancellationToken: CancellationToken): Task<DescribeTableResponse> =
            request.TableName
            |> execute id db.DescribeTable (DescribeTable.Local.output awsAccountId) cancellationToken

        /// <inheritdoc />
        member _.Dispose() = if disposeOfDatabase then db.Dispose()

        /// <inheritdoc />
        member _.GetItemAsync(tableName, key, cancellationToken) =  
            struct (tableName, key)
            |> execute GetItem.inputs2 db.Get GetItem.output cancellationToken
        /// <inheritdoc />
        member _.GetItemAsync(tableName, key, consistentRead, cancellationToken) =  
            struct (tableName, key, consistentRead)
            |> execute GetItem.inputs3 db.Get GetItem.output cancellationToken
        /// <inheritdoc />
        member _.GetItemAsync(request, cancellationToken) = 
            execute GetItem.inputs1 db.Get GetItem.output cancellationToken request

        /// <inheritdoc />
        member _.ListGlobalTablesAsync(request, cancellationToken) =
            let cluster =
                parent
                |> ValueOption.map fstT
                |> ValueOption.defaultWith (fun _ -> notSupported "This operation is only supported on clients which have a distributed database")
                
            execute DescribeTable.Global.List.inputs cluster.ListGlobalTables (DescribeTable.Global.List.output request.Limit) cancellationToken request

        /// <inheritdoc />
        member _.ListTablesAsync(cancellationToken) = 
            execute DescribeTable.List.inputs2 db.ListTables (DescribeTable.List.output Int32.MaxValue) cancellationToken ()
        /// <inheritdoc />
        member _.ListTablesAsync(exclusiveStartTableName: string, cancellationToken: CancellationToken): Task<ListTablesResponse> = 
            execute DescribeTable.List.inputs3 db.ListTables (DescribeTable.List.output Int32.MaxValue) cancellationToken exclusiveStartTableName
        /// <inheritdoc />
        member _.ListTablesAsync(exclusiveStartTableName, limit, cancellationToken) = 
            execute DescribeTable.List.inputs4 db.ListTables (DescribeTable.List.output limit) cancellationToken struct (exclusiveStartTableName, limit)
        /// <inheritdoc />
        member _.ListTablesAsync(limit: int, cancellationToken: CancellationToken): Task<ListTablesResponse> =
            execute DescribeTable.List.inputs5 db.ListTables (DescribeTable.List.output limit) cancellationToken limit
        /// <inheritdoc />
        member _.ListTablesAsync(request: ListTablesRequest, cancellationToken: CancellationToken): Task<ListTablesResponse> =
            execute DescribeTable.List.inputs1 db.ListTables (DescribeTable.List.output request.Limit) cancellationToken request

        /// <inheritdoc />
        member _.PutItemAsync(tableName, item, cancellationToken) =
            struct (tableName, item)
            |> execute PutItem.inputs2 db.Put PutItem.output cancellationToken
        /// <inheritdoc />
        member _.PutItemAsync(tableName, item, returnValues, cancellationToken) =
            struct (tableName, item, returnValues)
            |> execute PutItem.inputs3 db.Put PutItem.output cancellationToken
        /// <inheritdoc />
        member _.PutItemAsync(request, cancellationToken) =
            execute PutItem.inputs1 db.Put PutItem.output cancellationToken request

        /// <inheritdoc />
        member _.QueryAsync(request, cancellationToken) =
            execute (Query.inputs1 scanSizeLimits) db.Query Query.output cancellationToken request

        /// <inheritdoc />
        member _.ScanAsync(request, cancellationToken)  =
            execute (Scan.inputs1 scanSizeLimits) db.Query Scan.output cancellationToken request
        /// <inheritdoc />
        member _.ScanAsync(tableName: string, attributesToGet: List<string>, cancellationToken: CancellationToken): Task<ScanResponse> =
            execute (Scan.inputs2 scanSizeLimits) db.Query Scan.output cancellationToken (tableName, attributesToGet)
        /// <inheritdoc />
        member _.ScanAsync(tableName: string, scanFilter: Dictionary<string,Condition>, cancellationToken: CancellationToken): Task<ScanResponse> =
            execute (Scan.inputs3 scanSizeLimits) db.Query Scan.output cancellationToken (tableName, scanFilter)
        /// <inheritdoc />
        member _.ScanAsync(tableName: string, attributesToGet: List<string>, scanFilter: Dictionary<string,Condition>, cancellationToken: CancellationToken): Task<ScanResponse> =
            execute (Scan.inputs4 scanSizeLimits) db.Query Scan.output cancellationToken (tableName, attributesToGet, scanFilter)    

        /// <inheritdoc />
        member _.TransactGetItemsAsync(request, cancellationToken) =
            request
            |> execute GetItem.Transaction.inputs1 db.Gets GetItem.Transaction.output cancellationToken

        /// <inheritdoc />
        member _.TransactWriteItemsAsync(request, cancellationToken) =
            request
            |> execute TransactWriteItems.inputs1 db.TransactWrite TransactWriteItems.output cancellationToken

        /// <inheritdoc />
        member _.UpdateGlobalTableAsync(request, cancellationToken) =
            let update logger =
                let local = maybeUpdateTable db logger
                let ``global`` = parent ?|> (fun struct (p, id) -> p.UpdateTable id logger)
                MultiClientOperations.UpdateTable.updateGlobalTable awsAccountId parentDdb db.Id local ``global``

            flip (execute id update (asLazy id)) request cancellationToken

        /// <inheritdoc />
        member _.UpdateItemAsync(tableName, key, attributeUpdates, cancellationToken) =
            struct (tableName, key, attributeUpdates)
            |> execute (UpdateItem.inputs3 loggerOrDevNull) db.Update UpdateItem.output cancellationToken
        /// <inheritdoc />
        member _.UpdateItemAsync(tableName, key, attributeUpdates, returnValues, cancellationToken) =
            struct (tableName, key, attributeUpdates, returnValues)
            |> execute (UpdateItem.inputs2 loggerOrDevNull) db.Update UpdateItem.output cancellationToken
        /// <inheritdoc />
        member _.UpdateItemAsync(request, cancellationToken) =
            request
            |> execute (UpdateItem.inputs1 loggerOrDevNull) db.Update UpdateItem.output cancellationToken

        /// <inheritdoc />
        member this.UpdateTableAsync(tableName, provisionedThroughput, cancellationToken) =
            let r = UpdateTableRequest(tableName, provisionedThroughput)
            (this :> IInMemoryDynamoDbClient).UpdateTableAsync(r, cancellationToken)
        /// <inheritdoc />
        member _.UpdateTableAsync(request, cancellationToken) =
            let update logger =
                let local = maybeUpdateTable db logger
                let dt = parent ?|> (fun struct (p, id) -> p.UpdateTable id logger)
                MultiClientOperations.UpdateTable.updateTable awsAccountId parentDdb db.Id local dt

            flip (execute id update (asLazy id)) request cancellationToken

        /// <inheritdoc />
        member _.UpdateTimeToLiveAsync(request, cancellationToken) = notSupported "UpdateTimeToLiveAsync"

        /// <inheritdoc />
        member _.BatchExecuteStatementAsync(request, cancellationToken) = notSupported "BatchExecuteStatementAsync"

        /// <inheritdoc />
        member _.CreateBackupAsync(request, cancellationToken) = notSupported "CreateBackupAsync"

        /// <inheritdoc />
        member _.DeleteBackupAsync(request, cancellationToken) = notSupported "DeleteBackupAsync"

        /// <inheritdoc />
        member _.DeleteResourcePolicyAsync(request, cancellationToken) = notSupported "DeleteResourcePolicyAsync"

        /// <inheritdoc />
        member _.DescribeBackupAsync(request, cancellationToken) = notSupported "DescribeBackupAsync"

        /// <inheritdoc />
        member _.DescribeContinuousBackupsAsync(request, cancellationToken) = notSupported "DescribeContinuousBackupsAsync"

        /// <inheritdoc />
        member _.DescribeContributorInsightsAsync(request, cancellationToken) = notSupported "DescribeContributorInsightsAsync"

        /// <inheritdoc />
        member _.DescribeEndpointsAsync(request, cancellationToken) = notSupported "DescribeEndpointsAsync"

        /// <inheritdoc />
        member _.DescribeExportAsync(request, cancellationToken) = notSupported "DescribeExportAsync"

        /// <inheritdoc />
        member _.DescribeGlobalTableSettingsAsync(request, cancellationToken) = notSupported "DescribeGlobalTableSettingsAsync"

        /// <inheritdoc />
        member _.DescribeImportAsync(request, cancellationToken) = notSupported "DescribeImportAsync"

        /// <inheritdoc />
        member _.DescribeKinesisStreamingDestinationAsync(request, cancellationToken) = notSupported "DescribeKinesisStreamingDestinationAsync"

        /// <inheritdoc />
        member _.DescribeLimitsAsync(request, cancellationToken) = notSupported "DescribeLimitsAsync"

        /// <inheritdoc />
        member _.DescribeTableReplicaAutoScalingAsync(request, cancellationToken) = notSupported "DescribeTableReplicaAutoScalingAsync"
        
        /// <inheritdoc />
        member _.DescribeTimeToLiveAsync(tableName: string, cancellationToken: CancellationToken): Task<DescribeTimeToLiveResponse> = notSupported "DescribeTimeToLiveAsync"
        /// <inheritdoc />
        member _.DescribeTimeToLiveAsync(request: DescribeTimeToLiveRequest, cancellationToken: CancellationToken): Task<DescribeTimeToLiveResponse> = notSupported "DescribeTimeToLiveAsync"

        /// <inheritdoc />
        member _.DetermineServiceOperationEndpoint(request) = notSupported "DetermineServiceOperationEndpoint"

        /// <inheritdoc />
        member _.DisableKinesisStreamingDestinationAsync(request, cancellationToken) = notSupported "DisableKinesisStreamingDestinationAsync"

        /// <inheritdoc />
        member _.EnableKinesisStreamingDestinationAsync(request, cancellationToken) = notSupported "EnableKinesisStreamingDestinationAsync"

        /// <inheritdoc />
        member _.ExecuteStatementAsync(request, cancellationToken) = notSupported "ExecuteStatementAsync"

        /// <inheritdoc />
        member _.ExecuteTransactionAsync(request, cancellationToken) = notSupported "ExecuteTransactionAsync"

        /// <inheritdoc />
        member _.ExportTableToPointInTimeAsync(request, cancellationToken) = notSupported "ExportTableToPointInTimeAsync"

        /// <inheritdoc />
        member _.GetResourcePolicyAsync(request, cancellationToken) = notSupported "GetResourcePolicyAsync"

        /// <inheritdoc />
        member _.ImportTableAsync(request, cancellationToken) = notSupported "ImportTableAsync"

        /// <inheritdoc />
        member _.ListBackupsAsync(request, cancellationToken) = notSupported "ListBackupsAsync"

        /// <inheritdoc />
        member _.ListContributorInsightsAsync(request, cancellationToken) = notSupported "ListContributorInsightsAsync"

        /// <inheritdoc />
        member _.ListExportsAsync(request, cancellationToken) = notSupported "ListExportsAsync"

        /// <inheritdoc />
        member _.ListImportsAsync(request, cancellationToken) = notSupported "ListImportsAsync"

        /// <inheritdoc />
        member _.ListTagsOfResourceAsync(request, cancellationToken) = notSupported "ListTagsOfResourceAsync"

        /// <inheritdoc />
        member _.PutResourcePolicyAsync(request, cancellationToken) = notSupported "PutResourcePolicyAsync"

        /// <inheritdoc />
        member _.RestoreTableFromBackupAsync(request, cancellationToken) = notSupported "RestoreTableFromBackupAsync"

        /// <inheritdoc />
        member _.RestoreTableToPointInTimeAsync(request, cancellationToken) = notSupported "RestoreTableToPointInTimeAsync"

        /// <inheritdoc />
        member _.TagResourceAsync(request, cancellationToken) = notSupported "TagResourceAsync"

        /// <inheritdoc />
        member _.UntagResourceAsync(request, cancellationToken) = notSupported "UntagResourceAsync"

        /// <inheritdoc />
        member _.UpdateContinuousBackupsAsync(request, cancellationToken) = notSupported "UpdateContinuousBackupsAsync"

        /// <inheritdoc />
        member _.UpdateContributorInsightsAsync(request, cancellationToken) = notSupported "UpdateContributorInsightsAsync"

        /// <inheritdoc />
        member _.UpdateGlobalTableSettingsAsync(request, cancellationToken) = notSupported "UpdateGlobalTableSettingsAsync"

        /// <inheritdoc />
        member _.UpdateKinesisStreamingDestinationAsync(request, cancellationToken) = notSupported "UpdateKinesisStreamingDestinationAsync"

        /// <inheritdoc />
        member _.UpdateTableReplicaAutoScalingAsync(request, cancellationToken) = notSupported "UpdateTableReplicaAutoScalingAsync"

        /// <inheritdoc />
        member _.Config = notSupported "Config"

        /// <inheritdoc />
        member _.Paginators = notSupported "Paginators"
