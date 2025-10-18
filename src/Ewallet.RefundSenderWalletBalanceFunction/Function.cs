using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Dapper;
using Npgsql;

namespace Ewallet.RefundSenderWalletBalanceFunction;

public class Function
{
    private static async Task Main()
    {
        Func<
            RefundSenderWalletBalanceRequest,
            ILambdaContext,
            Task<RefundSenderWalletBalanceResponse>
        > handler = RefundSenderWalletBalanceHandler;

        await LambdaBootstrapBuilder
            .Create(
                handler,
                new SourceGeneratorLambdaJsonSerializer<LambdaFunctionJsonSerializerContext>()
            )
            .Build()
            .RunAsync();
    }

    public static async Task<RefundSenderWalletBalanceResponse> RefundSenderWalletBalanceHandler(
        RefundSenderWalletBalanceRequest request,
        ILambdaContext context
    )
    {
        var connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");
        if (string.IsNullOrEmpty(connectionString))
            throw new InvalidConnectionStringException(
                "Missing DB_CONNECTION_STRING environment variable."
            );

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            // 1️⃣ Insert inbox entry — detect duplicate refund transactions
            const string insertInboxSql =
                @"
                INSERT INTO wallet.inbox (
                    transaction_id, type, amount, created_at, updated_at
                )
                VALUES (
                    @TransactionId, @Type, @Amount,
                    NOW() AT TIME ZONE 'UTC', NOW() AT TIME ZONE 'UTC'
                );";

            try
            {
                await connection.ExecuteAsync(
                    insertInboxSql,
                    new
                    {
                        TransactionId = "REFUND#" + request.TransactionId,
                        Type = "REFUND_SENDER",
                        Amount = request.Amount,
                    },
                    transaction
                );
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                context.Logger.LogError(
                    $"Duplicate refund transaction detected: {request.TransactionId}"
                );
                throw new DuplicateTransactionException(
                    $"Duplicate transaction_id {request.TransactionId} detected. Step Function retry suppressed."
                );
            }

            const string selectAccountSql =
                @"
                SELECT id, user_id, balance
                FROM wallet.account
                WHERE user_id = @SenderUserId
                FOR UPDATE;";

            var account = await connection.QuerySingleOrDefaultAsync<Account>(
                selectAccountSql,
                new { SenderUserId = request.SenderUserId },
                transaction
            );

            if (account == null)
            {
                context.Logger.LogError($"No account found for sender: {request.SenderUserId}");
                throw new AccountNotFoundException(
                    $"No account found for sender: {request.SenderUserId}"
                );
            }

            const string updateBalanceSql =
                @"
                UPDATE wallet.account
                SET balance = balance + @Amount,
                    updated_at = NOW() AT TIME ZONE 'UTC'
                WHERE id = @Id;";

            await connection.ExecuteAsync(
                updateBalanceSql,
                new { Amount = request.Amount, Id = account.Id },
                transaction
            );

            await transaction.CommitAsync();

            context.Logger.LogInformation(
                $"Refunded {request.Amount} to sender {request.SenderUserId}. New balance: {account.Balance + request.Amount}"
            );

            return new RefundSenderWalletBalanceResponse(
                request.TransactionId,
                request.ReceiverUserId,
                request.SenderUserId,
                request.SenderAccountId,
                request.ReceiverAccountId,
                request.Amount
            );
        }
        catch (AccountNotFoundException)
        {
            await transaction.RollbackAsync();
            throw;
        }
        catch (DuplicateTransactionException)
        {
            await transaction.RollbackAsync();
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            context.Logger.LogError(
                $"Unexpected error refunding sender {request.SenderUserId}: {ex.Message}"
            );
            throw new UnexpectedException(
                $"Unexpected error refunding sender {request.SenderUserId}: {ex.Message}"
            );
        }
    }
}

#region Models & Serialization

[JsonSerializable(typeof(Account))]
[JsonSerializable(typeof(RefundSenderWalletBalanceRequest))]
[JsonSerializable(typeof(RefundSenderWalletBalanceResponse))]
public partial class LambdaFunctionJsonSerializerContext : JsonSerializerContext { }

public sealed record RefundSenderWalletBalanceRequest(
    string TransactionId,
    string ReceiverUserId,
    string SenderUserId,
    string SenderAccountId,
    string ReceiverAccountId,
    decimal Amount
);

public sealed record RefundSenderWalletBalanceResponse(
    string TransactionId,
    string ReceiverUserId,
    string SenderUserId,
    string SenderAccountId,
    string ReceiverAccountId,
    decimal Amount
);

public sealed class Account
{
    public string Id { get; set; } = null!;
    public string UserId { get; set; } = null!;
    public decimal Balance { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTime.UtcNow;
}

#endregion

#region Error Codes & Exceptions

public static class ErrorCode
{
    public const string Unexpected = "FAILED.REFUND_SENDER";
    public const string FailedAccountNotFound = "FAILED.REFUND_SENDER.ACCOUNT_NOT_FOUND";
    public const string DuplicateTransaction = "FAILED.REFUND_SENDER.DUPLICATE";
    public const string InvalidConnectionString = "FAILED.REFUND_SENDER.INVALID_CONNECTION_STRING";
    public const string InvalidAmountType = "FAILED.REFUND_SENDER.INVALID_AMOUNT_TYPE";
}

public class AccountNotFoundException : Exception
{
    public readonly string ErrorCode = RefundSenderWalletBalanceFunction
        .ErrorCode
        .FailedAccountNotFound;

    public AccountNotFoundException(string message)
        : base(message) { }
}

public class DuplicateTransactionException : Exception
{
    public readonly string ErrorCode = RefundSenderWalletBalanceFunction
        .ErrorCode
        .DuplicateTransaction;

    public DuplicateTransactionException(string message)
        : base(message) { }
}

public class UnexpectedException : Exception
{
    public readonly string ErrorCode = RefundSenderWalletBalanceFunction.ErrorCode.Unexpected;

    public UnexpectedException(string message)
        : base(message) { }
}

public class InvalidAmountType : Exception
{
    public readonly string ErrorCode = RefundSenderWalletBalanceFunction
        .ErrorCode
        .InvalidAmountType;

    public InvalidAmountType(string message)
        : base(message) { }
}

public class InvalidConnectionStringException : Exception
{
    public readonly string ErrorCode = RefundSenderWalletBalanceFunction
        .ErrorCode
        .InvalidConnectionString;

    public InvalidConnectionStringException(string message)
        : base(message) { }
}

#endregion
