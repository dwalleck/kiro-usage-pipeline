using Amazon.CDK;
using Amazon.CDK.AWS.Athena;
using Amazon.CDK.AWS.Glue;
using Amazon.CDK.AWS.S3;
using Constructs;
using System.Collections.Generic;

namespace KiroInfra
{
    // The query layer over the analytics bucket: a Glue database with the two frozen
    // fact tables and a dedicated Athena workgroup. Partitions are resolved by
    // projection (no crawler, no partition registration) — Athena derives
    // date=YYYY-MM-DD/client_type=... straight from the projection config below.
    public class QueryLayer : Construct
    {
        public const string DatabaseName = "kiro_usage";
        public const string WorkGroupName = "kiro-usage";

        // Modest guardrail so a runaway query can't scan the whole bucket. 1 GiB is
        // far more than the tiny fact tables need, but bounds a mistake.
        private const double BytesScannedCap = 1073741824;

        private readonly string _account;
        private readonly IBucket _analyticsBucket;

        public QueryLayer(Construct scope, string id, IBucket analyticsBucket) : base(scope, id)
        {
            _account = Stack.Of(this).Account;
            _analyticsBucket = analyticsBucket;

            var database = new CfnDatabase(this, "Database", new CfnDatabaseProps
            {
                CatalogId = _account,
                DatabaseInput = new CfnDatabase.DatabaseInputProperty
                {
                    Name = DatabaseName,
                },
            });

            // Daily Usage Fact — grain (date, user_id, client_type).
            CreateFactTable(database, "UsageDailyTable", "usage_daily",
            [
                Column("user_id", "string"),
                Column("user_email", "string"),
                Column("chat_conversations", "bigint"),
                Column("credits_used", "double"),
                Column("overage_cap", "double"),
                Column("overage_credits_used", "double"),
                Column("overage_enabled", "boolean"),
                Column("subscription_tier", "string"),
                Column("total_messages", "bigint"),
                Column("new_user", "boolean"),
                Column("profile_id", "string"),
            ]);

            // Model Message Fact — grain (date, user_id, client_type, model).
            CreateFactTable(database, "ModelMessagesTable", "model_messages",
            [
                Column("user_id", "string"),
                Column("user_email", "string"),
                Column("model", "string"),
                Column("messages", "bigint"),
            ]);

            new CfnWorkGroup(this, "WorkGroup", new CfnWorkGroupProps
            {
                Name = WorkGroupName,
                Description = "Kiro usage dashboard queries over the kiro_usage Glue tables",
                State = "ENABLED",
                RecursiveDeleteOption = true,
                WorkGroupConfiguration = new CfnWorkGroup.WorkGroupConfigurationProperty
                {
                    EnforceWorkGroupConfiguration = true,
                    PublishCloudWatchMetricsEnabled = true,
                    BytesScannedCutoffPerQuery = BytesScannedCap,
                    ResultConfiguration = new CfnWorkGroup.ResultConfigurationProperty
                    {
                        OutputLocation = $"s3://{_analyticsBucket.BucketName}/athena-results/",
                    },
                },
            });
        }

        // Both facts are EXTERNAL Parquet tables partitioned by date + client_type via
        // partition projection. Only the storage prefix and column set differ.
        private void CreateFactTable(CfnDatabase database, string constructId, string tableName, CfnTable.ColumnProperty[] columns)
        {
            var prefixLocation = $"s3://{_analyticsBucket.BucketName}/{tableName}/";
            var table = new CfnTable(this, constructId, new CfnTableProps
            {
                CatalogId = _account,
                DatabaseName = DatabaseName,
                TableInput = new CfnTable.TableInputProperty
                {
                    Name = tableName,
                    TableType = "EXTERNAL_TABLE",
                    Parameters = ProjectionParameters(prefixLocation),
                    PartitionKeys = new CfnTable.ColumnProperty[]
                    {
                        Column("date", "date"),
                        Column("client_type", "string"),
                    },
                    StorageDescriptor = new CfnTable.StorageDescriptorProperty
                    {
                        Location = prefixLocation,
                        InputFormat = "org.apache.hadoop.hive.ql.io.parquet.MapredParquetInputFormat",
                        OutputFormat = "org.apache.hadoop.hive.ql.io.parquet.MapredParquetOutputFormat",
                        SerdeInfo = new CfnTable.SerdeInfoProperty
                        {
                            SerializationLibrary = "org.apache.hadoop.hive.ql.io.parquet.serde.ParquetHiveSerDe",
                        },
                        Columns = columns,
                    },
                },
            });

            // The table's database must exist first (CfnTable references it by name).
            table.AddDependency(database);
        }

        // Identical projection config on both tables; only the storage template prefix
        // differs. ${date}/${client_type} are Athena projection placeholders, not C#
        // interpolation (the doubled braces emit a literal ${...}).
        private static Dictionary<string, string> ProjectionParameters(string prefixLocation)
        {
            return new Dictionary<string, string>
            {
                ["classification"] = "parquet",
                ["projection.enabled"] = "true",
                ["projection.date.type"] = "date",
                ["projection.date.format"] = "yyyy-MM-dd",
                ["projection.date.range"] = "2026-06-01,NOW",
                ["projection.date.interval"] = "1",
                ["projection.date.interval.unit"] = "DAYS",
                ["projection.client_type.type"] = "enum",
                ["projection.client_type.values"] = "KIRO_CLI,KIRO_IDE,PLUGIN",
                ["storage.location.template"] = $"{prefixLocation}date=${{date}}/client_type=${{client_type}}",
            };
        }

        private static CfnTable.ColumnProperty Column(string name, string type)
        {
            return new CfnTable.ColumnProperty { Name = name, Type = type };
        }
    }
}
