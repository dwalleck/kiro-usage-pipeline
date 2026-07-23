using Amazon.Lambda.Core;
using Amazon.ManagedGrafana;
using Amazon.ManagedGrafana.Model;
using Amazon.S3;
using Amazon.S3.Model;
using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiroGrafanaProvisioner;

// Lambda-backed CloudFormation custom-resource provider. It owns only the
// configuration inside the temporary workspace. A short-lived Grafana service-account
// token exists for the duration of an invocation and is deleted in the finally block.
public sealed class CustomResourceHandler
{
    private const string FolderUid = "kiro-usage";
    private const string FolderTitle = "Kiro Usage";
    private const string AthenaDataSourceUid = "kiro-athena";
    private const string AthenaDataSourceName = "Athena";
    private const string AthenaPluginId = "grafana-athena-datasource";
    private const string ServiceAccountName = "kiro-integration-spike-provisioner";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(90),
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task HandleAsync(Stream input, ILambdaContext context)
    {
        using var requestDocument = await JsonDocument.ParseAsync(input);
        var request = requestDocument.RootElement;
        var physicalResourceId = OptionalString(request, "PhysicalResourceId") ?? "kiro-grafana-integration-spike";

        try
        {
            var requestType = RequiredString(request, "RequestType");
            if (requestType == "Delete")
            {
                // The workspace is CloudFormation-owned. Service-account credentials are
                // deleted after every Create/Update, so Delete has nothing persistent to revoke.
                await RespondAsync(request, "SUCCESS", physicalResourceId, new Dictionary<string, string>
                {
                    ["Message"] = "No persistent Grafana provisioning credential to clean up",
                }, null);
                return;
            }

            if (requestType is not "Create" and not "Update")
            {
                throw new InvalidOperationException($"Unsupported CloudFormation request type '{requestType}'");
            }

            var properties = ProvisioningProperties.From(request.GetProperty("ResourceProperties"));
            var result = await ProvisionAsync(properties, context);
            await RespondAsync(request, "SUCCESS", properties.WorkspaceId, result, null);
        }
        catch (Exception exception)
        {
            context.Logger.LogError(JsonSerializer.Serialize(new
            {
                @event = "grafana_provisioning_error",
                error = exception.Message,
                details = exception.ToString(),
                type = exception.GetType().Name,
            }));

            await RespondAsync(request, "FAILED", physicalResourceId, null, exception.Message);
        }
    }

    private static async Task<Dictionary<string, string>> ProvisionAsync(
        ProvisioningProperties properties,
        ILambdaContext context)
    {
        using var grafana = new AmazonManagedGrafanaClient();
        using var s3 = new AmazonS3Client();

        await SetWorkspacePermissionsAsync(grafana, properties);

        var serviceAccount = await grafana.CreateWorkspaceServiceAccountAsync(new CreateWorkspaceServiceAccountRequest
        {
            WorkspaceId = properties.WorkspaceId,
            Name = ServiceAccountName,
            GrafanaRole = Role.ADMIN,
        });

        string? tokenId = null;
        try
        {
            var token = await grafana.CreateWorkspaceServiceAccountTokenAsync(new CreateWorkspaceServiceAccountTokenRequest
            {
                WorkspaceId = properties.WorkspaceId,
                ServiceAccountId = serviceAccount.Id,
                Name = $"cf-{context.AwsRequestId}",
                SecondsToLive = 900,
            });
            tokenId = token.ServiceAccountToken.Id;
            var tokenKey = token.ServiceAccountToken.Key;

            await EnsureFolderAsync(properties.WorkspaceEndpoint, tokenKey);
            await EnsureAthenaPluginAsync(properties.WorkspaceEndpoint, tokenKey);
            await UpsertAthenaDataSourceAsync(properties, tokenKey);
            await VerifyAthenaDataSourceAsync(properties.WorkspaceEndpoint, tokenKey);

            var fleetDashboard = await ReadAssetAsync(s3, properties.FleetDashboardAssetBucket, properties.FleetDashboardAssetKey);
            var userDrilldownDashboard = await ReadAssetAsync(s3, properties.UserDrilldownDashboardAssetBucket, properties.UserDrilldownDashboardAssetKey);
            await UpsertDashboardAsync(properties.WorkspaceEndpoint, tokenKey, fleetDashboard, properties.FleetDashboardUid);
            await UpsertDashboardAsync(properties.WorkspaceEndpoint, tokenKey, userDrilldownDashboard, properties.UserDrilldownDashboardUid);

            return new Dictionary<string, string>
            {
                ["WorkspaceUrl"] = WorkspaceUrl(properties.WorkspaceEndpoint),
                ["FleetOverviewUrl"] = $"{WorkspaceUrl(properties.WorkspaceEndpoint)}/d/{properties.FleetDashboardUid}",
                ["UserDrilldownUrl"] = $"{WorkspaceUrl(properties.WorkspaceEndpoint)}/d/{properties.UserDrilldownDashboardUid}",
                ["AthenaDataSourceUid"] = AthenaDataSourceUid,
                ["FolderUid"] = FolderUid,
            };
        }
        finally
        {
            await DeleteEphemeralCredentialsAsync(grafana, properties.WorkspaceId, serviceAccount.Id, tokenId, context);
        }
    }

