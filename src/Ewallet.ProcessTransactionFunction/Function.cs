using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;

namespace Ewallet.ProcessTransactionFunction;

public class Function
{
    private static AmazonDynamoDBClient? _dynamoDbClient;

    private static async Task Main()
    {
        Func<ProcessTransactionRequest, ILambdaContext, Task<ProcessTransactionResponse>> handler =
            FunctionHandler;
        await LambdaBootstrapBuilder
            .Create(
                handler,
                new SourceGeneratorLambdaJsonSerializer<LambdaFunctionJsonSerializerContext>()
            )
            .Build()
            .RunAsync();
    }

    public static async Task<ProcessTransactionResponse> FunctionHandler(
        ProcessTransactionRequest request,
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
                UpdateExpression = "SET #s = :processing",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "Status" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":processing"] = new AttributeValue { S = "PROCESSING" },
                },
                ReturnValues = ReturnValue.UPDATED_NEW,
            };

            var response = await _dynamoDbClient.UpdateItemAsync(updateRequest);

            context.Logger.LogInformation(
                $"Transaction {request.TransactionId} updated to FAILED."
            );

            return new ProcessTransactionResponse(
                request.TransactionId,
                request.ReceiverUserId,
                request.SenderUserId,
                request.Amount
            );
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Failed to update transaction {request.TransactionId}: {ex}");
            throw;
        }
    }
}

[JsonSerializable(typeof(ProcessTransactionRequest))]
[JsonSerializable(typeof(ProcessTransactionResponse))]
public partial class LambdaFunctionJsonSerializerContext : JsonSerializerContext
{
    // By using this partial class derived from JsonSerializerContext, we can generate reflection free JSON Serializer code at compile time
    // which can deserialize our class and properties. However, we must attribute this class to tell it what types to generate serialization code for.
    // See https://docs.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-source-generation
}

public sealed record ProcessTransactionRequest(
    string TransactionId,
    string SenderUserId,
    string ReceiverUserId,
    string Amount
);

public sealed record ProcessTransactionResponse(
    string TransactionId,
    string ReceiverUserId,
    string SenderUserId,
    string Amount
);
