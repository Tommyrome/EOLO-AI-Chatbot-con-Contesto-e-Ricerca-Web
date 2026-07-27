var builder = DistributedApplication.CreateBuilder(args);

var apiService = builder.AddProject<Projects.TommasoEOlo_ApiService>("apiservice");

builder.AddProject<Projects.TommasoEOlo_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
