using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Dapper;
using Npgsql;

namespace Ewallet.ReverseReceiverCreditFunction;

public class Function
{
    private static async Task Main()
    {
        Func<
            ReverseReceiverCreditRequest,
            ILambdaContext,
            Task<ReverseReceiverCreditResponse>
        > handler = ReverseReceiverCreditHandler;

        await LambdaBootstrapBuilder
            .Create(
                handler,
                new SourceGeneratorLambdaJsonSerializer<LambdaFunctionJsonSerializerContext>()
            )
            .Build()
            .RunAsync();
    }

    public static async Task<ReverseReceiverCreditResponse> ReverseReceiverCreditHandler(
        ReverseReceiverCreditRequest request,
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
            // 1️⃣ Insert inbox entry — detect duplicate reversal transactions
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
                        TransactionId = "REVERSAL#" + request.TransactionId,
                        Type = "RECEIVER_REVERSAL",
                        Amount = -request.Amount, // Negative amount for reversal
                    },
                    transaction
                );
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                context.Logger.LogError(
                    $"Duplicate reversal transaction detected: {request.TransactionId}"
                );
                throw new DuplicateTransactionException(
                    $"Duplicate transaction_id {request.TransactionId} detected. Step Function retry suppressed."
                );
            }

            // 2️⃣ Retrieve receiver's account with FOR UPDATE to lock the row
            const string selectAccountSql =
                @"
                SELECT id, user_id, balance
                FROM wallet.account
                WHERE user_id = @ReceiverUserId
                FOR UPDATE;";

            var account = await connection.QuerySingleOrDefaultAsync<Account>(
                selectAccountSql,
                new { ReceiverUserId = request.ReceiverUserId },
                transaction
            );

            if (account == null)
            {
                context.Logger.LogError($"No account found for receiver: {request.ReceiverUserId}");
                throw new AccountNotFoundException(
                    $"No account found for receiver: {request.ReceiverUserId}"
                );
            }

            // 3️⃣ Update receiver's balance (subtract amount)
            const string updateBalanceSql =
                @"
                UPDATE wallet.account
                SET balance = balance - @Amount,
                    updated_at = NOW() AT TIME ZONE 'UTC'
                WHERE id = @Id;";

            await connection.ExecuteAsync(
                updateBalanceSql,
                new { Amount = request.Amount, Id = account.Id },
                transaction
            );

            // 4️⃣ Commit the transaction
            await transaction.CommitAsync();

            context.Logger.LogInformation(
                $"Reversed {request.Amount} from receiver {request.ReceiverUserId}. New balance: {account.Balance - request.Amount}"
            );

            return new ReverseReceiverCreditResponse(
                request.TransactionId,
                request.ReceiverUserId,
                request.SenderUserId,
                request.SenderAccountId,
                account.Id,
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
        catch (InvalidAmountType)
        {
            await transaction.RollbackAsync();
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            context.Logger.LogError(
                $"Unexpected error reversing receiver {request.ReceiverUserId}: {ex.Message}"
            );
            throw new UnexpectedException(
                $"Unexpected error reversing receiver {request.ReceiverUserId}: {ex.Message}"
            );
        }
    }
}

#region Models & Serialization

[JsonSerializable(typeof(Account))]
[JsonSerializable(typeof(ReverseReceiverCreditRequest))]
[JsonSerializable(typeof(ReverseReceiverCreditResponse))]
public partial class LambdaFunctionJsonSerializerContext : JsonSerializerContext { }

public sealed record ReverseReceiverCreditRequest(
    string TransactionId,
    string ReceiverUserId,
    string SenderUserId,
    string SenderAccountId,
    string ReceiverAccountId,
    decimal Amount
);

public sealed record ReverseReceiverCreditResponse(
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
    public const string Unexpected = "FAILED.REVERSE_RECEIVER";
    public const string FailedAccountNotFound = "FAILED.REVERSE_RECEIVER.ACCOUNT_NOT_FOUND";
    public const string DuplicateTransaction = "FAILED.REVERSE_RECEIVER.DUPLICATE";
    public const string InvalidConnectionString =
        "FAILED.REVERSE_RECEIVER.INVALID_CONNECTION_STRING";
    public const string InvalidAmountType = "FAILED.REVERSE_RECEIVER.INVALID_AMOUNT_TYPE";
}

public class AccountNotFoundException : Exception
{
    public readonly string ErrorCode = ReverseReceiverCreditFunction
        .ErrorCode
        .FailedAccountNotFound;

    public AccountNotFoundException(string message)
        : base(message) { }
}

public class DuplicateTransactionException : Exception
{
    public readonly string ErrorCode = ReverseReceiverCreditFunction.ErrorCode.DuplicateTransaction;

    public DuplicateTransactionException(string message)
        : base(message) { }
}

public class InvalidConnectionStringException : Exception
{
    public readonly string ErrorCode = ReverseReceiverCreditFunction
        .ErrorCode
        .InvalidConnectionString;

    public InvalidConnectionStringException(string message)
        : base(message) { }
}

public class InvalidAmountType : Exception
{
    public readonly string ErrorCode = ReverseReceiverCreditFunction.ErrorCode.InvalidAmountType;

    public InvalidAmountType(string message)
        : base(message) { }
}

public class UnexpectedException : Exception
{
    public readonly string ErrorCode = ReverseReceiverCreditFunction.ErrorCode.Unexpected;

    public UnexpectedException(string message)
        : base(message) { }
}

#endregion
