using KiroIngest;

namespace KiroIngest.Tests;

public class ReportTransformTests
{
    private const string TargetEmail = "dwalleck@proton.me";

    private static readonly string StaticHeader =
        "Date,UserId,Client_Type,Chat_Conversations,Credits_Used,Overage_Cap," +
        "Overage_Credits_Used,Overage_Enabled,ProfileId,Subscription_Tier," +
        "Total_Messages,New_User,User_Email";

    private static ISet<string> Targets(params string[] emails) =>
        new HashSet<string>(emails, StringComparer.Ordinal);

    // Real KIRO_CLI row from the ticket-01 findings (models: claude_haiku_4.5, claude_opus_4.8).
    private const string KiroCliCsv =
        "Date,UserId,Client_Type,Chat_Conversations,Credits_Used,Overage_Cap,Overage_Credits_Used,Overage_Enabled,ProfileId,Subscription_Tier,Total_Messages,New_User,User_Email,claude_haiku_4.5_messages,claude_opus_4.8_messages\n" +
        "2026-07-10,\"215bb5b0-00a1-70cd-1caf-57794fdc8915\",KIRO_CLI,8,114.45787414391377,2500.0,0.0,false,\"arn:aws:codewhisperer:us-east-1:369434902231:profile/UV4C4VUDDGRU\",PRO_MAX,131,false,\"dwalleck@proton.me\",8,123";

    // Real KIRO_IDE row from the ticket-01 findings (model: auto).
    private const string KiroIdeCsv =
        "Date,UserId,Client_Type,Chat_Conversations,Credits_Used,Overage_Cap,Overage_Credits_Used,Overage_Enabled,ProfileId,Subscription_Tier,Total_Messages,New_User,User_Email,auto_messages\n" +
        "2026-07-10,\"215bb5b0-00a1-70cd-1caf-57794fdc8915\",KIRO_IDE,2,0.7132330391376451,2500.0,0.0,false,\"arn:aws:codewhisperer:us-east-1:369434902231:profile/UV4C4VUDDGRU\",PRO_MAX,4,false,\"dwalleck@proton.me\",4";

    [Test]
    public async Task KiroCli_row_maps_usage_daily_fields()
    {
        var partitions = ReportTransform.Transform(KiroCliCsv, Targets(TargetEmail));

        await Assert.That(partitions.Count).IsEqualTo(1);
        var p = partitions[0];
        await Assert.That(p.Date).IsEqualTo("2026-07-10");
        await Assert.That(p.ClientType).IsEqualTo("KIRO_CLI");
        await Assert.That(p.UsageDaily.Count).IsEqualTo(1);

        var u = p.UsageDaily[0];
        await Assert.That(u.user_id).IsEqualTo("215bb5b0-00a1-70cd-1caf-57794fdc8915");
        await Assert.That(u.user_email).IsEqualTo(TargetEmail);
        await Assert.That(u.chat_conversations).IsEqualTo(8L);
        await Assert.That(u.credits_used).IsEqualTo(114.45787414391377).Within(1e-9);
        await Assert.That(u.overage_cap).IsEqualTo(2500.0).Within(1e-9);
        await Assert.That(u.overage_credits_used).IsEqualTo(0.0).Within(1e-9);
        await Assert.That(u.overage_enabled).IsFalse();
        await Assert.That(u.subscription_tier).IsEqualTo("PRO_MAX");
        await Assert.That(u.total_messages).IsEqualTo(131L);
        await Assert.That(u.new_user).IsFalse();
        await Assert.That(u.profile_id).IsEqualTo("arn:aws:codewhisperer:us-east-1:369434902231:profile/UV4C4VUDDGRU");
    }

    [Test]
    public async Task KiroCli_row_unpivots_models_preserving_dots()
    {
        var p = ReportTransform.Transform(KiroCliCsv, Targets(TargetEmail))[0];

        await Assert.That(p.ModelMessages.Count).IsEqualTo(2);
        await Assert.That(p.ModelMessages.Single(m => m.model == "claude_haiku_4.5").messages).IsEqualTo(8L);
        await Assert.That(p.ModelMessages.Single(m => m.model == "claude_opus_4.8").messages).IsEqualTo(123L);
        // Total_Messages must NOT be treated as a model column.
        await Assert.That(p.ModelMessages.Any(m => m.model.Contains("total", StringComparison.OrdinalIgnoreCase))).IsFalse();
    }

    [Test]
    public async Task Auto_messages_becomes_ordinary_auto_model_row()
    {
        var p = ReportTransform.Transform(KiroIdeCsv, Targets(TargetEmail))[0];

        await Assert.That(p.ClientType).IsEqualTo("KIRO_IDE");
        await Assert.That(p.ModelMessages.Count).IsEqualTo(1);
        await Assert.That(p.ModelMessages[0].model).IsEqualTo("auto");
        await Assert.That(p.ModelMessages[0].messages).IsEqualTo(4L);
    }

    [Test]
    public async Task User_absent_from_target_list_is_dropped_fail_closed()
    {
        var partitions = ReportTransform.Transform(KiroCliCsv, Targets("someone.else@example.com"));

        await Assert.That(partitions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Zero_count_model_cells_are_dropped()
    {
        var csv =
            StaticHeader + ",auto_messages,glm_5_messages\n" +
            "2026-07-01,\"u1\",KIRO_CLI,1,1.0,2500.0,0.0,false,\"arn\",PRO_MAX,5,false,\"dwalleck@proton.me\",0,5";

        var p = ReportTransform.Transform(csv, Targets(TargetEmail))[0];

        await Assert.That(p.ModelMessages.Count).IsEqualTo(1);
        await Assert.That(p.ModelMessages[0].model).IsEqualTo("glm_5");
        await Assert.That(p.ModelMessages[0].messages).IsEqualTo(5L);
    }

    [Test]
    public async Task Multiple_rows_same_partition_group_together()
    {
        var csv =
            StaticHeader + ",auto_messages\n" +
            "2026-07-01,\"u1\",KIRO_CLI,1,1.0,2500.0,0.0,false,\"arn\",PRO_MAX,5,false,\"dwalleck@proton.me\",5\n" +
            "2026-07-01,\"u1\",KIRO_CLI,2,2.0,2500.0,0.0,false,\"arn\",PRO_MAX,7,false,\"dwalleck@proton.me\",7";

        var partitions = ReportTransform.Transform(csv, Targets(TargetEmail));

        await Assert.That(partitions.Count).IsEqualTo(1);
        await Assert.That(partitions[0].UsageDaily.Count).IsEqualTo(2);
        await Assert.That(partitions[0].ModelMessages.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("user-activity-reports/AWSLogs/369434902231/KiroLogs/user_report/us-east-1/2026/07/10/00/KIRO_CLI_369434902231_user_report_202607100000.csv",
        "KIRO_CLI_369434902231_user_report_202607100000")]
    [Arguments("KIRO_IDE_1_user_report_1.csv", "KIRO_IDE_1_user_report_1")]
    public async Task BaseName_strips_path_and_csv_extension(string key, string expected)
    {
        await Assert.That(ReportTransform.BaseName(key)).IsEqualTo(expected);
    }

    [Test]
    public async Task OutputKey_places_basename_under_partition_path()
    {
        var key = ReportTransform.OutputKey(
            "usage_daily", "2026-07-10", "KIRO_CLI",
            "x/y/KIRO_CLI_369434902231_user_report_202607100000.csv");

        await Assert.That(key).IsEqualTo(
            "usage_daily/date=2026-07-10/client_type=KIRO_CLI/KIRO_CLI_369434902231_user_report_202607100000.parquet");
    }
}
