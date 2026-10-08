using Dapper;
using Npgsql;

namespace Hrs.Fiscal.Server.Data;

public static class DbSetup
{
    public static IServiceCollection AddFiscalDatabase(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("Fiscal")
                 ?? throw new InvalidOperationException("ConnectionStrings:Fiscal is not configured.");
        var builder = new NpgsqlConnectionStringBuilder(cs);
        if (string.IsNullOrEmpty(builder.SearchPath)) builder.SearchPath = "fiscal,public";

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        services.AddSingleton(NpgsqlDataSource.Create(builder.ConnectionString));
        services.AddSingleton<Migrator>();
        services.AddScoped<ReceiptQueries>();
        services.AddScoped<AuditLog>();
        services.AddScoped<SettingsStore>();
        services.AddScoped<UserStore>();
        services.AddScoped<DashboardQueries>();
        services.AddScoped<ReceiptArchive>();
        return services;
    }

    /// <summary>Inclusive local-date range → [from, to) UTC instants, using the property's time zone.</summary>
    public static (DateTime? From, DateTime? To) ToUtcRange(DateOnly? from, DateOnly? to, TimeZoneInfo tz)
    {
        DateTime? f = from is { } a ? TimeZoneInfo.ConvertTimeToUtc(a.ToDateTime(TimeOnly.MinValue), tz) : null;
        DateTime? t = to is { } b ? TimeZoneInfo.ConvertTimeToUtc(b.AddDays(1).ToDateTime(TimeOnly.MinValue), tz) : null;
        return (f, t);
    }
}

/// <summary>Applies db/migrations/*.sql in order, once each, recording them in public.schema_migrations.</summary>
public sealed class Migrator(NpgsqlDataSource db, ILogger<Migrator> log)
{
    public async Task MigrateAsync(string directory, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS public.schema_migrations (
                version text PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT now())
            """);
        await conn.ExecuteAsync("SELECT pg_advisory_lock(hashtext('hrs.fiscal.migrations'))");
        try
        {
            var applied = (await conn.QueryAsync<string>("SELECT version FROM public.schema_migrations")).ToHashSet();
            foreach (var file in Directory.GetFiles(directory, "V*.sql").OrderBy(f => f, StringComparer.Ordinal))
            {
                var version = Path.GetFileNameWithoutExtension(file);
                if (applied.Contains(version)) continue;

                log.LogInformation("Applying migration {Version}", version);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(await File.ReadAllTextAsync(file, ct), transaction: tx);
                await conn.ExecuteAsync("INSERT INTO public.schema_migrations (version) VALUES (@version)", new { version }, tx);
                await tx.CommitAsync(ct);
            }
        }
        finally
        {
            await conn.ExecuteAsync("SELECT pg_advisory_unlock(hashtext('hrs.fiscal.migrations'))");
        }
    }
}
