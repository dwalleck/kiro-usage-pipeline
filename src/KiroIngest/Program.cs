using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.S3Events;
using Amazon.Lambda.Serialization.SystemTextJson;
using KiroIngest;

// Executable-assembly Lambda entrypoint (managed .NET 10 runtime). The Function is
// constructed once per execution environment so the Target List cache is reused
// across warm invocations.
var function = new Function();

Func<S3Event, ILambdaContext, Task> handler = function.HandleAsync;

await LambdaBootstrapBuilder
    .Create(handler, new DefaultLambdaJsonSerializer())
    .Build()
    .RunAsync();