    private static async Task SetWorkspacePermissionsAsync(AmazonManagedGrafanaClient grafana, ProvisioningProperties properties)
    {
        var response = await grafana.UpdatePermissionsAsync(new UpdatePermissionsRequest
        {
            WorkspaceId = properties.WorkspaceId,
            UpdateInstructionBatch =
            [
                Permission(Role.ADMIN, properties.AdminGroupId),
                Permission(Role.EDITOR, properties.EditorGroupId),
                Permission(Role.VIEWER, properties.ViewerGroupId),
            ],
        });

        if (response.Errors is not { Count: > 0 })
        {
            return;
        }

        // The API may report an already-present group on a repeat reconciliation. Other
        // errors are material: the stack must not claim the intended access model exists.
        var unexpected = response.Errors
            .Where(error => error.Message?.Contains("already", StringComparison.OrdinalIgnoreCase) != true)
            .Select(error => $"{error.Code?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}: {error.Message ?? "No error message returned"}")
            .ToArray();
        if (unexpected.Length > 0)
        {
            throw new InvalidOperationException($"Managed Grafana permission update failed: {string.Join("; ", unexpected)}");
        }
    }

    private static UpdateInstruction Permission(Role role, string groupId)
    {
        return new UpdateInstruction
        {
            Action = UpdateAction.ADD,
            Role = role,
            Users =
            [
                new Amazon.ManagedGrafana.Model.User
                {
                    Id = groupId,
                    Type = UserType.SSO_GROUP,
                },
            ],
        };
    }

