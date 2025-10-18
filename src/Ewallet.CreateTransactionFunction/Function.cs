using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;

namespace Ewallet.CreateTransactionFunction;

public class Function
{
    private static AmazonDynamoDBClient? _dynamoDbClient;

    public static async Task Main()
    {
        Func<CreateTransactionRequest, ILambdaContext, Task<CreateTransactionResponse>> handler =
            CreateTransactionHandler;

        await LambdaBootstrapBuilder
            .Create(
                handler,
                new SourceGeneratorLambdaJsonSerializer<LambdaFunctionJsonSerializerContext>()
            )
            .Build()
            .RunAsync();
    }

    public static async Task<CreateTransactionResponse> CreateTransactionHandler(
        CreateTransactionRequest request,
        ILambdaContext context
    )
    {
        _dynamoDbClient ??= new AmazonDynamoDBClient();
        var tableName = Environment.GetEnvironmentVariable("EWALLET_TABLE") ?? "Ewallet";
        var now = $"CREATED#{DateTime.UtcNow.ToString("o")}";
        var transactionId = $"TRANSACTION#{Guid.NewGuid()}";
        var idempotentPk = $"IDEMPOTENT#{request.IdempotentKey}";

        var idempotentItem = new Dictionary<string, AttributeValue>
        {
            ["PK"] = new AttributeValue { S = idempotentPk },
            ["SK"] = new AttributeValue { S = idempotentPk },
            ["CreatedAt"] = new AttributeValue { S = now },
        };

        var transactionItem = new Dictionary<string, AttributeValue>
        {
            ["PK"] = new AttributeValue { S = transactionId },
            ["SK"] = new AttributeValue { S = now },
            ["IdempotentKey"] = new AttributeValue { S = idempotentPk },
            ["SenderId"] = new AttributeValue { S = request.SenderId },
            ["ReceiverId"] = new AttributeValue { S = request.ReceiverId },
            ["Amount"] = new AttributeValue { N = request.Amount.ToString() },
            ["Status"] = new AttributeValue { S = "PENDING" },
            ["Type"] = new AttributeValue { S = request.Type },
            ["Details"] = new AttributeValue { S = request.Details ?? string.Empty },
            ["CreatedAt"] = new AttributeValue { S = now },
            ["UpdatedAt"] = new AttributeValue { S = now },
        };

        var transactRequest = new TransactWriteItemsRequest
        {
            TransactItems = new List<TransactWriteItem>
            {
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = tableName,
                        Item = idempotentItem,
                        ConditionExpression = "attribute_not_exists(PK)",
                    },
                },
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = tableName,
                        Item = transactionItem,
                        ConditionExpression = "attribute_not_exists(PK)",
                    },
                },
            },
        };

        try
        {
            await _dynamoDbClient.TransactWriteItemsAsync(transactRequest);
            context.Logger.LogInformation($"Transaction {transactionId} created successfully.");
            return new CreateTransactionResponse(
                transactionId,
                "CREATED",
                $"Transaction {transactionId} created successfully."
            );
        }
        catch (TransactionCanceledException)
        {
            context.Logger.LogInformation(
                $"Duplicate idempotent key {request.IdempotentKey}, skipping insert."
            );
            return new CreateTransactionResponse(
                transactionId,
                "DUPLICATE",
                $"Transaction for idempotent key {request.IdempotentKey} already exists."
            );
        }
        catch (Exception ex)
        {
            var wholeMessage = ex.ToString();
            context.Logger.LogError($"Failed to create transaction: {wholeMessage}");
            return new CreateTransactionResponse(transactionId, "FAILED", $"Error: {wholeMessage}");
        }
    }
}

#region Models and Serializer Context

[JsonSerializable(typeof(CreateTransactionRequest))]
[JsonSerializable(typeof(CreateTransactionResponse))]
public partial class LambdaFunctionJsonSerializerContext : JsonSerializerContext { }

public sealed record CreateTransactionRequest(
    string IdempotentKey,
    string SenderId,
    string ReceiverId,
    decimal Amount,
    string Type,
    string? Details
);

public sealed record CreateTransactionResponse(string TransactionId, string Result, string Message);

#endregion
