using System.Text.Json;
using ModelContextProtocol.Protocol;
using OpenClaw.TestSupport.Ui;

namespace OpenClaw.Tray.IntegrationTests;

public sealed class LvtProtocolTests
{
    private const string Element = """
        {"id":"e2","ref":"uia:e2","key":"uia|preference","type":"CheckBox","properties":{"AutomationId":"preference","Toggle.ToggleState":"On"}}
        """;

    [Theory]
    [InlineData("""{"ok":false,"error":"refused"}""")]
    [InlineData("""{"ok":"true"}""")]
    [InlineData("""{"ok":true,"error":"refused"}""")]
    [InlineData("""{"error":"refused"}""")]
    public void SuccessShapedTransportDoesNotHideToolFailure(string json) =>
        Assert.Throws<InvalidOperationException>(() => LvtProtocol.RequireSuccess("toggle", Result(json)));

    [Fact]
    public void McpErrorIsNotOverriddenByStructuredSuccess() =>
        Assert.Throws<InvalidOperationException>(() =>
            LvtProtocol.RequireSuccess("toggle", new CallToolResult
            {
                IsError = true, StructuredContent = JsonSerializer.SerializeToElement(new { ok = true })
            }));

    [Fact]
    public void TextOnlySuccessIsNotAccepted() =>
        Assert.Throws<InvalidDataException>(() =>
            LvtProtocol.RequireSuccess("find_elements", new CallToolResult
            {
                Content = [new TextContentBlock { Text = """{"ok":true}""" }]
            }));

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"truncated":"deadline","elements":[]}""")]
    [InlineData("""{"root":{"properties":{"Truncated":"true"}}}""")]
    public void IncompleteOrMalformedResultsCannotProveAbsence(string json) =>
        Assert.Throws<InvalidDataException>(() => LvtProtocol.RequireSuccess("find_elements", Result(json)));

    [Fact]
    public void InspectionDoesNotRequireAnUndocumentedOkField()
    {
        var response = LvtProtocol.RequireSuccess("find_elements",
            Result($$"""{"tree":"uia","elements":[{{Element}}]}"""));
        var element = LvtProtocol.UniqueElement(response, "preference");
        Assert.NotNull(element);
        Assert.Equal("On", LvtProtocol.Property(element.Value, "Toggle.ToggleState"));
    }

    [Fact]
    public void ACompleteEmptySearchMeansAbsent()
    {
        using var response = JsonDocument.Parse("""{"tree":"uia","elements":[]}""");
        Assert.Null(LvtProtocol.UniqueElement(response.RootElement, "missing"));
    }

    [Theory]
    [InlineData("wrong", "uia:e2")]
    [InlineData("preference", "visual:e2")]
    [InlineData("preference", "e2")]
    [InlineData("preference", "")]
    public void WrongIdentityOrTreeReferenceIsRejected(string automationId, string reference)
    {
        var response = JsonSerializer.SerializeToElement(new
        {
            tree = "uia",
            elements = new[] { new { @ref = reference, key = "uia|preference", properties = new { AutomationId = automationId } } }
        });
        Assert.Throws<InvalidDataException>(() => LvtProtocol.UniqueElement(response, "preference"));
    }

    [Fact]
    public void DuplicateAutomationIdsDoNotSelectTheFirstMatch()
    {
        using var response = JsonDocument.Parse($$"""{"tree":"uia","elements":[{{Element}},{{Element}}]}""");
        Assert.Throws<InvalidDataException>(() => LvtProtocol.UniqueElement(response.RootElement, "preference"));
    }

    [Fact]
    public void VisualSearchIsNotASilentAccessibilityFallback()
    {
        using var response = JsonDocument.Parse("""{"tree":"visual","elements":[]}""");
        Assert.Throws<InvalidDataException>(() => LvtProtocol.UniqueElement(response.RootElement, "preference"));
    }

    [Fact]
    public void UnsupportedPropertyDoesNotBecomeADefaultValue()
    {
        using var response = JsonDocument.Parse(Element);
        Assert.Throws<InvalidDataException>(() => LvtProtocol.Property(response.RootElement, "Value.Value"));
    }

    [Fact]
    public void PositionalReferenceWithoutDurableKeyCannotBeUsedForAnAction()
    {
        using var response = JsonDocument.Parse(
            """{"tree":"uia","elements":[{"id":"e2","ref":"uia:e2","properties":{"AutomationId":"preference"}}]}""");
        Assert.Throws<InvalidDataException>(() => LvtProtocol.UniqueElement(response.RootElement, "preference"));
    }

    private static CallToolResult Result(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new() { StructuredContent = document.RootElement.Clone() };
    }
}
