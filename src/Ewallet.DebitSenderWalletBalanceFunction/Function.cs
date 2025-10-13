using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Npgsql;
using Dapper;

namespace Ewallet.DebitSenderWalletBalanceFunction;

public class Function
{
    private static async Task Main()
    {
        Func<DebitSenderWalletBalanceRequest, ILambdaContext, Task<DebitSenderWalletBalanceResponse>> handler = DebitSenderWalletBalanceHandler;
        await LambdaBootstrapBuilder.Create(handler, new SourceGeneratorLambdaJsonSerializer<LambdaFunctionJsonSerializerContext>())
            .Build()
            .RunAsync();
    }

    public static async Task<DebitSenderWalletBalanceResponse> DebitSenderWalletBalanceHandler(DebitSenderWalletBalanceRequest request, ILambdaContext context)
    {
        try
        {
            var connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");
            if (string.IsNullOrEmpty(connectionString))
                throw new InvalidOperationException("Missing DB_CONNECTION_STRING environment variable.");

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            const string selectAccountForUpdateSql = @"
                SELECT id, user_id, balance FROM wallet.account WHERE id = @SenderAccountId FOR UPDATE;
            ";
            var account = await connection.QuerySingleOrDefaultAsync<Account>(
            selectAccountForUpdateSql,
            new { SenderAccountId = request.SenderAccountId },
            transaction);

            if (account == null)
            {
                context.Logger.LogError($"No account found for sender: {request.SenderAccountId}");
                throw new AccountNotFoundException($"No account found for sender: {request.SenderAccountId} not found");
            }

            var updateWalletBalanceSql = @"
                UPDATE wallet.account
                SET balance = balance - @Amount,
                    updated_at = NOW() AT TIME ZONE 'UTC'
                WHERE id = @Id;
            ";
            await connection.ExecuteAsync(
            updateWalletBalanceSql,
            new { Amount = request.Amount, Id = account.Id },
            transaction);

            await transaction.CommitAsync();
            context.Logger.LogInformation($"Debited {request.Amount} from sender {request.SenderAccountId}. New balance: {account.Balance - request.Amount}");
            return new DebitSenderWalletBalanceResponse(request.ReceiverUserId, request.Amount);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Unexpected error debiting sender {request.SenderAccountId}: {ex.Message}");
            throw new UnexpectedException($"Unexpected error debiting sender {request.SenderAccountId}: {ex.Message}");
        }
    }
}

[JsonSerializable(typeof(Account))]
[JsonSerializable(typeof(DebitSenderWalletBalanceRequest))]
[JsonSerializable(typeof(DebitSenderWalletBalanceResponse))]
public partial class LambdaFunctionJsonSerializerContext : JsonSerializerContext
{
    // By using this partial class derived from JsonSerializerContext, we can generate reflection free JSON Serializer code at compile time
    // which can deserialize our class and properties. However, we must attribute this class to tell it what types to generate serialization code for.
    // See https://docs.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-source-generation
}

public sealed record DebitSenderWalletBalanceRequest(
    string SenderAccountId,
    string ReceiverUserId,
    decimal Amount
);

public sealed record DebitSenderWalletBalanceResponse(
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
    public const string Unexpected = "FAILED.DEBIT_SENDER";
    public const string FailedAccountNotFound = "FAILED.DEBIT_SENDER.ACCOUNT_NOT_FOUND";
}

public class AccountNotFoundException : Exception
{
    public readonly string ErrorCode = DebitSenderWalletBalanceFunction.ErrorCode.FailedAccountNotFound;
    public AccountNotFoundException(string message) : base(message) { }
}
public class UnexpectedException : Exception
{
    public readonly string ErrorCode = DebitSenderWalletBalanceFunction.ErrorCode.Unexpected;
    public UnexpectedException(string message) : base(message) { }
}
