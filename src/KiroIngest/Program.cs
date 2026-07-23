using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using KiroIngest;

// Executable-assembly Lambda entrypoint (managed .NET 10 runtime). The Function and
// its AWS SDK clients are constructed once per execution environment and reused across
// warm invocations; the Target List is deliberately re-read from SSM on every object so
// list edits apply without a redeploy. Stream input → polymorphic dispatch (live S3
// event or backfill payload) inside Function.HandleAsync.
var function = new Function();

await LambdaBootstrapBuilder
    .Create(function.HandleAsync, new DefaultLambdaJsonSerializer())
    .Build()
    .RunAsync();
