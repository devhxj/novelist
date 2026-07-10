using System.Text.Json;

namespace Novelist.IntegrationTests.TestDoubles;

public sealed class CorpusDrivenWritingGoldenFixtureTests
{
    private static readonly ISet<string> ForbiddenExpectedPropertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "text",
        "content",
        "source_text",
        "raw_text",
        "raw_source",
        "node_text",
        "source_path",
        "embedding",
        "embedding_json",
        "vector",
        "prompt",
        "model_output_json",
        "value_json",
        "technique_abstract",
        "transfer_template",
        "why_it_works_json"
    };

    [Fact]
    public void GoldenFixtureSkeletonContainsCorpusContextAndExpectedOutputs()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "corpus-driven-writing",
            "g3-harness-golden-skeleton.json");

        using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var root = document.RootElement;
        var fixture = root.GetProperty("fixtures")[0];

        Assert.Equal("corpus-driven-writing-golden-fixtures-v1", root.GetProperty("schema_version").GetString());
        Assert.Equal("g3-small-rain-doorway", fixture.GetProperty("fixture_id").GetString());
        Assert.NotEmpty(fixture.GetProperty("corpus").GetProperty("sources").EnumerateArray());
        Assert.NotEmpty(fixture.GetProperty("current_chapter_contexts").EnumerateArray());
        Assert.NotEmpty(fixture.GetProperty("query_contexts").EnumerateArray());
        Assert.NotEmpty(fixture.GetProperty("expected_retrieval").GetProperty("top_nodes").EnumerateArray());
        Assert.NotEmpty(fixture.GetProperty("expected_blueprint").GetProperty("beats").EnumerateArray());
        Assert.NotEmpty(fixture.GetProperty("expected_insertion").GetProperty("pieces").EnumerateArray());
    }

    [Fact]
    public void M3RetrievalGoldenFixtureContainsExpectedRetrievalContract()
    {
        using var document = LoadCorpusDrivenWritingFixture("m3-retrieval-golden.json");
        var root = document.RootElement;
        var fixtures = root.GetProperty("fixtures").EnumerateArray().ToArray();

        Assert.Equal("corpus-driven-writing-m3-retrieval-golden-v1", root.GetProperty("schema_version").GetString());
        Assert.Equal(
            ["m3-rain-doorway-basic-search", "m3-four-way-recall-diagnostics"],
            fixtures.Select(item => item.GetProperty("fixture_id").GetString() ?? string.Empty).ToArray());
        Assert.All(fixtures, fixture =>
        {
            var expected = fixture.GetProperty("expected_retrieval");
            Assert.True(expected.GetProperty("page").GetProperty("total").GetInt32() > 0);
            Assert.NotEmpty(expected.GetProperty("ranked_node_ids").EnumerateArray());
            Assert.NotEmpty(expected.GetProperty("candidates").EnumerateArray());
            Assert.NotEmpty(expected.GetProperty("excluded_node_ids").EnumerateArray());
            Assert.True(expected.GetProperty("cache_expectations").GetProperty("node_embedding_count").GetInt32() > 0);
        });
    }

    [Fact]
public void M3RetrievalGoldenExpectedBlocksDoNotExposeRawSourceOrEmbeddings()
    {
        using var document = LoadCorpusDrivenWritingFixture("m3-retrieval-golden.json");

        foreach (var fixture in document.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            AssertExpectedBlockDoesNotExposeForbiddenProperties(
                fixture.GetProperty("expected_retrieval"),
                "$.fixtures[" + fixture.GetProperty("fixture_id").GetString() + "].expected_retrieval");
        }
    }

 [Fact]
 public void FullGoldenFixtureContainsFiveHundredLicensedSentencesAcrossLibraries()
 {
 using var document = LoadCorpusDrivenWritingFixture("m0-500-sentence-golden.json");
 var root = document.RootElement;
 var sentences = root.GetProperty("sentences").EnumerateArray().ToArray();

 Assert.Equal("corpus-driven-writing-500-sentence-golden-v1", root.GetProperty("schema_version").GetString());
 Assert.Equal(500, root.GetProperty("sentence_count").GetInt32());
 Assert.Equal(500, sentences.Length);
 Assert.Equal(2, sentences.Select(item => item.GetProperty("library_id").GetString()).Distinct().Count());
 Assert.Equal(5, sentences.Select(item => item.GetProperty("source_id").GetString()).Distinct().Count());
 Assert.All(sentences, sentence =>
 {
 Assert.Equal("authorized", sentence.GetProperty("license_state").GetString());
 Assert.False(string.IsNullOrWhiteSpace(sentence.GetProperty("text").GetString()));
 Assert.Equal(64, sentence.GetProperty("text_hash").GetString()?.Length);
 Assert.True(sentence.GetProperty("expected_evidence").GetProperty("end").GetInt32() > 0);
 });
 }

 private static void AssertExpectedBlockDoesNotExposeForbiddenProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                Assert.False(
                    ForbiddenExpectedPropertyNames.Contains(property.Name),
                    $"Forbidden golden property '{property.Name}' found at {path}.");
                AssertExpectedBlockDoesNotExposeForbiddenProperties(property.Value, path + "." + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                AssertExpectedBlockDoesNotExposeForbiddenProperties(item, path + "[" + index + "]");
                index++;
            }
        }
    }

    private static JsonDocument LoadCorpusDrivenWritingFixture(string fileName)
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "corpus-driven-writing",
            fileName);
        return JsonDocument.Parse(File.ReadAllText(fixturePath));
    }
}
