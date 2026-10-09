using System.Collections.Concurrent;
using System.Security.Claims;
using Dapper;
using Hrs.Fiscal.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace Hrs.Fiscal.Server.Security;

/// <summary>Everything a role can be allowed to do. Admin always has all of them.</summary>
public static class Permissions
{
    public const string DashboardSystem = "dashboard.system";
    public const string ReceiptsView = "receipts.view";
    public const string ReceiptsCopy = "receipts.copy";
    public const string Export = "export";
    public const string AuditView = "audit.view";
    public const string AuditIntegrity = "audit.integrity";
    public const string FlipView = "flip.view";
    public const string SettingsManage = "settings.manage";
    public const string WorkstationsManage = "workstations.manage";
    public const string UsersManage = "users.manage";

    public sealed record Info(string Key, string Label, string Description);

    public static readonly IReadOnlyList<Info> All =
    [
        new(DashboardSystem, "System status on the dashboard", "OPERA → FLIP → ATK status, alerts, certificate expiry"),
        new(ReceiptsView, "View receipts", "Search receipts and open their details and QR"),
        new(ReceiptsCopy, "Download receipt copies", "PDF copy marked KOPJE E KUPONIT (logged as reprint)"),
        new(Export, "Export reports", "CSV/PDF of receipts, workstation summary and audit log"),
        new(AuditView, "View audit log", "All events, filtered by workstation, event or period"),
        new(AuditIntegrity, "Run integrity check", "Verify that no receipt or log entry was changed"),
        new(FlipView, "View FLIP messages", "Messages from OPERA/FLIP, including guest data"),
        new(SettingsManage, "Manage settings", "General, business, OPERA overrides, VAT, FLIP token"),
        new(WorkstationsManage, "Manage workstations", "Add workstations and register them with ATK"),
        new(UsersManage, "Manage users and permissions", "Create users, assign roles, edit this matrix"),
    ];

    public static string Policy(string permission) => "perm:" + permission;
}

/// <summary>Role → permissions, read from the database and cached briefly. Admin always has all permissions.</summary>
public sealed class RolePermissions(NpgsqlDataSource db)
{
    private readonly ConcurrentDictionary<string, (DateTime At, IReadOnlySet<string> Set)> _cache = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlySet<string>> ForRoleAsync(string role, CancellationToken ct = default)
    {
        if (role == Roles.Admin) return Permissions.All.Select(p => p.Key).ToHashSet();
        if (_cache.TryGetValue(role, out var c) && DateTime.UtcNow - c.At < Ttl) return c.Set;
        await using var conn = await db.OpenConnectionAsync(ct);
        var set = (await conn.QueryAsync<string>("SELECT permission FROM fiscal.role_permission WHERE role = @role", new { role })).ToHashSet();
        _cache[role] = (DateTime.UtcNow, set);
        return set;
    }

    public Task<IReadOnlySet<string>> ForUserAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
        user.Identity?.IsAuthenticated == true
            ? ForRoleAsync(user.FindFirstValue(ClaimTypes.Role) ?? "", ct)
            : Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

    public async Task<IReadOnlyDictionary<string, IReadOnlySet<string>>> MatrixAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<string, IReadOnlySet<string>>();
        foreach (var role in Roles.All) { _cache.TryRemove(role, out _); result[role] = await ForRoleAsync(role, ct); }
        return result;
    }

    /// <summary>Replaces the permissions of one role (not Admin). Returns what was added and removed.</summary>
    public async Task<(string[] Added, string[] Removed)> SaveAsync(string role, IEnumerable<string> permissions, CancellationToken ct = default)
    {
        if (role == Roles.Admin || !Roles.All.Contains(role)) throw new ArgumentException("Administrators always have every permission.");
        var wanted = permissions.Where(p => Permissions.All.Any(a => a.Key == p)).ToHashSet();
        _cache.TryRemove(role, out _);
        var before = await ForRoleAsync(role, ct);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync("DELETE FROM fiscal.role_permission WHERE role = @role", new { role }, tx);
        foreach (var p in wanted)
            await conn.ExecuteAsync("INSERT INTO fiscal.role_permission (role, permission) VALUES (@role, @p)", new { role, p }, tx);
        await tx.CommitAsync(ct);
        _cache.TryRemove(role, out _);
        return (wanted.Except(before).Order().ToArray(), before.Except(wanted).Order().ToArray());
    }
}

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

public sealed class PermissionHandler(RolePermissions roles) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if ((await roles.ForUserAsync(context.User)).Contains(requirement.Permission)) context.Succeed(requirement);
    }
}

public static class PermissionSetup
{
    public static AuthorizationBuilder AddPermissionPolicies(this AuthorizationBuilder builder)
    {
        foreach (var p in Permissions.All)
            builder.AddPolicy(Permissions.Policy(p.Key), policy => policy.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(p.Key)));
        return builder;
    }
}
