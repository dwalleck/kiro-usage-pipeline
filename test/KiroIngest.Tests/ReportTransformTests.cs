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
    public async Task Transform_KiroCliCsv_MapsAllUsageDailyFields()
    {
        var result = ReportTransform.Transform(KiroCliCsv, Targets(TargetEmail));

        await Assert.That(result.RowsRead).IsEqualTo(1);
        await Assert.That(result.RowsKept).IsEqualTo(1);
        await Assert.That(result.Partitions.Count).IsEqualTo(1);
        var p = result.Partitions[0];
        await Assert.That(p.Date).IsEqualTo("2026-07-10");
        await Assert.That(p.ClientType).IsEqualTo("KIRO_CLI");
        await Assert.That(p.UsageDaily.Count).IsEqualTo(1);

        var u = p.UsageDaily[0];
        await Assert.That(u.UserId).IsEqualTo("215bb5b0-00a1-70cd-1caf-57794fdc8915");
        await Assert.That(u.UserEmail).IsEqualTo(TargetEmail);
        await Assert.That(u.ChatConversations).IsEqualTo(8L);
        await Assert.That(u.CreditsUsed).IsEqualTo(114.45787414391377).Within(1e-9);
        await Assert.That(u.OverageCap).IsEqualTo(2500.0).Within(1e-9);
        await Assert.That(u.OverageCreditsUsed).IsEqualTo(0.0).Within(1e-9);
        await Assert.That(u.OverageEnabled).IsFalse();
        await Assert.That(u.SubscriptionTier).IsEqualTo("PRO_MAX");
        await Assert.That(u.TotalMessages).IsEqualTo(131L);
        await Assert.That(u.NewUser).IsFalse();
        await Assert.That(u.ProfileId).IsEqualTo("arn:aws:codewhisperer:us-east-1:369434902231:profile/UV4C4VUDDGRU");
    }

    [Test]
    public async Task Transform_KiroCliCsv_UnpivotsModelsPreservingDots()
    {
        var p = ReportTransform.Transform(KiroCliCsv, Targets(TargetEmail)).Partitions[0];

        await Assert.That(p.ModelMessages.Count).IsEqualTo(2);
        await Assert.That(p.ModelMessages.Single(m => m.Model == "claude_haiku_4.5").Messages).IsEqualTo(8L);
        await Assert.That(p.ModelMessages.Single(m => m.Model == "claude_opus_4.8").Messages).IsEqualTo(123L);
        // Total_Messages must NOT be treated as a model column.
        await Assert.That(p.ModelMessages.Any(m => m.Model.Contains("total", StringComparison.OrdinalIgnoreCase))).IsFalse();
    }

    [Test]
    public async Task Transform_KiroIdeCsv_MapsAutoMessagesToAutoModel()
    {
        var p = ReportTransform.Transform(KiroIdeCsv, Targets(TargetEmail)).Partitions[0];

        await Assert.That(p.ClientType).IsEqualTo("KIRO_IDE");
        await Assert.That(p.ModelMessages.Count).IsEqualTo(1);
        await Assert.That(p.ModelMessages[0].Model).IsEqualTo("auto");
        await Assert.That(p.ModelMessages[0].Messages).IsEqualTo(4L);
    }

    [Test]
    public async Task Transform_UserNotInTargetList_ReturnsEmpty()
    {
        var result = ReportTransform.Transform(KiroCliCsv, Targets("someone.else@example.com"));

        await Assert.That(result.Partitions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Transform_OverageEnabledTrue_ParsesTrue()
    {
        var csv =
            StaticHeader + ",auto_messages\n" +
            "2026-07-01,\"u1\",KIRO_CLI,1,1.0,2500.0,0.0,true,\"arn\",PRO_MAX,5,false,\"dwalleck@proton.me\",5";

        var p = ReportTransform.Transform(csv, Targets(TargetEmail)).Partitions[0];

        await Assert.That(p.UsageDaily[0].OverageEnabled).IsTrue();
    }

    [Test]
    public async Task Transform_MissingRequiredColumn_Throws()
    {
        var csv =
            "Date,UserId,Client_Type,Overage_Cap,User_Email,auto_messages\n" +
            "2026-07-01,u1,KIRO_CLI,2500.0,dwalleck@proton.me,5";

        await Assert.That(() => ReportTransform.Transform(csv, Targets(TargetEmail)))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task Transform_TruncatedRow_Throws()
    {
        var csv =
            StaticHeader + ",auto_messages\n" +
            "2026-07-01,\"u1\",KIRO_CLI,1,1.0,2500.0,0.0,false,\"arn\",PRO_MAX,5,false,\"dwalleck@proton.me";

        await Assert.That(() => ReportTransform.Transform(csv, Targets(TargetEmail)))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task Transform_NegativeModelMessages_Throws()
    {
        var csv =
            StaticHeader + ",auto_messages,claude_opus_4.8_messages\n" +
            "2026-07-01,\"u1\",KIRO_CLI,1,1.0,2500.0,0.0,false,\"arn\",PRO_MAX,5,false,\"dwalleck@proton.me\",5,-1";

        await Assert.That(() => ReportTransform.Transform(csv, Targets(TargetEmail)))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task Transform_MissingDateColumn_Throws()
    {
        var csv =
            "UserId,Client_Type,User_Email,auto_messages\n" +
            "u1,KIRO_CLI,dwalleck@proton.me,5";

        await Assert.That(() => ReportTransform.Transform(csv, Targets(TargetEmail)))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task Transform_ZeroMessageModelCell_DropsRow()
    {
        var csv =
            StaticHeader + ",auto_messages,glm_5_messages\n" +
            "2026-07-01,\"u1\",KIRO_CLI,1,1.0,2500.0,0.0,false,\"arn\",PRO_MAX,5,false,\"dwalleck@proton.me\",0,5";

        var p = ReportTransform.Transform(csv, Targets(TargetEmail)).Partitions[0];

        await Assert.That(p.ModelMessages.Count).IsEqualTo(1);
        await Assert.That(p.ModelMessages[0].Model).IsEqualTo("glm_5");
        await Assert.That(p.ModelMessages[0].Messages).IsEqualTo(5L);
    }

    [Test]
    public async Task Transform_DuplicateDailyUsageGrain_Throws()
    {
        var csv =
            StaticHeader + ",auto_messages\n" +
            "2026-07-01,\"u1\",KIRO_CLI,1,1.0,2500.0,0.0,false,\"arn\",PRO_MAX,5,false,\"dwalleck@proton.me\",5\n" +
            "2026-07-01,\"u1\",KIRO_CLI,2,2.0,2500.0,0.0,false,\"arn\",PRO_MAX,7,false,\"dwalleck@proton.me\",7";

        await Assert.That(() => ReportTransform.Transform(csv, Targets(TargetEmail)))
            .Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("not-a-date", "KIRO_CLI", "1.0", "5")]
    [Arguments("2026-07-01", "UNKNOWN", "1.0", "5")]
    [Arguments("2026-07-01", "KIRO_CLI", "NaN", "5")]
    [Arguments("2026-07-01", "KIRO_CLI", "Infinity", "5")]
    [Arguments("2026-07-01", "KIRO_CLI", "-Infinity", "5")]
    [Arguments("2026-07-01", "KIRO_CLI", "1.0", "-1")]
    public async Task Transform_InvalidDomainValue_Throws(
        string date,
        string clientType,
        string credits,
        string totalMessages)
    {
        var csv =
            StaticHeader + ",auto_messages\n" +
            $"{date},\"u1\",{clientType},1,{credits},2500.0,0.0,false,\"arn\",PRO_MAX,{totalMessages},false,\"dwalleck@proton.me\",5";

        await Assert.That(() => ReportTransform.Transform(csv, Targets(TargetEmail)))
            .Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("user-activity-reports/AWSLogs/369434902231/KiroLogs/user_report/us-east-1/2026/07/10/00/KIRO_CLI_369434902231_user_report_202607100000.csv",
        "KIRO_CLI_369434902231_user_report_202607100000")]
    [Arguments("KIRO_IDE_1_user_report_1.csv", "KIRO_IDE_1_user_report_1")]
    [Arguments("UPPERCASE.CSV", "UPPERCASE")]
    public async Task BaseName_MultipleKeys_StripsPathAndExtension(string key, string expected)
    {
        await Assert.That(ReportTransform.BaseName(key)).IsEqualTo(expected);
    }

    [Test]
    public async Task OutputKey_StandardInput_PlacesUnderPartitionWithSourceIdentity()
    {
        var key = ReportTransform.OutputKey(
            "usage_daily",
            "2026-07-10",
            "KIRO_CLI",
            "raw-bucket",
            "x/y/KIRO_CLI_369434902231_user_report_202607100000.csv");

        await Assert.That(key)
            .StartsWith("usage_daily/date=2026-07-10/client_type=KIRO_CLI/KIRO_CLI_369434902231_user_report_202607100000-")
            .And.EndsWith(".parquet");
    }

    [Test]
    public async Task OutputKey_SameBasenameDifferentSourcePaths_DoesNotCollide()
    {
        var first = ReportTransform.OutputKey(
            "usage_daily", "2026-07-10", "KIRO_CLI", "raw-bucket", "a/report.csv");
        var second = ReportTransform.OutputKey(
            "usage_daily", "2026-07-10", "KIRO_CLI", "raw-bucket", "b/report.csv");

        await Assert.That(first).IsNotEqualTo(second);
    }
}
