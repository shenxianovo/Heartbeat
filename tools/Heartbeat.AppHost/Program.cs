using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
var root = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../.."));
var username = builder.AddParameter("postgres-user");
var password = builder.AddParameter("postgres-password", secret: true);

var postgres = builder.AddPostgres("db", username, password,
        port: builder.Configuration.GetValue<int>("Ports:Database"))
    .WithImageTag("18.6-alpine")
    .WithVolume(builder.Configuration["DatabaseVolume"]!, "/var/lib/postgresql")
    .WithEnvironment("PGDATA", "/var/lib/postgresql/18/docker")
    .WithBindMount(Path.Combine(root, "src/Backend/Heartbeat.Infrastructure/Database/Docker/initialize.sql"),
        "/docker-entrypoint-initdb.d/initialize.sql", isReadOnly: true)
    .WithArgs("-c", "shared_preload_libraries=pg_stat_statements",
        "-c", "pg_stat_statements.track=top", "-c", "track_io_timing=on",
        "-c", "track_wal_io_timing=on", "-c", "log_min_duration_statement=500ms",
        "-c", "log_lock_waits=on");
var database = postgres.AddDatabase("Heartbeat", "heartbeat");

var migrate = builder.AddExecutable("migrate", "dotnet", root,
        "ef", "database", "update", "--project", "src/Backend/Heartbeat.Infrastructure",
        "--startup-project", "src/Backend/Heartbeat.Api", "--no-build")
    .WithReference(database)
    .WaitFor(database);

var api = builder.AddProject<Projects.Heartbeat_Api>("api")
    .WithHttpEndpoint(name: "http")
    .WithReference(database)
    .WaitForCompletion(migrate)
    .WithHttpHealthCheck("/entities/019a0d3b-1d20-7000-8000-000000000001", statusCode: 404);

var docs = builder.AddJavaScriptApp("docs", Path.Combine(root, "src/Docs/Heartbeat.Docs"))
    .WithPnpm(installArgs: ["--frozen-lockfile"])
    .WithHttpEndpoint(env: "PORT", name: "http")
    .WithEnvironment("HEARTBEAT_TEST_REPORTS_DIR", Path.Combine(root, "TestResults"))
    .WithHttpHealthCheck("/core");

builder.AddContainer("nginx", "nginx", "1.30.5-alpine")
    .WithBindMount(Path.Combine(root, "docker/nginx/default.conf"),
        "/etc/nginx/templates/default.conf.template", isReadOnly: true)
    .WithEnvironment("API_HOST", api.GetEndpoint("http").Property(EndpointProperty.Host))
    .WithEnvironment("API_PORT", api.GetEndpoint("http").Property(EndpointProperty.Port))
    .WithEnvironment("DOCS_HOST", docs.GetEndpoint("http").Property(EndpointProperty.Host))
    .WithEnvironment("DOCS_PORT", docs.GetEndpoint("http").Property(EndpointProperty.Port))
    .WithHttpEndpoint(port: builder.Configuration.GetValue<int>("Ports:Api"), targetPort: 80,
        name: "api", isProxied: false)
    .WithHttpEndpoint(port: builder.Configuration.GetValue<int>("Ports:Docs"), targetPort: 3000,
        name: "docs", isProxied: false)
    .WithHttpHealthCheck("/core", endpointName: "docs")
    .WithHttpHealthCheck("/api/entities/019a0d3b-1d20-7000-8000-000000000001",
        statusCode: 404, endpointName: "api");

builder.Build().Run();
