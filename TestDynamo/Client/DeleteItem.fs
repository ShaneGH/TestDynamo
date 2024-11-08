﻿module TestDynamo.Client.DeleteItem

open TestDynamo
open TestDynamo.Client.PutItem
open Amazon.DynamoDBv2
open Amazon.DynamoDBv2.Model
open TestDynamo.Model
open TestDynamo.Client
open TestDynamo.Client.Query
open TestDynamo.Utils
open TestDynamo.Data.Monads.Operators
open TestDynamo.Client.ItemMapper

type DynamoAttributeValue = Amazon.DynamoDBv2.Model.AttributeValue
type MList<'a> = System.Collections.Generic.List<'a>

let inputs1 (req: DeleteItemRequest) =
    // ReturnValuesOnConditionCheckFailure https://aws.amazon.com/blogs/database/handle-conditional-write-errors-in-high-concurrency-scenarios-with-amazon-dynamodb/

    if req.Expected <> null && req.Expected.Count <> 0 then notSupported "Legacy Expected parameter is not supported"
    if req.ConditionalOperator <> null then notSupported "Legacy ConditionalOperator parameter is not supported"

    { key = ItemMapper.itemFromDynamodb "$" req.Key
      conditionExpression =
          { conditionExpression = req.ConditionExpression |> filterExpression
            tableName = req.TableName |> CSharp.mandatory "TableName is mandatory"
            returnValues = mapReturnValues req.ReturnValues
            expressionAttrNames = req.ExpressionAttributeNames |> expressionAttrNames
            expressionAttrValues = req.ExpressionAttributeValues |> expressionAttrValues } } : DeleteItemArgs<_>

let inputs2 struct (
    tableName: string,
    key: Dictionary<string, DynamoAttributeValue>) =

    DeleteItemRequest (tableName, key) |> inputs1

let inputs3 struct (
    tableName: string,
    key: Dictionary<string, DynamoAttributeValue>,
    returnValue: ReturnValue) =

    DeleteItemRequest (tableName, key, returnValue) |> inputs1

let private newDict () = Dictionary<_, _>()
let output databaseId (items: Map<string, AttributeValue> voption) =
    let output = Shared.amazonWebServiceResponse<DeleteItemResponse>()
    output.Attributes <-
        items
        ?|> itemToDynamoDb
        |> ValueOption.defaultWith newDict
    output