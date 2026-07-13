using KiroIngest;
using Parquet;

namespace KiroIngest.Tests;

public class ParquetSerializationTests
{
    [Test]
    public async Task SerializeAsync_UsageDailyRecord_ProducesCorrectSchema()
    {
        var record = new UsageDailyRecord
        {
            UserId = "u",
            UserEmail = "e",
            ChatConversations = 8,
            CreditsUsed = 114.45787414391377,
            OverageCap = 2500.0,
            OverageCreditsUsed = 0.0,
            OverageEnabled = false,
            SubscriptionTier = "PRO_MAX",
            TotalMessages = 131,
            NewUser = false,
            ProfileId = "arn",
        };

        var (names, types) = await SchemaOf([record]);

        await Assert.That(names).IsEquivalentTo(new[]
        {
            "user_id", "user_email", "chat_conversations", "credits_used", "overage_cap",
            "overage_credits_used", "overage_enabled", "subscription_tier", "total_messages",
            "new_user", "profile_id",
        });

        // Partition keys must never appear in the Parquet body.
        await Assert.That(names.Contains("date")).IsFalse();
        await Assert.That(names.Contains("client_type")).IsFalse();

        // Spot-check the type mapping that matters for Athena.
        await Assert.That(types["credits_used"]).IsEqualTo(typeof(double));
        await Assert.That(types["chat_conversations"]).IsEqualTo(typeof(long));
        await Assert.That(types["total_messages"]).IsEqualTo(typeof(long));
        await Assert.That(types["overage_enabled"]).IsEqualTo(typeof(bool));
        await Assert.That(types["user_email"]).IsEqualTo(typeof(string));
    }

    [Test]
    public async Task SerializeAsync_ModelMessageRecord_ProducesCorrectSchema()
    {
        var record = new ModelMessageRecord { UserId = "u", UserEmail = "e", Model = "auto", Messages = 4 };

        var (names, types) = await SchemaOf([record]);

        await Assert.That(names).IsEquivalentTo(new[] { "user_id", "user_email", "model", "messages" });
        await Assert.That(types["messages"]).IsEqualTo(typeof(long));
        await Assert.That(types["model"]).IsEqualTo(typeof(string));
    }

    private static async Task<(string[] Names, Dictionary<string, Type> Types)> SchemaOf<T>(IReadOnlyList<T> records)
        where T : class, new()
    {
        using var stream = await ParquetSerialization.SerializeAsync(records);
        await using var reader = await ParquetReader.CreateAsync(stream);

        var fields = reader.Schema.DataFields;
        var names = fields.Select(f => f.Name).ToArray();
        var types = fields.ToDictionary(f => f.Name, f => f.ClrType);
        return (names, types);
    }
}
