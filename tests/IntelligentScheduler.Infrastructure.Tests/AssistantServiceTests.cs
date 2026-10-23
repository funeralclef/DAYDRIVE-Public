using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Xunit;

namespace IntelligentScheduler.Infrastructure.Tests;

public sealed class AssistantServiceTests
{
    [Fact]
    public async Task LocalAssistant_CreatesReviewableSuggestionWithoutNetwork()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        // No local model is installed, so the offline parser answers.
        var service = harness.CreateAssistant();

        var reply = await service.AskAsync("create reading session tomorrow evening 40 minutes", cancellationToken);

        var suggestion = Assert.Single(reply.Suggestions);
        Assert.Contains("reading session", suggestion.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(40, suggestion.DurationMinutes);
        Assert.Equal("Local", reply.Provider);
    }

    [Fact]
    public void StructuredReply_ParsesScheduleActionsAndCriticality()
    {
        const string json = """
            {"message":"Готово","tasks":[],"actions":[
              {"kind":"create_event","title":"Фокус","startLocal":"2026-08-24T10:00:00+03:00","endLocal":"2026-08-24T11:00:00+03:00","category":"Work","priority":75},
              {"kind":"delete_event","targetId":"11111111-1111-1111-1111-111111111111","reason":"По просьбе пользователя"}
            ]}
            """;

        var reply = AssistantService.ParseReply(json, "Test");

        Assert.Equal("Готово", reply.Message);
        Assert.Equal(2, reply.ScheduleActions.Count);
        Assert.Equal(AssistantActionKind.CreateEvent, reply.ScheduleActions[0].Kind);
        Assert.False(reply.ScheduleActions[0].IsCritical);
        Assert.Equal(EventCategory.Work, reply.ScheduleActions[0].Category);
        Assert.Equal(AssistantActionKind.DeleteEvent, reply.ScheduleActions[1].Kind);
        Assert.True(reply.ScheduleActions[1].IsCritical);
    }

    [Fact]
    public void StructuredReply_AllowsOrdinaryConversationWithoutActions()
    {
        var reply = AssistantService.ParseReply(
            """{"message":"Можно обсудить это подробнее.","tasks":[],"actions":[]}""", "Test");

        Assert.Equal("Можно обсудить это подробнее.", reply.Message);
        Assert.Empty(reply.ScheduleActions);
    }

    [Fact]
    public void StructuredReply_SurvivesWrongTypesInsteadOfThrowing()
    {
        // A model that answers 45 where the schema said "string" used to throw out of the
        // parser, and the blanket catch upstream reported it as a lost connection.
        const string json = """
            {"message":"ок","tasks":[{"title":"Отчёт","durationMinutes":"45","priority":70.0}],
             "actions":[{"kind":"create_event","title":123,"startLocal":"2026-08-24T10:00:00","durationMinutes":"30"}]}
            """;

        var reply = AssistantService.ParseReply(json, "Test");

        Assert.Equal("ок", reply.Message);
        Assert.Equal(45, Assert.Single(reply.Suggestions).DurationMinutes);
        var action = Assert.Single(reply.ScheduleActions);
        Assert.Equal("123", action.Title);
        Assert.Equal(30, action.DurationMinutes);
    }

    [Fact]
    public void StructuredReply_KeepsProseWhenTheModelWrapsJsonInText()
    {
        var fence = new string('`', 3);
        var wrapped = string.Join('\n',
            "Конечно! " + fence + "json",
            """{"message":"Вот план","tasks":[],"actions":[]}""",
            fence + " Готов уточнить.");
        var reply = AssistantService.ParseReply(wrapped, "Test");

        Assert.Equal("Вот план", reply.Message);
    }

    [Fact]
    public void StructuredReply_TreatsUnrelatedJsonAsPlainText()
    {
        var reply = AssistantService.ParseReply("""Тут пример конфига: {"foo": 1}""", "Test");
        Assert.Contains("Тут пример конфига", reply.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderErrorBody_IsReducedToItsHumanMessage()
    {
        Assert.Equal("context length exceeded",
            AssistantService.ExtractError("""{"error":{"message":"context length exceeded","code":"ctx"}}"""));
        Assert.Equal("plain failure", AssistantService.ExtractError("plain failure"));
    }

    [Theory]
    [InlineData("http://localhost:11434", "/v1/chat/completions", "http://localhost:11434/v1/chat/completions")]
    [InlineData("https://api.example.com/v1", "/v1/chat/completions", "https://api.example.com/v1/chat/completions")]
    [InlineData("https://api.example.com/v1/chat/completions", "/v1/chat/completions", "https://api.example.com/v1/chat/completions")]
    public void EndpointCombination_DoesNotDuplicateTheVersionSegment(string endpoint, string path, string expected)
        => Assert.Equal(expected, AssistantService.CombineEndpoint(endpoint, path));

    [Fact]
    public void ConversationTitle_IsShortenedOnAWordBoundary()
    {
        Assert.Equal("Коротко", AssistantService.BuildTitle("Коротко"));
        Assert.EndsWith("…", AssistantService.BuildTitle(new string('a', 20) + " " + new string('b', 60)), StringComparison.Ordinal);
    }

    [Fact]
    public void SecretProtector_RoundTripsWithoutStoringPlaintext()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"daydrive-secret-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var protector = new LocalSecretProtector(directory);
            var cipher = protector.Protect("private-api-key");
            Assert.DoesNotContain("private-api-key", cipher, StringComparison.Ordinal);
            Assert.Equal("private-api-key", protector.Unprotect(cipher));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TestConnection_LocalModelNotInstalled_ReturnsInformativeResult()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await harness.ConfigureAsync(settings =>
        {
            settings.AssistantProvider = AssistantProvider.Local;
            settings.LocalModelId = "balanced";
        }, cancellationToken);

        var result = await harness.CreateAssistant().TestConnectionAsync(cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("Choose and download", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