    private static async Task EnsureFolderAsync(string endpoint, string token)
    {
        var response = await GrafanaCallAsync(
            endpoint,
            token,
            HttpMethod.Post,
            "/api/folders",
            new Dictionary<string, string>
            {
                ["uid"] = FolderUid,
                ["title"] = FolderTitle,
            },
            HttpStatusCode.OK,
            HttpStatusCode.Conflict,
            HttpStatusCode.PreconditionFailed);

        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            _ = await GrafanaCallAsync(
                endpoint,
                token,
                HttpMethod.Get,
                $"/api/folders/{FolderUid}",
                null,
                HttpStatusCode.OK);
        }
    }

    private static async Task EnsureAthenaPluginAsync(string endpoint, string token)
    {
        if (await GrafanaResourceExistsAsync(endpoint, token, $"/api/plugins/{AthenaPluginId}/settings"))
        {
            return;
        }

        _ = await GrafanaCallAsync(
            endpoint,
            token,
            HttpMethod.Post,
            $"/api/plugins/{AthenaPluginId}/install",
            new Dictionary<string, object?>(),
            HttpStatusCode.OK,
            HttpStatusCode.Conflict);
    }

    private static async Task UpsertAthenaDataSourceAsync(ProvisioningProperties properties, string token)
    {
        var exists = await GrafanaResourceExistsAsync(
            properties.WorkspaceEndpoint,
            token,
            $"/api/datasources/uid/{AthenaDataSourceUid}");

        var dataSource = new Dictionary<string, object?>
        {
            ["uid"] = AthenaDataSourceUid,
            ["name"] = AthenaDataSourceName,
            ["type"] = AthenaPluginId,
            ["access"] = "proxy",
            ["isDefault"] = true,
            ["jsonData"] = new Dictionary<string, object?>
            {
                ["authType"] = "workspace-iam-role",
                ["defaultRegion"] = properties.Region,
                ["catalog"] = "AwsDataCatalog",
                ["database"] = properties.DatabaseName,
                ["workgroup"] = properties.WorkGroupName,
                // The workgroup enforces this output location; the explicit plugin
                // value documents the same invariant for the integration spike.
                ["outputLocation"] = $"s3://{properties.AnalyticsBucketName}/athena-results/",
            },
        };

        _ = await GrafanaCallAsync(
            properties.WorkspaceEndpoint,
            token,
            exists ? HttpMethod.Put : HttpMethod.Post,
            exists ? $"/api/datasources/uid/{AthenaDataSourceUid}" : "/api/datasources",
            dataSource,
            HttpStatusCode.OK);
    }

    private static async Task VerifyAthenaDataSourceAsync(string endpoint, string token)
    {
        var registrationWait = Stopwatch.StartNew();
        var registrationTimeout = TimeSpan.FromMinutes(4);
        while (true)
        {
            var response = await GrafanaCallAsync(
                endpoint,
                token,
                HttpMethod.Get,
                $"/api/datasources/uid/{AthenaDataSourceUid}/health",
                null,
                HttpStatusCode.OK,
                HttpStatusCode.NotFound);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                if (registrationWait.Elapsed >= registrationTimeout)
                {
                    throw new TimeoutException(
                        $"Athena plugin did not register within {registrationTimeout}: {response.Body}");
                }

                await Task.Delay(TimeSpan.FromSeconds(10));
                continue;
            }

            using var healthDocument = JsonDocument.Parse(response.Body);
            var health = healthDocument.RootElement;
            var status = RequiredString(health, "status");
            if (!string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
            {
                var message = OptionalString(health, "message") ?? "No health-check message returned";
                throw new InvalidOperationException($"Athena data source health check returned '{status}': {message}");
            }

            return;
        }
    }

    private static async Task UpsertDashboardAsync(string endpoint, string token, string dashboardJson, string expectedUid)
    {
        var rendered = dashboardJson.Replace("${DS_ATHENA}", AthenaDataSourceUid, StringComparison.Ordinal);
        using var dashboardDocument = JsonDocument.Parse(rendered);
        var dashboard = dashboardDocument.RootElement;

        var actualUid = RequiredString(dashboard, "uid");
        if (!string.Equals(expectedUid, actualUid, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Dashboard asset UID '{actualUid}' does not match expected UID '{expectedUid}'");
        }

        _ = await GrafanaCallAsync(
            endpoint,
            token,
            HttpMethod.Post,
            "/api/dashboards/db",
            new Dictionary<string, object?>
            {
                ["dashboard"] = dashboard,
                ["folderUid"] = FolderUid,
                ["overwrite"] = true,
                ["message"] = "Reconciled by Kiro Grafana integration spike",
            },
            HttpStatusCode.OK);
    }

    private static async Task<bool> GrafanaResourceExistsAsync(string endpoint, string token, string path)
    {
        var response = await GrafanaCallAsync(endpoint, token, HttpMethod.Get, path, null, HttpStatusCode.OK, HttpStatusCode.NotFound);
        return response.StatusCode == HttpStatusCode.OK;
    }

    private static async Task<GrafanaResponse> GrafanaCallAsync(
        string endpoint,
        string token,
        HttpMethod method,
        string path,
        object? payload,
        params HttpStatusCode[] expectedStatusCodes)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(WorkspaceUrl(endpoint) + "/"), path.TrimStart('/')));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        if (payload is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        }

        using var response = await Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!expectedStatusCodes.Contains(response.StatusCode))
        {
            throw new HttpRequestException($"Grafana API {method} {path} returned {(int)response.StatusCode}: {body}");
        }

        return new GrafanaResponse(response.StatusCode, body);
    }

    private static async Task<string> ReadAssetAsync(AmazonS3Client s3, string bucket, string key)
    {
        using var response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key });
        using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task DeleteEphemeralCredentialsAsync(
        AmazonManagedGrafanaClient grafana,
        string workspaceId,
        string serviceAccountId,
        string? tokenId,
        ILambdaContext context)
    {
        Exception? cleanupFailure = null;
        if (!string.IsNullOrWhiteSpace(tokenId))
        {
            try
            {
                await grafana.DeleteWorkspaceServiceAccountTokenAsync(new DeleteWorkspaceServiceAccountTokenRequest
                {
                    WorkspaceId = workspaceId,
                    ServiceAccountId = serviceAccountId,
                    TokenId = tokenId,
                });
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
                context.Logger.LogError($"Failed to delete temporary Grafana service-account token: {exception.Message}");
            }
        }

        try
        {
            await grafana.DeleteWorkspaceServiceAccountAsync(new DeleteWorkspaceServiceAccountRequest
            {
                WorkspaceId = workspaceId,
                ServiceAccountId = serviceAccountId,
            });
        }
        catch (Exception exception)
        {
            cleanupFailure ??= exception;
            context.Logger.LogError($"Failed to delete temporary Grafana service account: {exception.Message}");
        }

        if (cleanupFailure is not null)
        {
            throw new InvalidOperationException("Grafana provisioning succeeded but temporary credentials could not be fully removed", cleanupFailure);
        }
    }

    private static async Task RespondAsync(
        JsonElement request,
        string status,
        string physicalResourceId,
        Dictionary<string, string>? data,
        string? reason)
    {
        var responseUrl = RequiredString(request, "ResponseURL");
        var response = new Dictionary<string, object?>
        {
            ["Status"] = status,
            ["Reason"] = reason ?? "See CloudWatch Logs for the custom-resource provider",
            ["PhysicalResourceId"] = physicalResourceId,
            ["StackId"] = RequiredString(request, "StackId"),
            ["RequestId"] = RequiredString(request, "RequestId"),
            ["LogicalResourceId"] = RequiredString(request, "LogicalResourceId"),
            ["NoEcho"] = false,
            ["Data"] = data ?? new Dictionary<string, string>(),
        };

        using var put = new HttpRequestMessage(HttpMethod.Put, responseUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(response, JsonOptions), Encoding.UTF8),
        };
        using var result = await Http.SendAsync(put);
        result.EnsureSuccessStatusCode();
    }

    private static string WorkspaceUrl(string endpoint)
    {
        return endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? endpoint.TrimEnd('/')
            : $"https://{endpoint.TrimEnd('/')}";
    }

    private static string RequiredString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidOperationException($"CloudFormation request is missing required string property '{propertyName}'");
        }

        return property.GetString()!;
    }

    private static string? OptionalString(JsonElement value, string propertyName)
    {
        return value.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private sealed record GrafanaResponse(HttpStatusCode StatusCode, string Body);

    private sealed record ProvisioningProperties(
        string WorkspaceId,
        string WorkspaceEndpoint,
        string AdminGroupId,
        string EditorGroupId,
        string ViewerGroupId,
        string AnalyticsBucketName,
        string DatabaseName,
        string WorkGroupName,
        string Region,
        string FleetDashboardAssetBucket,
        string FleetDashboardAssetKey,
        string UserDrilldownDashboardAssetBucket,
        string UserDrilldownDashboardAssetKey,
        string FleetDashboardUid,
        string UserDrilldownDashboardUid)
    {
        public static ProvisioningProperties From(JsonElement properties)
        {
            return new ProvisioningProperties(
                RequiredString(properties, nameof(WorkspaceId)),
                RequiredString(properties, nameof(WorkspaceEndpoint)),
                RequiredString(properties, nameof(AdminGroupId)),
                RequiredString(properties, nameof(EditorGroupId)),
                RequiredString(properties, nameof(ViewerGroupId)),
                RequiredString(properties, nameof(AnalyticsBucketName)),
                RequiredString(properties, nameof(DatabaseName)),
                RequiredString(properties, nameof(WorkGroupName)),
                RequiredString(properties, nameof(Region)),
                RequiredString(properties, nameof(FleetDashboardAssetBucket)),
                RequiredString(properties, nameof(FleetDashboardAssetKey)),
                RequiredString(properties, nameof(UserDrilldownDashboardAssetBucket)),
                RequiredString(properties, nameof(UserDrilldownDashboardAssetKey)),
                RequiredString(properties, nameof(FleetDashboardUid)),
                RequiredString(properties, nameof(UserDrilldownDashboardUid)));
        }
    }
}
