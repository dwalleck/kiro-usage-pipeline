using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using KiroIngest;

// Executable-assembly Lambda entrypoint (managed .NET 10 runtime). The Function is
// constructed once per execution environment so the Target List cache is reused
// across warm invocations. Stream input → polymorphic dispatch (live S3 event or
// backfill payload) inside Function.HandleAsync.
var function = new Function();

await LambdaBootstrapBuilder
    .Create(function.HandleAsync, new DefaultLambdaJsonSerializer())
    .Build()
    .RunAsync();
