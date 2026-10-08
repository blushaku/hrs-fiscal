using System.Net;
using System.Text.RegularExpressions;
using Hrs.Fiscal.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Hrs.Fiscal.Server.Tests;

/// <summary>
/// Integration tests run against a real PostgreSQL. Set HRS_TEST_PG to an admin connection string
/// (e.g. "Host=localhost;Username=postgres;Password=…"); each run creates and drops its own database.
/// Without HRS_TEST_PG the tests are skipped.
/// </summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HRS_TEST_PG")))
            Skip = "Set HRS_TEST_PG to run PostgreSQL integration tests.";
    }
}

public sealed class ServerFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminPassword = "test-admin-password";
    private readonly string? _adminCs = Environment.GetEnvironmentVariable("HRS_TEST_PG");
    private readonly string _dbName = "hrs_test_" + Guid.NewGuid().ToString("N")[..10];
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_adminCs)) return;
        await using (var conn = new NpgsqlConnection(_adminCs))
        {
            await conn.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {_dbName}", conn).ExecuteNonQueryAsync();
        }
        ConnectionString = new NpgsqlConnectionStringBuilder(_adminCs) { Database = _dbName }.ConnectionString;

        // Starting the host runs migrations and creates the bootstrap admin; then add demo data.
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Fiscal", ConnectionString);
        builder.UseSetting("Bootstrap:AdminPassword", AdminPassword);
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (string.IsNullOrEmpty(_adminCs)) return;
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_adminCs);
        await conn.OpenAsync();
        await new NpgsqlCommand($"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)", conn).ExecuteNonQueryAsync();
    }

    public HttpClient Anonymous() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public async Task<HttpClient> SignedInAsync(string user, string password)
    {
        var client = Anonymous();
        var token = await AntiforgeryTokenAsync(client, "/Login");
        var response = await client.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = user, ["Password"] = password, ["__RequestVerificationToken"] = token,
        }));
        if (response.StatusCode != HttpStatusCode.Redirect) throw new InvalidOperationException($"Login failed for {user}: {response.StatusCode}");
        return client;
    }

    public static async Task<string> AntiforgeryTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : throw new InvalidOperationException($"No antiforgery token on {path}");
    }

    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        var v = await new NpgsqlCommand(sql, conn).ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)Convert.ChangeType(v, typeof(T));
    }
}
