module TestDynamo.Settings

open System
open System.Net
open Amazon.Runtime

/// <summary>
/// Min and max size of a compiled query cache. This is not the same as a query results cache
/// </summary>
let internal QueryCacheSize = ValueSome struct (500, 1000)

/// <summary>
/// The default value to use as the AWS account id for all ITestDynamo clients
/// </summary>
let mutable DefaultAwsAccountId = "123456789012"

/// <summary>
/// An artificial delay to add to dynamodb requests.
/// If included, this can help to simulate IO by deferring execution to another thread
/// 
/// Default: no delay
/// </summary>
let mutable DefaultClientResponseDelay = System.TimeSpan.Zero

/// <summary>
/// <para>
/// Max amount of time to wait when attempting to synchronously lock a mutable value
/// This timeout will come into play if you are executing 1000s of write requests per second on a single database 
/// </para>
/// <para>
/// Default: 10 seconds
/// </para>
/// </summary>
let mutable MutableValueLockTimeout = TimeSpan.FromSeconds(float 10)

/// <summary>
/// <para>
/// Max amount of time to wait when attempting to asynchronously lock a database for mutation
/// This timeout will come into play if you are executing 1000s of write requests
/// per second on global table for a long period of time
/// </para>
/// <para>
/// Default: 1 minute
/// </para>
/// </summary>
let mutable DatabaseLockWaitTime = TimeSpan.FromMinutes(float 1)

/// <summary>
/// <para>
/// Maximum number of operators and functions allowed in a query. This is an AWS imposed limit
/// If increasing this value, you may need to increase the stack size in your dotnet environment
/// </para>
/// <para>
/// https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-expression-parameters
/// </para>
/// 
/// <para>
/// Default: 300
/// </para> 
/// </summary>
let mutable ExpressionOperatorLimit = 300

/// <summary>
/// <para>
/// The maximum time expected for items to replicate items between regions
/// Items which have been deleted are retained for this period in order to resolve conflicts in replication
/// </para>
/// <para> 
/// Default: 10 seconds
/// </para>
/// </summary>
let mutable MaxReplicationTime = TimeSpan.FromSeconds(float 10)

/// <summary>
/// <para>
/// The region of any databases created in a non-global context
/// </para>
/// <para> 
/// Default: "us-east-1"
/// </para>
/// </summary>
let mutable DefaultRegion = "us-east-1"

/// <summary>
/// If true, the DynamoDbClient will dispose of any MemoryStreams after reading the stream data
/// </summary>
let mutable DisposeOfInputMemoryStreams = false

module TransactReadSettings =

    /// <summary>
    /// The max number of batch write items in a single request
    /// Default 100
    /// </summary>
    let mutable MaxItemsPerRequest = 100

module TransactWriteSettings =
    /// <summary>
    /// Values to use as the SizeEstimateRangeGB ItemCollectionMetric in a Transact write response
    /// Default (1, 1)
    /// </summary>
    let mutable SizeRangeEstimateResponse = struct (1.0, 1.0)

    /// <summary>
    /// The max number of batch write items in a single request
    /// Default 100
    /// </summary>
    let mutable MaxItemsPerRequest = 100

    /// <summary>
    /// The max size of data in a batch write request
    /// Default 4MB
    /// </summary>
    let mutable MaxRequestDataBytes = 4_000_000

    /// <summary>
    /// The amount of time to keep a ClientRequestToken (idempotency key) before it expires. The real dynamodb value
    /// for this key is 10minutes
    /// Default: 10 seconds 
    /// </summary>
    let mutable ClientRequestTokenTTL = TimeSpan.FromSeconds(float 10)

