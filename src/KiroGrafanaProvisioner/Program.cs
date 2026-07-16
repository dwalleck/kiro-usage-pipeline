using Amazon.Lambda.RuntimeSupport;
using KiroGrafanaProvisioner;

await LambdaBootstrapBuilder
    .Create(CustomResourceHandler.HandleAsync)
    .Build()
    .RunAsync();
