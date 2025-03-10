
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
namespace LeoAnalyzers.Sample;

[ApiController]
public class ControllerExample: ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(string),StatusCodes.Status200OK)]    //   --|      these two shouldn't
    [ProducesResponseType(typeof(int),StatusCodes.Status200OK)]         //   --|           be the same
    [ProducesResponseType(StatusCodes.Status400BadRequest)] // This should be detected as redundant
    public IActionResult Get()
    {
        return BadRequest();
        return Ok("result"); // unreachable code shouldn't be detected;
    }

    [HttpPost]
    [ProducesResponseType(typeof(string),StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)] // This should be detected as redundant
    public IActionResult Post()
    {
        return new ObjectResult("result")
        {
            StatusCode = StatusCodes.Status200OK
        };
    }

}

public class Foo{
    public int Bar { get; set; }
}