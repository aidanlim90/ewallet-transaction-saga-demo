using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Dapper;
using Npgsql;

namespace Ewallet.DebitSenderWalletBalanceFunction;

public class Function
{
    private static async Task Main()
    {
        Func<
            DebitSenderWalletBalanceRequest,
            ILambdaContext,
            Task<DebitSenderWalletBalanceResponse>
        > handler = DebitSenderWalletBalanceHandler;

        await LambdaBootstrapBuilder
            .Create(
                handler,
                new SourceGeneratorLambdaJsonSerializer<LambdaFunctionJsonSerializerContext>()
            )
            .Build()
            .RunAsync();
    }

    public static async Task<DebitSenderWalletBalanceResponse> DebitSenderWalletBalanceHandler(
        DebitSenderWalletBalanceRequest request,
        ILambdaContext context
    )
    {
        var connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");
        if (string.IsNullOrEmpty(connectionString))
            throw new InvalidOperationException(
                "Missing DB_CONNECTION_STRING environment variable."
            );

        if (!decimal.TryParse(request.Amount, out var amount))
        {
            throw new ArgumentException($"Invalid amount value: {request.Amount}");
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            // 1️⃣ Insert inbox entry — detect duplicate transactions
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
                        TransactionId = request.TransactionId,
                        Type = "SENDER",
                        Amount = amount,
                    },
                    transaction
                );
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                context.Logger.LogError($"Duplicate transaction detected: {request.TransactionId}");
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

            if (account.Balance < amount)
            {
                context.Logger.LogError(
                    $"Insufficient funds: balance={account.Balance}, required={amount}"
                );
                throw new InsufficientBalanceException(
                    $"Insufficient funds: balance={account.Balance}, required={amount}"
                );
            }

            const string updateBalanceSql =
                @"
                UPDATE wallet.account
                SET balance = balance - @Amount,
                    updated_at = NOW() AT TIME ZONE 'UTC'
                WHERE id = @Id;";

            await connection.ExecuteAsync(
                updateBalanceSql,
                new { Amount = amount, Id = account.Id },
                transaction
            );

            await transaction.CommitAsync();

            context.Logger.LogInformation(
                $"Debited {amount} from sender {request.SenderUserId}. New balance: {account.Balance - amount}"
            );

            return new DebitSenderWalletBalanceResponse(
                request.TransactionId,
                request.ReceiverUserId,
                amount
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
        catch (InsufficientBalanceException)
        {
            await transaction.RollbackAsync();
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            context.Logger.LogError(
                $"Unexpected error debiting sender {request.SenderUserId}: {ex.Message}"
            );
            throw new UnexpectedException(
                $"Unexpected error debiting sender {request.SenderUserId}: {ex.Message}"
            );
        }
    }
}

#region Models & Serialization

[JsonSerializable(typeof(Account))]
[JsonSerializable(typeof(DebitSenderWalletBalanceRequest))]
[JsonSerializable(typeof(DebitSenderWalletBalanceResponse))]
public partial class LambdaFunctionJsonSerializerContext : JsonSerializerContext { }

public sealed record DebitSenderWalletBalanceRequest(
    string TransactionId,
    string SenderUserId,
    string ReceiverUserId,
    string Amount // kept as string to avoid breaking existing JSON contract
);

public sealed record DebitSenderWalletBalanceResponse(
    string TransactionId,
    string ReceiverUserId,
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
    public const string Unexpected = "FAILED.DEBIT_SENDER";
    public const string FailedAccountNotFound = "FAILED.DEBIT_SENDER.ACCOUNT_NOT_FOUND";
    public const string DuplicateTransaction = "FAILED.DEBIT_SENDER.DUPLICATE";
    public const string FailedInsufficient = "FAILED.DEBIT_SENDER.INSUFFICIENT";
}

public class AccountNotFoundException : Exception
{
    public readonly string ErrorCode = DebitSenderWalletBalanceFunction
        .ErrorCode
        .FailedAccountNotFound;

    public AccountNotFoundException(string message)
        : base(message) { }
}

public class DuplicateTransactionException : Exception
{
    public readonly string ErrorCode = DebitSenderWalletBalanceFunction
        .ErrorCode
        .DuplicateTransaction;

    public DuplicateTransactionException(string message)
        : base(message) { }
}

public class InsufficientBalanceException : Exception
{
    public readonly string ErrorCode = DebitSenderWalletBalanceFunction
        .ErrorCode
        .FailedInsufficient;

    public InsufficientBalanceException(string message)
        : base(message) { }
}

public class UnexpectedException : Exception
{
    public readonly string ErrorCode = DebitSenderWalletBalanceFunction.ErrorCode.Unexpected;

    public UnexpectedException(string message)
        : base(message) { }
}

#endregion
