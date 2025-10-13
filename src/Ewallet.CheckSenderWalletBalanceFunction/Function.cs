using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Npgsql;
using Dapper;

namespace Ewallet.CheckSenderWalletBalanceFunction;

public class Function
{
    public static async Task Main()
    {
        Func<CheckSenderWalletBalanceRequest , ILambdaContext, Task<CheckSenderWalletBalanceResponse>> handler = CheckSenderWalletBalanceHandler;
        await LambdaBootstrapBuilder.Create(handler, new SourceGeneratorLambdaJsonSerializer<LambdaFunctionJsonSerializerContext>())
            .Build()
            .RunAsync();
    }

    public static async Task<CheckSenderWalletBalanceResponse> CheckSenderWalletBalanceHandler(CheckSenderWalletBalanceRequest request, ILambdaContext context)
    {
        var connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");
        if (string.IsNullOrEmpty(connectionString))
            throw new InvalidOperationException("Missing DB_CONNECTION_STRING environment variable.");

        await using var connection = new NpgsqlConnection(connectionString);
        const string sql = @"
            SELECT id, user_id, balance
            FROM wallet.account
            WHERE user_id = @UserId;
        ";
        var account = await connection.QuerySingleOrDefaultAsync<Account>(sql, new { UserId = request.SenderUserId });
        if (account == null)
        {
            context.Logger.LogError($"No account found for sender: {request.SenderUserId}");
            throw new AccountNotFoundException($"No account found for sender: {request.SenderUserId} not found");
        }

        if (account.Balance < request.Amount)
        {
            context.Logger.LogError($"Insufficient funds: balance={account.Balance}, required={request.Amount}");
            throw new InsufficientBalanceException($"Insufficient funds: balance={account.Balance}, required={request.Amount}");
        }

        context.Logger.LogInformation($"Sufficient funds: balance={account.Balance}, required={request.Amount}");

        return new CheckSenderWalletBalanceResponse(account.Id, request.ReceiverUserId, request.Amount);
    }
}

[JsonSerializable(typeof(Account))]
[JsonSerializable(typeof(CheckSenderWalletBalanceRequest))]
[JsonSerializable(typeof(CheckSenderWalletBalanceResponse))]
public partial class LambdaFunctionJsonSerializerContext : JsonSerializerContext
{
    // By using this partial class derived from JsonSerializerContext, we can generate reflection free JSON Serializer code at compile time
    // which can deserialize our class and properties. However, we must attribute this class to tell it what types to generate serialization code for.
    // See https://docs.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-source-generation
}

public sealed record CheckSenderWalletBalanceRequest(
    string SenderUserId,
    string ReceiverUserId,
    decimal Amount
);

public sealed record CheckSenderWalletBalanceResponse(
    string SenderAccountId,
    string ReceiverUserId,
    decimal Amount);

public sealed class Account
{
    public string Id { get; set; } = null!;

    public string UserId { get; set; } = null!;

    public decimal Balance { get; set; } = 0.0m;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}


public static class ErrorCode
{
    public const string FailedInsufficient = "FAILED.CHECK_SENDER.INSUFFICIENT";
    public const string FailedAccountNotFound = "FAILED.CHECK_SENDER.ACCOUNT_NOT_FOUND";
}

public class InsufficientBalanceException : Exception
{
    public readonly string ErrorCode = CheckSenderWalletBalanceFunction.ErrorCode.FailedInsufficient;
    public InsufficientBalanceException(string message) : base(message) { }
}

public class AccountNotFoundException : Exception
{
    public readonly string ErrorCode = CheckSenderWalletBalanceFunction.ErrorCode.FailedAccountNotFound;
    public AccountNotFoundException(string message) : base(message) { }
}
