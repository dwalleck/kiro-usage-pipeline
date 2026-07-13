using KiroIngest;

namespace KiroIngest.Tests;

public class IngestSourceTests
{
    [Test]
    public async Task Constructor_EmptyBucket_Throws()
    {
        await Assert.That(() => new IngestSource("", "key.csv"))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments("")]
    [Arguments("  ")]
    [Arguments("ZZ")]
    public async Task Constructor_InvalidSequencer_Throws(string sequencer)
    {
        await Assert.That(() => new IngestSource("bucket", "key.csv", sequencer: sequencer))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Constructor_LiveSequencerAndBackfillDate_Throws()
    {
        await Assert.That(() => new IngestSource(
                "bucket",
                "key.csv",
                sequencer: "01",
                expectedDate: new DateOnly(2026, 7, 10)))
            .Throws<ArgumentException>();
    }
}
