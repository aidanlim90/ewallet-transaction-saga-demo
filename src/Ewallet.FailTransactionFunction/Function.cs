using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;

namespace Ewallet.FailTransactionFunction;

public class Function
{
    private static AmazonDynamoDBClient? _dynamoDbClient;

    private static async Task Main()
    {
        Func<FailTransactionRequest, ILambdaContext, Task<string>> handler = FunctionHandler;
        await LambdaBootstrapBuilder
            .Create(
                handler,
                new SourceGeneratorLambdaJsonSerializer<LambdaFunctionJsonSerializerContext>()
            )
            .Build()
            .RunAsync();
    }

    public static async Task<string> FunctionHandler(
        FailTransactionRequest request,
        ILambdaContext context
    )
    {
        _dynamoDbClient ??= new AmazonDynamoDBClient();
        var tableName = Environment.GetEnvironmentVariable("EWALLET_TABLE") ?? "Ewallet";
        try
        {
            var updateRequest = new UpdateItemRequest
            {
                TableName = tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = request.TransactionId },
                    ["SK"] = new AttributeValue { S = request.SenderUserId },
                },
                UpdateExpression = "SET #s = :failed",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "Status" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":failed"] = new AttributeValue { S = "FAILED" },
                },
                ReturnValues = ReturnValue.UPDATED_NEW,
            };

            var response = await _dynamoDbClient.UpdateItemAsync(updateRequest);

            context.Logger.LogInformation(
                $"Transaction {request.TransactionId} updated to FAILED."
            );

            return $"Transaction {request.TransactionId} marked as FAILED.";
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Failed to update transaction {request.TransactionId}: {ex}");
            throw;
        }
    }
}

[JsonSerializable(typeof(FailTransactionRequest))]
public partial class LambdaFunctionJsonSerializerContext : JsonSerializerContext
{
    // By using this partial class derived from JsonSerializerContext, we can generate reflection free JSON Serializer code at compile time
    // which can deserialize our class and properties. However, we must attribute this class to tell it what types to generate serialization code for.
    // See https://docs.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-source-generation
}

public class FailTransactionRequest
{
    public string TransactionId { get; set; } = null!;

    public string SenderUserId { get; set; } = null!;
}
