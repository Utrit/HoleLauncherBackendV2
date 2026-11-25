using System.Security.Cryptography;
using HoleLauncherBackend.Contracts;

namespace HoleLauncherBackend.AccessService;

internal sealed class AccountStore(DBUtils database, SessionLookup lookup)
{
    private const int Iterations = 600_000;
    private static readonly byte[] UnknownAccountSalt = RandomNumberGenerator.GetBytes(16);

    public async Task<bool> ChangePasswordAsync(Guid accountId, string sessionLookup, string newPassword, CancellationToken cancellation)
    {
        await using var connection = await database.OpenConnectionAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);

        await DBUtils.LockAccountsAsync(transaction, [accountId], cancellation);
        var (hash, salt) = CreatePasswordHash(newPassword);
        if (!await DBUtils.UpdatePasswordAsync(transaction, accountId, sessionLookup, hash, salt, Iterations, cancellation))
            return false;

        await DBUtils.RevokeSessionsAsync(transaction, accountId, cancellation, sessionLookup);
        await transaction.CommitAsync(cancellation);
        return true;
    }

    public async Task<CreatedSession?> LoginAsync(Credentials credentials, CancellationToken cancellation)
    {
        var login = await database.ReadLoginAsync(credentials.UserName!, cancellation);
        var hash = Utils.HashPassword(credentials.Password!, login?.Salt ?? UnknownAccountSalt, login?.Iterations ?? Iterations);
        if (login is null || !CryptographicOperations.FixedTimeEquals(hash, login.Hash))
            return null;

        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return await database.InsertSessionAsync(lookup.Compute(sessionId), login.Account.AccountId, login.Hash, cancellation)
            ? new CreatedSession(login.Account, sessionId)
            : null;
    }

    public async Task<AccountIdentity?> RegisterAsync(Credentials credentials, CancellationToken cancellation)
    {
        var account = new AccountIdentity(Guid.NewGuid(), credentials.UserName!, [
            Permissions.StorageDownload, Permissions.StorageUserDataUpload, Permissions.RepositoryOffer,
            Permissions.EditGameUser, Permissions.EditGameSkin, Permissions.EditGameCape
        ]);
        var (hash, salt) = CreatePasswordHash(credentials.Password!);
        return await database.InsertAccountAsync(account, hash, salt, Iterations, cancellation) ? account : null;
    }

    private static (byte[] Hash, byte[] Salt) CreatePasswordHash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return (Utils.HashPassword(password, salt, Iterations), salt);
    }

    public Task RevokeSessionAsync(string sessionLookup, CancellationToken cancellation) =>
        database.RevokeSessionAsync(sessionLookup, cancellation);

    public Task<AccountSession?> FindSessionAsync(string sessionLookup, CancellationToken cancellation) =>
        database.FindSessionAsync(sessionLookup, cancellation);

    public Task<AccountIdentity[]> ListAsync(CancellationToken cancellation) =>
        database.ListAccountsAsync(cancellation);
}
