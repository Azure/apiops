using common;
using FluentAssertions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace common.tests;

public class ApiDiagnosticSerializationTests
{
    [Fact]
    public void ApiDiagnosticDto_Deserializes_WhenLargeLanguageModelMissing()
    {
        const string json = """
        {
          "properties": {
            "loggerId": "/loggers/test-logger"
          }
        }
        """;

        var dto = JsonSerializer.Deserialize<ApiDiagnosticDto>(json, JsonObjectExtensions.SerializerOptions);

        dto.Should().NotBeNull();
        dto!.Properties.LargeLanguageModel.Should().BeNull();
    }

    [Fact]
    public void DiagnosticDto_Deserializes_WhenLargeLanguageModelMissing()
    {
        const string json = """
        {
          "properties": {
            "loggerId": "/loggers/test-logger"
          }
        }
        """;

        var dto = JsonSerializer.Deserialize<DiagnosticDto>(json, JsonObjectExtensions.SerializerOptions);

        dto.Should().NotBeNull();
        dto!.Properties.LargeLanguageModel.Should().BeNull();
    }

    [Fact]
    public void ApiDiagnosticDto_UsesCamelCase_ForLargeLanguageModel()
    {
        var dto = new ApiDiagnosticDto
        {
            Properties = new ApiDiagnosticDto.DiagnosticContract
            {
                LargeLanguageModel = new ApiDiagnosticDto.LargeLanguageModelSettings
                {
                    Logs = "enabled",
                    Requests = new ApiDiagnosticDto.LargeLanguageModelMessageSettings
                    {
                        Messages = "all",
                        MaxSizeInBytes = 4096
                    },
                    Responses = new ApiDiagnosticDto.LargeLanguageModelMessageSettings
                    {
                        Messages = "errors",
                        MaxSizeInBytes = 1024
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(dto, JsonObjectExtensions.SerializerOptions);
        var node = JsonNode.Parse(json)!.AsObject();

        var properties = node["properties"]!.AsObject();
        properties.ContainsKey("largeLanguageModel").Should().BeTrue();

        var llm = properties["largeLanguageModel"]!.AsObject();
        llm.ContainsKey("logs").Should().BeTrue();
        llm.ContainsKey("requests").Should().BeTrue();
        llm.ContainsKey("responses").Should().BeTrue();

        var requests = llm["requests"]!.AsObject();
        requests["maxSizeInBytes"]!.GetValue<int>().Should().Be(4096);

        var responses = llm["responses"]!.AsObject();
        responses["maxSizeInBytes"]!.GetValue<int>().Should().Be(1024);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void ApiDiagnosticDto_SerializesEdge_MaxSizeInBytes(int maxSize)
    {
        var dto = new ApiDiagnosticDto
        {
            Properties = new ApiDiagnosticDto.DiagnosticContract
            {
                LargeLanguageModel = new ApiDiagnosticDto.LargeLanguageModelSettings
                {
                    Requests = new ApiDiagnosticDto.LargeLanguageModelMessageSettings
                    {
                        Messages = "all",
                        MaxSizeInBytes = maxSize
                    },
                    Responses = new ApiDiagnosticDto.LargeLanguageModelMessageSettings
                    {
                        Messages = "all",
                        MaxSizeInBytes = maxSize
                    }
                }
            }
        };

        var roundTripped = JsonSerializer.Deserialize<ApiDiagnosticDto>(
            JsonSerializer.Serialize(dto, JsonObjectExtensions.SerializerOptions),
            JsonObjectExtensions.SerializerOptions);

        roundTripped!.Properties.LargeLanguageModel!.Requests!.MaxSizeInBytes.Should().Be(maxSize);
        roundTripped.Properties.LargeLanguageModel!.Responses!.MaxSizeInBytes.Should().Be(maxSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void DiagnosticDto_SerializesEdge_MaxSizeInBytes(int maxSize)
    {
        var dto = new DiagnosticDto
        {
            Properties = new DiagnosticDto.DiagnosticContract
            {
                LargeLanguageModel = new DiagnosticDto.LargeLanguageModelSettings
                {
                    Logs = "disabled",
                    Requests = new DiagnosticDto.LargeLanguageModelMessageSettings
                    {
                        Messages = "errors",
                        MaxSizeInBytes = maxSize
                    },
                    Responses = new DiagnosticDto.LargeLanguageModelMessageSettings
                    {
                        Messages = "all",
                        MaxSizeInBytes = maxSize
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(dto, JsonObjectExtensions.SerializerOptions);
        var node = JsonNode.Parse(json)!.AsObject();

        var properties = node["properties"]!.AsObject();
        properties.ContainsKey("largeLanguageModel").Should().BeTrue();

        var llm = properties["largeLanguageModel"]!.AsObject();
        llm.ContainsKey("logs").Should().BeTrue();
        llm.ContainsKey("requests").Should().BeTrue();
        llm.ContainsKey("responses").Should().BeTrue();

        var requests = llm["requests"]!.AsObject();
        requests["messages"]!.GetValue<string>().Should().Be("errors");
        requests["maxSizeInBytes"]!.GetValue<int>().Should().Be(maxSize);

        var responses = llm["responses"]!.AsObject();
        responses["messages"]!.GetValue<string>().Should().Be("all");
        responses["maxSizeInBytes"]!.GetValue<int>().Should().Be(maxSize);
    }
}
