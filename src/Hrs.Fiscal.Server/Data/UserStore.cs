using Dapper;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace Hrs.Fiscal.Server.Data;

public static class Roles
{
    public const string Cashier = "Cashier", Supervisor = "Supervisor", Admin = "Admin", Auditor = "Auditor";
    public static readonly string[] All = [Cashier, Supervisor, Admin, Auditor];
}

public sealed class UserStore(NpgsqlDataSource db, AuditLog audit)
{
    private static readonly PasswordHasher<AppUser> Hasher = new();
    public const int MinPasswordLength = 10;

    public async Task<IReadOnlyList<AppUser>> AllAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<AppUser>("SELECT id, username, display_name, role, password_hash, active FROM fiscal.app_user ORDER BY username")).ToList();
    }

    public async Task<bool> AnyAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM fiscal.app_user)");
    }

    /// <summary>Returns the user when the password matches and the account is active.</summary>
    public async Task<AppUser?> ValidateAsync(string username, string password, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var user = await conn.QuerySingleOrDefaultAsync<AppUser>(
            "SELECT id, username, display_name, role, password_hash, active FROM fiscal.app_user WHERE username = @u",
            new { u = username.Trim().ToLowerInvariant() });
        if (user is null || !user.Active) return null;
        var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            await conn.ExecuteAsync("UPDATE fiscal.app_user SET password_hash = @h WHERE id = @id", new { h = Hasher.HashPassword(user, password), id = user.Id });
        return result == PasswordVerificationResult.Failed ? null : user;
    }

    public async Task<long> CreateAsync(string username, string displayName, string role, string password, string actor, CancellationToken ct = default)
    {
        Validate(role, password);
        var user = new AppUser { Username = username.Trim().ToLowerInvariant() };
        await using var conn = await db.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO fiscal.app_user (username, display_name, role, password_hash) VALUES (@username, @displayName, @role, @hash) RETURNING id
            """, new { username = user.Username, displayName, role, hash = Hasher.HashPassword(user, password) });
        await audit.WriteAsync(actor, AuditLog.Actions.UserCreated, "app_user", user.Username, new { displayName, role }, ct: ct);
        return id;
    }

    public async Task UpdateAsync(long id, string displayName, string role, bool active, string? newPassword, string actor, CancellationToken ct = default)
    {
        Validate(role, newPassword);
        var before = (await AllAsync(ct)).SingleOrDefault(u => u.Id == id) ?? throw new InvalidOperationException("Unknown user.");
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("UPDATE fiscal.app_user SET display_name = @displayName, role = @role, active = @active WHERE id = @id",
            new { id, displayName, role, active });
        if (!string.IsNullOrEmpty(newPassword))
            await conn.ExecuteAsync("UPDATE fiscal.app_user SET password_hash = @h WHERE id = @id", new { id, h = Hasher.HashPassword(before, newPassword) });
        await audit.WriteAsync(actor, AuditLog.Actions.UserChanged, "app_user", before.Username, new
        {
            old = new { before.DisplayName, before.Role, before.Active },
            @new = new { displayName, role, active },
            passwordChanged = !string.IsNullOrEmpty(newPassword),
        }, ct: ct);
    }

    private static void Validate(string role, string? password)
    {
        if (!Roles.All.Contains(role)) throw new ArgumentException($"Unknown role '{role}'.");
        if (password is not null && password.Length > 0 && password.Length < MinPasswordLength)
            throw new ArgumentException($"Passwords must have at least {MinPasswordLength} characters.");
    }
}
