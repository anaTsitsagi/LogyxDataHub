using System.Text.Json;
using DataHub.Api.Controllers;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace DataHub.Api;

/// <summary>
/// Valid sample request bodies for Swagger, so "Try it out" starts from a request that works
/// instead of "string" placeholders. These are examples only, not defaults.
/// </summary>
public sealed class OpenApiExamples : ISchemaFilter
{
    private static readonly Dictionary<Type, object> Examples = new()
    {
        [typeof(CreateInvitationRequest)] = new CreateInvitationRequest(
            CompanyCode: "123456789",
            CompanyName: "შპს მაგალითი",
            Email: "owner@example.ge",
            Phone: "+995555123456",
            Channels: ["email", "sms"]),
    };

    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is OpenApiSchema concrete && Examples.TryGetValue(context.Type, out var example))
            concrete.Example = JsonSerializer.SerializeToNode(example, JsonSerializerOptions.Web);
    }
}