module AmazonWebServiceResponse =

    /// <summary>
    /// <para>
    /// A constant value to put as the ContentLength in all dynamodb responses
    /// </para>
    /// <para>
    /// Default: 100
    /// </para> 
    /// </summary>
    let mutable ResponseContentLength = 100

    /// <summary>
    /// <para>
    /// A constant value to put as the HttpStatusCode in all dynamodb responses
    /// </para>
    /// <para>
    /// Default: OK
    /// </para> 
    /// </summary>
    let mutable HttpStatusCode = HttpStatusCode.OK

    /// <summary>
    /// <para>
    /// A constant value to put as the ChecksumAlgorithm in all dynamodb responses
    /// </para>
    /// <para>
    /// Default: NONE
    /// </para> 
    /// </summary>
    let mutable ChecksumAlgorithm = TestDynamo.GeneratedCode.Dtos.CoreChecksumAlgorithm.NONE

    /// <summary>
    /// <para>
    /// A constant value to put as the ChecksumValidationStatus in all dynamodb responses
    /// </para>
    /// <para>
    /// Default: NOT_VALIDATED
    /// </para> 
    /// </summary>
    let mutable ChecksumValidationStatus = TestDynamo.GeneratedCode.Dtos.ChecksumValidationStatus.NOT_VALIDATED

    /// <summary>
    /// <para>
    /// A generator to create the RequestId in all dynamodb responses
    /// </para>
    /// <para>
    /// Default: new Guid
    /// </para> 
    /// </summary>
    let mutable RequestId = new System.Func<string>(fun (_: unit) -> System.Guid.NewGuid().ToString())

module Logging =

    /// <summary>
    /// <para>
    /// Log any client exceptions which occur inside the database. Client exceptions are logged as "Information"
    /// Non client exceptions will always be logged
    /// </para>
    ///  
    /// <para>
    /// Default: true
    /// </para>
    /// </summary>
    let mutable LogDatabaseExceptions = true

    /// <summary>
    /// <para>
    /// TestDynamo uses extensive logging for information and debugging purposes
    /// Loggers are considered immutable, even though the underlying ILogger may not be, and may be shared by
    /// multiple threads. This can get confusing
    /// </para>
    /// <para>
    /// To overcome this, TestDynamo has a custom log format to differentiate logs from different scopes,
    /// which takes the format: " {ScopeId} > {scope indentation} {message}" e.g.
    /// </para>
    /// <code>
    /// 12345 > Operation 1, Message 1.1
    /// 12345 >   Operation 1, Scoped Message 1.1.1
    /// 12345 >   Operation 1, Scoped Message 1.1.23
    /// 54321 > Operation 2, Message 2.1
    /// 12345 > Operation 1, Message 1.2
    /// 54321 >   Operation 2, Scoped Message 2.1.1
    /// </code>
    /// <para>
    /// If set to false, custom log formatting is removed globally
    /// </para>
    /// </summary>
    let internal UseDefaultLogFormatting = true

module BatchItems =

    /// <summary>
    /// <para>
    /// Batch get item max page size in bytes
    /// </para>
    /// <para>
    /// Default: 16MB
    /// </para>
    /// </summary>
    let mutable BatchGetItemMaxSizeBytes = 16_000_000

    /// <summary>
    /// <para>
    /// Batch put item max page size in bytes
    /// </para>
    /// <para>
    /// Default: 16MB
    /// </para>
    /// </summary>
    let mutable BatchPutItemMaxSizeBytes = 16_000_000

    /// <summary>
    /// <para>
    /// Max items allowed in a batch write request. This is an AWS limitation
    /// </para>
    /// <para>
    /// Default: 25
    /// </para>
    /// </summary>
    let mutable MaxBatchWriteItems = 25

module ScanSizeLimits =

    /// <summary>Default value for ScanLimits.maxScanItems. See ScanLimits class for more details</summary>
    let mutable DefaultMaxScanItems = 2_000

    /// <summary>Default value for ScanLimits.maxPageSizeBytes. See ScanLimits class for more details</summary>
    let mutable DefaultMaxPageSizeBytes = 1_000_000

module SupressErrorsAndWarnings =
    /// <summary>
    /// <para>
    /// Ignore any errors related to invalid aws account ids by routing all requests to the same account
    /// </para>
    ///
    /// <para>
    /// In general, TestDynamo does not deal with aws account ids. However, there are some request types which
    /// use an ARN, containing an aws account id.
    /// </para>
    ///
    /// <para>
    /// If this value is false, and an incorrect aws account id is found, an exception is thrown
    /// </para>
    /// <para>
    /// If this value is true, and an incorrect aws account id is found, the aws account id is treated as if it were correct
    /// </para>
    /// </summary>
    let mutable AwsAccountIdErrors = false
