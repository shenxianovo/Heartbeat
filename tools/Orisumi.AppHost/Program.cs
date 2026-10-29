using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
var root = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../.."));
var services = Orisumi.Dev.ServiceSelection.Resolve(builder.Configuration["Services"]?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? []);
IResourceBuilder<ProjectResource>? api = null;
IResourceBuilder<JavaScriptAppResource>? docs = null;
IResourceBuilder<JavaScriptAppResource>? web = null;

if (services.Contains("api"))
{
    var username = builder.AddParameter("postgres-user");
    var password = builder.AddParameter("postgres-password", secret: true);

    var postgres = builder.AddPostgres("db", username, password,
            port: builder.Configuration.GetValue<int>("Ports:Database"))
        .WithEndpoint("tcp", endpoint => endpoint.IsProxied = false)
        .WithImageTag("18.6-alpine")
        .WithVolume(builder.Configuration["DatabaseVolume"]!, "/var/lib/postgresql")
        .WithEnvironment("PGDATA", "/var/lib/postgresql/18/docker")
        .WithBindMount(Path.Combine(root, "src/Backend/Orisumi.Infrastructure/Database/Docker/initialize.sql"),
            "/docker-entrypoint-initdb.d/initialize.sql", isReadOnly: true)
        .WithArgs("-c", "shared_preload_libraries=pg_stat_statements",
            "-c", "pg_stat_statements.track=top", "-c", "track_io_timing=on",
            "-c", "track_wal_io_timing=on", "-c", "log_min_duration_statement=500ms",
            "-c", "log_lock_waits=on");
    var database = postgres.AddDatabase("Orisumi", "orisumi");

    var migrationArguments = new List<string> { "ef", "database", "update" };
    if (builder.Configuration.GetValue<bool>("Testing:FailMigration")) migrationArguments.Add("Orisumi_Missing_Migration");
    migrationArguments.AddRange(["--project", "src/Backend/Orisumi.Infrastructure",
        "--startup-project", "src/Backend/Orisumi.Api", "--no-build"]);
    var migrate = builder.AddExecutable("migrate", "dotnet", root,
            migrationArguments.ToArray())
        .WithReference(database)
        .WaitFor(database);

    api = builder.AddProject<Projects.Orisumi_Api>("api")
        .WithHttpEndpoint(name: "http")
        .WithReference(database)
        .WaitForCompletion(migrate)
        .WithHttpHealthCheck("/entities/019a0d3b-1d20-7000-8000-000000000001", statusCode: 404);
}

if (services.Contains("application-identity"))
{
    var localIdentity = new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(root, ".local", "application-identity.json"), optional: true)
        .Build();
    builder.AddProject<Projects.Orisumi_Observer_ApplicationIdentity>("application-identity")
        .WithArgs("--backend", api!.GetEndpoint("http"),
            "--data-dir", Path.Combine(root, ".artifacts", "application-identity", builder.Configuration["DatabaseVolume"]!),
            "--model", builder.Configuration["ApplicationIdentity:Model"] ?? localIdentity["model"] ?? "gpt-6.1-terra",
            "--interval-seconds", builder.Configuration["ApplicationIdentity:IntervalSeconds"] ?? localIdentity["intervalSeconds"] ?? "60")
        .WithEnvironment("OPENAI_API_KEY", builder.Configuration["OPENAI_API_KEY"] ?? localIdentity["apiKey"] ?? "")
        .WaitFor(api!);
}

if (services.Contains("docs"))
{
    docs = builder.AddJavaScriptApp("docs", Path.Combine(root, "src/Docs/Orisumi.Docs"))
        .WithPnpm(installArgs: ["--frozen-lockfile"])
        .WithHttpEndpoint(env: "PORT", name: "http")
        .WithEnvironment("ORISUMI_TEST_REPORTS_DIR", Path.Combine(root, "TestResults"))
        .WithHttpHealthCheck("/core");
}

if (services.Contains("web"))
{
    web = builder.AddJavaScriptApp("web", Path.Combine(root, "src/Clients/Orisumi.Web"))
        .WithNpm(installCommand: "ci")
        .WithHttpEndpoint(env: "PORT", name: "http")
        .WithHttpHealthCheck("/");
}

var nginx = builder.AddContainer("nginx", "nginx", "1.30.5-alpine")
    .WithBindMount(Path.Combine(root, "docker/nginx/default.conf"),
        "/etc/nginx/templates/default.conf.template", isReadOnly: true)
    .WithHttpEndpoint(port: builder.Configuration.GetValue<int>("Ports:Api"), targetPort: 80,
        name: "api", isProxied: false)
    .WithHttpEndpoint(port: builder.Configuration.GetValue<int>("Ports:Docs"), targetPort: 3000,
        name: "docs", isProxied: false)
    .WithHttpEndpoint(port: builder.Configuration.GetValue<int>("Ports:Web"), targetPort: 3100,
        name: "web", isProxied: false);

nginx.WithBindMount(Path.Combine(root, "docker/nginx/common.conf"),
    "/etc/nginx/templates/common.conf.template", isReadOnly: true);
foreach (var service in new[] { "api", "docs", "web" })
{
    var fragment = services.Contains(service) ? "enabled" : "disabled";
    nginx.WithBindMount(Path.Combine(root, $"docker/nginx/{service}-{fragment}.conf"),
        $"/etc/nginx/templates/{service}.conf.template", isReadOnly: true);
}
if (api is not null)
{
    nginx.WithEnvironment("API_HOST", api.GetEndpoint("http").Property(EndpointProperty.Host))
        .WithEnvironment("API_PORT", api.GetEndpoint("http").Property(EndpointProperty.Port))
        .WithHttpHealthCheck("/api/entities/019a0d3b-1d20-7000-8000-000000000001", statusCode: 404, endpointName: "api")
        .WaitFor(api);
}
if (docs is not null)
{
    nginx.WithEnvironment("DOCS_HOST", docs.GetEndpoint("http").Property(EndpointProperty.Host))
        .WithEnvironment("DOCS_PORT", docs.GetEndpoint("http").Property(EndpointProperty.Port))
        .WithHttpHealthCheck("/core", endpointName: "docs").WaitFor(docs);
}
if (web is not null)
{
    nginx.WithEnvironment("WEB_HOST", web.GetEndpoint("http").Property(EndpointProperty.Host))
        .WithEnvironment("WEB_PORT", web.GetEndpoint("http").Property(EndpointProperty.Port))
        .WithHttpHealthCheck("/", endpointName: "web").WaitFor(web);
}

builder.Build().Run();
