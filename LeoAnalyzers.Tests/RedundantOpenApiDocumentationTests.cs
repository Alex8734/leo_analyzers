using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace LeoAnalyzers.Tests;

public class UnusedOpenApiDocumentationTests
{



    [Fact]
    public async Task MultipleReturnTypes_NoRedundantWarning()
    {
        var testCode = @"
        using Microsoft.AspNetCore.Mvc;

        [ApiController]
        public class TestController : ControllerBase
        {
            [HttpGet]
            [ProducesResponseType(200)]
            [ProducesResponseType(404)]
            public IActionResult Get(int id)
            {
                if (id > 0)
                    return Ok();
                return NotFound();
            }
        }";

        // No diagnostics expected since both status codes are used
        await VerifyAnalyzerAsync(testCode);
    }

    [Fact]
    public async Task RedundantProducesResponseType_Detected_ObjectResponse_Multi()
    {
        var testCode = """
                       using Microsoft.AspNetCore.Mvc;
                       using Microsoft.AspNetCore.Http;

                       [ApiController]
                       public class TestController : ControllerBase
                       {
                           [HttpGet]
                           [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
                           [ProducesResponseType(StatusCodes.Status404NotFound)]
                           public IActionResult Get()
                           {
                               return Ok("result");
                           }
                       }
                       """;

        var expectedDiagnostic = new DiagnosticResult(RedundantProducesResponseTypeAttributeAnalyzer.Rule)
            .WithSpan(9, 6, 9, 57)
            .WithMessage("Redundant ProducesResponseType attribute");
        await VerifyAnalyzerAsync(testCode, expectedDiagnostic);

    }


    [Fact]
    public async Task  RedundantProducesResponseType_Detected_ObjectResponse()
    {
        var testCode = """
                               using Microsoft.AspNetCore.Mvc;
                               using Microsoft.AspNetCore.Http;
                       
                               [ApiController]
                               public class TestController : ControllerBase
                               {
                                   [HttpGet]
                                   [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
                                   [ProducesResponseType(StatusCodes.Status404NotFound)]
                                   public IActionResult Get()
                                   {
                                       return Ok("result");
                                   }
                               }
                               """;

        var expectedDiagnostic = new DiagnosticResult(RedundantProducesResponseTypeAttributeAnalyzer.Rule)
            .WithSpan(9, 6, 9, 57)
            .WithMessage("Redundant ProducesResponseType attribute");

        await VerifyAnalyzerAsync(testCode, expectedDiagnostic);
    }
    private static async Task VerifyAnalyzerAsync(string source, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<RedundantProducesResponseTypeAttributeAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net60,
            TestState = {
                AdditionalReferences = {
                    MetadataReference.CreateFromFile(typeof(Microsoft.AspNetCore.Mvc.ControllerBase).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Microsoft.AspNetCore.Http.StatusCodes).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Microsoft.AspNetCore.Mvc.IActionResult).Assembly.Location)
                }
            }
        };

        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
    [Fact]
    public async Task RedundantProducesResponseType_Detected_Simple()
    {
        var testCode = """
                       using Microsoft.AspNetCore.Http;
                       using Microsoft.AspNetCore.Mvc;

                       [ApiController]
                       public class TestController : ControllerBase
                       {
                           [HttpGet]
                           [ProducesResponseType(StatusCodes.Status200OK)]
                           [ProducesResponseType(StatusCodes.Status400BadRequest)] // This should be flagged
                           public IActionResult Get()
                           {
                               return Ok();
                           }
                       }
                       """;
            var expected = new DiagnosticResult(RedundantProducesResponseTypeAttributeAnalyzer.Rule)
                .WithSpan(9, 6, 9, 59)
                .WithMessage("Redundant ProducesResponseType attribute");

        await VerifyAnalyzerAsync(testCode, expected);
    }
}