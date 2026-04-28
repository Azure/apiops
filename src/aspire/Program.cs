var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.integration_tests>("integration-tests");

builder.Build().Run();
